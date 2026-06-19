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
| **Player skills** (actor-value block in the PlayerRef change form) | ✅ decoded **and safely editable** — format + 13-skill index map verified; storage is sparse (modified-only) |
| **Change-form record header / walker** | ✅ decoded — walks to exactly `ChangeFormCount` records, lands on `GlobalData3Offset` (both characters, fresh→4 h) |
| **Player inventory** (item stacks in the player's inventory change form) | ✅ decoded **and safely editable** — `[iref][7C][u32 count][7C]` entries; a controlled drop-1 diff confirms the count (9→8) |
| **FormID → display name** (item names from the game's ESM/ESP masters) | ✅ custom TES4 reader resolves inventory/FormID names (Stimpak, Vault 21 Jumpsuit…); all 10 base+DLC plugins parse, DLC renumbering + compressed records handled |
| **Change forms** (per-stack extra data, perks, per-actor state) | 🔬 walker + inventory + skills decoded; remaining per-record internals **next** |

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

**Skills** live in the **PlayerRef (`0x14`) change form** as an actor-value modification list —
`[count*4][7C]` then entries of `[avIndex:u8][7C][value:float32][7C]` (7 bytes each). The 13 skill
indices (`0x20` Barter … `0x2D` Unarmed, skipping the FO3-only `0x21` "Big Guns") were verified by
setting all 13 to distinct values via console `setav` and byte-diffing. Storage is **sparse**: the
engine computes skills from base + SPECIAL + perks and only stores *deviations*, so the tool reads
and **safely edits** exactly what's stored (it can't show all 13 on a save that never modified them).
The block is located by anchoring on the length prefix and choosing the validating block with the
most recognised skills.

**Change-form walker.** Every change form is `[refID:3 BE][changeFlags:u32][type:u8][version:u8]
[length][data]`, where the top two bits of `type` size the length field (u8/u16/u32). Walking from the
change-forms offset reproduces **exactly** `ChangeFormCount` records and lands precisely on the next
section — verified on every save, both characters, fresh through 4 hours. This is the foundation for
decoding any per-record state.

**Player inventory.** Items are **not** in the PlayerRef ACHR record (that's actor state) but in a
dedicated reference change form (iref = PlayerRef + 1). Its entries are
`[itemIref:3 BE][7C][count:u32 LE][7C]` (plus per-stack extra data — condition/equip — not yet
decoded). A controlled drop-1 diff confirmed the format: dropping one of a stacked item decremented
exactly one `count` from 9 → 8, so counts are **safely editable** in place. Items now show their
**display name** (see below).

**FormID → display name.** A small custom **TES4 plugin reader** (`Core/TesPlugin.cs`) reads the
game's ESM/ESP masters and builds a `FormID → FULL/EDID` index, so inventory (and any FormID the tool
surfaces) shows a real name — "Stimpak", "Vault 21 Jumpsuit", "Weapon Repair Kit" — instead of raw hex.
FNV stores the `FULL` name **inline** in each record (no `.STRINGS` localization), so names are read
directly. Each plugin's forms are remapped into the save's FormID space via its master list, which
handles **DLC renumbering**; **compressed records** (zlib) and `GRUP`-skipping over the 245 MB
`FalloutNV.esm` are handled too (all 10 base+DLC plugins of a real save parse in ~2 s). The game's
`Data` folder is auto-detected (with an override); when it isn't found, FormIDs fall back to hex.
Forms that resolve to placed references (ACHR/REFR — not item templates) or runtime-created `0xFF…`
FormIDs show `?` / `(created)`.

**Still next:** per-stack extra data, perks, and other per-record internals — reachable now that the
walker enumerates records. This needs controlled in-game diffs (change one thing, save, byte-diff) —
the tooling (`walk`, `find`, `diff … cf`, `idiff`, `probe`, `hex`, `playerdump`) is in place to support it.

## Architecture — the "retention model"

`FalloutSave` keeps the **entire original byte array** and only decodes the well-understood
region, recording the offset of each editable field. The undecoded body is never
re-serialized from a model — it is preserved verbatim. Saving with no edits returns the exact
original bytes; edits are applied as in-place splices. This makes round-tripping provably safe
even though the body isn't fully understood.

## Projects

- `src/FnvSaveExplorer.Core` — UI-agnostic parser/writer (`FalloutSave`, `ByteReader`, `SaveScreenshot`)
  plus the FormID-name resolver (`TesPlugin`, `PluginDatabase`, `GameDataLocator`).
- `src/FnvSaveExplorer.App` — WPF GUI: screenshot, character panel, plugins, File Location Table, SPECIAL/skills/safe edits, named inventory.
- `src/FnvSaveExplorer.Cli` — `dump`, `flt`, `check`, `setlevel`, `special`, `skills`, `setskill`, `inventory`, `setcount`, `names`, `walk`, `find`, `diff`/`idiff`, `playerdump`, … (run with no args to list all).
- `tests/FnvSaveExplorer.Tests` — synthetic-save unit tests + a theory that round-trips every real `.fos` it finds.

## Usage

```pwsh
dotnet build FnvSaveExplorer.slnx

# GUI
dotnet run --project src/FnvSaveExplorer.App

# CLI (run with no args to list all commands)
dotnet run --project src/FnvSaveExplorer.Cli -- dump    "<save.fos>"   # metadata + plugins
dotnet run --project src/FnvSaveExplorer.Cli -- special "<save.fos>"   # player SPECIAL
dotnet run --project src/FnvSaveExplorer.Cli -- skills  "<save.fos>"   # stored skill modifications
dotnet run --project src/FnvSaveExplorer.Cli -- inventory "<save.fos>" # player inventory (name + FormID x count)
dotnet run --project src/FnvSaveExplorer.Cli -- names   "<save.fos>"   # FormID->name resolver status (masters)
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
2. `fnvsave diff A B cf` annotates each differing run with the change form that contains it; if the
   change inserted/removed a record (e.g. dropping an item), `fnvsave idiff A B` aligns records by
   FormID across the insertion and reports the one record whose **data** changed.
3. Add a typed accessor + same-length editor for it (as done for SPECIAL / Misc Stats / inventory).

## Next: decoding the body (R&D)

The header, File Location Table, global-data tables, Misc Stats, the FormID array, the change-form
record header/walker, SPECIAL, skills, inventory item stacks, and FormID→display-name resolution are
decoded. Remaining:

1. **Per-stack inventory extra data** — condition / equip state / weapon mods attached to each stack.
2. **Caps / karma / XP** — caps may simply be an inventory stack; karma/XP via controlled diffs.
3. **Other per-record state** (perks, quest/script data) — now enumerable record-by-record via the
   walker; crack each with a controlled in-game change → `idiff`, cross-referencing FormIDs in FNVEdit.

### References

- `Nexus-Mods/node-gamebryo-savegames` — working C++ header parser (FO3/FNV/FO4/Skyrim).
- Vault-Tec Labs *FOS file format* (falloutmods wiki) — header + stats byte tables.
- UESP *Oblivion / Skyrim Save File Format* — the change-record / FormID-array model FNV mirrors.
- FNVEdit + the GECK — resolve FormIDs while reverse-engineering.
