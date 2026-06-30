# Development Setup

Sorcerer is scaffolded as a .NET solution plus a Godot C# project.

## Required Tools

- .NET SDK 8 or newer.
- Godot 4.7 .NET editor, or a compatible Godot 4.x .NET editor after updating the Godot
  project SDK reference.

Current local verification uses .NET SDK 8 and Godot 4.7 .NET.

## Solution Layout

```text
Sorcerer.sln
src/Sorcerer.Core
src/Sorcerer.Magic
src/Sorcerer.Llm
src/Sorcerer.Cli
src/Sorcerer.Godot
tests/Sorcerer.Tests
content/
```

## Expected Commands

Once the SDK is installed:

```powershell
dotnet restore C:\Games\Sorcerer\Sorcerer.sln
dotnet build C:\Games\Sorcerer\Sorcerer.sln
dotnet test C:\Games\Sorcerer\Sorcerer.sln
```

CLI mock run:

```powershell
dotnet run --project C:\Games\Sorcerer\src\Sorcerer.Cli -- --json --debug-state --command "inspect" --command "cast blue fire at the nearest soldier"
```

Spell eval:

```powershell
dotnet run --project C:\Games\Sorcerer\src\Sorcerer.Cli -- --provider mock --eval
```

Unattended mock episode runner:

```powershell
dotnet run --project C:\Games\Sorcerer\src\Sorcerer.Cli -- --provider mock --episode --episodes 2 --max-turns 30 --episode-log C:\Games\Sorcerer\logs\episode_smoke.jsonl
```

Background job smoke:

```powershell
dotnet run --project C:\Games\Sorcerer\src\Sorcerer.Cli -- --provider mock --command "move east" --command "move northeast" --command "examine brazier" --command "jobs" --command "wait" --command "jobs"
```

Script and transcript smoke:

```powershell
dotnet run --project C:\Games\Sorcerer\src\Sorcerer.Cli -- --provider mock --script C:\Games\Sorcerer\content\scripts\background_smoke.txt --transcript C:\Games\Sorcerer\logs\cli_transcript_smoke.jsonl
```

Ollama run:

```powershell
dotnet run --project C:\Games\Sorcerer\src\Sorcerer.Cli -- --provider ollama --model qwen3.5:9b --json --debug-state --command "cast bind the nearest soldier in blue glass"
```

## Godot

Open:

```text
C:\Games\Sorcerer\src\Sorcerer.Godot\project.godot
```

Or launch directly:

```powershell
& "C:\Tools\Godot\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64.exe" --path C:\Games\Sorcerer\src\Sorcerer.Godot
```

The Godot GUI constructs the same `GameSession` as the CLI. It currently provides a
playable ASCII encounter with keyboard movement, mouse targeting, spell entry, command
entry, quick spells, Ollama model controls, pending casts, and read-only state panels.
Mock provider mode is intentionally CLI-only for deterministic agent testing.
