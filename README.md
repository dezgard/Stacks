# Stacks

Example code for Ostranauts container stack limiting.

Stacks is a small BepInEx/Harmony plugin that shows how to enforce different
item stack limits per container at runtime without editing vanilla container
definitions.

Default example limits:

- vanilla containers: 15 items per tile
- Dezgard freight containers: 30 items per tile
- backpacks: 50 items per tile

The plugin patches the game's inventory/container methods and splits oversized
stacks before they can be placed into a container tile.

This is example mod code, not an official Ostranauts feature.
