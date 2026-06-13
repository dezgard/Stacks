# Changelog

## 0.1.6 - 2026-06-13

- Changed oversized hand/hold slot placement to split the cursor stack.
- Hold slots now take only the amount vanilla says they can hold and leave the remainder on the cursor.
- Removed the broad `Slot.OpenSpaces` override that made hold slots accept an entire oversized stack.
- Added `[StacksHoldSlotSplit]` diagnostics.

## 0.1.0 - 2026-06-08

- Initial standalone Stacks plugin.
- Added configurable per-container stack limits.
- Added default vanilla container limit of 15 for unlisted `IsContainer` owners.
- Added default condition rules:
  - `IsBackpack:50`
  - `IsDezgardFreightContainer:30`
  - `IsDezgardSmallFreightContainer:30`
- Patched `CondOwner.CanStackOnItem`.
- Patched `Container.CanFit`.
- Added diagnostics for stack-room decisions, full-container fit overrides, and direct container stack attempts.
