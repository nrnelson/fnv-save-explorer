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
| **Misc Stats** counters (quests, kills, locations…) | ✅ decoded, **named** (43 indices), **and safely editable** (stat 1→999 = 2-byte diff) |
| **FormID array** + iref resolution | ✅ decoded; locates the **player change forms** in all 16 saves |
| **Player SPECIAL** (S P E C I A L) | ✅ decoded **and safely editable** — verified on all 16 saves (each sums to 40) |
| **Player skills** (actor-value block in the PlayerRef change form) | ✅ decoded **and safely editable** — format + 13-skill index map verified; storage is sparse (modified-only) |
| **Change-form record header / walker** | ✅ decoded — walks to exactly `ChangeFormCount` records, lands on `GlobalData3Offset` (both characters, fresh→4 h) |
| **Player inventory** (item stacks in the player's inventory change form) | ✅ **deterministic** decode **and safely editable** — `[ref][7C][u32 count][7C]` + per-stack extra data (`ref` = index + 1); confirmed by controlled diffs (Antivenom 1→2→1) — every stack resolves with correct counts, no scan window. List start is a **pure structural walk** (no scan): MOVE block + **fixed 1160-byte havok array** + **sized ExtraDataList** → the **`vsval` stack count** → first item; byte-identical on all 30 saves, deterministic path taken on all 30 |
| **Item condition / equipped / mods** (per-stack extra data) | ✅ cracked by a controlled 3-save diff — condition (`0x25` float, **editable**), equipped (`0x16`), weapon-mod ref (`0x21`); surfaced in CLI + GUI |
| **FormID → display name** (item names from the game's ESM/ESP masters) | ✅ custom TES4 reader resolves inventory/FormID names (Stimpak, Vault 21 Jumpsuit…); all 10 base+DLC plugins parse, DLC renumbering + compressed records handled; works on **Mod Organizer 2 / Viva New Vegas** saves (auto-detects the MO2 `mods\` folder) |
| **Inventory list start — modded too** | ✅ **deterministic on all 607 real saves** (vanilla 30, base VNV 98, VNV Extended 479): the typed-entry ExtraDataList walk (variable order + modded `0x1D`/`0x75`) + the ExtraDataList-header anchor for havok-physics records → the `vsval` count |
| **Caps** (the `0x0000000F` "Bottle Cap" stack) | ✅ decoded **and safely editable** |
| **Karma + XP** (float actor-values in the player reference) | ✅ decoded **and safely editable** — cracked via a float-aware diff (slot 100 = karma, 101 = XP), confirmed on a 2nd character |
| **Pip-Boy notes** (Data → Notes, read **and** unread) | ✅ fully decoded — read markers (corpus-proven over 45,783) + the acquired-notes ref-list in the player inventory record; CLI + GUI show the full list with read/unread status + media type (Text/Voice/Sound/Image). Read-only |
| **Change forms** (perks, other per-actor state) | 🔬 walker + inventory + skills + per-stack extra data + notes decoded; remaining per-record internals (perks, quest/script state) **next** |

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
to a name. The save stores only values; the 43 indices are labelled from the fixed FO3/FNV engine
misc-stat array (`MiscStatNames`, verified against the corpus — index 35 "Total Things Killed" =
"People Killed" + "Creatures Killed" on every save), so `stats` and the GUI read "Quests Completed: 4"
rather than "[0]: 4". These are decoded and **safely editable** in place.

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
`[ref:3 BE][7C][count:u32 LE][7C]` followed by a per-stack **extra-data block**, where `ref` is the
**FormID-array index + 1** (the item is `FormIdArray[ref - 1]`) and `count` is the entry's own stack
count. A controlled in-game diff (add then consume one Antivenom) pinned the count moving **1 → 2 → 1**,
confirming both the `+ 1` encoding and that counts are **safely editable** in place. The decoder is
**deterministic** — the extra-data block's exact length is decoded, so the walk advances stack-to-stack
without a scan window. The extra data was cracked by a controlled 3-save diff (equip a 9mm pistol, then
repair it): **condition/health** (`0x25`, a float — **editable**, e.g. repair-to-full), the **equipped**
flag (`0x16`), and a weapon-mod ref (`0x21`). Four further per-stack property types (`0x6E`/`0x1C`/`0x24`/`0x30`)
were later **sized by corpus alignment** across all 607 saves, so the walk steps over them deterministically too —
which also **corrected a handful of modded-save item counts** (phantom over-read stacks the old scan picked up now
drop, several inventories now matching the engine's own count exactly). Every stack resolves to its **display name** (see below).
The list **start** is now a **pure structural walk** (no scan): the walk skips the 27-byte MOVE block
(`CHANGE_REFR_MOVE`), the **fixed 1160-byte havok/float array** after it (232 `[u32][7C]` slots — an empirical
invariant across all 30 saves, independent of bit22), **and the reference's ExtraDataList** (a fixed-shape typed
list: header + a `0x5E` ref-list + a fixed 24-byte `0x18` block + a `0x74` entry + an optional `0x60`), then reads
the inventory's **`vsval` stack count** to land on the first item. The count self-validates the start (Save 31 →
`0x90` → 36 stacks; quicksave → 96). This replaced the old whole-record most-distinct ranking **and** the
per-ExtraDataList forward scan — verified byte-identical across all 30 real saves, deterministic path taken on all
30, with tests pinning the start at `MOVE+1+1160` and the vsval count ≤ decoded count.

**FormID → display name.** A small custom **TES4 plugin reader** (`Core/TesPlugin.cs`) reads the
game's ESM/ESP masters and builds a `FormID → FULL/EDID` index, so inventory (and any FormID the tool
surfaces) shows a real name — "Stimpak", "Vault 21 Jumpsuit", "Weapon Repair Kit" — instead of raw hex.
FNV stores the `FULL` name **inline** in each record (no `.STRINGS` localization), so names are read
directly. Each plugin's forms are remapped into the save's FormID space via its master list, which
handles **DLC renumbering**; **compressed records** (zlib) and `GRUP`-skipping over the 245 MB
`FalloutNV.esm` are handled too (all 10 base+DLC plugins of a real save parse in ~2 s). The game's
`Data` folder is auto-detected (with an override); when it isn't found, FormIDs fall back to hex.
A runtime-created `0xFF…` FormID shows `(created)`; a form not found in the masters shows `?`.

