# FNV Save Explorer

A from-scratch tool to **analyze** and (safely, within limits) **edit** Fallout: New Vegas
`.fos` save files. Built in C# / .NET 10 with a reusable Core library, a WPF desktop GUI, and
a CLI — and validated against real saves.

## Status

| Capability | State |
|---|---|
| Parse header metadata (name, title, level, location, playtime, save #, language) | ✅ validated on 16 real saves |
| Decode + render the embedded screenshot | ✅ |
| Read the plugin / load order list | ✅ |
| Byte-identical round-trip (open → save) | ✅ all 16 real saves + autosave/quicksave |
| Safe in-place edits (level, save #, same-length rename) | ✅ proven (1-byte diff, file size unchanged) |
| **File Location Table** (offsets/counts into the body) | ✅ decoded & verified on all 16 saves |
| **Global data tables** (Player Location, Global Variables, Weather…) | ✅ 12 records (types 0–11) enumerated |
| **Misc Stats** counters (quests, kills, locations…) | ✅ decoded **and safely editable** (stat 1→999 = 2-byte diff) |
| **FormID array** + iref resolution | ✅ decoded; locates the **player change forms** in all 16 saves |
| **Player SPECIAL** (S P E C I A L) | ✅ decoded **and safely editable** — verified on all 16 saves (each sums to 40) |
| **Change forms** (inventory, perks, per-actor state) | 🔬 region/count/player located; full per-record decode **next** |

## The `.fos` format (validated against real New Vegas saves)

`.fos` files are little-endian and share the `FO3SAVEGAME` signature with Fallout 3. The
header region is **uncompressed** (unlike Fallout 4 / Skyrim SE). Strings are length-prefixed
and fields are separated by `0x7C` (`|`) delimiters.

### Header layout (offsets from a real save)

| Offset | Field | Type | Notes |
|---|---|---|---|
| `0x00` | signature | `char[11]` | `FO3SAVEGAME` |
| `0x0B` | `saveHeaderSize` | `u32` | **screenshot starts at `0x0F + saveHeaderSize`** (the key fixup value) |
| `0x0F` | version | `u32` | `0x30` |
| `0x13` | delimiter | `0x7C` | |
| `0x14` | language | 64-byte null-padded | e.g. `ENGLISH` — **New Vegas only**; Fallout 3 omits it |
| … | width, height | `u32`, `u32` | screenshot dims (e.g. 512×288, 16:9) |
| … | saveNumber | `u32` | |
| … | name, title | prefixed strings | `u16` length, `0x7C`, bytes, `0x7C` |
| … | level | `u32` | |
| … | location, playtime | prefixed strings | playtime like `000.24.41` |
| `0x0F + saveHeaderSize` | screenshot | `width*height*3` | raw 24-bit RGB |
| then | trailer byte | `u8` | documented as `0x15`; **varies in practice** |
| then | `pluginStructSize` | `u32` | size of the plugin block |
| then | pluginCount | `u8` | followed by `0x7C` |
| then | plugins | prefixed strings | `pluginCount` of them |
| then | **body** | — | File Location Table → globals → change forms → FormID array (undecoded) |

### The body and why editing is constrained

After the plugin list is a **File Location Table** of `u32` *absolute file offsets* into the
body sections. Because those offsets are absolute, **any edit that changes a length shifts
everything after it and invalidates the table.** So this tool only performs **same-length /
fixed-width edits** (e.g. a `u32` level or stat in place), which shift nothing. Length-changing
edits (arbitrary rename, plugin changes) need full offset rewriting and are not enabled yet.

### Decoded body layout (verified across the 16 saves)

| FLT slot | Meaning | Notes |
|---|---|---|
| `[0]` | FormID array count offset | array is `u32 count` then `count` × `u32` (e.g. 8108 entries) |
| `[1]` | trailing/unknown table offset | small footer near EOF |
| `[2]` | global data table 1 offset | **12 records, types 0–11** |
| `[3]` | change forms offset | the ~1.4 MB region, `[7]` records (e.g. 4134) |
| `[4]` | global data table 3 offset | one type-1000 record |
| `[5] [6] [7]` | counts | gdt1 count (12), gdt3 count (1), change-form count |

**Global data record** = `[type:u32][length:u32][data]`. In table 1: `0`=Misc Stats,
`1`=Player Location, `2`=TES, `3`=Global Variables, `6`=Weather, etc.

**Misc Stats** (type 0) = `u32 count, 0x7C, then count × (u32 value, 0x7C)` — the Pip-Boy
counters (quests completed, kills, locations discovered…). Positional: the game maps each index
to a name. These are decoded and **safely editable** in place.

### Change forms: player located, SPECIAL decoded

Change forms reference forms by **iref** — an index into the FormID array (`count` then `count` ×
`u32`). Resolving the player FormIDs (`0x07` base, `0x14` PlayerRef) to their irefs and scanning the
change-forms region for the 3-byte big-endian refID pinpoints the **player change forms** (works on
all 16 saves).

Inside the player base record, **SPECIAL** is 7 consecutive bytes immediately before the
length-prefixed player-name field (fenced by `0x7C`). Located by name-adjacency within the change
forms (skipping the header name), it is verified on every save — each sums to 40 (the chargen
budget) and is consistent per character — and is **safely editable** in place.

**Still next:** the general per-record header (changeFlags / type / variable length) to walk all
~4134 forms and decode inventory, perks, and skills. This needs controlled in-game diffs (change one
thing, save, byte-diff) — the tooling (`probe`, `hex`, `findplayer`) is in place to support it.

## Architecture — the "retention model"

`FalloutSave` keeps the **entire original byte array** and only decodes the well-understood
region, recording the offset of each editable field. The undecoded body is never
re-serialized from a model — it is preserved verbatim. Saving with no edits returns the exact
original bytes; edits are applied as in-place splices. This makes round-tripping provably safe
even though the body isn't fully understood.

## Projects

- `src/FnvSaveExplorer.Core` — UI-agnostic parser/writer (`FalloutSave`, `ByteReader`, `SaveScreenshot`).
- `src/FnvSaveExplorer.App` — WPF GUI: screenshot, character panel, plugins, File Location Table, safe edits.
- `src/FnvSaveExplorer.Cli` — `dump`, `flt`, `check`, `setlevel`.
- `tests/FnvSaveExplorer.Tests` — synthetic-save unit tests + a theory that round-trips every real `.fos` it finds.

## Usage

```pwsh
dotnet build FnvSaveExplorer.slnx

# GUI
dotnet run --project src/FnvSaveExplorer.App

# CLI (run with no args to list all commands)
dotnet run --project src/FnvSaveExplorer.Cli -- dump    "<save.fos>"   # metadata + plugins
dotnet run --project src/FnvSaveExplorer.Cli -- special "<save.fos>"   # player SPECIAL
dotnet run --project src/FnvSaveExplorer.Cli -- stats   "<save.fos>"   # Misc Stats counters
dotnet run --project src/FnvSaveExplorer.Cli -- globals "<save.fos>"   # global data records
dotnet run --project src/FnvSaveExplorer.Cli -- check   "<save.fos>"   # round-trip safety
dotnet run --project src/FnvSaveExplorer.Cli -- diff    "<a.fos>" "<b.fos>"   # byte-diff two saves
```

### Cracking the rest via controlled diffs

`diff` is the workhorse for the remaining reverse-engineering. On a **same-size** pair it pinpoints
exactly what changed — e.g. editing Strength 5→6 shows a single differing byte at the SPECIAL
offset. The recipe to locate any value (a skill, caps, a perk):

1. In-game, save (A). Change *one thing* (spend a skill point, read a skill book). Save again (B).
2. `fnvsave diff A B` → the differing run(s) point straight at the bytes for that value.
3. Add a typed accessor + same-length editor for it (as done for SPECIAL / Misc Stats).

## Next: decoding the body (R&D)

1. Label the File Location Table fields (offsets vs counts) by cross-comparing several saves.
2. Walk the globals tables; the FO3/FNV "stats" counters and SPECIAL likely live in a global-data
   record (cf. Skyrim's "Misc Stats" global), reachable via the table.
3. Decode change forms (type, flags, FormID/iref, `dataSize`) — enumerable via per-record size
   even before contents are understood. Use a diff lab (one controlled in-game change → byte-diff)
   and cross-reference FormIDs in FNVEdit.

### References

- `Nexus-Mods/node-gamebryo-savegames` — working C++ header parser (FO3/FNV/FO4/Skyrim).
- Vault-Tec Labs *FOS file format* (falloutmods wiki) — header + stats byte tables.
- UESP *Oblivion / Skyrim Save File Format* — the change-record / FormID-array model FNV mirrors.
- FNVEdit + the GECK — resolve FormIDs while reverse-engineering.
