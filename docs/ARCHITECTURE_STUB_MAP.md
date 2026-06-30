# Architecture Stub Map

This file tracks the broad system lanes created before the first long implementation pass.
Most lanes are intentionally thin. They exist so future work has a correct home instead of
growing one-off feature paths.

## Core

- `Entities`: one component model for actors, items, props, books, doors, husks, and anchors.
- `Commands`: the shared typed command surface used by Godot, CLI, tests, and agents.
- `References`: normalized refs and selectors such as `self`, `nearest_enemy`, `selected_target`, and `all_enemies`.
- `Validation`: state invariants such as bounds, control pointer validity, and blocking overlap.
- `Transactions`: lightweight state snapshots for rollback around multi-step mutations.
- `Items`: item definitions, inventory cards, reagent cards, and protected inventory support.
- `Status`: mechanical status definitions and flavor aliases.
- `World`: promises, deeds, factions, legend, memories, canon, bonds, scheduled events, regions, and traditions.
- `Runtime`: replay and background-job record shapes.
- `Telemetry`: durable audit record shape.

## Magic

- `Capabilities`: capability-card registry for prompt routing and future context selection.
- `Operations`: canonical engine operation names and aliases.
- `Resolution`: spell response contracts plus validation.
- `Costs`: post-effect cost application for mana, HP, max stats, items, statuses, and curses.
- `Auditing`: spell audit sink interface.

## LLM

- `Configuration`: provider-purpose settings for wild magic, dialogue, item, canon, background, and agent calls.
- `Auditing`: JSONL spell audit sink.
- Providers: mock, Ollama, and an explicit API-compatible placeholder.

## Frontends

- `Sorcerer.Cli` parses JSON and text into the same command records.
- `Sorcerer.Godot` renders a `GameView` ASCII map and maps keys to shared commands.

## Rule

Do not fill these stubs by bypassing them. New mechanics should enter through typed
commands, validated references, engine-owned operations, state transactions, and shared
views.
