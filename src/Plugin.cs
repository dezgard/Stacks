using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Ostranauts.Inventory;
using UnityEngine;

namespace Stacks
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal const string PluginGuid = "com.dezgard.ostranauts.stacks";
        internal const string PluginName = "Stacks";
        internal const string PluginVersion = "0.1.1";

        private Harmony _harmony;

        private void Awake()
        {
            StackLimitRules.Configure(Config, Logger);

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll();

            Logger.LogInfo(PluginName + " loaded version=" + PluginVersion
                + " defaultVanillaContainerLimit=" + StackLimitRules.DefaultContainerLimit
                + " definitionRules=" + StackLimitRules.DefinitionRuleCount
                + " conditionRules=" + StackLimitRules.ConditionRuleCount
                + " rules=" + StackLimitRules.RuleSummary);
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }

    internal static class StackLimitRules
    {
        private const int AbsoluteMaxLimit = 9999;
        private const int DefaultVanillaContainerLimit = 15;
        private const string DefaultConditionRules = "IsBackpack:50,IsDezgardFreightContainer:30,IsDezgardSmallFreightContainer:30";

        private static readonly string[] ForbiddenCargoConds =
        {
            "IsHuman",
            "IsCrew",
            "IsNPC",
            "IsInstalled",
            "IsSystem",
            "IsContainer"
        };

        private static readonly Dictionary<string, int> DefinitionLimits = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly List<LimitRule> ConditionLimits = new List<LimitRule>();
        private static readonly Dictionary<string, int> LogCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        private static ConfigEntry<int> _defaultContainerLimit;
        private static ConfigEntry<string> _definitionRules;
        private static ConfigEntry<string> _conditionRules;
        private static ConfigEntry<bool> _requireIsContainerForDefaultLimit;
        private static ConfigEntry<bool> _logStackDecisions;
        private static ConfigEntry<bool> _logCanFitDecisions;
        private static ConfigEntry<bool> _logStackInsideResults;
        private static ConfigEntry<bool> _logDirectPlacementResults;
        private static ConfigEntry<int> _maxLogEntriesPerKey;
        private static ConfigEntry<int> _maxLogEntriesPerSession;
        private static ManualLogSource _log;
        private static int _totalLogEntries;

        internal static int DefaultContainerLimit
        {
            get
            {
                if (_defaultContainerLimit == null)
                    return DefaultVanillaContainerLimit;

                return ClampAllowZero(_defaultContainerLimit.Value, AbsoluteMaxLimit);
            }
        }

        internal static int DefinitionRuleCount => DefinitionLimits.Count;
        internal static int ConditionRuleCount => ConditionLimits.Count;
        internal static string RuleSummary => "definitions=[" + DescribeDefinitionRules() + "] conditions=[" + DescribeConditionRules() + "]";

        internal static void Configure(ConfigFile config, ManualLogSource log)
        {
            _log = log;

            _defaultContainerLimit = config.Bind(
                "StackLimits",
                "DefaultVanillaContainerLimit",
                DefaultVanillaContainerLimit,
                "Fallback per-stack limit for any container without a more specific rule. Set to 0 to leave unlisted containers on vanilla behavior.");

            _definitionRules = config.Bind(
                "StackLimits",
                "ByDefinition",
                "",
                "Comma-separated container definition rules, e.g. MyCargoCrate:15,SmallCrate:3. Uses the container owner's strCODef.");

            _conditionRules = config.Bind(
                "StackLimits",
                "ByCondition",
                DefaultConditionRules,
                "Comma-separated container condition rules, e.g. IsBackpack:50,IsDezgardFreightContainer:30,IsDezgardSmallFreightContainer:30.");

            _requireIsContainerForDefaultLimit = config.Bind(
                "StackLimits",
                "RequireIsContainerForDefaultLimit",
                true,
                "When true, the fallback default limit only applies to owners with IsContainer. Exact definition and condition rules still apply.");

            _logStackDecisions = config.Bind(
                "Logging",
                "LogStackDecisions",
                true,
                "Log configured CanStackOnItem decisions. Useful for testing, but can be disabled after validation.");

            _logCanFitDecisions = config.Bind(
                "Logging",
                "LogCanFitDecisions",
                true,
                "Log when Stacks changes Container.CanFit from false to true because a matching stack has room.");

            _logStackInsideResults = config.Bind(
                "Logging",
                "LogStackInsideResults",
                true,
                "Log direct Container.StackOnInsideItem results so shift-click and auto-add paths can be traced.");

            _logDirectPlacementResults = config.Bind(
                "Logging",
                "LogDirectPlacementResults",
                true,
                "Log when Stacks splits an oversized stack before placing it into an empty container tile.");

            _maxLogEntriesPerKey = config.Bind(
                "Logging",
                "MaxLogEntriesPerKey",
                8,
                "Maximum repeated log entries for the same container/item/reason key per game session.");

            _maxLogEntriesPerSession = config.Bind(
                "Logging",
                "MaxLogEntriesPerSession",
                400,
                "Maximum Stacks diagnostic log entries per game session.");

            RebuildRules();
        }

        internal static bool TryGetConfiguredStackRoom(CondOwner existing, CondOwner incoming, out int room)
        {
            StackRoomDecision decision;
            var configured = TryGetConfiguredStackRoom(existing, incoming, out decision);
            room = decision != null ? decision.Room : 0;
            return configured;
        }

        internal static bool TryGetConfiguredStackRoom(CondOwner existing, CondOwner incoming, out StackRoomDecision decision)
        {
            decision = null;

            if (existing == null || incoming == null || existing == incoming)
                return false;

            var owner = existing.objCOParent;
            var container = owner != null ? owner.objContainer : null;
            if (container == null)
                return false;

            int limit;
            string source;
            if (!TryGetLimit(container, out limit, out source))
                return false;

            if (!SameDefinition(existing, incoming))
                return false;

            decision = new StackRoomDecision
            {
                Container = container,
                ContainerOwner = owner,
                Existing = existing,
                Incoming = incoming,
                Limit = limit,
                Source = source,
                ExistingCount = StackCountSafe(existing),
                IncomingCount = StackCountSafe(incoming)
            };

            string reason;
            if (!CanStackInContainer(container, owner, existing, incoming, out reason))
            {
                decision.Room = 0;
                decision.Reason = reason;
                decision.Allowed = false;
                return true;
            }

            decision.Room = Math.Min(decision.IncomingCount, Math.Max(limit - decision.ExistingCount, 0));
            decision.Allowed = decision.Room > 0;
            decision.Reason = decision.Allowed ? "room" : "full";
            return true;
        }

        internal static bool HasDirectStackTarget(Container container, CondOwner incoming, out StackRoomDecision matchingDecision)
        {
            matchingDecision = null;

            try
            {
                if (container == null || incoming == null || !container.bAllowStacking)
                    return false;

                int limit;
                string source;
                if (!TryGetLimit(container, out limit, out source))
                    return false;

                if (!SafeAllowedCO(container, incoming))
                    return false;

                var owner = container.CO;
                var contents = container.GetCOs(true, null);
                if (contents == null)
                    return false;

                foreach (var candidate in contents)
                {
                    if (candidate == null || candidate.coStackHead != null)
                        continue;

                    if (owner != null && candidate.objCOParent != owner)
                        continue;

                    StackRoomDecision decision;
                    if (TryGetConfiguredStackRoom(candidate, incoming, out decision) && decision.Room > 0)
                    {
                        matchingDecision = decision;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.LogWarning("HasDirectStackTarget failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            return false;
        }

        internal static bool TryGetDirectPlacementDecision(Container container, CondOwner incoming, string context, out DirectPlacementDecision decision)
        {
            decision = null;

            if (container == null || incoming == null || incoming.coStackHead != null)
                return false;

            var owner = container.CO;
            if (owner == null || owner.bDestroyed || incoming.bDestroyed)
                return false;

            int limit;
            string source;
            if (!TryGetLimit(container, out limit, out source))
                return false;

            var incomingCount = StackCountSafe(incoming);
            if (incomingCount <= limit)
                return false;

            if (IsForbiddenCargo(incoming))
                return false;

            if (!SafeAllowedCO(container, incoming))
                return false;

            decision = new DirectPlacementDecision
            {
                Context = context,
                Container = container,
                ContainerOwner = owner,
                Incoming = incoming,
                Limit = limit,
                Source = source,
                IncomingCount = incomingCount,
                PlaceCount = limit,
                RemainderCount = incomingCount - limit
            };

            return true;
        }

        internal static bool TrySplitDetachedStackForLimit(CondOwner incoming, int limit, out CondOwner placed, out CondOwner remainder)
        {
            placed = incoming;
            remainder = null;

            if (incoming == null || limit <= 0)
                return false;

            var stack = incoming.StackAsList;
            if (stack == null || stack.Count <= limit)
                return false;

            var remainderCount = stack.Count - limit;
            var remainderStack = stack.GetRange(0, remainderCount);
            var placedStack = stack.GetRange(remainderCount, limit);

            remainder = CondOwner.StackFromList(remainderStack);
            placed = CondOwner.StackFromList(placedStack);

            return placed != null;
        }

        internal static CondOwner AddWithConfiguredLimit(Container container, CondOwner incoming, string context)
        {
            if (container == null || incoming == null)
                return incoming;

            DirectPlacementDecision directDecision;
            if (!TryGetDirectPlacementDecision(container, incoming, context, out directDecision))
                return null;

            var remaining = incoming;

            foreach (var candidate in container.GetCOs(true, null))
            {
                if (remaining == null)
                    break;

                if (candidate == null || candidate == remaining || candidate.coStackHead != null)
                    continue;

                if (candidate.objCOParent != directDecision.ContainerOwner)
                    continue;

                if (candidate.CanStackOnItem(remaining) <= 0)
                    continue;

                var beforeCount = StackCountSafe(remaining);
                remaining = candidate.StackCO(remaining);
                LogDirectPlacement(context + ":merge", directDecision, null, remaining, beforeCount, StackCountSafe(remaining), PairXY.GetInvalid());
            }

            while (remaining != null)
            {
                var remainingCount = StackCountSafe(remaining);
                var placeCount = Math.Min(remainingCount, directDecision.Limit);

                PairXY pairXY;
                if (!container.CanAddSimple(remaining, out pairXY))
                    break;

                if (remainingCount > directDecision.Limit)
                {
                    CondOwner placed;
                    CondOwner remainder;
                    if (!TrySplitDetachedStackForLimit(remaining, directDecision.Limit, out placed, out remainder))
                        break;

                    container.AddCOSimple(placed, pairXY);
                    LogDirectPlacement(context + ":auto-tile", directDecision, placed, remainder, remainingCount, StackCountSafe(remainder), pairXY);
                    remaining = remainder;
                    continue;
                }

                container.AddCOSimple(remaining, pairXY);
                LogDirectPlacement(context + ":auto-final", directDecision, remaining, null, remainingCount, 0, pairXY);
                remaining = null;
            }

            container.Redraw();
            return remaining;
        }

        internal static void LogStackDecision(string context, StackRoomDecision decision, int vanillaResult, int finalResult)
        {
            if (!LogStackDecisions || decision == null)
                return;

            var changed = vanillaResult != finalResult;
            var key = context
                + "|" + SafeDef(decision.ContainerOwner)
                + "|" + SafeDef(decision.Existing)
                + "|" + decision.Source
                + "|" + decision.Reason
                + "|" + finalResult;

            LogThrottled(
                key,
                "[StacksStack] context=" + context
                + " changed=" + changed
                + " source=" + decision.Source
                + " limit=" + decision.Limit
                + " existingCount=" + decision.ExistingCount
                + " incomingCount=" + decision.IncomingCount
                + " room=" + finalResult
                + " vanillaRoom=" + vanillaResult
                + " reason=" + decision.Reason
                + " container=" + DescribeCO(decision.ContainerOwner)
                + " existing=" + DescribeCO(decision.Existing)
                + " incoming=" + DescribeCO(decision.Incoming));
        }

        internal static void LogCanFitDecision(Container container, CondOwner incoming, StackRoomDecision decision)
        {
            if (!LogCanFitDecisions || decision == null)
                return;

            var key = "CanFit|" + SafeDef(decision.ContainerOwner) + "|" + SafeDef(incoming) + "|" + decision.Source;
            LogThrottled(
                key,
                "[StacksCanFit] changed=false-to-true"
                + " source=" + decision.Source
                + " limit=" + decision.Limit
                + " existingCount=" + decision.ExistingCount
                + " incomingCount=" + decision.IncomingCount
                + " room=" + decision.Room
                + " container=" + DescribeContainer(container)
                + " incoming=" + DescribeCO(incoming)
                + " stackTarget=" + DescribeCO(decision.Existing));
        }

        internal static void LogDirectPlacement(string context, DirectPlacementDecision decision, CondOwner placed, CondOwner remainder, int originalCount, int remainderCount, PairXY pairXY)
        {
            if (!LogDirectPlacementResults || decision == null)
                return;

            var key = "Direct|" + context + "|" + SafeDef(decision.ContainerOwner) + "|" + SafeDef(decision.Incoming) + "|" + decision.Source;
            LogThrottled(
                key,
                "[StacksDirectPlace] context=" + context
                + " source=" + decision.Source
                + " limit=" + decision.Limit
                + " originalCount=" + originalCount
                + " placeCount=" + StackCountSafe(placed)
                + " remainderCount=" + remainderCount
                + " tile=" + DescribePair(pairXY)
                + " container=" + DescribeCO(decision.ContainerOwner)
                + " incoming=" + DescribeCO(decision.Incoming)
                + " placed=" + DescribeCO(placed)
                + " remainder=" + DescribeCO(remainder));
        }

        internal static void LogStackInsideResult(Container container, CondOwner incoming, CondOwner leftover)
        {
            if (!LogStackInsideResults)
                return;

            int limit;
            string source;
            if (!TryGetLimit(container, out limit, out source))
                return;

            var owner = SafeContainerOwner(container);
            var key = "StackInside|" + SafeDef(owner) + "|" + SafeDef(incoming) + "|" + SafeDef(leftover);
            LogThrottled(
                key,
                "[StacksStackInside] source=" + source
                + " limit=" + limit
                + " container=" + DescribeCO(owner)
                + " incoming=" + DescribeCO(incoming)
                + " leftover=" + DescribeCO(leftover));
        }

        private static bool TryGetLimit(Container container, out int limit, out string source)
        {
            limit = 0;
            source = null;

            if (container == null)
                return false;

            var owner = container.CO;
            if (owner == null || owner.bDestroyed)
                return false;

            var definition = owner.strCODef;
            if (!string.IsNullOrEmpty(definition) && DefinitionLimits.TryGetValue(definition, out limit))
            {
                source = "definition:" + definition;
                return limit > 0;
            }

            foreach (var rule in ConditionLimits)
            {
                if (HasCond(owner, rule.Key))
                {
                    limit = rule.Limit;
                    source = "condition:" + rule.Key;
                    return limit > 0;
                }
            }

            if (RequireIsContainerForDefaultLimit && !HasCond(owner, "IsContainer"))
                return false;

            limit = DefaultContainerLimit;
            source = "default";
            return limit > 0;
        }

        private static bool CanStackInContainer(Container container, CondOwner owner, CondOwner existing, CondOwner incoming, out string reason)
        {
            reason = "blocked";

            if (container == null || owner == null || existing == null || incoming == null)
            {
                reason = "missing";
                return false;
            }

            if (!container.bAllowStacking)
            {
                reason = "container-stacking-disabled";
                return false;
            }

            if (existing.bDestroyed || incoming.bDestroyed)
            {
                reason = "destroyed";
                return false;
            }

            if (existing.coStackHead != null || incoming.coStackHead != null)
            {
                reason = "stack-child";
                return false;
            }

            if (existing.objCOParent != owner)
            {
                reason = "parent-mismatch";
                return false;
            }

            if (IsForbiddenCargo(existing) || IsForbiddenCargo(incoming))
            {
                reason = "forbidden-cargo";
                return false;
            }

            if (!SafeAllowedCO(container, incoming))
            {
                reason = "container-filter";
                return false;
            }

            reason = "room";
            return true;
        }

        private static bool SameDefinition(CondOwner existing, CondOwner incoming)
        {
            return existing != null
                && incoming != null
                && !string.IsNullOrEmpty(existing.strCODef)
                && string.Equals(existing.strCODef, incoming.strCODef, StringComparison.Ordinal);
        }

        private static bool IsForbiddenCargo(CondOwner co)
        {
            foreach (var cond in ForbiddenCargoConds)
            {
                if (HasCond(co, cond))
                    return true;
            }

            return false;
        }

        private static bool SafeAllowedCO(Container container, CondOwner incoming)
        {
            try
            {
                return container != null && incoming != null && container.AllowedCO(incoming);
            }
            catch
            {
                return false;
            }
        }

        private static bool HasCond(CondOwner co, string cond)
        {
            try
            {
                return co != null && !string.IsNullOrEmpty(cond) && co.HasCond(cond);
            }
            catch
            {
                return false;
            }
        }

        private static int StackCountSafe(CondOwner co)
        {
            try
            {
                return co != null && co.StackCount > 0 ? co.StackCount : 1;
            }
            catch
            {
                return 1;
            }
        }

        private static void RebuildRules()
        {
            DefinitionLimits.Clear();
            ConditionLimits.Clear();
            LogCounts.Clear();
            _totalLogEntries = 0;

            ParseDefinitionRules(_definitionRules?.Value, DefinitionLimits, "ByDefinition");
            ParseConditionRules(_conditionRules?.Value, ConditionLimits, "ByCondition");
        }

        private static bool RequireIsContainerForDefaultLimit
        {
            get { return _requireIsContainerForDefaultLimit == null || _requireIsContainerForDefaultLimit.Value; }
        }

        private static bool LogStackDecisions
        {
            get { return _logStackDecisions == null || _logStackDecisions.Value; }
        }

        private static bool LogCanFitDecisions
        {
            get { return _logCanFitDecisions == null || _logCanFitDecisions.Value; }
        }

        private static bool LogStackInsideResults
        {
            get { return _logStackInsideResults == null || _logStackInsideResults.Value; }
        }

        private static bool LogDirectPlacementResults
        {
            get { return _logDirectPlacementResults == null || _logDirectPlacementResults.Value; }
        }

        private static int MaxLogEntriesPerKey
        {
            get { return _maxLogEntriesPerKey == null ? 8 : Clamp(_maxLogEntriesPerKey.Value, 1, 1000); }
        }

        private static int MaxLogEntriesPerSession
        {
            get { return _maxLogEntriesPerSession == null ? 400 : Clamp(_maxLogEntriesPerSession.Value, 1, 100000); }
        }

        private static void ParseDefinitionRules(string value, Dictionary<string, int> destination, string label)
        {
            foreach (var rule in ParseRules(value, label))
                destination[rule.Key] = rule.Limit;
        }

        private static void ParseConditionRules(string value, List<LimitRule> destination, string label)
        {
            foreach (var rule in ParseRules(value, label))
                destination.Add(rule);
        }

        private static IEnumerable<LimitRule> ParseRules(string value, string label)
        {
            if (string.IsNullOrWhiteSpace(value))
                yield break;

            var entries = value.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var rawEntry in entries)
            {
                var entry = rawEntry.Trim();
                if (entry.Length == 0)
                    continue;

                var splitIndex = entry.IndexOf(':');
                if (splitIndex < 0)
                    splitIndex = entry.IndexOf('=');

                if (splitIndex <= 0 || splitIndex >= entry.Length - 1)
                {
                    _log?.LogWarning("Ignoring invalid " + label + " rule: " + entry);
                    continue;
                }

                var key = entry.Substring(0, splitIndex).Trim();
                var rawLimit = entry.Substring(splitIndex + 1).Trim();

                int limit;
                if (key.Length == 0 || !int.TryParse(rawLimit, out limit) || limit <= 0)
                {
                    _log?.LogWarning("Ignoring invalid " + label + " rule: " + entry);
                    continue;
                }

                yield return new LimitRule(key, Clamp(limit, 1, AbsoluteMaxLimit));
            }
        }

        private static int ClampAllowZero(int value, int max)
        {
            if (value <= 0)
                return 0;

            return Clamp(value, 1, max);
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
                return min;

            if (value > max)
                return max;

            return value;
        }

        private static void LogThrottled(string key, string message)
        {
            if (_log == null || string.IsNullOrEmpty(message))
                return;

            if (_totalLogEntries >= MaxLogEntriesPerSession)
                return;

            if (string.IsNullOrEmpty(key))
                key = "<none>";

            int count;
            LogCounts.TryGetValue(key, out count);
            if (count >= MaxLogEntriesPerKey)
                return;

            LogCounts[key] = count + 1;
            _totalLogEntries++;

            if (count + 1 == MaxLogEntriesPerKey)
                message += " suppressedFurtherForKey=true";

            _log.LogInfo(message);
        }

        private static string DescribeContainer(Container container)
        {
            return DescribeCO(SafeContainerOwner(container));
        }

        private static CondOwner SafeContainerOwner(Container container)
        {
            try
            {
                return container != null ? container.CO : null;
            }
            catch
            {
                return null;
            }
        }

        private static string DescribeCO(CondOwner co)
        {
            if (co == null)
                return "<null>";

            try
            {
                return SafeName(co)
                    + " def=" + SafeDef(co)
                    + " id=" + SafeId(co)
                    + " stack=" + StackCountSafe(co)
                    + " parent=" + SafeName(co.objCOParent);
            }
            catch
            {
                return "<error>";
            }
        }

        private static string DescribePair(PairXY pairXY)
        {
            try
            {
                if (pairXY.IsInvalid())
                    return "<invalid>";

                return pairXY.x + "," + pairXY.y;
            }
            catch
            {
                return "<error>";
            }
        }

        private static string SafeName(CondOwner co)
        {
            if (co == null)
                return "<null>";

            try
            {
                if (!string.IsNullOrEmpty(co.strNameFriendly))
                    return co.strNameFriendly;

                if (!string.IsNullOrEmpty(co.strName))
                    return co.strName;

                return SafeDef(co);
            }
            catch
            {
                return "<error>";
            }
        }

        private static string SafeDef(CondOwner co)
        {
            if (co == null)
                return "<null>";

            try
            {
                return !string.IsNullOrEmpty(co.strCODef) ? co.strCODef : "<none>";
            }
            catch
            {
                return "<error>";
            }
        }

        private static string SafeId(CondOwner co)
        {
            if (co == null)
                return "<null>";

            try
            {
                return !string.IsNullOrEmpty(co.strID) ? co.strID : "<none>";
            }
            catch
            {
                return "<error>";
            }
        }

        private static string DescribeDefinitionRules()
        {
            if (DefinitionLimits.Count == 0)
                return "<none>";

            var parts = new List<string>();
            foreach (var rule in DefinitionLimits)
                parts.Add(rule.Key + ":" + rule.Value);

            return string.Join(",", parts.ToArray());
        }

        private static string DescribeConditionRules()
        {
            if (ConditionLimits.Count == 0)
                return "<none>";

            var parts = new List<string>();
            foreach (var rule in ConditionLimits)
                parts.Add(rule.Key + ":" + rule.Limit);

            return string.Join(",", parts.ToArray());
        }

        private sealed class LimitRule
        {
            internal LimitRule(string key, int limit)
            {
                Key = key;
                Limit = limit;
            }

            internal string Key { get; }
            internal int Limit { get; }
        }

        internal sealed class StackRoomDecision
        {
            internal Container Container;
            internal CondOwner ContainerOwner;
            internal CondOwner Existing;
            internal CondOwner Incoming;
            internal int Limit;
            internal string Source;
            internal int ExistingCount;
            internal int IncomingCount;
            internal int Room;
            internal bool Allowed;
            internal string Reason;
        }

        internal sealed class DirectPlacementDecision
        {
            internal string Context;
            internal Container Container;
            internal CondOwner ContainerOwner;
            internal CondOwner Incoming;
            internal int Limit;
            internal string Source;
            internal int IncomingCount;
            internal int PlaceCount;
            internal int RemainderCount;
        }
    }

    internal static class DirectPlacementHandler
    {
        private static readonly MethodInfo ProcessRemainderMethod = AccessTools.Method(
            typeof(GUIInventoryItem),
            "ProcessRemainder",
            new Type[] { typeof(CondOwner), typeof(GUIInventoryWindow), typeof(CondOwner), typeof(Ship) });

        internal static bool TryHandleMoveInventories(GUIInventoryItem item, GUIInventoryWindow destination, Vector2 position, bool canPlaceSelf, out bool result)
        {
            result = false;

            try
            {
                if (item == null || destination == null || item.CO == null)
                    return false;

                if (destination.type != InventoryWindowType.Container || destination.CO == null || destination.CO.objContainer == null)
                    return false;

                var container = destination.CO.objContainer;
                var incoming = item.CO;

                StackLimitRules.DirectPlacementDecision decision;
                if (!StackLimitRules.TryGetDirectPlacementDecision(container, incoming, "MoveInventories", out decision))
                    return false;

                if (!canPlaceSelf && destination.gridLayout.GetInventoryItem(incoming.pairInventoryXY) == item)
                    return false;

                var pairXY = GetDropPair(item, destination, position);
                if (!IsEmptyPlacement(destination, item, incoming, pairXY))
                    return false;

                result = PlaceOneLimitedStack(item, container, destination, pairXY, decision, "MoveInventories");
                return true;
            }
            catch (Exception ex)
            {
                StackLimitRules.LogDirectPlacement("MoveInventories:error", null, null, null, 0, 0, PairXY.GetInvalid());
                UnityEngine.Debug.LogWarning("Stacks MoveInventories direct placement failed: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        internal static bool TryHandleStackOrAddToContainer(GUIInventoryItem item, Container container, out bool result)
        {
            result = false;

            try
            {
                if (item == null || container == null || item.CO == null)
                    return false;

                StackLimitRules.DirectPlacementDecision decision;
                if (!StackLimitRules.TryGetDirectPlacementDecision(container, item.CO, "StackOrAddToContainer", out decision))
                    return false;

                var previousContainer = item.CO.objCOParent;
                var previousShip = item.CO.ship;
                var remaining = item.CO;

                remaining.RemoveFromCurrentHome();

                foreach (var candidate in container.GetCOs(false, null))
                {
                    if (remaining == null)
                        break;

                    if (candidate == null || candidate == remaining || candidate.coStackHead != null)
                        continue;

                    if (candidate.CanStackOnItem(remaining) <= 0)
                        continue;

                    var beforeCount = SafeStackCount(remaining);
                    remaining = candidate.StackCO(remaining);
                    StackLimitRules.LogDirectPlacement("StackOrAddToContainer:merge", decision, null, remaining, beforeCount, SafeStackCount(remaining), PairXY.GetInvalid());
                }

                if (remaining == null)
                {
                    FinishGuiMove(item, container.InventoryWindow, null, previousContainer, previousShip);
                    result = true;
                    return true;
                }

                PairXY pairXY;
                if (!container.CanAddSimple(remaining, out pairXY))
                {
                    RestoreRemainder(item, remaining, container.InventoryWindow, previousContainer, previousShip);
                    result = true;
                    return true;
                }

                CondOwner placed;
                CondOwner remainder;
                var beforeSplit = SafeStackCount(remaining);
                if (StackLimitRules.TrySplitDetachedStackForLimit(remaining, decision.Limit, out placed, out remainder))
                {
                    container.AddCOSimple(placed, pairXY);
                    item.CO = placed;
                    StackLimitRules.LogDirectPlacement("StackOrAddToContainer:direct", decision, placed, remainder, beforeSplit, SafeStackCount(remainder), pairXY);
                    FinishGuiMove(item, container.InventoryWindow, remainder, previousContainer, previousShip);
                    result = true;
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("Stacks StackOrAddToContainer direct placement failed: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static bool PlaceOneLimitedStack(GUIInventoryItem item, Container container, GUIInventoryWindow destination, PairXY pairXY, StackLimitRules.DirectPlacementDecision decision, string context)
        {
            var incoming = item.CO;
            var previousContainer = incoming.objCOParent;
            var previousShip = incoming.ship;

            incoming.RemoveFromCurrentHome();

            CondOwner placed;
            CondOwner remainder;
            var originalCount = SafeStackCount(incoming);
            if (!StackLimitRules.TrySplitDetachedStackForLimit(incoming, decision.Limit, out placed, out remainder))
                return false;

            if (placed.Item != null)
                placed.Item.fLastRotation = item.fRotLast;

            container.AddCOSimple(placed, pairXY);
            item.CO = placed;

            StackLimitRules.LogDirectPlacement(context + ":direct", decision, placed, remainder, originalCount, SafeStackCount(remainder), pairXY);
            FinishGuiMove(item, destination, remainder, previousContainer, previousShip);
            return true;
        }

        private static void FinishGuiMove(GUIInventoryItem item, GUIInventoryWindow destination, CondOwner remainder, CondOwner previousContainer, Ship previousShip)
        {
            if (item == null)
                return;

            UnityEngine.Object.Destroy(item.gameObject);
            CrewSim.objInstance.SetPartCursor(null);

            InvokeProcessRemainder(item, remainder, destination, previousContainer, previousShip);

            if (destination != null)
                destination.Redraw();

            if (item.windowData != null)
                item.windowData.Redraw();
        }

        private static void RestoreRemainder(GUIInventoryItem item, CondOwner remainder, GUIInventoryWindow destination, CondOwner previousContainer, Ship previousShip)
        {
            InvokeProcessRemainder(item, remainder, destination, previousContainer, previousShip);
        }

        private static void InvokeProcessRemainder(GUIInventoryItem item, CondOwner remainder, GUIInventoryWindow destination, CondOwner previousContainer, Ship previousShip)
        {
            if (remainder == null || item == null)
                return;

            if (ProcessRemainderMethod != null)
            {
                ProcessRemainderMethod.Invoke(item, new object[] { remainder, destination, previousContainer, previousShip });
                return;
            }

            if (previousContainer != null && previousContainer.objContainer != null)
            {
                PairXY pairXY;
                if (previousContainer.objContainer.CanAddSimple(remainder, out pairXY))
                {
                    previousContainer.objContainer.AddCOSimple(remainder, pairXY);
                    return;
                }
            }

            var guiItem = GUIInventoryItem.SpawnInventoryItem(remainder.strID);
            if (guiItem != null)
                guiItem.AttachToCursor();
        }

        private static PairXY GetDropPair(GUIInventoryItem item, GUIInventoryWindow destination, Vector2 position)
        {
            var width = (int)(item.itemRect.rect.width - 24f * CanvasManager.CanvasRatio);
            var height = (int)(item.itemRect.rect.height - 24f * CanvasManager.CanvasRatio);
            if (MathUtils.IsRotationVertical(item.fRotLast))
                MathUtils.Swap(ref width, ref height);

            return destination.PairXYFromPosition(position, width, height);
        }

        private static bool IsEmptyPlacement(GUIInventoryWindow destination, GUIInventoryItem item, CondOwner incoming, PairXY pairXY)
        {
            if (destination == null || destination.gridLayout == null || item == null || incoming == null)
                return false;

            if (!destination.gridLayout.IsGridRectangleUnoccupied(pairXY.x, pairXY.y, pairXY.x + item.itemWidthOnGrid, pairXY.y + item.itemHeightOnGrid, incoming.strID))
                return false;

            if (destination.type == InventoryWindowType.Container && !destination.CO.objContainer.AllowedCO(incoming))
                return false;

            return true;
        }

        private static int SafeStackCount(CondOwner co)
        {
            try
            {
                return co != null && co.StackCount > 0 ? co.StackCount : 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    [HarmonyPatch(typeof(CondOwner), nameof(CondOwner.CanStackOnItem), new Type[] { typeof(CondOwner) })]
    internal static class CondOwnerCanStackOnItemPatch
    {
        private static void Postfix(CondOwner __instance, CondOwner objIncoming, ref int __result)
        {
            var vanillaResult = __result;

            StackLimitRules.StackRoomDecision decision;
            if (StackLimitRules.TryGetConfiguredStackRoom(__instance, objIncoming, out decision))
            {
                __result = decision.Room;
                StackLimitRules.LogStackDecision("CanStackOnItem", decision, vanillaResult, __result);
            }
        }
    }

    [HarmonyPatch(typeof(Container), nameof(Container.CanFit), new Type[] { typeof(CondOwner), typeof(bool), typeof(bool) })]
    internal static class ContainerCanFitPatch
    {
        private static void Postfix(Container __instance, CondOwner coFit, ref bool __result)
        {
            if (__result)
                return;

            StackLimitRules.StackRoomDecision decision;
            if (StackLimitRules.HasDirectStackTarget(__instance, coFit, out decision))
            {
                __result = true;
                StackLimitRules.LogCanFitDecision(__instance, coFit, decision);
            }
        }
    }

    [HarmonyPatch(typeof(Container), "StackOnInsideItem", new Type[] { typeof(CondOwner) })]
    internal static class ContainerStackOnInsideItemPatch
    {
        private static void Postfix(Container __instance, CondOwner coIncoming, CondOwner __result)
        {
            StackLimitRules.LogStackInsideResult(__instance, coIncoming, __result);
        }
    }

    [HarmonyPatch(typeof(Container), nameof(Container.AddCO), new Type[] { typeof(CondOwner) })]
    internal static class ContainerAddCOPatch
    {
        private static bool Prefix(Container __instance, CondOwner objCO, ref CondOwner __result)
        {
            if (__instance == null || objCO == null)
                return true;

            StackLimitRules.DirectPlacementDecision decision;
            if (!StackLimitRules.TryGetDirectPlacementDecision(__instance, objCO, "Container.AddCO", out decision))
                return true;

            __result = StackLimitRules.AddWithConfiguredLimit(__instance, objCO, "Container.AddCO");
            return false;
        }
    }

    [HarmonyPatch(typeof(GUIInventoryItem), nameof(GUIInventoryItem.MoveInventories), new Type[] { typeof(GUIInventoryWindow), typeof(Vector2), typeof(bool) })]
    internal static class GUIInventoryItemMoveInventoriesPatch
    {
        private static bool Prefix(GUIInventoryItem __instance, GUIInventoryWindow destination, Vector2 position, bool canPlaceSelf, ref bool __result)
        {
            bool result;
            if (DirectPlacementHandler.TryHandleMoveInventories(__instance, destination, position, canPlaceSelf, out result))
            {
                __result = result;
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(GUIInventoryItem), nameof(GUIInventoryItem.StackOrAddToContainer), new Type[] { typeof(Container) })]
    internal static class GUIInventoryItemStackOrAddToContainerPatch
    {
        private static bool Prefix(GUIInventoryItem __instance, Container container, ref bool __result)
        {
            bool result;
            if (DirectPlacementHandler.TryHandleStackOrAddToContainer(__instance, container, out result))
            {
                __result = result;
                return false;
            }

            return true;
        }
    }
}
