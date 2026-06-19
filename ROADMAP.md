# FNV Save Explorer — Roadmap & Status

A cold-start working document: where the project is, exactly what's been reverse-engineered, and
how to resume. For the user-facing overview see [README.md](README.md); for build/agent notes see
[CLAUDE.md](CLAUDE.md).

---

## 1. Goal & scope

Build a from-scratch tool to **analyze** and **edit** Fallout: New Vegas `.fos` save files.
Chosen direction: C# / .NET 10, a reusable **Core library** plus a **WPF GUI** and a **CLI**, and
to **reverse-engineer the save body** (globals, change forms) — not just the documented header.

The single principle that makes editing safe: the **retention model**. `FalloutSave` keeps the
entire original byte array and only decodes regions we understand, recording the offset of each
editable field. Saving with no edits reproduces the file byte-for-byte; edits are **same-length
splices** so nothing shifts. This is mandatory because the body's File Location Table stores
*absolute* offsets — a length change would invalidate them. **Never break round-trip identity.**

---

## 2. Quick start

```pwsh
dotnet build FnvSaveExplorer.slnx            # NOTE: solution is .slnx (XML), not .sln
dotnet test  tests/FnvSaveExplorer.Tests/FnvSaveExplorer.Tests.csproj
dotnet run   --project src/FnvSaveExplorer.App                 # WPF GUI
dotnet run   --project src/FnvSaveExplorer.Cli -- <command> "<save.fos>"
```

Real saves for testing live in `C:\Users\<user>\Documents\My Games\FalloutNV\Saves\` (16 present
during development: two characters "Nathan" and "Mace Windu" at various progression, plus autosave/
quicksave). The test theory auto-discovers them (and a local `samples/`) and skips if none exist.
**Never write to the originals** — all edit demos write to new files.

CLI commands: `dump`, `check`, `flt`, `probe`, `hex`, `globals`, `stats`, `setstat`, `formids`,
`findplayer`, `playerdump`, `special`, `setspecial`, `skills`, `setskill`, `inventory`, `setcount`,
`setlevel`, `diff`, plus R&D helpers `walk`, `idiff`, `find`, `irefscan`. Run with no args to list them.

---

## 3. Architecture & key files

- **`src/FnvSaveExplorer.Core`** (`net10.0`, UI-agnostic)
  - `FalloutSave.cs` — parser, retention writer, all decoders + same-length editors, change-form walker.
  - `ByteReader.cs` — little-endian cursor; throws `SaveFormatException` with the failing offset.
  - `FileLocationTable.cs`, `GlobalData.cs`, `MiscStats.cs`, `PlayerSpecial.cs`, `PlayerSkills.cs`, `PlayerInventory.cs`, `SaveScreenshot.cs`.
- **`src/FnvSaveExplorer.App`** (`net10.0-windows`, WPF MVVM) — `MainViewModel.cs`, `MainWindow.xaml`
  (+ code-behind for file dialogs). Tabs: Plugins, File Location Table, Edit (name/level/save#/SPECIAL),
  Misc Stats, Body. Left panel: screenshot + character summary.
- **`src/FnvSaveExplorer.Cli`** — `Program.cs` (top-level statements; all commands + diagnostics).
- **`tests/FnvSaveExplorer.Tests`** — xUnit; 160 tests. Synthetic-save unit tests + theories over
  every real `.fos` found (round-trip identity, globals, Misc Stats, SPECIAL + skills + inventory locate + edit).

---

## 4. The `.fos` format — validated spec

Little-endian; signature `FO3SAVEGAME` (shared with FO3; NV adds a 64-byte language block). Header
region uncompressed. Strings are `[u16 len][0x7C][bytes][0x7C]`; `0x7C` ("|") delimits fields
throughout the file (header, globals, change forms).

### 4a. Header (offsets from a real save)
```
0x00  char[11]  "FO3SAVEGAME"
0x0B  u32       saveHeaderSize        -> screenshot starts at 0x0F + saveHeaderSize   (KEY)
0x0F  u32       version (0x30)
0x13  0x7C
0x14  64 bytes  language (null-padded "ENGLISH")   [NV only]
      u32 width, u32 height, u32 saveNumber        (each followed by 0x7C)
      str name, str title, u32 level, str location, str playtime
      screenshot = width*height*3 bytes raw RGB (e.g. 512x288)
      u8 trailer (~0x1B; docs say 0x15 but it varies)
      u32 pluginStructSize, u8 pluginCount, 0x7C, then pluginCount x prefixed strings
      -> body begins (BodyOffset)
