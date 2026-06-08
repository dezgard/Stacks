# Stacks

Example code for Ostranauts container stack limiting.

Stacks is a small BepInEx/Harmony plugin that shows how to enforce different
item stack limits per container at runtime without editing vanilla container
definitions.

Default example limits:

- vanilla containers: 15 items per tile
- Dezgard freight containers: 30 items per tile
- backpacks: 50 items per tile

## Dependency Notes

Stacks does not reference code from any other Dezgard mod. It only builds
against BepInEx, Harmony, Unity, and Ostranauts' `Assembly-CSharp.dll`.

The freight container defaults depend on condition names used by Dezgard
container mods:

- `IsDezgardFreightContainer`
- `IsDezgardSmallFreightContainer`

If those conditions are not present, those optional rules simply do not match.
The vanilla `IsContainer` fallback and `IsBackpack` rule still work.

The plugin patches the game's inventory/container methods and splits oversized
stacks before they can be placed into a container tile.

This is example mod code, not an official Ostranauts feature.
