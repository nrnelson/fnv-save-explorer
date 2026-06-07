# CLAUDE.md

Guidance for working in this repository.

## What this is
A from-scratch C# / .NET 10 tool to analyze and (within limits) edit Fallout: New Vegas `.fos`
save files. See [README.md](README.md) for the validated format spec and roadmap.

## Build / test / run
```pwsh
dotnet build FnvSaveExplorer.slnx                       # NOTE: solution is .slnx (XML format), not .sln
dotnet test tests/FnvSaveExplorer.Tests/FnvSaveExplorer.Tests.csproj
dotnet run --project src/FnvSaveExplorer.App            # WPF GUI
dotnet run --project src/FnvSaveExplorer.Cli -- dump "<save.fos>"
```

## Architecture (read before changing the writer)
- **Retention model:** `FalloutSave` (in `Core`) keeps the original byte array verbatim and only
  decodes the header/screenshot/plugin region, recording editable-field offsets. The body
  (globals/change forms/FormID array) is **never re-serialized from a model** — it is preserved
  byte-for-byte. `ToBytes()` with no edits MUST equal the input. Don't break this invariant.
- **Editing is same-length only.** The body's File Location Table holds *absolute* file offsets,
  so a length change would shift them. Fixed-width edits (e.g. `u32` level) are safe; rejecting
  length-changing edits is intentional until offset-fixup is implemented.

## Key files
- `Core/FalloutSave.cs` — parse + retention writer + edits.
- `Core/ByteReader.cs` — little-endian cursor (throws `SaveFormatException` with offset).
- `App/MainViewModel.cs` + `MainWindow.xaml` — WPF GUI (MVVM, dialogs in code-behind).

## Testing against real data
Real `.fos` saves live in `C:\Users\<user>\Documents\My Games\FalloutNV\Saves\`. The test theory
`Real_saves_round_trip_byte_identical` auto-discovers them (and a local `samples/` folder) and is
skipped when none exist. **Never write to the originals** — edit demos write to new files only.

## Conventions
- Target `net10.0` (Core/Cli/Tests) and `net10.0-windows` (App). Nullable + implicit usings on.
- Reverse-engineering work belongs behind the validated parser; keep undecoded regions as raw bytes
  with clear "not yet decoded" labelling rather than guessing.