```

### 4b. File Location Table (body start) — verified across all 16 saves
Five absolute offsets then three counts (NV has one fewer global-data table than Skyrim):
```
[0] FormIdArrayCountOffset   [1] UnknownTable3Offset (footer near EOF)
[2] GlobalData1Offset (12 records)   [3] ChangeFormsOffset   [4] GlobalData3Offset (1 type-1000 rec)
[5] GlobalData1Count (=12)   [6] GlobalData3Count (=1)   [7] ChangeFormCount (e.g. 4134)
```

### 4c. Global data — `[type:u32][length:u32][data]`
Table 1 holds 12 records, types 0–11: `0`=Misc Stats, `1`=Player Location, `2`=TES, `3`=Global
Variables (large), `4`=Created Objects, `6`=Weather, … (7–11 unlabeled).

**Misc Stats (type 0):** `u32 count, 0x7C, then count x (u32 value, 0x7C)` — Pip-Boy counters
(quests/kills/locations…). Positional (no names stored). Decoded + editable.

### 4d. FormID array & change forms
- FormID array: `u32 count` then `count` x `u32` (full FormIDs; high byte = mod index).
- Change forms reference forms by **iref** = index into the FormID array, encoded as a **3-byte
  big-endian refID**. Player: FormID `0x07` (base TESNPC_) and `0x14` (PlayerRef ACHR); find their
  irefs in the array, scan change forms for the 3-byte refID to locate the records.
- **Player SPECIAL:** 7 consecutive bytes immediately before the length-prefixed player-name field
  inside the player base record (fenced by `0x7C`). Located by name-adjacency within the change-forms
  region. Verified: every save sums to 40 (chargen budget), consistent per character. Editable.

### 4e. Player skills — actor-value modification block (PlayerRef / ACHR change form)
Skills are **not** stored inline like SPECIAL and **not** in the base record (that record is FaceGen
data, byte-stable across same-character saves). They live in the volatile **PlayerRef (FormID `0x14`)
change form** as an **actor-value modification list**:
```
[count*4 : u8][7C]   then count × ( [avIndex : u8][7C][value : float32 LE][7C] )   # 7 bytes/entry
```
AV-index → skill (verified by setting all 13 to distinct values via console `setav` and diffing):
`0x20`=Barter, `0x22`=Energy Weapons, `0x23`=Explosives, `0x24`=Lockpick, `0x25`=Medicine,
`0x26`=Melee Weapons, `0x27`=Repair, `0x28`=Science, `0x29`=Guns, `0x2A`=Sneak, `0x2B`=Speech,
`0x2C`=Survival, `0x2D`=Unarmed (`0x21` = FO3 "Big Guns", unused in NV — the index run skips it).

**Storage is sparse.** The engine computes a skill from base + SPECIAL + perks + tag skills and only
writes an entry when it *deviates* — a fresh character stores none, a typical played save ~3. So the
tool reads/edits exactly what's stored; it can't enumerate all 13 on an unmodified save, and adding a
missing entry would be length-changing (unsupported). Editing a stored value is a safe same-length
float splice. **Locator:** the lone `0x7C` also occurs inside float bytes, so single-entry blocks are
indistinguishable from noise; we anchor on the length prefix and pick the validating block with the
most recognised skills (≥2). Verified across all 16 saves.

### 4f. Change-form record header — the walker (general; was next-step #4)
Every change form is a fixed header then a variable payload:
```
[refID : 3 bytes BE]   index (iref) into the FormID array
[changeFlags : u32 LE]
[type : u8]            low 6 bits = form type; high 2 bits select the length width (0→u8, 1→u16, 2/3→u32)
[version : u8]         0x1B on NV
[length : u8|u16|u32]  payload size, width per the type byte's high bits
[data : length bytes]
```
**Verified decisively:** walking from `ChangeFormsOffset` yields exactly `ChangeFormCount` records and
lands *precisely* on `GlobalData3Offset` on every save tested (both characters, fresh→4 h). `Core`
exposes `EnumerateChangeForms()` (each record's iref/FormID/flags/type/data span) and `PlayerRefChangeForm`;
the CLI `walk` validates the count/landing and histograms form types. This is the foundation the
inventory decode (and any future per-record browser) builds on.

### 4g. Player inventory — item list in the player's inventory change form
The player's carried items are **not** in the PlayerRef (`0x14`) ACHR record (that holds actor state —
6 bytes fresh, 293 bytes mid-game, never growing with items). They live in a **dedicated reference
change form** whose **iref = (PlayerRef iref) + 1** (type `0x41`). After the record's 3D/position
preamble and a zeroed array, the items are a run of stacks:
```
[itemIref : 3 bytes BE][7C][count : u32 LE][7C]  ( extra-data: 7C-delimited condition/equip fields, not yet decoded )
```
**Verified** by a controlled drop-1 diff (Save 26→27): dropping one of a stacked item decremented
exactly one stack's `count` from 9→8 as a little-endian u32 — so editing a count is a **safe
same-length splice**. Items are referenced by iref; the tool surfaces the resolved FormID + mod index,
not a display name (names need the GECK/ESM masters). **Decoder:** parse the record's longest contiguous
run of entries, requiring iref ≠ 0 that resolves, both `0x7C` delimiters, and a sane count whose upper
bytes aren't the `0x7C` delimiter (which rejects misaligned reads of a stack's extra data). Validated
across all real saves (sane stack/item totals; the drop-1 stack reads 9 then 8).

---

## 5. Completed (with verification)

| Area | Status |
|---|---|
| Header / screenshot / plugins parse | ✅ validated on 16 saves |
| Byte-identical round-trip (open→save) | ✅ all 16 incl. 4 MB autosave/quicksave |
| Same-length edits: level, save#, name | ✅ proven (size unchanged, re-parses) |
| File Location Table decode | ✅ verified across 16 saves |
| Global data tables (12 records) | ✅ enumerated |
| Misc Stats decode + edit | ✅ (e.g. stat 1→999 = 2-byte diff) |
| FormID array + iref resolution | ✅ locates player change forms in all 16 |
| Player SPECIAL decode + edit | ✅ all 16 sum to 40; edit round-trips |
| Player skills decode + edit (ACHR actor-value block, §4e) | ✅ format + index map verified; same-length float edit round-trips; sparse (modified-only) |
| Change-form record header / walker (§4f) | ✅ exact: walks to `ChangeFormCount` records, lands on `GlobalData3Offset` (both characters, fresh→4 h) |
| Player inventory decode + edit (§4g) | ✅ located via PlayerRef iref+1; `[iref][7C][u32 count][7C]` entries; drop-1 diff confirms count 9→8; same-length count edit round-trips |
| WPF GUI (metadata, screenshot, plugins, stats, SPECIAL + skills + inventory edit) | ✅ launches + builds |
| `diff` tool (pinpoints same-size changes) | ✅ Strength 5→6 = 1 byte; `cf` mode names the containing change form; `idiff` aligns records across an insertion |
| Tests | ✅ 160 xUnit, all green |

**Editable today:** level, save number, name (same-length), Misc Stats, full SPECIAL, stored skill
modifications (§4e), inventory stack counts (§4g) — all safe same-length splices.

---

## 6. Next steps (in priority order)

1. ~~**Skills**~~ — ✅ **DONE** (§4e). Located via the controlled-diff method: skills are floats in
   the PlayerRef (`0x14`) actor-value modification block, not an inline structure. Decoder + index map
   + same-length float editor shipped (Core `PlayerSkills`/`TrySetSkill`, CLI `skills`/`setskill`, GUI
   Skills tab). Remaining nuance: storage is sparse (modified-only) and the absolute-vs-modifier
   semantics of naturally-occurring small entries (vs console `setav`, which writes absolute) is not
   yet pinned — a follow-up controlled diff (read a single skill book, +3) could confirm it.
2. ~~**Inventory**~~ — ✅ **DONE** (§4g). Cracked via a controlled drop-1 diff: items live in a dedicated
   reference change form (iref = PlayerRef+1), entries are `[iref][7C][u32 count][7C]`, count edits are
   same-length. Decoder + `TrySetItemCount` + CLI `inventory`/`setcount` + GUI Inventory tab shipped.
   Remaining nuance: per-stack **extra data** (condition / equip / mods) isn't decoded yet, and editing
   targets the first stack of a given FormID (duplicate-FormID stacks are ambiguous by FormID alone).
3. **Item / form name resolution (FormID → display name)** — inventory (and every FormID we surface)
   currently shows raw hex; the human names live in the game's **ESM/ESP master files**
   (`<game>/Data/*.esm`), confirmed present locally (`C:\Games\Steam\steamapps\common\Fallout New
   Vegas\Data`). FNV stores the `FULL` (display name) **inline** in each record — no Skyrim-style
   `.STRINGS` files — so names are directly readable (verified: `FalloutNV.esm` contains "Stimpak",
   "Vault 21", … as plain text). **Plan:** a `Core` plugin database that parses the TES4 plugin format
   (records `[type:4][dataSize:u32][flags:u32][formID:u32][version…]` then subrecords
   `[type:4][size:u16][data]`, grouped in `GRUP`s) and builds a `FormID → FULL/EDID` index for item
   record types (`WEAP`/`ARMO`/`ALCH`/`AMMO`/`MISC`/`BOOK`/`NOTE`/`KEYM`/`IMOD`…). Resolve a stack by
   mapping its FormID's high byte through the save's plugin list (already parsed as `Plugins`) to the
   owning ESM, then look up the form. Wire into CLI `inventory` + the GUI Inventory tab (and retro-fit
   onto any other FormID display). **Wrinkles:** (a) **DLC renumbering** — inside a DLC ESM its own forms
   use high byte = index in *its own* master list, so swap the save's load-order byte for the plugin-local
   master index (base-game `0x00` maps directly); (b) **compressed records** (flag `0x00040000`) are
   zlib-deflated; (c) cache the index once — `FalloutNV.esm` is 245 MB, but `GRUP` sizes let us skip
   non-item groups; (d) `0xFF…` FormIDs are runtime-created → no name (`(created)`). No off-the-shelf C#
   lib covers FNV (Mutagen is Skyrim/FO4/Starfield), so it's a small custom TES4 reader.
4. **Caps / karma / XP** — single values; controlled-diff to locate, then same-length edit. (Caps may
   simply be an inventory stack — check the inventory list first.)
5. ~~**General change-form record header**~~ — ✅ **DONE** (§4f). Walker (`EnumerateChangeForms`) reproduces
   all records exactly; CLI `walk` validates. Enables a future full change-form browser.
6. **Length-changing edits** (arbitrary rename, add/remove plugins, add/remove items) — requires rewriting every
   absolute offset in the File Location Table (and any internal absolute offsets). Deferred.
7. **Quick win (no new saves):** label the 43 Misc Stat indices by name (diff an early vs late save
   of the same character) so the GUI reads "Quests Completed: 4" instead of "[0]: 4".
8. **GUI/UX polish:** screenshot export (PNG), a raw hex viewer tab, backup management.

---

## 7. The controlled-diff methodology (how to crack §6.4 and the like)

`diff` is surgical on **same-size** save pairs (a value change keeps the file the same size):
1. In-game: **save A** → change exactly one thing (spend 1 skill point / read one skill book / drop
   one item) → **save B**.
2. `fnvsave diff A B` → the differing run(s) point at the bytes for that value (section-labeled).
3. Confirm by repeating with a different delta; then add a typed accessor + `TrySet…` same-length
   editor in `FalloutSave.cs` (mirror `TrySetSpecial` / `TrySetMiscStat`), expose in CLI + GUI, add a
   real-save test.

Diagnostics already available for RE: `probe` (FLT + what offsets point to), `hex <off> <len>`,
`findplayer`, `playerdump` (player change-form anchors + hex; `diff` also reports `playerBase±0x..` /
`playerRef±0x..` / `special±0x..` for change-form runs), `formids`, `globals`, `special`, `skills`,
`inventory`, `walk` (walk every change form + form-type histogram), `find <hexbytes>` (locate a byte
pattern + name the containing record), `irefscan <off> <len>` (resolve iref+count sites), and two `diff`
modes: `diff a b cf` annotates each differing run with the change form that contains it, and
`idiff a b` aligns records by FormID across an insertion (drop/pickup) to surface the exact data change.

---

## 8. Reference sources
- `Nexus-Mods/node-gamebryo-savegames` — C++ parser (FO3/FNV/FO4/Skyrim). **Header-only**: stops at the
  plugin list, does not decode the body — the change-form/inventory format here was reverse-engineered locally.
- Vault-Tec Labs "FOS file format" (falloutmods wiki) — header + stats tables.
- UESP "Oblivion / Skyrim Save File Format" — change-record / FormID-array model NV mirrors.
- **Game ESM/ESP master files** (`<game>/Data/*.esm`) — the source for FormID → display name (§6.3). On
  this machine: `C:\Games\Steam\steamapps\common\Fallout New Vegas\Data` (`FalloutNV.esm` + DLC esms).
  Standard Bethesda TES4 plugin format; FNV stores `FULL` names **inline** (no `.STRINGS` localization
  files), so names are readable directly. UESP "Mod File Format" (TES4/FO3) documents the record/GRUP/subrecord layout.
- FNVEdit + GECK — resolve FormIDs/irefs while reverse-engineering.
- Note: those wikis block automated fetchers on `/wiki/` URLs; Fandom's `api.php?action=parse` works.

---

## 9. Known limitations / risks
- Change-form **internals**: skills (§4e) and inventory item stacks (§4g) are decoded; per-stack extra
  data (condition/equip/mods), perks, and most other per-record state are not yet decoded — needs more
  controlled diffs. The walker (§4f) makes these reachable record-by-record.
- Inventory editing targets the **first** stack of a given FormID; duplicate-FormID stacks (same item,
  different extra data) can't be disambiguated by FormID alone yet.
- SPECIAL locator relies on the player-name field appearing in the player base record (held on all 16
  saves); a save lacking it would return null (handled gracefully).
- Only **same-length** edits are supported by design (see §1). Length-changing edits are unsafe until
  full offset-fixup is implemented.
- `findplayer`'s refID scan can report false positives in data; the player records are confirmed via
  the SPECIAL/name anchor, which is the reliable locator.