The inventory list *start* is now **deterministic on all 607 real saves** (vanilla 30, base VNV 98, VNV Extended
479): the typed-entry ExtraDataList walk handles the variable order and modded entry types (`0x1D`/`0x75`), and
records whose pre-list region is a variable-length Havok physics blob are located by an ExtraDataList-header anchor
(the prior forward scan is retained only as an unused safety net). **Still next:** perks and other per-record
internals. Decoding the rest needs controlled in-game diffs (change one thing, save, byte-diff) — the tooling
(`walk`, `refdump`, `find`, `diff … cf`, `idiff` / `idiff … clean`, `fdiff`, `probe`, `hex`, `playerdump`,
`notescan`, `resolve`) is in place to support it.

## Architecture — the "retention model"

`FalloutSave` keeps the **entire original byte array** and only decodes the well-understood
region, recording the offset of each editable field. The undecoded body is never
re-serialized from a model — it is preserved verbatim. Saving with no edits returns the exact
original bytes; edits are applied as in-place splices. This makes round-tripping provably safe
even though the body isn't fully understood.

## Projects

- `src/FnvSaveExplorer.Core` — UI-agnostic parser/writer (`FalloutSave`, `ByteReader`, `SaveScreenshot`)
  plus the FormID-name resolver (`TesPlugin`, `PluginDatabase`, `GameDataLocator`).
- `src/FnvSaveExplorer.App` — WPF GUI: screenshot, character panel, plugins, File Location Table, SPECIAL/skills/inventory/caps/karma/XP safe edits, named inventory, and the full Notes tab (read/unread + media type).
- `src/FnvSaveExplorer.Cli` — `dump`, `flt`, `check`, `setlevel`, `special`, `skills`, `setskill`, `inventory`, `setcount`, `setcondition`, `names`, `notes`, `caps`/`setcaps`, `karma`/`xp`/`setkarma`/`setxp`, `walk`, `refdump`, `find`, `diff`/`idiff` (+ `idiff … clean`)/`fdiff`, `notescan`, `resolve`, `playerdump`, … (run with no args to list all).
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
dotnet run --project src/FnvSaveExplorer.Cli -- notes   "<save.fos>"   # Pip-Boy notes — read AND unread + type
dotnet run --project src/FnvSaveExplorer.Cli -- karma   "<save.fos>"   # player karma (and `xp` for XP)
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
record header/walker, SPECIAL, skills, inventory item stacks **and their per-stack extra data**
(condition / equipped / mods), FormID→display-name resolution, **caps**, **karma + XP**, and the full
**Pip-Boy notes** list (read and unread, with media type) are decoded. The inventory list start is
**deterministic on all 607 real saves** (vanilla + base VNV + VNV Extended). Remaining:

1. **Other per-record state** (perks, quest/script data) — now enumerable record-by-record via the
   walker; crack each with a controlled in-game change → `idiff` / `idiff … clean` (the latter hides
   the recurring game-time/havok churn), cross-referencing FormIDs in FNVEdit.
2. **Length-changing edits** (arbitrary rename, add/remove items/plugins) — needs full File Location
   Table offset rewriting; deferred by design (see the retention model).

### References

- `Nexus-Mods/node-gamebryo-savegames` — working C++ header parser (FO3/FNV/FO4/Skyrim).
- Vault-Tec Labs *FOS file format* (falloutmods wiki) — header + stats byte tables.
- UESP *Oblivion / Skyrim Save File Format* — the change-record / FormID-array model FNV mirrors.
- FNVEdit + the GECK — resolve FormIDs while reverse-engineering.

### License

[MIT](LICENSE) © Nathan Nelson.
