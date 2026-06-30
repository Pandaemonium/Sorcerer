# Development Setup

Sorcerer is scaffolded as a .NET solution plus a Godot C# project.

## Required Tools

- .NET SDK 8 or newer.
- Godot 4.7 .NET editor, or a compatible Godot 4.x .NET editor after updating the Godot
  project SDK reference.

This machine currently has .NET runtimes but no .NET SDK, so `dotnet build` and
`dotnet test` cannot run until the SDK is installed.

Current local check:

```text
dotnet --info
  .NET runtimes present
  No SDKs were found

godot
  not found on PATH
```

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

Ollama run:

```powershell
dotnet run --project C:\Games\Sorcerer\src\Sorcerer.Cli -- --provider ollama --model qwen3.5:9b --json --debug-state --command "cast bind the nearest soldier in blue glass"
```

## Godot

Open:

```text
C:\Games\Sorcerer\src\Sorcerer.Godot\project.godot
```

The Godot shell is intentionally minimal. It constructs the same `GameSession` as the CLI
and renders a simple `GameView` placeholder. The real ASCII renderer should replace the
placeholder after the backend contracts settle.
