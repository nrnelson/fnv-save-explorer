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
`setcondition`, `names`, `notes`, `setlevel`, `caps`, `setcaps`, `karma`, `xp`, `setkarma`, `setxp`, `diff`, plus
R&D helpers `walk`, `refdump`, `edlscan`, `invsig`, `notescan`, `resolve`, `idiff`, `fdiff`, `find`, `irefscan`.
Run with no args to list them. (`edlscan <dir>` aggregates the modded ExtraDataList grammar + a deterministic-path
tally across a save folder; `invsig <dir>` prints a per-save decoded-inventory signature for byte-identical-decode checks — §4i;
`notescan <dir>` aggregates the read-note markers — flag-value + `0x1F`→NOTE + inventory-reference tallies — §4k.1.)

---

## 3. Architecture & key files

- **`src/FnvSaveExplorer.Core`** (`net10.0`, UI-agnostic)
  - `FalloutSave.cs` — parser, retention writer, all decoders + same-length editors, change-form walker.
  - `ByteReader.cs` — little-endian cursor; throws `SaveFormatException` with the failing offset.
  - `FileLocationTable.cs`, `GlobalData.cs`, `MiscStats.cs`, `PlayerSpecial.cs`, `PlayerSkills.cs`, `PlayerInventory.cs`, `PlayerNotes.cs`, `SaveScreenshot.cs`.
  - `ReferenceChangeForm.cs` — reference (REFR/ACHR) change-form helpers: the `0x7C` field tokenizer, `changeFlags` describer, the per-stack extra-data catalog/decoder (`TryReadStackExtra`) behind the deterministic inventory walk, and the generalised typed-entry ExtraDataList walk (`WalkExtraDataList`/`ExtraEntryLength` — modded-grammar RE, inspection-only) (§4i).
  - `TesPlugin.cs`, `PluginDatabase.cs`, `GameDataLocator.cs` — FormID → display-name resolution from the game's ESM/ESP masters (§4h / §6.3).
- **`src/FnvSaveExplorer.App`** (`net10.0-windows`, WPF MVVM) — `MainViewModel.cs`, `MainWindow.xaml`
  (+ code-behind for file dialogs). Tabs: Plugins, File Location Table, Edit (name/level/save#/SPECIAL),
  Skills, Inventory, Notes, Misc Stats, Body. Left panel: screenshot + character summary.
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
Variables (large), `4`=Created Objects, `6`=Weather, … (5, 7–11 unlabeled). **Candidate labels (UESP Skyrim
spec, §8a — verify; FNV's set differs from Skyrim's):** `5`=Effects, `7`=Audio, `8`=SkyCells; 9–11 are
FNV-specific (Skyrim moves higher categories into a separate table).

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
[ref : 3 bytes BE][7C][count : u32 LE][7C]  ( extra-data: 7C-delimited condition/equip fields, not yet decoded )
```
**The reference is the FormID-array index + 1.** Each entry's 3-byte `ref` is the FormID-array index
**plus one** (index 0 reserved), so the item is `FormIdArray[ref - 1]` and `count` is the entry's **own**
stack count. **Confirmed by a controlled in-game diff:** save A → `player.additem 000E2C6F 1` → save B →
consume one → save C (Saves 28/29/30). `idiff` pinned a single u32 in the inventory change form going
**1 → 2 → 1**; that entry's `ref` resolved to *Antivenom* only through the `- 1` index. The `- 1` fix
alone makes the whole list correct: every stack now resolves (Stimpak ×10, Super Stimpak ×3, Doctor's
Bag ×3, Weapon Repair Kit ×4, Bleak Venom ×5, Antivenom ×1, Bottle Cap/caps ×18, Pip-Boy 3000, …), with
**no spurious entries** — the earlier `?` rows (ACHR/ACRE/REFR) were just `FormIdArray[ref]` landing on
the neighbouring form. (A prior reading mistook this for a one-slot "count lag"; that was an artefact of
the off-by-one and only held when references happened to be consecutive.) **Decoder:** scan the record
for runs of entries (`ref ≠ 0` whose `ref - 1` resolves, both `0x7C` delimiters, a sane count whose upper
bytes aren't the `0x7C` delimiter), breaking a run only when the gap to the next entry exceeds a wide
window (2048 B — a modded item's condition + weapon mods can split the list by hundreds of bytes, and a
big inventory can fragment into several runs), and pick the run with the **most distinct references**, not
the most entries: a misaligned read of a record's non-item region forms a long run that repeats a handful
of refs, so it scores far lower than the genuine item list. The wide window also absorbs a few non-item
bytes; when the name resolver is available the CLI/GUI **hide entries that don't resolve to an item**, so
the list is both complete and clean. Verified on real VNV saves: one that decoded 0/127 now reads its full
inventory (Lead, caps, reloading components), and a 193 KB record's split item list reunites — a known
1,414-count ammo stack that was dropped now appears. Editing a count is a **safe same-length splice**.
Names resolve via §4h.

> **Superseded:** the 2048-byte window above is gone — the per-stack extra data is now decoded, so each stack's
> exact length is known and the walk is deterministic. See **§4i** for the current decoder + the extra-data catalog
> (condition / equipped / mods). The list **start** is no longer a whole-record most-distinct ranking either: on
> vanilla saves it is a pure structural walk (MOVE skip + fixed havok array + sized ExtraDataList → the `vsval`
> stack count → first item); modded ExtraDataLists fall back to a forward scan (§4i).

### 4h. FormID → display name — reading the game's ESM/ESP masters
Every FormID the tool surfaces (inventory above all) is resolved to a human name by a small custom
**TES4 plugin reader** (`TesPlugin`) over the game masters. FNV stores the `FULL` (display) name
**inline** per record (no Skyrim-style `.STRINGS`), so names read directly. FO3/FNV use **24-byte**
record and group headers; a record's `DataSize` excludes its header, a group's `GroupSize` includes it.
```
record  [type:4][dataSize:u32][flags:u32][formID:u32][vc:8][data]
field   [type:4][size:u16][data]               # EDID = editor id, FULL = display name (zstring)
```
- **GRUP-skipping:** only top-level groups whose label is an item record type (`WEAP ARMO ALCH AMMO
  MISC BOOK NOTE KEYM IMOD` + NV `CCRD CHIP CMNY CDCK`) are decoded; the rest are seeked past, so the
  245 MB `FalloutNV.esm` + 9 DLC/pack esms index in ~2 s.
- **Compressed records** (flag `0x00040000`) are `[u32 decompSize][zlib]` → inflated.
- **DLC renumbering:** each plugin numbers forms against *its own* master list, so `PluginDatabase`
  remaps every plugin's local high byte onto the save's load order (master-name match; a form's own
  high byte == the plugin's master count). Plugins are indexed in load order so overrides win.
- The `Data` folder is auto-detected (`GameDataLocator`, with an override); absent → FormIDs stay hex.
**Verified** on a real save: all 10 plugins parse, 3,985 named forms indexed, and the player inventory
resolves fully (Stimpak / Vault 21 Jumpsuit / Weapon Repair Kit / …). Where the tool surfaces other
FormIDs (e.g. `formids`), a runtime-created `0xFF…` FormID shows `(created)` and a form not in the
masters shows `?`.

**Source mod (which DLC/mod an item is from).** A FormID's high byte (mod index) indexes the save's load
order (`Plugins`), so the owning plugin is `Plugins[modIndex]` (`FalloutSave.PluginForModIndex`); the
inventory surfaces it as a friendly name (`PluginNames.Friendly` → `FriendlySourceForModIndex`). FO3/FNV
plugins have **no content-name field** — the TES4 header is only author/masters/overrides, and the DLC
names survive merely incidentally in gameplay `MESG` records (no consistent EDID/FormID), so the in-game
"Downloadable Content" menu uses the engine's built-in known-content list. We mirror that: a small table
maps the 10 official files to their exact menu names (Fallout: New Vegas, Dead Money, Gun Runners'
Arsenal, …), with a PascalCase-split fallback for any other mod.

**Mod Organizer 2 saves.** Modded setups (e.g. Viva New Vegas) run under MO2, which keeps each mod's
files in `<root>\mods\<Mod>\` and only merges them into `Data` via a virtual filesystem *at launch* — so
the mod plugins aren't physically in the game `Data` folder and their item names won't resolve. When a
save is loaded from an MO2 profile (`<root>\profiles\<profile>\saves\`), `GameDataLocator.FindMo2Mods`
derives `<root>\mods` from the path, and `PluginDatabase.CollectPlugins` indexes both the `Data` folder
(base/DLC, authoritative) and each `mods\<Mod>\` root (one VNV save went from 10/43 → 43/43 plugins
resolved). Large modded inventories used to mis-decode (the item run is split by big per-stack extra-data
blocks, and a non-item run elsewhere in the record was longer) — fixed by the wider window + distinct-ref
run selection in §4g (that VNV Courier save went from 0/127 named stacks to 105/110: Lead, caps,
reloading components, …).

**Pip-Boy category (which tab an item appears under).** The tab is **not stored in the save** — it is a
pure function of the base form's **record type**, which `TesPlugin` already reads (the GRUP signature) but
used to discard. `PluginDatabase` now keeps the type per FormID and exposes `RecordType(formId)` +
`Category(formId)` via `PipBoyTab(recordType)` (CLI `inventory` shows a `[Tab/TYPE]` column). Mapping,
**verified in-game on a VNV save:** `WEAP`→Weapons, `ARMO`→Apparel, `AMMO`→Ammo; `ALCH` **and** `BOOK`→Aid
(Aid = "single-use with an effect": food/chems/stimpaks + skill *magazines* (timed, `ALCH`) + skill *books*
(permanent, `BOOK`, e.g. "Duck and Cover!")); `NOTE`→Pip-Boy *Data → Notes* (not an item tab, §6.5);
everything else→Misc (`MISC`, `CMNY`, `CCRD`/`CDCK`, `CHIP`, `IMOD`, and **keys** `KEYM` — the Pip-Boy
collapses all keys into one **"Keyring"** pseudo-row, a UI grouping not stored in the save).

### 4i. Per-stack extra data (condition / equipped / mods) — the deterministic inventory walk
The inventory decoder is now **deterministic**: there is no 2048-byte scan window. Each stack is the fixed
9-byte `[ref:3 BE][7C][count:u32 LE][7C]` entry followed by a per-stack **extra-data block** whose exact byte
length is computed from its decoded properties, so the walk advances to the next stack precisely. Layout:
```
[a:u8][7C]                          a == 0x00  -> no extra data (block is 2 bytes)
[a=04:u8][7C][b:u8][7C] props…      a == 0x04  -> b/4 typed properties follow
property = [type:u8][7C] [payload][7C]   (the trailing [7C] only when the payload is non-empty)
```
Property type → payload catalog (**confirmed by a controlled 3-save diff** — vanilla Saves 31/32/33: equip a
9mm pistol then repair it with a Weapon Repair Kit):
- `0x25` **ExtraCondition** — 4-byte LE float = the item's **absolute current health** (NOT a 0–100 %). The repair
  moved exactly this float `52.5 → 67.5`; it appears only on degradable gear. Values differ per item (real save:
  9mm Pistol 45, SMG 205, Metal Armor 497.2, Grenade Rifle 99.9); the **max is the base-form Health** stat, not
  yet decoded — see §6 #11. **Editable** as a same-length splice (`TrySetItemCondition`).
- `0x16` **ExtraEquipped** — 0-byte flag; its presence means the stack is equipped/worn. It *appeared* on the
  pistol when equipped (Save 31→32), and is present on the always-worn Pip-Boy / worn armor.
- `0x21` — a 3-byte BE refID. On a weapon this is an attached **weapon mod**; the type is reused for other
  linked refs (a VNV "Bill of Sale" note appears on a consumable), so the general semantics aren't pinned.
- `0x6E` (0-byte flag), `0x1C` (3-byte refID), `0x24` (2-byte value), `0x30` (4-byte float) — **payload lengths
  now pinned by corpus alignment** (see "Per-stack property sizing" below); sized but semantics unlabelled.
- `0x0D` — **structured/variable, now DECODED** (sized). It is `[0D][7C][ref:3 BE][7C][n:u8][7C]` then `n/4`
  `[u32:4][7C][f64:8][7C]` pairs then two fixed `[00][7C]` fields, so its length is **`12 + 14·(n/4)`** — pinned by
  corpus alignment (recovered lengths were exactly 12, 26, 54, 68, 110 … for 0/1/3/4/7 pairs across all 607 saves).
  `ReferenceChangeForm.VariablePropertyLength` sizes it and `TryReadStackExtra` now walks straight through it; the
  ≤512-byte resync is retired to a never-hit safety net (semantics stay unlabelled per "size, don't guess").

The list **start** is now anchored by `changeFlags` and a sized preamble, not by ranking every run in the
record. The walk (`ReferenceChangeForm.InventorySearchStart` + `FalloutSave.WalkInventory`) skips **two
deterministic sections** before scanning:
1. the `changeFlags`-gated 27-byte **MOVE** block (`CHANGE_REFR_MOVE`, bit 1 — cell ref + position + rotation), then
2. a **fixed 1160-byte havok/float array** — exactly **232** `[u32][7C]` delimited slots (mostly the reference's
   zeroed havok/animation arrays, but **some slots cache actor values** — slots 100/101 are the player's karma/XP,
   §4j). This size is an **empirical invariant** across all 30 real saves (both characters,
   fresh→4 h) and is **independent of bit 22** (flags `0xB0400832` and `0xB0000832` land identically), so it is a
   structural skip, not a scan. `InventorySearchStart` validates the exact 232-slot shape before skipping (a
   delimiter at every 5th byte) and falls back to just-past-MOVE otherwise, so it can never mis-skip.

That lands on the reference's own **ExtraDataList**, which is now **sized too** — so the start is reached with
**no forward scan and no distinct-ref acceptance test**. The ExtraDataList is a fixed-shape typed list, decoded by
aligning all 30 real saves (`ReferenceChangeForm.TryInventoryItemsStart`):
```
[00][7C][scale:f32][7C]                      reference header (a flags byte + a 1.0 scale)
[xx][7C][5E][7C][N*4][7C] N×(ref:3 7C flag:1 7C)   ExtraDataList ref-list (N = byte/4 entries)
[18][7C][ref:3][7C][pos:3×f32][7C][rot:f32][7C]    fixed 24-byte block (identical bytes on every save)
[74][7C][ref:3][7C]                          a linked-ref entry
([60][7C][u32][7C])                          OPTIONAL — present only on large inventories
[stackCount : vsval][7C]                     Bethesda variable-size value: low 2 bits = byte width, value >> 2
item stacks…
```
The clincher is the **`vsval` stack count** immediately before the items: a variable-size integer whose value is
the number of item stacks (Save 31 → `0x90` → **36**; quicksave → `0x0181` → **96**; the late save → **88**).
`WalkInventory` reads it and decodes from the computed offset, accepting only when at least that many stacks
follow — a **self-validating** anchor that replaces the heuristic entirely on the deterministic path. The old
whole-record byte scan + global most-distinct-chain ranking is **gone**, and so is the per-ExtraDataList forward
scan. **Verified:** the deterministic path is taken on **all 30 real saves** (none fall back), the `inventory`
output is **byte-identical** to the prior decoder (diffed across all 30), and the vsval count equals the decoded
stack count on **28/30** (on quicksave + the 88-stack save the decoder over-reads two interspersed non-item stacks
that name-resolution already hides — see §9; the vsval *reveals* this but Core can't drop them without the
masters, so the full chain is kept rather than truncated, which would drop real trailing items). 283 tests green,
incl. real-save tests pinning the start at `dataOffset + MOVE + 1 + 1160` and `0 ≤ decoded − vsval`. Tooling:
`ReferenceChangeForm` (`InventorySearchStart`, `GatedArrayBlockLength`, `ReadVsval`, `TryInventoryItemsStart`,
`TryReadStackExtra`) + CLI `refdump` (prints the `changeFlags` bits, the MOVE + fixed-array spans, and the sized
ExtraDataList → first item + vsval count).
**The modded grammar — now LIVE, deterministic on all 607 real saves.** The fixed vanilla parse above was
vanilla-specific; it is now generalised into the **live decoder** so the modded list start is deterministic too —
**vanilla 30/30, base VNV 98/98, VNV Extended 479/479** (the §4g forward scan in §4g is retained only as an
unused safety net). `TryInventoryItemsStart` walks the ExtraDataList as a **general typed-entry sequence** via the
shared `ReferenceChangeForm.ExtraEntryLength` catalog (any order, incl. the modded types), terminating on the
inventory `vsval` — recognised by it being a sane count (`ReadVsval` ≤ `MaxInventoryStacks`) immediately followed
by a structurally-valid stack (`LooksLikeStackStart`). The grammar, **pinned by aligning all 479 + the 30 vanilla**:
```
[00][7C][scale:f32][7C]   reference header (7 bytes)
[xx][7C]                  ExtraDataList lead byte (a flag/count; meaning unpinned)
( [type:u8][7C][payload] )*   typed entries, VARIABLE ORDER — catalog (entry length incl. the [type][7C]):
    0x18 = 24            ref + position + rotation block
    0x74 = 6             a linked-ref entry
    0x5E = 4 + 6·N       ref-list, N = b/4, each sub-entry (ref:3 7C flag:1 7C)
    0x60 = 7             a u32 entry (large inventories)
    0x1D = 4 + 4·N       NEW (modded): sub ref-list, N = b/4, each sub-entry (ref:3 7C) — no flag byte
    0x75 = 12            NEW (modded): a 2-ref entry, [75][7C][ref:3][7C][ref:3][7C][flag:1][7C]
[stackCount : vsval][7C]  the terminator — its value lands EXACTLY on the first item (self-validating)
item stacks…
```
The three corpora form a clear progression: **vanilla** order is always `5E,18,74` (+ optional `60`); **base VNV**
keeps the `5E`-first order but **adds** the modded `0x1D`/`0x75` types; **VNV Extended** additionally **reorders**
to `18,74,5E,…`. The live typed-entry walk handles all of these directly. Two further structural facts that the
vanilla path assumed turned out to be Extended-specific — each now closed:

1. **Variable post-entry tail (the `0x04/0x14/0x15` ref-lists).** A handful of saves carry, after the recognised
   entries and before the vsval, an extra group (`7C 7C 04 7C [ref:3] 7C`, large-endgame `7C 7C 14 7C [ref:3…]`,
   or `7C 7C 15 [n] 7C [ref…]`). These are inconsistently framed (a count byte in `0x15`, none in `0x14`) so they
   aren't individually sized; instead `TryInventoryItemsStart` does a **bounded resync** (`PostEntryResyncWindow`)
   forward to the self-validating vsval. **Closed.**
2. **The pre-list region on bit2/bit10 records is NOT a sized slot array — it's a variable-length Havok physics
   blob.** Some records set `changeFlags` bit2 (`CHANGE_REFR_HAVOK_MOVE`) and/or bit10, and the region
   between MOVE and the ExtraDataList is then **active physics state**, not the vanilla 232-slot `[u32][7C]` array
   (the "~214 slots" first guess was wrong). **It is situational, not a "late-game" rule** (see §6 #12): present
   **only in VNV Extended** (113 records) and **absent from base VNV — even at level 31 / 39 h — and vanilla**;
   within Extended it appears on *some* later/auto/quick saves but not on others at the same level (L16–18 manual
   saves lack it), consistent with the player reference being in active Havok sim at the moment of saving.
   **Grammar now confirmed by corpus alignment over all 113 bit2/bit10
   records (VNV Extended ONLY — base VNV + vanilla have zero):** a **7-byte preamble** `[u16][7C][u8][7C][u8][7C]`
   (two families seen: `E1 10 7C 04 7C 4C 7C` and `49 11 7C 05 7C 4C 7C`), then **N × 58-byte entries**
   `[pos:3×f32][7C][quat:4×f32][7C][03][7C][vel:3×f32][7C][angvel:3×f32][7C]` (delimiters at offsets 12/29/31/44/57,
   type `0x03` at 30 — `ReferenceChangeForm.HavokPhysicsEntryLength` recognises one, test-pinned), then a
   **truncated final entry** (pos+quat+`03` trailing into zeros), then a **variable trailing `[4][7C]` slot array**
   (the same vanilla actor-value/havok array) up to the ExtraDataList header. It is **genuinely variable-length** (6
   distinct blob lengths, scattered mod 5) and the trailing slot array's values **locally collide** with
   `IsExtraDataListHeader` (a slot whose high byte is `00` matches), so it **can't be byte-sized to a fixed stride
   and the list end can't be found by structure alone** — a sizer would *still* need self-validation at the tail.
   Rather than decode the physics, `FalloutSave.ScanForExtraDataListAnchor` locates the list by the
   **first ExtraDataList header that self-validates** (typed entries → sane vsval → real stack chain), and
   `WalkInventory` chooses the **real (longest) chain** between that anchor and the §4g scan (a 2× gap separates a
   genuine 180–214-stack endgame list from the short coincidental chains either locator can otherwise latch onto;
   neither locator alone suffices — the anchor finds lists the scan misses, and the scan finds the bit10 lists the
   anchor has no header for). **Closed — and it fixed 35 Extended endgame inventories that previously decoded to
   empty** (the §4g scan had been returning name-unresolvable garbage from the havok blob, hidden by the name
   filter). The blob's exact byte decode is a logged follow-up (not needed for the list).

**Per-stack property sizing — four more pinned (now `0x16/0x21/0x25/0x6E/0x1C/0x24/0x30`).** Beyond the original
`0x25/0x16/0x21`, four further per-stack property types had their **payload length** pinned **structurally by a
corpus-alignment measurement** (the structural analogue of §7's controlled diff — no new in-game saves): CLI
`edlscan` histograms, per unsized type, the byte gap from the property's `[type][7C]` header to the next valid
stack, cleanest when the property is the block's **last** one (block ends → next stack, so `payload = gap==2 ? 0
: gap-3`). Each spiked at a single gap across the corpus, so the length is fixed (semantics stay unlabelled, per
"size, don't guess" — exactly as `0x21` was sized before its meaning was known):
- `0x6E` — **0-byte flag** (gap 2 on **929/929**; modded weapons)
- `0x1C` — **3-byte BE refID** (gap 6 on **108/108**)
- `0x24` — **2-byte value** (gap 5 on **1163/1169**; a `0x25` condition often follows it)
- `0x30` — **4-byte LE float** (gap 7 as last / 12 with a trailing `0x24`; a ~0.82 value)

Added to `ReferenceChangeForm.FixedPropertyPayload`, so the per-stack walk now decodes these blocks deterministically
instead of resyncing. **Bonus correctness win:** because the walk no longer scans forward over these blocks (where
the old resync occasionally latched onto a coincidental stack-like pattern *inside* the extra data), it **drops
phantom over-read stacks** — 11 modded saves moved closer to / exactly onto their `vsval` count (e.g. base VNV
Save 34: decoded 145 → **141 = vsval exactly**; every changed save decreased, **0 became under-read**). Pinned in
`ReferenceChangeFormTests`.

**`0x0D` — the last per-stack type — is now DECODED (sized), so *every* observed per-stack type is sized and the
≤512 B resync is retired to a never-hit safety net.** `0x0D` is structured, not single-fixed-length: `[0D][7C]`
`[ref:3 BE][7C]` `[n:u8][7C]` then `n/4` `[u32:4][7C][f64:8][7C]` pairs then two fixed `[00][7C]` fields. Its total
length is **`12 + 14·(n/4)`**, pinned by **corpus alignment**: a boundary recovery (anchoring on the known-sized
properties that *follow* the `0x0D` inside the same block — the `LooksLikeStackStart` gap is unreliable because
`0x0D`'s own payload contains stack-looking bytes) measured recovered lengths of exactly 12, 26, 54, 68, 110 … for
0/1/3/4/7 pairs across **all 607 saves** — a clean `12 + 14k` progression. `ReferenceChangeForm.VariablePropertyLength`
sizes it; `TryReadStackExtra` walks straight through it (semantics unlabelled per "size, don't guess"). **Correctness:**
over-read **strictly decreased on every corpus with 0 under-reads** (vanilla 2 saves/+4 → **0/+0**; base VNV 8/+35 → 4/+12;
Extended 318/+448 → 314/+393), **and** condition/equipped extra-data that the old resync *dropped* when it sat after a
`0x0D` is now **recovered** — verified on VNV Extended Save 116, where "Legion Recruit Armor" now correctly shows
`cond 117 [equipped]` (the only change in an otherwise byte-identical name-filtered inventory). Pinned in
`ReferenceChangeFormTests` + a real-save theory asserting no decoded stack carries an unsized type.

Tooling: CLI `refdump` prints the typed-entry ExtraDataList walk (flagging the first unrecognised type + a raw
window); **`edlscan <dir>`** aggregates the grammar + the per-save deterministic-path tally + the
`vsval`-vs-decoded over/under-read tally; **`invsig <dir>`** prints a per-save decoded-inventory signature for
byte-identical-decode checks across decoder changes (used with `git stash` to diff before/after).
`PlayerInventory.DeterministicStart` records, per save, whether the start was located deterministically.

### 4j. Player karma + XP — two floats in the player reference's actor-value array
The fixed array between the MOVE block and the ExtraDataList (the "232-slot havok/float array" of §4i) is **not**
all zeroed havok state — specific slots cache the player reference's **actor values**. Two of them are the
player's **karma** and **experience points**, stored as adjacent little-endian **float32** `[f32][7C]` slots in
the player **reference** change form (the iref = PlayerRef + 1 record — the same record that carries the
inventory, §4g):
```
… [karma : f32][7C] [xp : f32][7C] …      array slot 100 = karma, slot 101 = XP (0-indexed, 5 bytes/slot)
```
**Cracked by a controlled diff via the new `fdiff` helper.** A scalar like XP/karma is a float, and a float
change (e.g. `100.0 → 150.0`) only alters its high bytes — the low bytes stay `00` — so it never surfaces as a
clean byte-run delta in `diff`/`idiff` (this is why the first pass found nothing). `fdiff <a> <b> [delta]` aligns
change forms by FormID (like `idiff`) and reads the **full 4 bytes** at every offset of each same-length record,
reporting offsets whose float32 changed by ≈`delta`. On the controlled pairs it pinned a single field in each
case: **XP** `10→60→110` (vanilla Saves 33/34/35, `rewardxp 50` twice) at slot 101, **karma** `0→100→200` (Saves
35/36/37, `rewardkarma 100` twice) at slot 100. The two are **cross-stable** (XP unchanged across the karma saves
and vice-versa), and the **slot indices were confirmed on a second character** (Mace Windu: karma 35, XP 338 —
both sane), so they're structural, not character-specific.

**Locator (`ReferenceChangeForm.PlayerStatSlotOffset`):** skip the gated 27-byte MOVE block, require the full
vanilla 232-slot delimited array (which guarantees the slot is a real `[f32][7C]` and **excludes** the bit2/bit10
havok-physics records whose pre-list region isn't a slot array — there it declines, returning null karma/XP, the
graceful path like the SPECIAL/skills locators), then index the slot. `FalloutSave.Karma`/`Xp` read it,
`TrySetKarma`/`TrySetXp` edit it as same-length float splices (karma may be negative). Surfaced in CLI
(`karma`/`xp`/`setkarma`/`setxp`) and the GUI Edit tab. **Verified:** reads match the controlled deltas exactly on
all six pairs + the second character; edits round-trip same-length. Tooling: CLI **`fdiff`** (§7).

> **Note:** only two slots of this actor-value array are decoded so far. The rest (and the array's per-slot
> meaning generally) stay labelled as the undecoded havok/float array — more controlled diffs can graduate
> further slots (e.g. carry weight, action points) the same way.

### 4k. Player read notes — the Pip-Boy "Data → Notes" viewed markers
The notes the player has **read/viewed** (Pip-Boy *Data → Notes*, shown in normal font; unread ones are bold)
are recorded **one change form per read note**. Reading a note makes the engine write a tiny, zero-payload
change form on the note's inventory reference:
```
[refID : 3 bytes BE]   the note's inventory reference = FormID-array index + 1 (the §4g convention)
[changeFlags : 0x80000000]   the "read" marker (no other bits)
[type : 0x1F]          form-type 0x1F; high 2 bits 0 -> the length field is one byte
[version : 0x1B]
[length : 0]           NO payload — the marker's mere presence is the read state
```
The note's own FormID is therefore `FormIdArray[refID - 1]`, which resolves to a **`NOTE`** record (named via
the masters, §4h). **Cracked by a controlled in-game diff** (VNV Extended Saves 491→492: hover one inventory
note to mark it read): `idiff` showed **exactly one inserted change form** — `iref 54137 → 0x0014068C, type
0x1F, flags 0x80000000, len 0` — against a backdrop of pure game-time-stamp churn; the note it named via the
`-1` index was **"Recipes - Rose's Wasteland Omelet"**, the note that was read. Verified across the whole save:
**all 171 markers resolve to `NOTE` records via the `-1` index (171/171)**, and the count moved 170 → 171 with
the single read. Sane counts on vanilla too (a fresh Goodsprings save = 0; a played save names "How To Play
Caravan", "Mojave Express Delivery Order (6 of 6)", …).

**Corpus-confirmed across all three corpora** (`notescan <dir>`, §4k.1 #1–#3 — the `0x80000000` flag, the
`type 0x1F`→`NOTE` resolution, and the `+1` convention): of **45,783** `type 0x1F` markers (vanilla 20 + base VNV
1,551 + VNV Extended 44,212), the `changeFlags` is **always exactly `0x80000000`** (one distinct value) and
**every** marker resolves via the `−1` index to a `NOTE` (**0 non-NOTE / 0 unknown / 0 invalid**) — the earlier
"non-NOTE collisions" were a masters-remap artifact, gone once the `PluginDatabase` is built **per distinct load
order**. On Save 492 the read note sits at FormID-array iref 54136 and its marker's `refID` is iref **54137 =
54136 + 1** (the `+1` proven); the marker's own `refID` form (`0x0014068C`) is a *reference object*, distinct from
the note, and the note need **not** be currently carried (0/45,783 markers point at a held inventory stack). Pinned
in real-save tests.

**Decode is read-only.** The marker is a whole change form, so toggling read/unread is a **length-changing**
edit (add/remove a record + a FormID-array entry — deferred, §6.7), not a same-length splice; we surface the
list but don't edit it. `FalloutSave.ReadNotes` enumerates the markers (`type 0x1F`, `changeFlags 0x80000000`,
`len 0`) into `PlayerNotes`/`NoteEntry`; CLI `notes` and the GUI **Notes** tab resolve the names. **Scope:** the
save records *read* notes only — a note that's been **acquired but never opened leaves no marker**, so the
acquired-unread set (e.g. a bold "They Didn't Shoot The Deputy") is not surfaced by `ReadNotes`. That list has now
been **located** (a controlled triple, Saves 38→39→40): it is a `7C`-delimited ref-list **inside the player
inventory change form**, not a global table — see §4k.1 #4 for the structure and the decoder still to ship.

### 4k.1. Notes — decode worklist (✅ COMPLETE — items 1–7 all closed)
The notes system is now **fully understood**: the read list (§4k), the marker semantics (#1–#3, corpus-proven over
45,783 markers), the **full Pip-Boy list incl. unread** (#4, decoder shipped), the read/bold mechanism (#5), the
base-form metadata incl. holodisk-vs-text (#6), and the game-time-stamp churn (#7, suppression tool). What the
**save** stores about notes is exactly two things: which notes are held (refs in the player inventory record) and
which are read (`type 0x1F` markers); everything else is read from the masters. **Method throughout: a controlled
diff (§7) — change one thing in-game, save before/after, diff.** History below.

*Used-but-not-truly-decoded (the marker works empirically; the semantics aren't pinned):*
- [x] **1. `changeFlags = 0x80000000` — is it ever combined with other bits?** ✅ **CLOSED by a corpus tally**
  (`notescan <dir>` over all three corpora): the read marker's `changeFlags` is **always exactly `0x80000000`** —
  a *single* distinct value across **45,783 markers** (vanilla 20 + base VNV 1,551 + VNV Extended 44,212), never
  combined with other change bits. So the `ReadNotes` filter can neither miss (a read note with extra bits) nor
  over-match. Pinned in a real-save test. (Which named CHANGE_ enum bit `0x80000000` is — likely a generic
  "form is initialised/active" high bit — is cosmetic now that the value is proven invariant.)
- [x] **2. `type = 0x1F` — is form-type `0x1F` exactly NOTE?** ✅ **CLOSED.** `notescan` enumerates *every*
  `type 0x1F` change form and resolves each via the `−1` index: **all 45,783 resolve to a `NOTE` record — 0
  non-NOTE, 0 unknown, 0 invalid.** The earlier "apparent non-NOTE collisions" were a **masters-remap artifact**:
  reusing one save's FormID remap for a different load order mis-resolves FormIDs. Building the `PluginDatabase`
  **per distinct load order** (5 in Extended) makes the non-NOTE tail vanish entirely. Pinned in a real-save test
  (`Real_saves_every_type_0x1F_change_form_is_a_read_note_marker`).
- [x] **3. What is the marker's own `refID` form, and is the `+1` the inventory-reference convention?** ✅
  **CLOSED (structurally).** Confirmed on Save 492: the note *"Recipes - Rose's Wasteland Omelet"* (`0x0013D52C`)
  sits at **FormID-array iref 54136**, and its read marker's `refID` is **iref 54137 = 54136 + 1** — the `+1`
  proven, and the `−1` index resolves **45,783/45,783** to `NOTE`. The marker's own `refID` resolves to a
  *distinct* form (`0x0014068C`) — a **reference object** (not the note's base form) that `TesPlugin` can't name
  because it indexes only item record types, not `REFR`. **Refinement:** the note is **not required to be in the
  player's current inventory** — `notescan` finds **0/45,783** markers whose note is a currently-held stack, and
  Save 492's note shows `inventory: (not carried)`. So `+1` is the **FormID-array** convention (note → its
  reference), and read state persists independent of carrying; the earlier "appears as an inventory entry at
  data+0x296BD" was the *reference bytes* in the record, not a held stack. *Remaining (low priority):* naming what
  the `refID` reference object (`0x0014068C`) actually is needs a `REFR`/CELL decode in `TesPlugin` — not needed
  for the read-notes list.

*Located by a controlled triple (Saves 38→39→40, Doc Mitchell's House: additem a note unread → open it):*
- [x] **4. Acquired-but-unread notes list — ✅ DONE (decoder shipped).** The acquired-notes
  list is **not** a global-data table (the table 9/10 / quest-form guesses were **wrong**) — it is a **`7C`-delimited
  ref-list embedded in the player inventory change form** (iref = PlayerRef + 1; iref 368 here — the same record as
  item stacks §4g, but a **separate sub-list** within its ExtraDataList, which is why the §4g/§4i item-stack decoder
  never surfaces notes). **Decisively traced:** `player.additem 00117E37 1` (Philippe's Recipes), unread →
  `idiff`/`find`/`hex`:
  - **Acquire (38→39):** FormID array `+1` = the note **base form** (`0x00117E37` @ iref 8151); the note's 3-byte
    refID (`00 1F D8` = 8152 = base index + 1) is **inserted into the ref-list** inside the player inventory record
    (iref 368, at data+0x102D — absent in 38, present in 39), with a neighbouring count byte bumped. **No change form,
    no read marker.** ⇒ acquired/bold = *present in this ref-list*.
  - **Read (39→40):** FormID array `+1` = the note's **inventory-reference object** (`0x00024F80` @ iref 8152,
    **adjacent** to the base form) **and** `+1` change form = the **`type 0x1F` read marker** (`changeFlags
    0x80000000`, len 0) on that reference. ⇒ read/non-bold = *a marker now exists*.
  This also **proves the `+1` convention exactly (§4k.1 #3):** the base form (N) and its inventory-reference object
  (N+1) are **consecutive** FormID-array entries; the read marker's refID is N+1, so note = `array[(N+1)−1]
  = array[N]`. **✅ DECODER SHIPPED:** `FalloutSave.PipBoyNotes(isNoteForm)` scans the player inventory change form
  for `7C`-delimited 3-byte refIDs whose `FormIdArray[ref−1]` is a `NOTE` (the caller injects the masters test —
  `Core` stays UI-agnostic, mirroring inventory name resolution), unions them with the read markers, and flags each
  `Read` (a marker exists) vs **unread**. CLI `notes` and the GUI **Notes** tab now show the **full** list with a
  read/unread status. **Verified:** on the triple, Save 38 = 1 unread (the courier's starting delivery order), Save
  39 = +Philippe's Recipes **unread**, Save 40 = Philippe's **read**; on the modded Save 492, **197 notes (171 read +
  26 unread)**, all named, no false positives — and the unread set includes *"They Didn't Shoot The Deputy"*, the
  very note this checklist cited as an unrepresented bold/unread example. Real-save + synthetic tests pin it.
  *(Open: the exact byte framing of the ref-list — a `[count][7C] N×(ref:3 7C)` shape — isn't individually parsed;
  the scan finds note refs directly, which is robust to framing. Confirming a world-pickup behaves like `additem`
  is a nice-to-have, not required — the scan keys on the note reference, however it was acquired.)*
- [◑] **5. Inventory-side read/bold state — ANSWERED.** Bold→non-bold is **not** a per-stack item flag (§4i catalog).
  Reading writes the **`type 0x1F` read-marker change form** on the note's inventory-reference object (created on
  read, #4 above); the note's *membership* in the Pip-Boy Notes list is its presence in the acquired-notes ref-list,
  and its *read* state is the marker's presence. (Aside: in Save 40 the user noted that *selecting* the note set both
  the "selected/active" indicator **and** the read state; the read marker is the read half — a separate
  "currently-selected note" field, if persisted, is buried in the game-time-stamp churn, #7, and wasn't isolated.)
- [x] **6. Note metadata — ✅ DONE (nothing else is in the *save*).** The controlled triple is conclusive: acquiring
  a note wrote **only** a FormID-array entry + the ref-list entry, and reading wrote **only** the reference object +
  marker — **no text, media type, sort key, or "new" flag** is ever copied into the save. So every other note
  attribute is a pure function of the **base form** (read from the masters, §4h): the **name** (`FULL`), the **text**
  (`TNAM`/linked terminal), the **Pip-Boy sort/category** (always the *Notes* sub-tab, §4h), and the **"new"/bold
  indicator** (= the read state, already decoded #5). Concretely shipped: the **holodisk-vs-text** distinction now
  surfaces — `TesPlugin` reads the `NOTE` `DATA` media byte (0=Sound,1=Text,2=Image,3=Voice) and
  `PluginDatabase.NoteMediaType` exposes it; CLI `notes` + the GUI Notes tab show a **Type** column. Verified on Save
  492 (text journals → *Text*, "Justice Bloc HQ Security Tapes" → *Voice*); unit-tested.
- [x] **7. The game-time-stamp "noise" — ✅ ADDRESSED (suppression tool; characterised).** The ~3,300 same-length
  record changes that swamp a notes diff are **per-reference game-time / havok updates** in `REFR` change forms: each
  save rewrites a few fields — some are globally-identical stamps (e.g. `25 6A→33 BB`, `9E 02 FC→B4 1B FD`) written
  into *every* reference, others are per-reference position/time floats. Rather than byte-decode each field (low
  value), `idiff <a> <b> clean` **auto-subtracts** them: it tallies each byte-run's old→new value across all records
  and hides a record when every run is either a globally-recurring stamp or sits adjacent to one (same churn
  cluster); insertions/removals/length-changes and off-cluster runs always show. On the notes triple this collapses
  **3,314 → 11** (A→B) and surfaces the inserted read marker cleanly (B→C). *(The exact float semantics of each stamp
  remain undecoded — not needed; the filter is value-independent.)*

Tooling (now committed in the CLI): **`notescan <dir>`** aggregates the read-note markers across a save folder —
the `changeFlags`-value tally (#1), the `type 0x1F` → record-type tally via the `−1` index with a per-distinct-
load-order `PluginDatabase` (#2), and the inventory-reference cross-check (#3); **`resolve <save> <formId>`** is a
one-shot lookup (record type + name + source plugin, and where the FormID appears — FormID array iref / inventory
/ read-note marker). Both sit behind the existing `PluginDatabase`/`EnumerateChangeForms`. These closed #1–#3.

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
| Misc Stat index names (§6.8) | ✅ 43 positional counters labelled from the FO3/FNV engine misc-stat array (`MiscStatNames`); CLI `stats` + GUI Misc Stats tab show names. Verified vs corpus: count = 43, and idx 35 "Total Things Killed" = idx 2 + idx 3 on every save (test-pinned) |
| FormID array + iref resolution | ✅ locates player change forms in all 16 |
| Player SPECIAL decode + edit | ✅ all 16 sum to 40; edit round-trips |
| Player skills decode + edit (ACHR actor-value block, §4e) | ✅ format + index map verified; same-length float edit round-trips; sparse (modified-only) |
| Change-form record header / walker (§4f) | ✅ exact: walks to `ChangeFormCount` records, lands on `GlobalData3Offset` (both characters, fresh→4 h) |
| Player inventory decode + edit (§4g) | ✅ located via PlayerRef iref+1; `[ref][7C][u32 count][7C]` entries with **ref = array index + 1**; same-length count edit round-trips; confirmed by a controlled diff (Saves 28/29/30: Antivenom 1→2→1) — every stack resolves, no spurious rows |
| Deterministic inventory walk + per-stack extra data (§4i) | ✅ 2048-byte window removed; extra-data catalog cracked by a controlled 3-save diff (31/32/33): `0x25`=condition float (**editable**, 52.5→67.5 on repair), `0x16`=equipped flag, `0x21`=ref (weapon mod). Save 31 = 36 stacks, VNV = 103 stacks/12,999 items; condition edit round-trips |
| FormID → display name (§4h / §6.3) | ✅ custom TES4 reader over the ESM/ESP masters; 10/10 plugins of a real save parse, 3,985 named forms; DLC renumbering + compressed records handled; inventory CLI + GUI show names + source mod (friendly name) |
| Mod Organizer 2 / modded saves (§4h) | ✅ auto-detects the MO2 `mods\` folder from an MO2 save path; a 43-plugin Viva New Vegas save resolves 43/43; large fragmented inventories reunited (a dropped 1,414-count stack recovered) |
| Pip-Boy item category / tab (§4h) | ✅ from the base form's record type (read from the masters, not the save): `RecordType`/`Category`/`PipBoyTab`; verified in-game (WEAP/ARMO/AMMO; ALCH+BOOK→Aid; KEYM→Misc/"Keyring"; NOTE→Data) |
| Caps decode + edit (§6.4) | ✅ caps are an inventory stack (FormID `0x0000000F`); `Caps`/`TrySetCaps` wrap the inventory path; CLI `caps`/`setcaps` + GUI Edit field; same-length edit round-trips |
| Karma + XP decode + edit (§4j) | ✅ two float32 actor-values in the player reference record (slot 100 = karma, slot 101 = XP), cracked via the new `fdiff` float-aware diff on controlled pairs (XP `10→60→110`, karma `0→100→200`) + confirmed on a 2nd character; `Karma`/`Xp` + `TrySetKarma`/`TrySetXp`; CLI `karma`/`xp`/`setkarma`/`setxp` + GUI; same-length float edit round-trips |
| Read notes decode (§4k) | ✅ Pip-Boy *Data → Notes* "viewed" markers — one zero-payload change form per read note (`type 0x1F`, `changeFlags 0x80000000`, `len 0`) on the note's inventory reference (FormID-array index + 1); note = `FormIdArray[refID-1]` → `NOTE`. Cracked by a controlled diff (Saves 491→492: one note read = **+1 change form**, "Recipes - Rose's Wasteland Omelet"); **all 171 markers resolve to NOTE (171/171)**. `ReadNotes`/`PlayerNotes`; CLI `notes` + GUI Notes tab. **Read-only** (the marker is a whole change form → toggling is length-changing, §6.7) |
| Read-note marker semantics — corpus-confirmed (§4k.1 #1–#3) | ✅ `notescan <dir>` over all three corpora (**45,783** `type 0x1F` markers): `changeFlags` **always exactly `0x80000000`** (one value), **every** marker resolves via the `−1` index to a `NOTE` (**0 non-NOTE/unknown/invalid** — the old "collisions" were a masters-remap artifact, fixed by a per-load-order `PluginDatabase`), and the `+1` convention is proven (Save 492: note iref 54136 → marker refID 54137). New CLI `notescan`/`resolve`; pinned in real-save tests |
| Full Pip-Boy notes — read **and** unread (§4k.1 #4) | ✅ `FalloutSave.PipBoyNotes` scans the player inventory change form's note ref-list for refs resolving to `NOTE` records (masters test injected by the caller) ∪ the read markers; flags each read/unread. Cracked by the Saves 38→39→40 controlled triple (additem a note unread → read it). CLI `notes` + GUI Notes tab show the full list with status; Save 492 = 197 notes (171 read + 26 unread, incl. the bold "They Didn't Shoot The Deputy"), no false positives. Read-only (toggling is length-changing, §6.7); real-save + synthetic tests |
| Note metadata — holodisk-vs-text + base-form attributes (§4k.1 #6) | ✅ proven nothing else is stored per-save (the controlled triple wrote only refs + markers); `TesPlugin` reads the `NOTE` `DATA` media byte, `PluginDatabase.NoteMediaType` → Text/Voice/Sound/Image, surfaced in CLI `notes` + GUI Type column (Save 492: text journals → Text, "Justice Bloc HQ Security Tapes" → Voice); unit-tested |
| Game-time-stamp churn suppression (§4k.1 #7) | ✅ `idiff … clean` auto-hides the recurring per-reference game-time/havok churn (value-frequency + adjacency clustering), collapsing the notes diff 3,314 → 11 and surfacing the inserted read marker; characterised as per-`REFR` time/havok float updates |
| WPF GUI (metadata, screenshot, plugins, stats, SPECIAL + skills + inventory + caps + karma/XP edit + full notes read/unread + media type) | ✅ launches + builds |
| `diff` tool (pinpoints same-size changes) | ✅ Strength 5→6 = 1 byte; `cf` mode names the containing change form; `idiff` aligns records across an insertion, `idiff … clean` hides game-time churn (§4k.1 #7) |
| Tests | ✅ 725 xUnit, all green |
| Per-stack `0x0D` extra-data decode (§4i) | ✅ the last unsized per-stack type, sized by corpus alignment: `[0D][7C][ref:3][7C][n:u8][7C]` + `n/4` `[u32][f64]` pairs + two fixed fields = `12 + 14·(n/4)` (lengths 12/26/54/68/110 across all 607 saves). `VariablePropertyLength`; over-read strictly ↓ (vanilla 2→0, base 8→4, ext 318→314, **0 under-reads**) + recovers condition/equipped that the old resync dropped after a `0x0D`. ≤512 B resync now a never-hit safety net |
| Deterministic inventory decoder + condition edit (§4i) | ✅ window removed; condition (`0x25`) editable + equipped/`0x21` surfaced in CLI + GUI; condition edit round-trips |
| Deterministic inventory list *start* (§4i) | ✅ **deterministic on all 607 real saves** (vanilla 30/30, base VNV 98/98, VNV Extended 479/479): MOVE-skip + the typed-entry ExtraDataList walk (variable order + modded `0x1D`/`0x75`) + bounded post-entry resync + the **ExtraDataList-header anchor** for bit2/bit10 havok-physics records → the **`vsval` stack count** → first item. The §4g scan is now an unused safety net. vsval self-validates (decoded ≥ vsval, **0 under-reads**); verified **display byte-identical** across all 607 except **35 endgame inventories this *fixed* (empty → full)** |
| Modded inventory start — **deterministic on all 3 corpora** (§4i) | ✅ the typed-entry walk is now the **live decoder**: variable-order entries (`0x18/0x74/0x5E/0x60` + modded `0x1D`/`0x75`) + bounded post-entry resync + the **ExtraDataList-header anchor scan** for bit2/bit10 `CHANGE_REFR_HAVOK_MOVE` records (whose pre-list region is a variable-length Havok physics blob, not a sized slot array) + a `vsval` sanity cap. Deterministic list start on **vanilla 30/30, base VNV 98/98, VNV Extended 479/479**; the §4g scan is now an unused safety net. **0 under-reads**; display **byte-identical** across all 607 except **35 VNV Extended endgame inventories that this *fixed* from decoding-empty → full** (the §4g scan had latched onto havok-blob garbage). New: `PlayerInventory.DeterministicStart`, CLI `invsig` (decode-signature cross-check). 347 tests green |

**Editable today:** level, save number, name (same-length), Misc Stats, full SPECIAL, stored skill
modifications (§4e), inventory stack counts (§4g), **item condition/health (§4i)**, **caps (§6.4 — the
`0x0000000F` stack)**, **karma + XP (§4j)** — all safe same-length splices.

---

## 6. Next steps (in priority order)

> ### ✅ DONE (was ★ ACTIVE) — deterministic inventory decoder + per-stack extra data (§4i)
>
> The 2048-byte scan window is **gone**. The per-stack extra-data block is decoded, so each stack's exact
> length is known and the walk advances deterministically (`FalloutSave.WalkInventory`/`AdvancePastStack` +
> `ReferenceChangeForm.TryReadStackExtra`). The extra-data **type→length catalog** was cracked by a controlled
> 3-save diff (vanilla 31/32/33: equip a 9mm pistol, then repair it): `0x25` = condition float (**editable**,
> 52.5→67.5), `0x16` = equipped flag, `0x21` = a ref (weapon mod on weapons); structured/mod-added types fall
> back to a bounded 512-byte resync. Surfaced + editable in CLI (`inventory`, `setcondition`) and GUI. New
> R&D microscope: CLI `refdump`.
>
> **✅ Done (the deterministic *start*):** the whole-record byte scan, the global most-distinct-chain ranking,
> **and the per-ExtraDataList forward scan + distinct-ref acceptance** are all **gone** on the deterministic path.
> `ReferenceChangeForm.InventorySearchStart` skips the 27-byte MOVE block + the **fixed 1160-byte havok array**
> (232 `[u32][7C]` slots, shape-validated), then `ReferenceChangeForm.TryInventoryItemsStart` sizes the whole
> **ExtraDataList** (header + `0x5E` ref-list of `N=byte/4` + a fixed 24-byte `0x18` block + a `0x74` entry +
> optional `0x60`) and reads the inventory's **`vsval` stack count** to land on the first item. `WalkInventory`
> decodes from there and accepts when ≥ that many stacks follow — the count **self-validates** the start.
> **Verified:** deterministic path taken on **all 30 saves** (zero fall-backs), `inventory` output **byte-identical**
> to the prior decoder, vsval = decoded count on 28/30 (the two outliers over-read two non-item stacks that name
> resolution hides — §9). Tests pin the start at `MOVE+1+1160` and `0 ≤ decoded − vsval`. `refdump` prints the
> sized ExtraDataList → first item + the vsval count.
>
> **✅ DONE (the modded start, all three corpora).** The typed-entry walk is now the **live decoder** and the
> item-list start is located **deterministically on all 607 real saves** — vanilla **30/30**, base VNV **98/98**,
> VNV Extended **479/479** (the §4g scan is retained only as an unused safety net). Three mechanisms closed the
> three gaps: (1) `TryInventoryItemsStart` walks the **variable-order** typed entries via the shared
> `ExtraEntryLength` catalog (incl. modded `0x1D`/`0x75`), terminating on the inventory `vsval` recognised by a
> following structurally-valid stack (`LooksLikeStackStart`); (2) a **bounded resync** past the variable
> post-entry `0x04/0x14/0x15` ref-lists to that vsval; (3) for the **bit2/bit10 (`CHANGE_REFR_HAVOK_MOVE`)**
> records — whose pre-list region is a *variable-length Havok physics blob* that can't be byte-sized, **not** a
> 214-slot array as first guessed — `FalloutSave.ScanForExtraDataListAnchor` finds the list by the **first
> ExtraDataList header that self-validates** (typed entries → sane `vsval` → real stack chain), and
> `WalkInventory` picks the **real (longest) chain** between that anchor and the §4g scan. A `vsval` sanity cap
> (`MaxInventoryStacks`) rejects wide-misread counts. **Unexpected win:** this *fixed 35 endgame VNV Extended
> inventories that previously decoded to empty* (the §4g scan had latched onto name-unresolvable garbage in the
> havok blob). Verified **0 under-reads** and **display byte-identical** across all 607 except those 35 strict
> corrections (`invsig` cross-check + per-save before/after diff). 347 tests green.
>
> **Logged follow-up (not a correctness risk):** the bit2/bit10 havok blob is *located past* but not *byte-decoded*
> — its exact serialization (preamble + ~58-byte `pos/quat/vel/[03]/vel/angvel` entries with `02`/`03` per-entry
> type bytes + a truncated final entry) is partly RE'd in §4i for a future exact decode. **All per-stack extra-data
> types are now sized** — `0x6E/0x1C/0x24/0x30` (§4i "Per-stack property sizing") **and** the structured `0x0D`
> (`12 + 14·(n/4)`, §4i) — so the per-stack walk is fully deterministic and the ≤512 B resync is a never-hit guard.

1. ~~**Skills**~~ — ✅ **DONE** (§4e). Located via the controlled-diff method: skills are floats in
   the PlayerRef (`0x14`) actor-value modification block, not an inline structure. Decoder + index map
   + same-length float editor shipped (Core `PlayerSkills`/`TrySetSkill`, CLI `skills`/`setskill`, GUI
   Skills tab). Remaining nuance: storage is sparse (modified-only) and the absolute-vs-modifier
   semantics of naturally-occurring small entries (vs console `setav`, which writes absolute) is not
   yet pinned — a follow-up controlled diff (read a single skill book, +3) could confirm it.
2. ~~**Inventory**~~ — ✅ **DONE** (§4g). Cracked via a controlled drop-1 diff: items live in a dedicated
   reference change form (iref = PlayerRef+1), entries are `[iref][7C][u32 count][7C]`, count edits are
   same-length. Decoder + `TrySetItemCount` + CLI `inventory`/`setcount` + GUI Inventory tab shipped.
   Entry references are **array index + 1** (§4g) — fixed via a controlled diff (Saves 28/29/30), which
   made the whole list correct: every stack resolves, no spurious rows, and previously-missing items
   (Antivenom, caps, Pip-Boy 3000, …) appear. The decoder is now **deterministic** (§4i): the 2048-byte window
   is gone, the per-stack **extra data** (condition / equipped / `0x21` ref) is decoded, and **condition is
   editable**. The list *start* is **deterministic on all 607 real saves** (vanilla 30/30, base VNV 98/98, VNV
   Extended 479/479): MOVE skip + the typed-entry ExtraDataList walk (variable order + modded `0x1D`/`0x75`) +
   bounded post-entry resync + the **ExtraDataList-header anchor** for bit2/bit10 havok-physics records → the
   `vsval` stack count → first item; the §4g scan is an unused safety net. This also **fixed 35 VNV Extended
   endgame inventories that previously decoded to empty**. Remaining nuance: editing targets the first stack of a
   given FormID (duplicate-FormID stacks are ambiguous by FormID alone), and the bit2/bit10 havok blob is *located
   past* but not byte-decoded (logged follow-up, §4i).
3. ~~**Item / form name resolution (FormID → display name)**~~ — ✅ **DONE** (§4h). Small custom TES4
   reader (`TesPlugin`/`PluginDatabase`/`GameDataLocator`) over the ESM/ESP masters builds a
   `FormID → FULL/EDID` index in the save's FormID space; wired into CLI `inventory`/`formids`/`names`
   and the GUI Inventory tab. Auto-detects the `Data` folder (override supported); DLC renumbering,
   zlib-compressed records, and `GRUP`-skipping over the 245 MB `FalloutNV.esm` are handled; `0xFF…`
   runtime forms → `(created)`. Verified on a real save (10/10 plugins, 3,985 named forms; Stimpak /
   Vault 21 Jumpsuit / … resolve). No off-the-shelf C# lib covers FNV (Mutagen is Skyrim/FO4/Starfield).
   (Note: early on a few inventory stacks showed `?` as placed references — that was the inventory
   reference off-by-one, since fixed in §4g; the player inventory now resolves completely.)
4. ~~**Caps / karma / XP**~~ — ✅ **DONE** (§4j for karma/XP).
   - **Caps** — confirmed (as predicted) to be an ordinary inventory stack, base FormID `0x0000000F`
     ("Bottle Cap"), not a standalone field. `FalloutSave.Caps` reads the stack count, `TrySetCaps` edits it
     (a thin wrapper over `TrySetItemCount`). CLI `caps`/`setcaps`, GUI `EditCaps`. Verified on real saves.
   - **Karma & XP** — two adjacent **float32** actor-values in the player **reference** change form
     (iref = PlayerRef + 1), inside its post-MOVE array (§4j): **karma = slot 100, XP = slot 101**. Cracked by
     the new `fdiff` R&D helper (float-aware aligned diff) on controlled pairs — vanilla Saves 33/34/35 (XP
     `10→60→110`, +50 each via `rewardxp`) and 35/36/37 (karma `0→100→200`, +100 each via `rewardkarma`); the
     two are cross-stable (XP unchanged across the karma saves and vice-versa). Slot indices confirmed on a
     second character (Mace Windu: karma 35, XP 338). `FalloutSave.Karma`/`Xp` + `TrySetKarma`/`TrySetXp`
     (same-length float splices), CLI `karma`/`xp`/`setkarma`/`setxp`, GUI `EditKarma`/`EditXp`.
5. ~~**Notes / message log (Pip-Boy "Data → Notes")**~~ — ✅ **DONE (read side)** (§4k). Cracked by a controlled
   diff (VNV Extended Saves 491→492: hover one inventory note to mark it read): reading a note writes **one
   zero-payload change form** on the note's inventory reference — `type 0x1F`, `changeFlags 0x80000000`, `len 0`
   — whose `refID` is the note's FormID-array index + 1 (the §4g convention), so the note = `FormIdArray[refID-1]`,
   a `NOTE` record. The read produced **exactly +1 change form** ("Recipes - Rose's Wasteland Omelet") and **all
   171 markers in the save resolve to NOTE (171/171)**. `FalloutSave.ReadNotes` → `PlayerNotes`/`NoteEntry`; CLI
   `notes` + GUI Notes tab (names via the masters, §4h). **Read-only** — the marker is a whole change form, so
   toggling read/unread is length-changing (§6.7), not a same-length splice.
   **Toward *full* notes decode:** the read **list** is done, but the marker's flag/type semantics, the
   acquired-but-unread list, and the inventory-side read state are not yet pinned. These are tracked as a
   checklist in **§4k.1** (each with the controlled-diff experiment that closes it) — the active worklist for
   "understand every element of notes."
6. ~~**General change-form record header**~~ — ✅ **DONE** (§4f). Walker (`EnumerateChangeForms`) reproduces
   all records exactly; CLI `walk` validates. Enables a future full change-form browser.
7. **Length-changing edits** (arbitrary rename, add/remove plugins, add/remove items) — requires rewriting every
   absolute offset in the File Location Table (and any internal absolute offsets). Deferred.
8. ~~**Quick win** — label the 43 Misc Stat indices by name~~ — ✅ **DONE**. The Misc Stats record stores
   exactly **43** positional counters; they're the fixed FO3/FNV engine misc-stat array, so each index has a
   canonical name (`Core/MiscStatNames.cs`, surfaced in CLI `stats` + the GUI Misc Stats tab). Names taken from
   the FNV `MiscStatEnum` (matortheeternal/esp.json) + a C# save-stats decoder, **verified against the corpus**:
   the count is exactly 43, and index 35 **"Total Things Killed"** = index 2 "People Killed" + index 3 "Creatures
   Killed" on every real save (pinned in a test), with index 39 "Barter Amount Traded" the large fast-growing
   caps total — both anchor the alignment. (A few slots are vestigial FO3 names the engine still tracks under the
   same index, e.g. "Bobbleheads Found"; the label matches what the save stores.)
9. **GUI/UX polish:** screenshot export (PNG), a raw hex viewer tab, backup management.
10. **Quest log + objectives decode** (◑ IN PROGRESS — masters QUST reader **fixed & verified**; both storage
    mechanisms **decoded**; a generic quest-log **reader is now built** (`Core/QuestLog.cs`, surfaced in the
    CLI `quests` command + a read-only WPF **Quests** tab). It walks every QUST change form (classified by
    refID → FormID → masters type `QUST`), parses the delimited stage list, joins each stage to its masters
    `INDX`/`QSDT`/`CNAM` definition, and resolves each objective's `QSTA` target refs to their save-side
    enable-state. **Honesty boundary:** stage progress is read only for quests that use the *delimited* stage
    list (the **packed formType-7 bitmask stays undecoded**, so major story quests show their objectives but
    state `Unknown`); objective enable-state is `Unknown` unless a target ref recorded its form-flags. Verified
    on a real level-26 save: "Climb Ev'ry Mountain" → Completed with per-stage completion times, "Bye Bye Love"
    → Active, both with correct log text.) Surface the player's quests — **completed /
    active / failed** — and, within each, the **individual objectives/stages** (incl. *optional* ones).
    **Masters QUST reader — ✅ FIXED & VERIFIED (the committed step-1).** Root cause was **not** a misaligned
    group read: `TesPlugin.ItemTypes` simply **did not list `QUST`**, so the QUST top-level group was
    seek-skipped and every quest FormID resolved to `null` (the prior "✓ named correctly" notes came from a
    throwaway local edit, not committed code). Fix = a `NamedTypes` set (`ItemTypes` + `QUST`) used **only** at
    the group-decode guard, leaving `ItemTypes` (inventory / Pip-Boy tab) semantics untouched (`Core/TesPlugin.cs`).
    Verified against the real `FalloutNV.esm` + corpus: `0x00104C1C` → "Ain't That a Kick in the Head" / QUST and
    `0x0010A214` → "Back in the Saddle" / QUST, both cross-checked against the FNV wiki's quest IDs. New synthetic
    test `QUST_forms_are_named_and_typed_…`.
    **Key structural correction — the change-form *type byte* (low-6-bit "formType") is NOT the base record's
    form type.** Proven by counterexample now that the masters name records: two confirmed QUSTs have *different*
    change-form types — "Ain't That a Kick" (`0x00104C1C`, iref 2) is **type 0x07** (len 150; field[3] `1F 00 1F 00…`
    = stage 31 data), while the active "Back in the Saddle" (`0x0010A214`, iref 3486) is **type 0x41** (len 297,
    *actor-like*: `changeFlags 0x00040000`, inventory + MOVE). And `0x0010D9F4` (the "growing bitmap" form) is
    **not** a QUST yet also carries a type-0x07 change form. So the earlier "FNV QUST change-form = type 0x07" and
    "type-0x07 = fog-of-war" framings were **both artifacts of reading the type byte as the record type.** Change
    forms must be classified by resolving **refID → FormID → masters record type** (now possible), *not* by the
    type byte. The refID/FormID-array machinery itself is sound — refID is a **big-endian 24-bit** index
    (`00 0D 9E` = 3486 → array[3486] = `0x0010A214`, consistent in both directions).
    **Still confirmed (the type-0x07 model, from "Ain't That a Kick"):** changeFlags `0x80000000` → `0xC0000000`
    during chargen = `bit31 CHANGE_QUEST_STAGES` → `+ bit30 CHANGE_QUEST_SCRIPT` (a `[u32 script][7C]` field
    appeared); the data is **`0x7C`-delimited** and stage/script updates are **length-changing**.
    `ReferenceChangeForm.DescribeFlags` still prints **REFR** labels for QUST records (e.g. it shows `0x80000000`
    as "GAME_ONLY" where for QUST it is `CHANGE_QUEST_STAGES`) — the §6 #13 follow-up.
    **Zero-churn controlled diff — CAPTURED & ANALYSED** (method: from vanilla Save 43, paused console, no
    movement, `setstage 0x0010A214` → 10/20/30/40, saved as `zc0`–`zc4`; cross-checked against the natural
    Saves 43→47 playthrough):
    - **Quest stage state is a growing `0x7C`-delimited STAGE LIST inside formType-9 change forms.** A completed
      entry is `[stageNum][7C][done][7C][04][7C][stageIdx][7C][done2][7C][u32 game-time][7C]`; an incomplete one
      drops the trailing `[u32][7C]` and has `done=0`. So a setstage **grows the list +5 bytes** (`[u32 time][7C]`)
      and flips the two `done` bytes 0→1. Decoded from the cleanest signal — the formType-9 QUST `NVDLC04Ending`
      (`0x06009250`, Lonesome Road's ending tracker) grew one entry on **every** setstage (127→146→151→156→181),
      all entries sharing the frozen time `24 01 E9 08` (proving the paused capture froze game-time, and that the
      `u32` *is* the completion time).
    - **The change-form type byte is a *layout discriminator* keyed to which `changeFlags` are set — not the
      record type.** formType-7 (`0x00104C1C`, changeFlags `0x80000000` = STAGES-only) lays out a 3-byte header
      (`10 7C 00 7C 00 7C`) then **4× `[2 marker bytes][32-byte packed-bitmask array]`** (150 B; the arrays are
      contiguous bit-runs — `1F 1F 1F 1F 0F`, `00 FE 00 FE … C0`, `07 0F 1F 1F 1F 1F`, `E0 F8 FC FE FF FF FF FF`
      — i.e. bit-packed stage/objective state; exact per-bit meaning needs an *in-progress* formType-7 diff, which
      the already-**completed** chargen quest can't provide). formType-9 (changeFlags `0xE0000002` =
      STAGES+SCRIPT+OBJECTIVES) instead uses the delimited stage *list* above. A second formType-9 form
      `0x00174BC0` (*created/game-only*, 153 fields) grew **only on the "finish" stages 10 & 40** → an
      objective/completion manager; `NVDLC04Ending` grew on every stage.
    - **The active freeform quest's progress lives in its objective-target *references*, not its own change
      form.** `0x0010A214`'s own record (formType-1, iref 3486) only ever ticks a 2-byte timer (`data+0xF0`). A
      re-analysis of the **natural** playthrough (Saves 43→47, objectives completed in real play, filtered to the
      quest's FormID neighborhood) shows progress stored as the **enable-state of the quest's objective-target
      placed refs** — sequential FormIDs just below the quest (`0x0010A204`–`0x0010A209`, `0x0010A1Ex`,
      `0x0010A21C`…). Activating an objective **enables** its markers: the ref's form-flags clear `0x800`
      *Initially Disabled* (field[0] `09 08`→`0B 00`, i.e. `0x00800809`→`0x0080000B`), recorded as a
      `bit0 FORM_FLAGS` change form — at 44→45 six markers `0x0010A204`–`A209` enabled together (the gecko pack for
      "kill the geckos at the well"), and `0x0010A207` gained an ExtraDataList; at quest close (47) they revert. So
      "which objectives are active" ≈ "which of the quest's target refs are enabled" — **reconstructable from the
      save**. (The console `setstage` zero-churn captures missed this: setstage skips the result-script
      `Enable`/`SetObjectiveCompleted` calls — why objectives there displayed but never checked off.) Stage
      *sequence* is logged in parallel by tracker quests (`NVDLC04Ending`, formType-9).
    **Done since:** QUST `changeFlags` now have their own labels (bit31 STAGES / bit30 SCRIPT / bit29 OBJECTIVES —
    `ReferenceChangeForm.QuestFlagBitLabels`/`DescribeQuestFlags`, the §6 #13 follow-up); the masters QUST reader
    parses stage (`INDX`/`QSDT`/`CNAM`) and objective (`QOBJ`/`NNAM`/`QSTA`) subrecords (`TesPlugin.QuestDefinition`,
    re-keyed into save space by `PluginDatabase.Quest`); and the generic reader (`QuestLog`) walks target-ref
    enable-state + stage lists into a quest-log view (CLI + GUI). A stage-list entry is attributed to its owning
    quest via the change form's refID (resolves for real QUST forms).
    **Objective display/complete state — ✅ DECODED (but it is NOT a sufficient "shown in Pip-Boy" signal — see
    below).** The full FNV QUST change-form layout was cracked by aligning the bytes against the masters defs (UESP
    `QUST_Changeform` spec + corpus):
    `[questFlags][7C] [vsval stageCount][7C] stages [vsval varCount][7C] (u32 idx,f64 val) script-vars [00][7C][00][7C]
    [vsval objCount][7C] (u32 objIndex,u32 status)`. The **objective `status` is a bitfield — bit0 = displayed,
    bit1 = completed**. **Confirmed by a controlled diff** (Saves 56→57: a natural quest completion flipped
    `NVDLC04Ending` objective 70's status `1 → 3`). `QuestLog.ReadObjectiveStatuses` decodes it (a self-validating
    scan: a vsval count followed by that many `(masters-objIndex, small-status)` pairs), `QuestObjective` carries
    `Displayed`/`Completed`/`Active`, and `DeriveState` uses objective completion. CLI `quests` + the GUI tab show
    per-objective `[active]`/`[done]`. **Verified on real saves** ("Why Can't We Be Friends?" → obj 10 `[active]`;
    the 56→57 completion reads completed).
    **Why this is NOT the Pip-Boy list (a tried-and-rejected gate, established this session).** A first attempt
    gated the default view to "has a displayed objective or completed log stage", calling that the Pip-Boy. It's
    wrong: the engine **background-initializes** quests (sets stage + displays an objective) when content loads,
    *before the player starts them*, and that state is **byte-for-byte identical** to a started quest. Proven on
    vanilla Save 57: "Welcome to the Big Empty" (`0x05002FCB`, Old World Blues — the player has **not** entered the
    DLC) carries quest-flags `0x31` + objective 10 status `1` (displayed), **identical** to genuinely-active quests;
    same for "Supply Train" (`0x07008892`). There is **no save-side "started/given-to-player" bit** (corroborated by
    Saves 54→55: when a quest was actually *given* by an NPC, **no QUST change form changed** — only reference-enable
    markers). So the gate was removed; `QuestLog.Read` now returns simply "quests whose state the save records",
    honestly labelled as NOT the Pip-Boy list. The list both **over**-includes (background-initialized, not-yet-started
    quests like the above) and **under**-includes (Start-Game-Enabled quests at their masters default — e.g.
    **"They Went That-a-Way" `0x000842DD`, which has NO change form at all** yet shows in the Pip-Boy). The packed
    **formType-7** stage encoding ("Ain't That a Kick", chargen-only) is also undecoded. Aside: a change form whose
    refID resolves to a QUST FormID but carries bit18 (`0x00040000`) reference-like data is the quest's REFR-style
    state, not the clean quest layout; the FormType byte is a **layout discriminator, not the record type** (PlayerRef
    ACHR + player base are both FormType 9).
    **Remaining work:** decode the formType-7 packed bitmask (low payoff — 1 quest); and close the gap to a
    **full Pip-Boy mirror** — the Start-Game-Enabled / masters-default quests that leave no save delta — which
    needs the **Gamebryo quest-script interpreter, now in scope as §6 #16**.
    **Dataset note (ephemeral):** the `zc0`–`zc4` captures and the natural Saves 43→47 used above are a **temporary**
    dataset and will likely be deleted. The byte-level findings here — FormIDs, offsets, flag bits, the
    formType-7/9 layouts, and the exact capture method — are recorded to **stand alone**, so the decode is
    reproducible from a fresh equivalent capture (start FNV, in Doc Mitchell's house do "Back in the Saddle", save
    before/after each objective) without the original files.
11. **Item condition maximums (base-form Health)** (NEW — not started). Condition (`0x25`, §4i) is the item's
    **absolute current health**, not a percentage — verified on a real save: 9mm Pistol 45, 9mm SMG 205, Metal
    Armor 497.2, Grenade Rifle 99.9 (values differ per item). The **max** is the base form's **Health** stat,
    which differs per item and is **not yet decoded** — it lives in the `WEAP`/`ARMO`/… record's `DATA`
    subrecord, which `TesPlugin` already walks for the name (`FULL`) and could also read. Decoding it would let
    the tool show `cond X / max` (a true % bar), reject/clamp over-max edits, and offer "repair to full." **Open
    question (needs an in-game test):** what does the engine do with a stored condition *above* max — display
    >100%, clamp on load, or accept it? (We never write to originals, so this is a deliberate experiment, not an
    assumption.)
12. **Havok blob — "mod vs. situation" determination** (NEW follow-up to §4i / §10). The bit2/bit10
    `CHANGE_REFR_HAVOK_MOVE` pre-list physics blob appears **only in VNV Extended** (113 records), **never** in
    base VNV (98 saves, up to **level 31 / 39 h**) or vanilla. Within Extended it is **not** a clean
    progression threshold — early saves (L2/8/14) lack it, but it appears on some later ones (a L23 manual save,
    the L29 quicksave/autosaves) and **not** on others at similar levels (L16–18 manual saves) — so it reads as
    **situational**: the player reference is in **active Havok simulation at the instant of saving** (mid-jump /
    fall / ragdoll / moving surface), captured more often by autosaves/quicksaves. Yet base VNV at L31 *never*
    triggers it, which points at an **Extended-specific mod** keeping the player ref havok-active. The corpus
    alone can't separate "a mod causes it" from "the situation causes it." **Method:** controlled test — in base
    VNV *and* Extended, save while standing still vs. immediately after a jump/knockdown, manual vs. autosave,
    and compare the player-ref `changeFlags`. (Decode value is low — the list is already located correctly via
    the self-validating anchor, §10 — but it would settle the cause and could retire the anchor for a structural
    skip.) **Lead (§8a):** the UESP spec stores REFR `Havok data` as a `vsval count + uint8[count]` (length-prefixed)
    present iff `CHANGE_REFR_HAVOK_MOVE` — FNV's delimited preamble doesn't trivially decode as that size, but
    testing whether the FNV havok blob is length-prefixed is the concrete path to a deterministic skip.
13. ~~**Label the REFR/ACHR `changeFlags` bits from the UESP table**~~ — ✅ **DONE (labels shipped; per-bit FNV
    controlled-diff verification still owed).** `ReferenceChangeForm` now carries the full bit set: a shared table
    (`FlagBitLabels`, bits 0–7/25–31) plus `ActorFlagBitLabels`/`ObjectFlagBitLabels` for the bits that mean
    different things on actor vs object references (10/11/12/17/21/22/23). `DescribeFlags(flags, RefKind)` +
    `LabelForBit` pick the right label by record kind; with `RefKind.Unknown` an ambiguous bit shows **both** as
    `actor|object` so nothing is silently mislabelled. `refdump` passes `RefKind.Actor` for the player record (other
    refs stay Unknown). Output now reads e.g. `bit1(MOVE) bit4(SCALE) bit5(INVENTORY) bit11(ACTOR_PACKAGE_DATA)
    bit22(ACTOR_OVERRIDE_MODIFIERS) bit28(ANIMATION) bit29(ENCOUNTER_ZONE) bit31(GAME_ONLY)`. **Provenance:** bits
    1/2/5 are FNV-corpus-confirmed; the rest are cross-referenced from the UESP Skyrim spec (§8a) and surfaced for
    readability — the engine-level changeFlags enum is shared (1/2/5 match), but a controlled diff per bit (and a
    generic FNV form-type→`RefKind` classifier so non-player refs aren't all `Unknown`) is still owed. Tests pin the
    decode + the actor/object disambiguation. 727 green.
14. **Full ordered REFR/ACHR structural decode** (NEW — §8a). Use the spec's field order (Initial/MOVE →
    Havok(if bit2) → Flags(if bit0) → BaseObject(if bit7) → Scale(if bit4) → ExtraData → Inventory(if bit5) →
    Animation(if bit28)) as the blueprint to decode the player record end-to-end, which would reach the item list
    with **zero heuristics** (retiring the §10 anchor + the residual over-read) and expose more per-record state.
    Verify field-by-field against FNV (delimiter-aware); this is the principled successor to the current locators.
15. ~~**RefID 2-bit type handling**~~ — ✅ **DONE.** The 3-byte refID's top 2 bits are a type
    (`ReferenceChangeForm.RefIdType`/`RefIdValue`): 0 = FormID-array index, 1 = base-master formID, 2 = created
    (`0xFF`), 3 = unspecified. **Corpus scan settled which occur in FNV:** only **type 0** (array index) and
    **type 2** (created) — **type 1 and type 3 never appear** across vanilla + base VNV + VNV Extended, and
    type 2 occurs only on change-form **headers** (inventory item refs + extra-data refs are all type 0). Type-2
    (created) headers used to index out of bounds and resolve to `FormId 0`; **`FalloutSave.ResolveRefId`** now
    maps them to `0xFF000000 | value` (≈213k headers across the corpus: vanilla 135, base VNV 26k, ext 186k), so
    `EnumerateChangeForms` surfaces created references correctly — e.g. `refdump` of refID `0x801313` now reads
    `0xFF001313 (created)` instead of unknown. **Only types 0 and 2 are resolved** (the ones FNV uses); types 1/3
    are deliberately left as `0`/unknown rather than resolved on an unverified Skyrim-spec guess — per the repo's
    "don't guess" rule, surfacing an unseen type as unknown is honest and would flag the surprise. Unit + real-save
    tests pin the split and the created-form resolution. 768 green.
16. **Gamebryo quest-script interpreter — the full Pip-Boy quest list** (the successor to §6 #10).
    **STATUS (2026-06-24): BUILT, SHIPPED (CLI `pipboy` + GUI Quests tab), and validated on 3 ground-truth oracles —
    vanilla Save 57 = 7/7 EXACT (0 FP); VNV Extended Save 122 (mid) = 13/24; Save 420 (late) = 28/68 at 94% precision.
    `QuestPipboy.Compute` combines masters quest scripts + SGE startup (`ScriptStartup` guard eval) + the formType-7
    completion anchor + the said-INFO dialogue seed (Phase B). The remaining gap is a precisely-characterised
    BOUNDARY, not unfinished decode: event-completed quests (kills/activators) show active-not-completed and some
    dialogue/event starts are missed, because the engine recomputes that state from world events at load and it leaves
    no readable, persistent save signal. Precision stays ~94% at all playthrough lengths; recall/state degrade with
    length. See the dated progression log below for the full derivation; the original framing follows.**
    **Why:** §6 #10 decoded the quest progress the *save* records (stage lists + the
    `CHANGE_QUEST_OBJECTIVES` display/complete status), but the in-game Pip-Boy list is **computed by the engine
    from save + masters + compiled scripts**. Proven this session: **Start-Game-Enabled** quests sitting at their
    masters default leave **no save delta at all** — e.g. "They Went That-a-Way" (`0x000842DD`) is type QUST,
    shows in the Pip-Boy, yet has **zero change forms** — so they're displayed by the engine running each quest's
    startup/result scripts at load, not by anything in the save. To reproduce that list we must model what those
    scripts do. **The masters already hold the scripts** (`SCPT` records + quest stage/result-script fragments,
    which `TesPlugin` reads but currently skips); FOSE/FNVEdit decompile the FO3/FNV compiled-bytecode format, so
    it's documented (see §8). **The crux this item must solve is the "started / given-to-player" state**, NOT
    objective display: §6 #10 proved the save can't distinguish a started quest from a background-initialized one
    (both carry quest-flags `0x31` + a displayed objective — e.g. "Welcome to the Big Empty" before Old World Blues
    is entered). So the interpreter must determine *whether each quest is actually running/given* (StartQuest, the
    quest's start conditions, DLC-entry triggers) — only then does its objective-display state mean "in the Pip-Boy".
    **Phased plan:**
    - **A — static literal scan (high coverage, low cost).** Parse the masters `QUST` `DATA` flags (Start Game
      Enabled), then statically scan each quest's stage **result scripts** for literal `SetStage` /
      `SetObjectiveDisplayed` / `SetObjectiveCompleted` calls with constant args. Caveat from above: a static scan
      alone will also "predict" the background-initialized quests (their startup scripts *do* call
      `SetObjectiveDisplayed`), so Phase A must pair the scan with a **started-state determination** — at minimum,
      treat DLC/Start-Game-Enabled quests as *not shown until a start trigger fires*, which generally needs Phase B.
      Unions with the §6 #10 save-side status (save delta wins where present).
    - **B — a real bytecode VM (for the data-dependent cases).** A FO3/FNV compiled-script interpreter (opcodes,
      conditionals, quest/`GetStage` reads, quest variables) executing the startup-relevant fragments against the
      save's decoded state (globals §4c, quest stages/objectives, player data). Scope creep risk is real: scripts
      branch on arbitrary world state, so a *faithful* full reproduction trends toward re-implementing the quest
      engine. **Target a high-coverage approximation, labelled "computed (not save-resident)" to stay honest** —
      never silently present a guess as decoded fact (repo "don't guess" rule).
    - **C — validation.** Diff the computed list against the in-game Pip-Boy on the controlled corpus (the early
      Goodsprings saves where the ground truth is known — Saves 28/47/etc.) and report coverage, not perfection.
    **Honesty boundary carried forward:** until this lands, the quest view stays labelled "recorded progress",
    and the masters-default quests remain absent by design rather than faked. This item is the path to removing
    that caveat. Foundations in place: `TesPlugin.QuestDefinition`, `PluginDatabase.Quest`, `QuestLog`, and the
    §6 #10 objective-status decode.
    **PROGRESS 2026-06-23 — user authorised the build; the key enabler is CONFIRMED and the approach is now Phase-A
    static text scan (NOT a bytecode VM).** Two findings on the Save 57 ground-truth oracle (user screenshot: exactly
    7 Pip-Boy quests, 5 active + 2 completed — the validation target):
    - **FNV masters retain quest stage result-script SOURCE TEXT (`SCTX`), not just compiled `SCDA` bytecode.** Dumped
      via the new `TesPlugin.DumpQust` / CLI `qrec <plugin.esm> <localFormIdHex>`. E.g. *They Went That-a-Way* (VMQ01)
      stage 10 reads literally `SetObjectiveDisplayed VMQ01 10 1` / `…25 1` / `…30 1`; later stages `SetObjectiveCompleted
      VMQ01 20 1`, `if VMQ01.bPrimmClosed == 0 … endif`, `CompleteQuest VMQ01`, `RewardXP 1000`. *Back in the Saddle*
      (VCG02) shows `SetObjectiveDisplayed VCG02 3 1`, `SetStage CGTutorial 54`, `VCG02Gecko1REF.Enable`. So the Pip-Boy
      display logic is **readable as text** — Phase A scans `SetObjectiveDisplayed`/`SetObjectiveCompleted`/`SetStage`/
      `StartQuest`/`StopQuest`/`CompleteQuest`/`FailQuest`/`CompleteAllObjectives` calls (targets resolved by quest EDID).
      "(Optional)" is baked into the `NNAM` objective text, not a separate flag.
    - **The open risk is the SEED (current stage) for freeform/no-change-form quests, NOT the script reading.** The
      save records a clean stage list only for formType-9 quests (decoded). The 5 shown freeform quests store their stage
      nowhere clean: *Back in the Saddle* = empty ref-style template (byte-identical to hidden quests), *Ain't That a
      Kick* = undecoded formType-7 packed bitmask, and *They Went That-a-Way* + *Happy Trails Expedition* = **no change
      form at all** (VMQ01 is DATA `0x00`, not even Start-Game-Enabled — yet runs at stage 10+). So "what stage is each
      freeform quest at" must be reconstructed by **propagating `SetStage`/`StartQuest` across quests from a seed**
      (save stage-lists + SGE startups), a fixpoint that is **condition-blind** (it ignores `if`/`GetStage` guards) and
      will over-fire. This is the accuracy ceiling of Phase A and the concrete reason a faithful result may still need
      light condition evaluation (Phase B) for the propagated cases. Decoding the formType-7 packed bitmask (1 quest)
      and finding where VMQ01's running-stage actually persists are the two specific unknowns to close next.
    - **Scaffolding shipped this session:** `QuestDefinition.{DataFlags,Name,StartGameEnabled,
      IsPlayerFacing}` (QUST `DATA` bit0 = Start Game Enabled; player-facing = FULL name + ≥1 objective — the first-order
      filter: 194 player-facing / 67 SGE / only 7 shown on Save 57), `TesPlugin.DumpQust`, CLI `qrec` + `qdbg`
      (masters × SGE × change-form-presence × decoded-objective correlation).
    **PROGRESS 2026-06-23 (cont.) — the Phase-A interpreter is BUILT (`Core/QuestPipboy.cs`, CLI `pipboy`), and the
    Save-57 validation pins down EXACTLY where it stands.** The pipeline `TesPlugin SCTX → QuestScript.Parse →
    QuestPipboy.Compute` runs: it seeds Start-Game-Enabled quests at their lowest stage, fixpoint-propagates
    **non-conditional** `SetStage`/`StartQuest` across quests, applies reached-stage objective effects, and assembles
    "player-facing + running/completed + has a displayed objective". **Two real wins, one real miss, measured on Save 57
    (ground truth = 7):**
    - ✅ **The running-gate correctly EXCLUDES the background-init quests** that a raw save read wrongly surfaces —
      "Welcome to the Big Empty"/"Supply Train"/`NVDLC04*` are gone (they're not SGE and nothing started them), even
      though the save records a displayed objective for them. This is the single biggest correctness win over the old
      `QuestLog.Read` anti-set, and it's locked by a synthetic test (`Quest_that_is_never_started_is_excluded…`).
    - ✅ The 4 SGE DLC-intro quests come out right (Sierra Madre / Happy Trails Expedition / Midnight / The Reunion,
      Active with the correct objective).
    - ❌ **Precision is poor: 42 computed vs 7 actual.** Seeding *every* SGE quest at its lowest stage over-fires —
      ~37 SGE quests (Ring-a-Ding-Ding!, Still in the Dark, Climb Ev'ry Mountain, …) have a displayable first-stage
      objective in the masters but the player **hasn't reached that stage**, and the masters alone can't say so. And
      the 3 Goodsprings quests (Ain't That a Kick, Back in the Saddle, They Went That-a-Way) are **missed** — their
      reached stage lives only in the undecoded formType-7 bitmask (VCG01) or a script chain with no save anchor.
    **Root cause, now precise:** "is stage N reached" is *runtime* state. For the shown quests it is **neither in the
    save** (empty ref-style template / no change form) **nor in the masters** (a startup TRIGGER sets it — a GameMode
    `if`-gated `SetStage`, a dialogue/activator result, a level/time condition). So pure masters+SGE cannot reach the
    right precision. **The two candidate next levers:** (a) parse the quest's **own GameMode script** (`SCRI`→`SCPT`
    `SCTX`) and follow its self-`SetStage` calls condition-blind — may include the DLC intros' real start and exclude
    the externally-triggered quests; risk: GameMode `SetStage`s are heavily `if`-gated so this may still over/under-fire;
    (b) **gate on a save signal** — require a computed-displayed objective to be corroborated by the save (an enabled
    objective-target ref, §6 #10, or a `CHANGE_QUEST_OBJECTIVES` status), which won't help the no-delta intros but
    would prune the 37 false positives. Plus the standalone **formType-7 decode** to recover the Goodsprings chain.
    `QuestPipboy` + `pipboy` + 4 synthetic tests are the validated framework; the seed is the open problem.
    **PROGRESS 2026-06-23 (cont.) — GameMode-script seeding lands the chosen precision lever; Save 57 goes 42 → 15 → 8.**
    FNV masters keep quest **SCPT script source** too, so `TesPlugin` now reads the `SCPT` group, extracts each
    script's `Begin GameMode … End` block, and links it to its quest via `SCRI` (`QuestDefinition.GameModeScript`;
    CLI `qscript` prints it). `QuestPipboy` replaced the "seed every SGE quest at its lowest stage" with: **an SGE
    quest reaches a stage via its OWN GameMode `SetStage`** — followed condition-blind **only when it targets the
    quest's lowest (startup) stage**, because GameMode also holds if-guarded *catcher* `SetStage`s to late/recovery
    stages (e.g. Ring-a-Ding-Ding!'s `if … SetStage VMQTops 80`) that must not fire. Two-step measurement on Save 57:
    (1) reach via GameMode self-`SetStage` condition-blind → **15** (drops the ~27 SGE quests with *no* GameMode block:
    Still in the Dark / Climb Ev'ry Mountain / …); (2) gate that to the startup stage → **8** (drops the catcher-driven
    over-fires showing completed/late objectives: Bighorners / The White Wash / …). **Result vs the 7 ground truth:**
    the **4 SGE DLC-intro quests are correct** (Sierra Madre / Happy Trails Expedition / Midnight / The Reunion, Active
    + right objective); **4 false positives remain** (Caesar's Favor, Caesar's Hire, Don't Tread on the Bear!, Wild
    Card: Ace in the Hole — SGE quests whose startup `SetStage` is gated by a world condition not yet met, so they need
    Phase-B condition evaluation); **3 still missed** (the Goodsprings chain Ain't That a Kick / Back in the Saddle /
    They Went That-a-Way — reached stages live in the undecoded **formType-7** bitmask, no save anchor). So the two
    remaining levers are now sharply scoped: **(a) light condition evaluation** of the startup `SetStage` guard to cut
    the 4 FPs (e.g. honour `GetStage <self> == 0` / `GetQuestRunning`), and **(b) formType-7 decode** to recover the 3
    Goodsprings quests. Pinned by a new synthetic test (`Sge_quest_whose_gamemode_only_catcher_sets_a_late_stage…`);
    1109 tests green.
    **PROGRESS 2026-06-23 (cont.) — lever (a) DONE: startup condition evaluation cuts ALL 4 false positives; Save 57 is
    now 4 computed = 4 correct (perfect precision, 0 FP).** Added the quest **GameMode local-variable** decode
    (`QuestDefinition.LocalVars` from the SCPT's `short`/`float`/… declarations) and `ScriptStartup` — a tiny
    satisfiability evaluator that decides whether a startup `SetStage`'s `if`-guards hold **at game start**. Model:
    each **local variable**'s reachable startup value SET is computed by running the GameMode block's `set var to const`
    assignments to a fixpoint (applying one only while its own guards are satisfiable); **timers/counters** (`set T to
    (T - GetSecondsPassed)`) become dynamic (any value); **world state** (query functions, `Ref.Method`, non-local
    globals) is its game-start default of **0**. A guard fires iff it is *satisfiable* under those — so the intro
    radio quests' do-once+timer guard `DoOnceMessage == 1 && StartTimer <= 0` holds (flag reaches 1, timer reaches 0,
    nested `RNVTARef.GetDisabled == 0` is a default-enabled ref → `0==0`), while the FPs fail: `GetReputationThreshold …
    >= 2` → `0>=2`, `vStoryEventBennyKilledCasino == 1` → global `0==1`, and Don't Tread on the Bear!'s `iHouseObjective
    == 1` → that local is only bumped under world guards so it stays `{0}`. The earlier "lowest-stage" / "all-zero"
    heuristics were both wrong (one over-fired catchers, the other missed do-once flags + rejected default-enabled-ref
    guards); the satisfiability model handles all three families. **Remaining: only lever (b)** — decode the packed
    **formType-7** stage bitmask (Ain't That a Kick) to anchor the Goodsprings chain (VCG01 stage 200 → `StartQuest
    VMQ01` + `SetStage VMQ01 10` → They Went That-a-Way; VCG02 Back in the Saddle). `ScriptStartup` pinned by
    `ScriptStartupTests` (do-once / world-default / world-guarded-local / timer / and-or); 1115 tests green. Tooling:
    CLI `qscript` prints `LocalVars` + per-effect `fires=`.
    **PROGRESS 2026-06-23 (cont.) — lever (b) is BLOCKED at the project's controlled-diff boundary (needs the game), so
    Save 57 stands at 4/7 with PERFECT precision (4 correct, 0 false positives) and the 3 Goodsprings quests still
    missed.** Attempted the formType-7 decode by cross-save/cross-character diff of Ain't That a Kick (VCG01,
    `0x00104C1C`): its change form is `[99000000 7C](SCRIPT, only once the result script has run) [10 7C 00 7C 00 7C]
    then 4× [marker bytes][~32-byte array]`. **Two findings, one correcting the prior roadmap:** (1) the `99000000`
    SCRIPT prefix + `changeFlags` bit30 appear only **after** VCG01's completing stage ran — present in Nathan's
    left-the-house saves (47/57) and Mace's Goodsprings save (3), absent in Nathan's in-house saves (22/28); (2) the
    **4 arrays DO encode progress** (they are NOT the "static structural" data a first read suggested — they were
    identical only across Nathan's same-state saves; **Mace Windu's differ**: array1 `0F 0F 0F 0F 07` vs Nathan's
    `1F 1F 1F 1F 0F`, a clean bit-shift), and they look like **cumulative bit-runs / a thermometer encoding** of
    stages-reached. **But mapping bit→stage cannot be done from corpus alignment alone** (the arrays move as a whole;
    no save isolates a single stage), so it needs a **controlled `setstage` capture on a formType-7 quest** — the
    `zc0–zc4` captures don't apply (they were `setstage 0x0010A214`, a *ref-style* quest, not formType-7). Per the
    repo's "don't guess" rule + §7 controlled-diff methodology, the bitmask is **left undecoded rather than guessed**.
    **The exact capture that would close it:** from a fresh chargen, `setstage 0x00104C1C 5` → save → `10` → save → …,
    then `fdiff`/`hex` which array bit flips per stage. Two of the three missing quests then fall out for free:
    VCG01 reaching stage 200 propagates `StartQuest VMQ01` + `SetStage VMQ01 10` → **They Went That-a-Way** active, and
    VCG01 itself → completed; the third, **Back in the Saddle** (VCG02), has its completion in *no* decodable save field
    (empty ref-style template, §6 #10) and likely needs the same capture on VCG02 or a Goodsprings-controller model.
    **Net for §6 #16:** the interpreter (`QuestPipboy`) is real and the precision problem is solved; recall is gated on
    one controlled in-game capture, which is a user step.
    **PROGRESS 2026-06-23 (cont.) — the q8–q11 "Ghost Town Gunfight" probe DISPROVES the distributed-marker decode
    hypothesis, and reclassifies `0x0010D9F4`. Two prior claims are corrected; the Save-57 recall path is unchanged.**
    Investigated the natural-play captures q8→q9→q10→q11 (VMS16 "Ghost Town Gunfight", `0x00104EAE`, advanced one
    objective at a time). New `qscript` output prints each objective's `QSTA` target refs, so the markers could be
    checked against the actual objective→target map. Findings:
    - **The "type 0x08 marker = enable of the completed objective's QSTA target ref" hypothesis is FALSE.** VMS16's
      objective targets are obj5→`0x00104C7D` (Ringo), obj10→`0x00104E85` (Sunny), obj20→`0x00104C6D` (Trudy),
      obj30→`0x00104C79` (Chet), obj40→`0x00104C80` (Easy Pete), obj45→`0x00104C0F` (Doc), obj50/60→`0x00104C7D`,
      obj65→`0x00104E85`, obj70→6 Powder-Ganger refs. The **clean** q10→q11 transition (player stayed in the saloon;
      completed ONLY obj20 "Enlist the help of Trudy"; idiff delta +4) inserts type 0x08 markers `0x0017529A` /
      `0x0002ABCD` / `0x000B2503` plus one type 0x32 record `0x00104E7D` — **none is obj20's target `0x00104C6D`**,
      nor any other VMS16 objective target. Same for q8→q9 (completed obj5 / displayed obj10): inserted markers
      `0x0015551C` / `0x0010B293` / type 0x00 `0x00107D4A` / `0x00107D4D` / `0x00169330` — **none is `0x00104C7D` or
      `0x00104E85`**. So the markers are arbitrary script side-effects (`.Enable` on unrelated refs, AI/package refs),
      NOT a decodable objective↔target-ref signal. The §6 #10 "objective active ↔ target ref enabled" relationship,
      where it exists, is mediated by per-quest dialogue/result-script logic and is not recoverable by alignment.
    - **`0x0010D9F4` is cell/location data, NOT a quest-objective tracker** (corrects the earlier "tracks Back in the
      Saddle's objective lifecycle"). It is a formType-7 record whose len follows the player's **cell**, not quest
      progress: 76 (saloon, q8) → 81 (gas station, q9) → 76 (saloon, q10) → 76 (saloon, q11). It **shrank** 81→76 at
      q9→q10 — a monotonic map-reveal can't un-reveal, and it did **not** change at q10→q11 when an objective
      completed in place. Its 32-byte payload (`00 00 C0 07 F0 0F F8 0F F8 0F …` = 16-bit thermometer runs 0x07C0,
      0x0FF0, 0x0FF8…) is the symmetric radial fill of a local-map/cell bitmap that swaps per cell. The earlier
      grow/shrink "around Back in the Saddle" was coincidental movement through Goodsprings during that quest.
    - **Structural claim reconfirmed:** VMS16 is a fully active quest (2 completed + several displayed objectives) with
      **no per-quest change form at all** across q8–q11 — objective state for normal quests is genuinely not stored in
      a decodable per-quest record.
    **Consequence for recall:** VMS16 ("Ghost Town Gunfight") is NOT one of Save 57's 7 Pip-Boy quests, so even a
    perfect VMS16 decode would not lift the oracle's recall; it was only a mechanism probe, and the mechanism is fuzzy.
    The genuine Save-57 gap remains the 3 Goodsprings chargen quests, whose only tractable lever is the **VCG01
    formType-7 thermometer decode** (needs the controlled in-game `setstage 0x00104C1C N` capture — a user step) and/or
    the **VMQ01 `0x000842DD` bit18 ExtraDataList decode** (one of the 7; directly testable). The distributed type 0x08
    marker route is now closed as a dead end. Tooling: `qscript` now prints `obj N targets=[…] "text"`.
    **PROGRESS 2026-06-23 (cont.) — recall LIFTED 4/7 → 6/7 on Save 57 (still 0 false positives) via a controlled-diff
    save anchor for the Goodsprings chain.** Per the user's steer (no `setstage`; VCG01's natural stage-200 state is
    already in q4–q11), found the clean in-save signal by dumping VCG01 "Ain't That a Kick" (`0x00104C1C`, formType-7)
    across q3/q4/q11/Save 57 with the new `cf <save> <formIdHex>` command: its **`changeFlags` bit30 (SCRIPT,
    `0x40000000`) is ABSENT in Doc Mitchell's house (q3, `0x80000000`) and SET the instant the completing stage 200
    fires on leaving the house (q4/q11/Save 57, `0xC0000000`)** — and stays set in the oracle. The new `q7scan` shows
    VCG01 is the **only** player-facing formType-7 quest in Save 57, so the signal is unambiguous there (no over-fire).
    `qscript 0x00104C1C` confirms stage 200 (QSDT `0x01`) runs `StopQuest VCG01 / StartQuest VMQ01 / StartQuest VCG04 /
    SetStage VMQ01 10`. Implemented in `QuestPipboy.Compute` a **save-anchored seed**: a player-facing formType-7 quest
    whose change form carries bit30 has run its result scripts through its completing (QSDT-`0x01`) stage, so we reach
    that quest's stages up to and including the completing one — reconstructing its objective state AND its cross-quest
    propagation via the existing fixpoint. **Result on Save 57: 6 computed = 6 correct** — the 4 DLC intros + **"Ain't
    That a Kick" (completed)** + **"They Went That-a-Way" (active, recovered via the stage-200 hand-off → VMQ01 stage
    10 displays "Find the men who tried to kill you")**. Validated end-to-end on the natural transition: `pipboy q3` = 4
    (chain absent, in house), `pipboy q4` = 6 (chain present, stepped outside). **Still missed: only "Back in the
    Saddle" (VCG02 `0x0010A214`)** — its completion is in no decodable save field (empty ref-style template, §6 #10),
    so 6/7 is the precision-preserving ceiling without a new VCG02 signal. The bit30→completing-stage rule is
    controlled-diff-proven for VCG01 (the lone formType-7 player-facing quest here) and applied generally with that
    caveat documented in code. Pinned by two synthetic tests (`FormType7_quest_with[out]_script_changeflag_…`);
    `QuestSave` test builder gained an optional formType-7 change form (and `ChangeForm` now honours the type's
    length-field width). Tooling: CLI `cf` (dump change form by resolved FormID) + `q7scan` (formType-7 quest/bit30
    audit). All quest/script/pipboy tests green; the only failing tests are the pre-existing chargen inventory-decode
    edge case (§4i, unrelated).
    **PROGRESS 2026-06-23 (cont.) — the bit30 seed was validated against the FULL corpus (649 saves) and the gate
    TIGHTENED from "bit30 set" to the exact VCG01 completion pattern; zero spurious quests confirmed.** New CLI
    `q7corpus <dir>` aggregates, across a whole save folder (caching the masters DB per load order), every
    player-facing formType-7 quest whose change form matches the seed pattern. The first pass (gate = bit30 alone)
    surfaced a FALSE POSITIVE: "Ant Misbehavin'" (VNellisInfestation `0x001083DE`) carries `changeFlags 0x60000000`
    (bit29+bit30, len 9) — a DIFFERENT, unvalidated objective-state record family that also has bit30, present
    identically across 59 base-VNV saves. bit30 alone is therefore NOT specific to completion. **Fix:** the seed now
    requires the exact VCG01-validated pattern `(flags & 0xE0000000) == 0xC0000000` (bits 30+31 set, bit29 clear),
    excluding the `0x60000000`/`0xE0000000` objective-record family. **Re-validated across all three corpora (66
    vanilla + 98 base VNV + 485 VNV Extended = 649 saves): the seed fires on exactly TWO quests** — VCG01 "Ain't That
    a Kick" (everywhere) and VMS33 "The Finger of Suspicion" (Extended only, 439 saves). VMS33 *independently confirms*
    the signal: its record cycles through `0x00040000` (untriggered empty ref-template, 6 saves) → `0x80000000` (active,
    not completed — 1 save, len 335, no prefix) → `0xC0000000` (completed — 439 saves, len 340, the `XX 00 00 00 7C`
    prefix appears), the SAME `0x80000000 → 0xC0000000` completion transition as VCG01 on an unrelated quest. The gate
    correctly excludes VMS33's active/untriggered states. **Net: the generalization adds zero spurious Pip-Boy
    entries** (it also correctly recovers VMS33 as completed where applicable — a recall gain on Extended). Residual
    nuance (documented, not a false positive): a quest that reached its *fail* stage (QSDT `0x02`) while the record is
    `0xC0000000` would be labelled completed rather than failed, since complete-vs-fail needs the still-undecoded
    thermometer; the quest's *presence* is correct either way. Pinned by a third synthetic test
    (`FormType7_objective_record_family_0x60000000_does_not_fire_the_seed`). Tooling: `q7corpus`.
    **PROGRESS 2026-06-23 (cont.) — why "Back in the Saddle" (VCG02 `0x0010A214`) can't be loaded: its state is NOT
    stored anywhere — it is RECOMPUTED at load by the SGE chargen controller + dialogue scripts. Now rigorously
    proven (the earlier "empty template" claim was right but for the wrong reason).** Chased it the same way VCG01 was
    cracked, with a proper same-quest / natural comparison:
    - **VCG02 has NO change form in q4–q11** (the natural Goodsprings opening) and a single type-0x41 / `0x00040000`
      (bit18) / len-297 record ONLY in Save 57 (where it's completed). But that record is **byte-for-byte identical to
      the *active* SGE quest Sierra Madre's record** (`0x08005229`, also type-0x41/bit18/len-297) — exact `cf` diff:
      they differ ONLY in 2 embedded alias refIDs (`+0x07`, `+0x0B`) and one float (`+0x23`); the entire tail is
      identical. So this record does NOT encode started/stage/completed state — it's an alias-ref stub.
    - **No other VCG02-keyed record holds it either:** no formType-7 (`q7scan` shows only VCG01), and the §6 #10
      `QuestLog` stage/objective reader finds only the 4 DLC background-init quests on Save 57, not VCG02. `globals`
      shows the usual 12 GlobalData type-0–11 records (no per-quest stage table).
    - **The controller doesn't anchor it.** `CGTutorial` (`0x00059C85`, the chargen controller VCG02 drives via
      `SetStage CGTutorial 54/70/90`) is **SGE but has no change form at all** (q11 or Save 57), and its GameMode only
      *reads* `GetQuestCompleted VCG02` as a guard — it never sets/completes VCG02. (Added EDID lookup to `qscript` to
      find it, since the QUST record is zlib-compressed in the ESM and not greppable.)
    **Conclusion:** unlike VCG01 (a formType-7 record whose `0x80000000→0xC0000000` flag flip is a genuine save
    anchor) and VMQ01 (recovered by VCG01's stage-200 propagation), VCG02's Pip-Boy state is **derived at load**, not
    persisted: the engine re-runs `CGTutorial` (SGE) + Sunny's dialogue (DIAL/INFO) result scripts — which `StartQuest
    VCG02` / advance its stages — branching on chargen/world state our Phase-A static QUST-script interpreter does not
    evaluate (it reads QUST stage scripts, not INFO dialogue scripts). VCG01-completed does NOT imply VCG02-done either
    (VCG02 is the Sunny tutorial *after* leaving the house — at q4 VCG01 is complete but VCG02 isn't), so a chargen
    inference would false-positive. **So 6/7 on Save 57 is the principled ceiling**: recovering VCG02 needs Phase-B
    modeling of the chargen controller's load-time recomputation + dialogue result scripts, not a missing field read.
    **PROGRESS 2026-06-23 (cont.) — quantified the blind spot ("is VCG02 a one-off?") with a new `qaudit` command,
    and it surfaced + fixed a real `QuestScript.Parse` guard bug.** `qaudit <save> [--list] [--who EDID]` audits the
    masters: it runs the same SGE-seed + non-conditional QUST-propagation reachability the interpreter uses and
    classifies every player-facing quest. **Vanilla load order (194 player-facing quests):** ~**57 "external-only"**
    (never the target of ANY QUST start-script → started only by dialogue (DIAL/INFO) result scripts or activators →
    a guaranteed blind spot, the VCG02 class), ~**55 "conditional-only"** (only ever an `if`-guarded cross-quest
    target, which we deliberately don't follow → at risk), and ~77 "computable" (SGE or non-conditional propagation —
    surfaceable *if* the save triggers them). **So VCG02 is NOT a one-off: a majority (~110/194) of player-facing
    quests are structurally unreachable by a QUST-script-only interpreter.** The practical consequence: the computed
    list is most reliable for early-game / SGE-driven states (like Save 57); recall degrades on mid/late saves whose
    active quests are mostly dialogue-started. Measuring *actual* accuracy there needs more ground-truth Pip-Boy
    oracles (the only one we have is Save 57).
    **Bug found + fixed via `qaudit --who`:** tracing what "starts" VCG02 showed `VGenericTimer`'s GameMode
    `elseif(nEvent == 3) … SetStage VCG02 5` — but `QuestScript.Parse` reported it **non-conditional**. Cause:
    `elseif(nEvent` (no space before the paren) tokenises as one word, so the old `tokens[0].Equals("elseif")` check
    missed it and the guard stack desynced — which could make a genuinely if-guarded cross-quest `SetStage` propagate
    unconditionally (a latent false-positive risk on saves that reach such a quest; Save 57 was unaffected only because
    those quests weren't triggered). Fixed `TrackGuards` to recognise `if`/`elseif`/`else`/`endif` followed by a paren
    or whitespace (no-space form included); pinned by `Tracks_guards_when_if_elseif_have_no_space_before_paren`. The
    fix only makes the interpreter *more* conservative (more guards captured → fewer unconditional fires), so it can't
    add false positives; Save 57 stays 6/7. Tooling: `qaudit` (+`--list`/`--who`), `qscript` EDID lookup.
    **PROGRESS 2026-06-23 — Phase B STARTED: TesPlugin now reads dialogue (DIAL→INFO) result-script effects
    (opt-in), the foundation for the dialogue-started quests qaudit flagged.** `TesPlugin.Read(…, readDialogue:
    true)` descends the `DIAL` group, recurses its nested Topic-Children `GRUP`s, and parses each `INFO`'s result-
    script `SCTX` with the existing `QuestScript.Parse` → `TesPlugin.DialogueEffects` (StartQuest/SetStage/SetObjective
    calls, targeting quests by editor id). `PluginDatabase.Build(…, withDialogue: true)` aggregates these and resolves
    the StartQuest/SetStage targets to FormIDs → `PluginDatabase.DialogueStartedQuests`. **Performance is a non-issue:
    reading ALL of FalloutNV.esm's dialogue + parsing the INFO scripts adds ~120 ms** (only `SCTX` is decoded; it's
    kept off the fast name/inventory path). On the vanilla load order, `qaudit` now shows **86 of the 112 at-risk
    (conditional-only + external-only) player-facing quests have a known dialogue start/advance trigger**; the
    remaining 26 (mostly DLC main quests like NVDLC04MQ*, X-8/X-13 setups) are activator/script-started.
    **Key finding that scopes Step 2:** dialogue-started is a STATIC property and is NOT, by itself, a Pip-Boy gate —
    the background-init quest "Welcome to the Big Empty" (`0x05002FCB`, the Save-57 anti-set) is ALSO `[dlg]`-targeted
    (some Old World Blues INFO does `SetStage NVDLC03MQ01 …`), yet it's not shown until the player enters the DLC. So
    Phase B's recall payoff requires combining the dialogue edge with a SAVE signal that the trigger actually fired /
    the quest is genuinely running (the long-standing started-vs-background-init problem) — NOT the dialogue edge
    alone. Candidate save signals to evaluate next: the INFO "said" state, or the §6 #10 objective-target-ref
    enable-state (a genuinely-active objective's QSTA target ref is enabled; a background-init quest in an unloaded
    DLC worldspace's is not). Step 1 is the data layer; the gate is Step 2. Shipped: `TesPlugin.DialogueEffects`,
    `PluginDatabase.DialogueStartedQuests`, `qaudit` dialogue reclassification (+`[dlg]` in `--list`), 2 tests
    (`QuestDialogueTests`). All quest/script/dialogue tests green.
    **PROGRESS 2026-06-23 — Phase B STEP 2 cracked + integrated: the SAID-INFO change form is the "dialogue actually
    fired" save signal, and active dialogue-started quests now surface (validated).** When the player says a dialogue
    line, the engine writes a change form for that **INFO** record. So an INFO that (a) carries a quest-affecting
    result script (Step 1) and (b) is **present as a change form in the save** is a trigger that genuinely fired.
    `TesPlugin.DialogueInfos` now keys effects by INFO FormID; `PluginDatabase.DialogueInfoEffects` maps save-space
    INFO FormID → its effects; `QuestPipboy.Compute` adds a **save-gated dialogue seed**: for each said-INFO present
    in the save, apply its `StartQuest`/non-conditional `SetStage` effects (then the existing fixpoint + objective
    application run). New CLI `qfired` probes the raw signal. **Validation:**
    - **q11 (the q8–q11 "Ghost Town Gunfight" ground truth): now surfaces VMS16 "Ghost Town Gunfight" ACTIVE** with
      "Talk to Sunny" ticked + "Return to Ringo" active — matching the recorded progression. First dialogue-started
      quest the interpreter recovers, confirmed against ground truth.
    - **Save 57 oracle: stays 6/6 correct, 0 false positives** — the background-init anti-set (Welcome to the Big
      Empty / Supply Train / NVDLC04*) is correctly NOT added, because their start dialogue was never said (no said-
      INFO change form), even though they're dialogue-*targetable*. This is the precise gate Step 1 lacked.
    - `qfired` on Save 57 shows VCG02 "Back in the Saddle" DID fire (said-INFO `0x0010A1E6` present), but it still
      doesn't surface: its only non-conditional dialogue effect is `SetStage VCG02 10`, and stage 10 displays no
      objective (it just `SetStage CGTutorial 54`); VCG02's later progression + completion are StopQuest/CGTutorial-
      driven, not via a VCG02-targeting said-INFO. So **Save 57 is still 6/7** — VCG02 remains the lone gap, now
      precisely characterised. The big win is that the **dialogue-started-quest mechanism is proven and live**; recall
      on mid/late saves (dominated by such quests) should rise substantially — a mid-game Pip-Boy oracle is the next
      thing needed to measure it. Shipped: `TesPlugin.DialogueInfos`, `PluginDatabase.DialogueInfoEffects`, the
      QuestPipboy dialogue seed, CLI `qfired`, wired into `pipboy` + the GUI; 4 new tests. Full suite green except the
      pre-existing chargen inventory edge case.
    **PROGRESS 2026-06-23 — Save 57 is now a FULL 7/7 EXACT ORACLE MATCH (0 false positives): "Back in the Saddle"
    solved.** The user's intuition — that an object/event mechanism analogous to the said-INFO must complete VCG02 —
    was right, and it's *also* dialogue. Extended `qaudit --who` to scan dialogue INFOs + all quest verbs; it showed
    VCG02 is **stopped/completed by dialogue**: INFO `0x00104C5B` does `StopQuest VCG02` (and `0x0015D97E` does
    `CompleteQuest VCG02`). `0x00104C5B` is present (said) in Save 57. The dialogue seed previously applied only
    `StartQuest`/`SetStage`; it now also applies non-conditional `CompleteQuest`/`StopQuest` (→ completed/greyed) and
    `FailQuest` (→ failed), and **reaches every stage ≤ the highest said stage** (so a said `SetStage` to a non-display
    stage like VCG02's 10 still reconstructs the earlier objective display). **Result: `pipboy` on Save 57 = the exact
    7: 5 active (Happy Trails / Midnight / Sierra Madre / The Reunion / They Went That-a-Way) + 2 completed (Ain't That
    a Kick / Back in the Saddle), 0 false positives.** q11 still correct (Ghost Town Gunfight active, no spurious
    greyed quests). **Net for §6 #16:** the interpreter reconstructs the early-game Pip-Boy EXACTLY by combining
    masters quest scripts + Start-Game-Enabled startup + the formType-7 completion anchor + the said-INFO dialogue
    seed (start/advance/complete/stop), all gated on save signals so precision stays at 0 FP. The remaining unknown is
    *mid/late-game recall* — only measurable with a mid-game Pip-Boy screenshot oracle. Precision caveat carried
    forward: a quest a said-INFO `StopQuest`s while it had reached a display stage is shown completed/greyed; a quest
    started-then-genuinely-abandoned (rare) could be mislabelled completed. Pinned by `Dialogue_stopped_quest_shows_
    completed_greyed`; full suite green except the pre-existing chargen inventory edge case.
    **PROGRESS 2026-06-23 — FIRST MID-GAME ORACLE (VNV Extended Save 122, user-screenshotted: 10 active + 14
    completed = 24 quests) + Phase-B recall fix B1.** The user counted their actual Pip-Boy, giving the first
    mid/late-game ground truth. Built a reconciliation harness (computed-vs-truth by quest name). **Baseline: 11/24
    fully correct, 4 false positives, 7 missed, 6 mislabelled** (state wrong) — confirming the honest picture that
    early-game is exact but mid-game is a rough approximation. Three failure classes, now quantified:
    (1) **event-completed quests shown active** (6: Ghost Town Gunfight, Come Fly With Me, I Fought the Law, …) — they
    complete via kills/activators with NO uniform save signal (their own change form carries no completion bit:
    "Can You Find It in Your Heart?" completed is byte-identical to "ED-E My Love"/"Three-Card Bounty" active; 3 have
    no change form at all) → this is the **Phase-C boundary** (per-quest event modeling);
    (2) **false positives** (4: SGE DLC-radio Happy Trails/Sierra Madre/The Reunion suppressed by VNV Extended's
    DLC-delay mod, + a completed-and-dropped quest) — SGE seeding over-fires on modded saves;
    (3) **recall misses** (7).
    **Fix B1 (shipped): apply a said-INFO's `SetObjectiveDisplayed`/`SetObjectiveCompleted` effects** — needed for
    quests with NO objective-bearing stage (objectives are purely dialogue-driven, e.g. "High Times" / "I Put a Spell
    on You", whose only stage is a fail stage; the stage reach shows nothing for them). Two precision guards keep it
    clean: objective effects (a) only apply to quests with no objective-bearing stage (a stage-driven quest gets its
    objectives from stages, and a stray said line must not resurface it — e.g. "By a Campfire on the Trail"), and
    (b) only to quests still ACTIVE (running, not completed/failed — a dialogue-completed quest that dropped off the
    Pip-Boy must not be resurfaced). **Result: Save 122 11→13 correct (+High Times, +I Put a Spell on You), zero new
    false positives; Save 57 stays 7/7.** Tried B2 (apply *conditional* dialogue `SetStage`) — recovered "Heartache by
    the Number" but added a false positive ("I Forgot to Remember to Forget"), a precision wash, so **dropped** per the
    precision-first rule. 2 new tests (`Dialogue_objective_managed_quest_shows…`, `…does_not_resurface_a_completed…`).
    Next: Phase C (per-quest event-completion detection) for the 6 mislabels. Reconciliation harness + the full
    Save-122 ground truth are recorded so this stays measurable.
    **PROGRESS 2026-06-23 — Phase C (event-completion detection) investigated and found WALLED; the SGE-DLC false
    positives are walled for the same reason. The mid-game error classes are runtime state with no readable save
    signal.** Pursued the 6 event-completed mislabels (Ghost Town Gunfight, Come Fly With Me, I Fought the Law, …)
    across every candidate signal:
    - **Quest's own change form: no completion bit.** "Can You Find It in Your Heart?" (completed) is byte-identical to
      "ED-E My Love"/"Three-Card Bounty" (active); 3 of the 6 have NO change form at all; the ones that do are tiny
      misc records (`00 7C`). No flag distinguishes completed from active.
    - **No completion dialogue** — none of the 6 has a present `CompleteQuest`/`StopQuest` said-INFO.
    - **Dead-ref signal fails:** Ghost Town Gunfight's 6 kill-target Powder Gangers — 5 of 7 have NO change form on
      Save 122 (corpses cleaned up long after completion); one still carries an "enabled" marker. Event-completion
      leaves no trace that survives to a later save.
    - **SGE DLC false positives (Happy Trails/Sierra Madre/The Reunion) are the SAME class:** their startup guard is
      `nEnableDLC == 1` where `nEnableDLC` is a **quest-LOCAL** var (not a global), and the quest has no change form,
      so its value is not persisted — on load it resets to default. Whether the DLC-delay mod set it to 1 (→ shown,
      like Midnight) vs left it 0 (→ suppressed, like Happy Trails) is runtime state with no readable save signal, and
      `ScriptStartup`'s satisfiability model can't distinguish them without breaking the vanilla Save-57 case (where
      these quests are correctly shown).
    **Conclusion — the accuracy boundary is now precisely characterised.** The interpreter reconstructs the Pip-Boy
    EXACTLY when quest state is driven by signals the save records: SGE startup at masters defaults (vanilla),
    formType-7 completion flags, and said-INFO dialogue (start/advance/complete/stop). It CANNOT reconstruct quest
    state that the engine recomputes from world events at load — event completion (kills/activators), DLC-enable
    runtime flags, and quests that drop off the Pip-Boy — because those leave no uniform, persistent, readable save
    signal. **Net: Save 57 (early/vanilla) = 7/7 exact; Save 122 (mid-game/modded) = 13/24 correct + 4 FP + 5 missed,
    with the remaining gaps being this runtime-state boundary, not a missed field.** Further mid/late-game gains would
    require either modeling per-quest world-state completion (impractical/fragile, partial coverage) or more
    ground-truth oracles to safely tune heuristics — i.e. they need new data or accept the boundary, not more decoding.
    **PROGRESS 2026-06-24 — SECOND mid-game oracle (VNV Extended Save 420, late-game: user-counted 13 active + 55
    completed = 68 quests) confirms the boundary, and PRECISION HOLDS AT SCALE.** Reconciliation: **28 fully correct,
    19 mislabelled, 3 false positives, 21 missed** (computed 50). Key reads:
    - **Precision is strong even late-game: 94%** (47/50) — only **3 false positives, the SAME stable ones** as Save
      122: Happy Trails Expedition + Sierra Madre Grand Opening! (SGE DLC-radios suppressed by the DLC-delay mod) and
      The Finger of Suspicion (a formType-7 quest that completes then drops OFF the Pip-Boy, so its 0xC0000000
      "completed" flag over-reports). No new FP classes at scale — the interpreter does not "explode" on a busy save.
    - **The 19 mislabelled are all the event-completed class** (shown active, actually completed) — the Phase-C wall.
    - **The 21 missed split into ~5 dialogue/event-started actives** (Wild Card series, The House Always Wins II,
      Beware the Wrath of Caesar!, Heartache) **and ~16 event-completed quests** that can't be shown completed.
    - **Re-evaluated B2 (apply conditional dialogue SetStage) against BOTH oracles**: Save 420 +3 correct / +3 FP,
      Save 122 +1 / +1 — a consistent ~1:1 recall-for-precision trade. **Kept DROPPED** (precision-first: a wrong entry
      is worse than an absent one). Save 57 stays 7/7; Save 122 stays 13/24.
    **Bottom line across three oracles:** early/vanilla = exact (7/7); mid-game = 13/24 (~81% present); late-game =
    28/68 (69% present) at 94% precision. Accuracy degrades with playthrough length purely because more quests have
    completed via world events (the unrecoverable class), while precision stays high.
    **PROGRESS 2026-06-24 — the oracle ground truth + reconciliation harness are now PERSISTED in the repo, and
    angle 1 (quest-chain hand-off completion back-propagation) was investigated and found WALLED.** Two things:
    - **Oracles persisted (`docs/pipboy-oracles/`).** The prior session's ephemeral `/tmp/gt` harness is replaced by
      `reconcile.sh` (reads an oracle file, runs `pipboy`, reports correct/mislabelled/falsePos/missed + precision)
      plus `save57.oracle` (7 quests, complete) and `save122.oracle` (24 quests, complete — recovered verbatim from
      the surviving `/tmp/gt`). Both reproduce their documented baselines exactly: **Save 57 = 7/7, 0 FP; Save 122 =
      13/24 correct, 4 FP, 5 missed.** Save 420's full 68-quest list did NOT survive (only the summary is in this
      log), so `save420.partial.md` records the recoverable breakdown + the computed list and flags that the
      screenshot must be re-supplied to make it a runnable oracle. A `.gitattributes` pins LF on the harness/fixtures
      (CRLF would break bash + append `\r` to parsed quest names). Accuracy is now measurable every session.
    - **Angle 1 WALLED: FNV completing stages self-complete; they do NOT hand off to a successor.** The hypothesis
      was "if a successor S is running, the predecessor P is completed" — build the cross-quest graph from each
      quest's COMPLETING stage (QSDT-0x01) `StartQuest`/`SetStage` calls and back-propagate "completed". Implemented
      it conservatively (only completing-stage, non-conditional, successor-running, gated to already-running quests so
      it can only reclassify active→completed — never add a FP or drop a quest) and measured. **Result: zero useful
      flips on any oracle.** `qscript` confirms why on the canonical chain: They Went That-a-Way (VMQ01) stage 100
      (QSDT 0x01) runs `CompleteAllObjectives VMQ01` + `CompleteQuest VMQ01` — it completes *itself*; it never
      `StartQuest`s Ring-a-Ding-Ding! (VMQ02). The main-quest hand-off is dialogue/world-driven (Benny's INFO starts
      VMQ02), not a completing-stage script call, so the back-prop graph has essentially no edges — the only edge that
      fired touched "Happy Trails Expedition", itself an existing false positive. On Save 122 (the measurable oracle)
      it changed nothing (still 13/24). Reverted; no code shipped. This is the same boundary as Phase C: VMQ01's
      completion lives in whatever fires `SetStage VMQ01 100`, which is not a said-INFO present in Save 420 (else the
      existing dialogue seed would already grey it).
    **PROGRESS 2026-06-24 (cont.) — full Save 420 oracle restored (user re-supplied the 68-quest log), and with it
    angle 4 (change-form completion flags) + the QuestLog-feed idea were probed and found WALLED.** `save420.oracle`
    now reproduces the documented baseline exactly (28 correct / 19 mislabelled / 3 FP / 21 missed, 94% precision),
    so late-game is measurable again. The 19 mislabels split 18 "shown active, actually completed" (the event-
    completion wall) + 1 reverse ("Bleed Me Dry" shown completed via an SGE seed, actually active). Probes with the
    measurable oracle:
    - **Angle 4 (CHANGE_QUEST flag/contents): no signal.** `cf` on the 18 active-shown/completed-actually quests vs
      the 7 correctly-active ones: no distinguishing flag. "They Went That-a-Way" (completed) = type-0x42
      `0x20000802` (bit29 objectives, NO bit31 stages); "For the Republic, Part 2" (active) = `0x80040807` (bit31
      stages set); "Aba Daba Honeymoon" (active) = `0x00040802`. Completed has *fewer* flags than active, the
      opposite of a completion bit. And most truth-completed quests (Ring-a-Ding-Ding!, Come Fly With Me, You Can
      Depend on Me) have **no change form at all**, so no per-quest signal exists to read.
    - **QuestLog-feed (surface §6 #10's decoded `[Completed]` quests in the Pip-Boy): a 1:1 precision wash.** The §6
      #10 reader decodes state for only **9 of 68** quests on Save 420 (objective status mostly `[unknown]`). Of its
      two `[Completed]` player-facing quests, one ("Climb Ev'ry Mountain") is a genuine missed-completed (a recall
      gain) but the other ("Why Can't We Be Friends?") is completed-and-dropped → NOT in the Pip-Boy (a false
      positive), and its `[Active]` set is led by "Welcome to the Big Empty" — the canonical background-init trap.
      Feeding them in is +1 correct / +1 FP, the same ~1:1 trade B2 and the SGE radios hit. No save-readable
      discriminator separates "completed, still in log" from "completed, dropped off". **Kept out** (precision-first).
    **Net:** with all three oracles now persisted and measurable, angles 1 and 4 are both walled by the same
    event-completion boundary; the only remaining under-explored angle is #2 (objective-target-ref enable-state),
    which the §6 #10 work already flagged as murky (markers didn't map 1:1 to objectives).
    **PROGRESS 2026-06-24 (cont.) — bucketed the 18 "shown-active/actually-completed" mislabels by what their save
    record actually holds, and found that the "rich change form" bucket is a MIRAGE (the records are REFR/alias
    stubs, not quest progress). Decided the path with the user: precision-safe automated decode first; bucket C
    (no-record) is a documented tail (no in-game captures for now).** `cf` census on Save 420:
    - **~6 have NO change form at all** (Ghost Town Gunfight 0x00104EAE, Ring-a-Ding-Ding! 0x0011345D, Come Fly
      With Me, Guess Who I Saw Today, Wang Dang, You Can Depend on Me) — zero save record of any type/FormID. Their
      completed state is recomputed by the engine at load from *other* save data (dead-NPC change forms, globals,
      other quests). This is bucket C — needs world-state modeling, deferred.
    - **~5 are the empty bit18 template** (len 293, flags 0x00040000 = "script ran", no progress data).
    - **~3 looked like they carried quest stage/objective blocks (bit31/bit29 set) but DON'T.** Dumped
      "They Went That-a-Way" (0x000842DD, formType-2, 650 B, flags 0x20000802): the payload is a **REFR record** —
      a MOVE block (`00 01 CF` cell + pos/rot floats) followed by alias/REFR fields — with **no
      CHANGE_QUEST_OBJECTIVES (objIndex,status) block** matching VMQ01's masters objective indices (10/20/25/30…).
      Because the record is REFR-layout, bit29/bit31 are **REFR change-flags**, not the QUST objective/stage flags;
      the FormType byte is a layout discriminator (same as the VCG02/Sierra Madre alias-stub finding). So
      **"decode the ref-style quest change forms for completion" (step 1) is NOT viable — the completion data is
      genuinely not in these records.** `QuestLog.Read` already attempts them (it classifies by RecordType==QUST,
      not formType) and correctly finds nothing decodable, which is why it surfaces only 9 clean-QUST quests.
    **Consequence — the only viable PRECISION-SAFE automated lever left is the CTDA-on-said-INFO precondition graph
    (a new decode):** an INFO the player SAID (present as a change form) fired because its CTDA conditions held at
    that time; a condition like `GetQuestCompleted X`/`GetStage X >= N` on a said-INFO is therefore proof X reached
    that state (monotonic for completion). Decoding INFO `CTDA` subrecords (not currently read — TesPlugin reads
    only SCTX) and back-propagating to RECLASSIFY already-surfaced active quests to completed would fix chain
    mislabels with no FP risk. This is the next build; it is sizable and depends on getting the FNV CTDA layout +
    quest-function opcodes right, so it should start as a feasibility spike (read CTDA on a few known chain INFOs,
    confirm the predecessor-completion conditions exist) before wiring into QuestPipboy.
    **PROGRESS 2026-06-24 (cont.) — CTDA-on-said-INFO completion SHIPPED; recall lifted with ZERO precision loss.
    Save 122 13→15, Save 420 28→32, Save 57 held 7/7.** Built the decode: `TesPlugin` now parses INFO `CTDA`
    subrecords (28-byte FNV layout) keeping the quest-state functions whose first param is a quest —
    `GetQuestRunning`(56)/`GetStage`(58)/`GetStageDone`(59)/`GetQuestCompleted`(397) — surfaced as
    `PluginDatabase.DialogueInfoConditions` (re-keyed to save space). `QuestPipboy.Compute` adds a completion pass:
    a said-INFO (present as a change form) fired, so its conditions held; a condition proving the quest reached a
    completing (QSDT-0x01) stage — `GetStage X >= minCompleteStage`, `GetStageDone X <complete>`, or
    `GetQuestCompleted X` — marks X completed. **Precision-safe by construction:** applied ONLY to quests already
    surfaced as running, so it strictly reclassifies a shown-active quest to completed (never adds/drops an entry).
    The `qcond` CLI probe validated the signal on Save 420 first: **13 quests implied-completed, ALL 13 truth-
    completed (100% precision)** — 4 were active-shown mislabels (Ghost Town Gunfight, Come Fly With Me, Can You
    Find It in Your Heart?, Restoring Hope). Key calibration: use the quest's MIN completing stage (reaching any
    QSDT-0x01 stage finishes it) — the first pass used max and under-fired. **Results (all gains mislabel→correct,
    FP/missed unchanged):** Save 57 = 7/7; Save 122 = 13→15 correct (mislabelled 6→4); Save 420 = 28→32 correct
    (mislabelled 19→15), precision still 94%. Most quest-state CTDA on a said-INFO is the INFO's OWN quest gating
    (dialogue happens DURING the quest, so it rarely proves completion); the win is the subset where a LATER quest's
    dialogue is gated on an earlier quest's completion. 3 synthetic tests pin it
    (`Said_info_ctda_precondition_completes_a_running_quest` + below-completing-stage guard + not-running/no-add
    guard). Tooling: CLI `qcond`. Remaining gaps: bucket-C event-completed quests with no said-INFO completion gate
    (the documented tail) + the 21 missed (mostly completed-and-dropped) on Save 420.
    **PROGRESS 2026-06-24 (cont., AFK session) — CTDA STAGE ADVANCE shipped (Save 420 32→33); plus several levers
    measured and walled/deferred with evidence.** Generalised the CTDA signal: a said-INFO's `GetStage X >= N`
    (`==`/`>`) or `GetStageDone X N` proves X reached stage N when the line fired, so for an ALREADY-RUNNING quest
    we reach that stage — advancing its objectives and, when N is at/after a completing stage, completing it
    (catching a mislabel the completion-pass `ImpliesCompleted` alone missed). Gated to `target.Running` so the
    computed count is unchanged (no added entry → provably no new FP). **Save 420 = 33/68 (mislabelled 15→14),
    Save 57 = 7/7, Save 122 = 15/24, FP unchanged everywhere.** Pinned by
    `Said_info_ctda_getstage_advances_a_running_quests_objectives`. Levers measured this session:
    - **Surface NOT-running quests from CTDA proofs (Lever 1 + ungated Lever 3):** +1 each on Save 420 (0 measured
      FP on all 3 oracles) but they ADD Pip-Boy entries, reintroducing the completed-and-dropped FP risk on
      unmeasured real saves (e.g. The Finger of Suspicion drops off the log after completion). DEFERRED until the
      "drops off the Pip-Boy after completion" quest flag is decoded — checked QUST `DATA` byte 0: Finger of
      Suspicion is `0x00`, identical to staying-completed quests (G.I. Blues / Volare! / Ghost Town Gunfight), so
      that flag is NOT the visibility discriminator. Open.
    - **SGE DLC-radio FPs (Happy Trails / Sierra Madre) WALLED (re-confirmed):** their NVDLC0xMQ00 GameMode is the
      vanilla `nEnableDLC` 0→1→2 DLC-start chain; the quest has no change form and there is no DLC-enable global,
      so the VNV-Extended delay mod's runtime suppression of `nEnableDLC` (a quest-local) leaves no save signal.
    - **Missed ACTIVE quests (Heartache, Wild Card series, House Always Wins II):** their starting/advancing dialogue
      SetStage is SCRIPT-conditional (`if (...) SetStage X N` inside the INFO result script) — applying those is the
      already-dropped B2 ~1:1 recall/precision wash, RE-MEASURED with the current CTDA code (apply conditional
      dialogue SetStage as a start): Save 122 +1 correct / +1 FP (precision 83→80%), Save 420 +1 / +1 (94→92%) —
      a net FP increase, so NOT reinstated. `qfired` confirms e.g. Heartache fires only
      `[SetStage 7/6/5 ?]` (all conditional). Same cause as the vanilla q8 "Ghost Town Gunfight" miss: its pickup
      line (INFO 0x00104C54) does a conditional `SetStage VMS16 5` with no CTDA to anchor it, so q8/q9 miss it
      (q10+ surface it once a NON-conditional `SetStage 50` line is said). A precise fix needs evaluating the
      result-script internal `if` guards (a future enhancement, not a free win).
    **PROGRESS 2026-06-24 (cont.) — BUCKET-C CONTROLLED DIFF (kill-completion): the prior "event-completion leaves
    no readable save signal" is TOO STRONG — the kill IS persisted, but the quest↔kill binding is compiled-script
    only, so it stays not-generically-recoverable.** The user captured a clean vanilla before/after pair across the
    natural combat completion of "Ghost Town Gunfight" (VMS16 `0x00104EAE`): `gtg-active.fos` (quest active, fight
    breaking out) → `gtg-complete.fos` (last Powder Ganger killed → quest greyed in the Pip-Boy). Findings from
    `idiff`/`globals`/`cf`/`quests`:
    - **VMS16 has NO QUST change form in EITHER save** (the `quests`/QuestLog reader finds the same 2 quests in
      both; `cf 0x00104EAE` = none). So a kill-completed quest does NOT persist its stage/objective state in a
      per-quest record — re-confirmed, and now for the completion transition specifically.
    - **The completion IS persisted, as WORLD STATE.** `gtg-complete` has +15 change forms. The cleanest signal is
      in **GlobalData type 2 ("TES")**, which grew 107→149 B by inserting a structured **6-entry `(ref, status=1)`
      list** — `00 21 BC` / `00 21 BD` / … / `00 21 C1`, six consecutive ref IDs — appearing exactly with the 6
      kills, alongside 6 new `type 0x32` runtime records (len-10 `[u32][7C][u32][7C]` counters) and a `type 0x32`
      increment (`0x0011EB96` 02→03) + ACHR death/havok deltas. So a kill event leaves a **readable, structured,
      persistent** save signal (a death/kill registry in GlobalData type-2). This is a concrete decode target.
    - **The blocker is the quest↔kill-target binding.** VMS16 objective 70 "Defeat the Powder Gangers" `QSTA`
      targets are 6 MARKER refs (`0x00104C70/75/73/68/72/77`) — NOT the killed actors (the dead ACHRs / the type-2
      list refs). The "6 gangers dead ⟹ SetStage VMS16 100" logic lives in the actors' compiled `OnDeath` scripts
      (SCDA bytecode + a quest-local `nGangerDeathCount`), which we do NOT read (we read quest/INFO SCTX source, not
      compiled actor scripts). So even with the death registry decoded, mapping it to a quest completion can't be
      done generically from save + readable masters — it needs per-quest, compiled-script modeling (the deferred
      fragile path). Nothing speculative shipped (no-guess rule). **Net: bucket C is re-characterised — kill events
      ARE in the save (GlobalData type-2 death registry + ACHR death state); the wall is the kill-target↔quest
      binding, not absence of signal. Dataset kept: `gtg-active.fos` / `gtg-complete.fos` (vanilla Saves folder).**
    **RELOAD TEST (user-confirmed): loading `gtg-complete` STILL shows Ghost Town Gunfight completed** (and even
    restores the un-picked level-2 perk prompt) — so the completion is genuinely PERSISTED and the engine
    re-derives it from save data alone. The signal we have access to is therefore SUFFICIENT; the only missing
    piece is decoding it + the per-quest binding. This upgrades bucket C from "walled" to "recoverable in principle,
    pending a GlobalData type-2 decode + per-quest kill-target mapping." Concrete next step (sizable, partial,
    hard-to-validate on the modded oracles since late-game registries differ): (1) decode GlobalData type-2 ("TES")
    structure — the `[count][7C](ref,status)…` death/kill registry; (2) resolve its refs; (3) for quests whose
    `QSTA` objective-target IS the killed actor, mark the objective complete when its target is in the registry
    (the §6 #10 angle-2, now with a concrete signal). VMS16 itself won't benefit (its QSTA targets are markers, not
    the killed actors — its binding is compiled `OnDeath` only), so this is a partial, per-quest-shaped win, not a
    general solution. Pursue only with a validation plan (it can't be measured on the three oracles cleanly).
    **PROGRESS 2026-06-24 (cont.) — GlobalData type-2 ("TES") DECODED (structure cracked + `gddump` tool shipped),
    but it does NOT yield a generic kill-completion signal — the binding wall holds.** Decoded the type-2 payload:
    `[vsval count][7C]` then `count × ([refID:3][7C][u16 status][7C])` then a fixed tail (`05 00 00 00` + zeros).
    Verified: `gtg-active` count=0 (empty), `gtg-complete` count=6 — the 6 entries resolve to the Goodsprings
    Powder Ganger refs (`0x00104C67`/`6F`/`76`/`6A`/`71` + a created ref), each status `1`, added exactly on death.
    So type-2 is a **registry of state-changed references** (not deaths-only): on Save 420 it has **451** mixed
    entries (the first is "East Central Sewer Key"), with status codes spread 1–7 (semantics per-code not pinned —
    "label, don't guess"). **Why it doesn't help quest completion:** (a) membership ≠ death (mixed registry, status
    semantics unpinned); (b) the binding gap persists — intersecting Save 420's registry with the **mislabeled**
    quests' objective-target refs, **5 of 6 have ZERO overlap** (their `QSTA` targets are markers, exactly like
    VMS16), and the one that overlaps ("Can You Find It in Your Heart?") is already completed by the CTDA pass. So
    even fully decoded, the registry can't drive completion for the marker-target event-quests that dominate bucket
    C. **Net: a real format decode (type-2 structure + `gddump` diagnostic, which dumps any GlobalData table as
    0x7C tokens with refID→FormID resolution), but no accuracy gain — bucket-C completion remains gated on the
    compiled-script quest↔kill-target binding, which is not in readable save+masters data.** A validatable
    assassination/bounty quest (objective `QSTA` = the actual killed NPC) would be the only way to test a partial
    death-state angle; none of the current oracles or captures provide one.
    **PROGRESS 2026-06-24 (cont.) — STAGE 1 of the static save-condition evaluator SHIPPED: the counter/event-gated
    completion graph + `qgate` probe, which SIZES the bucket-C prize before investing in the harder save-state half.**
    The completion logic for kill-completed quests is readable SCTX, so this is a masters-only, save-independent
    analysis (no `QuestPipboy.Compute` change → all three oracles + the gtg pair are byte-for-byte unregressed:
    Save 57 = 7/7, Save 122 = 15/24, Save 420 = 33/68; gtg still both active). Built:
    - `QuestScript.ParseCounterIncrements` — harvests `set <Quest>.<counter> to <counter> ± N` increments (qualified
      = names its quest by editor id, the actor `OnDeath` form; or bare self) from script SCTX; rejects resets.
      `QuestScript.FindCounterComparison` — detects a counter-gate guard `<counter> <op> N` (e.g. `nGangerDeathCount
      >= 6`), incl. the reversed form.
    - `TesPlugin` now scans the FULL SCTX of **every** `SCPT` (not just the quest-linked GameMode block) for (a)
      qualified counter increments and (b) quest-completing effects (`CompleteQuest`/`CompleteAllObjectives`/
      `SetStage`-to-QSDT-complete), tagged with their `Begin <block>` type via a new `SplitBlocks` — so an `OnDeath`
      completion (kill-reachable from the save death registry) is told apart from a `GameMode` world-poll.
    - `CounterGatedQuests` (new) builds two graphs: the **counter-gated** completions (a counter-guarded self-
      completion whose counter is incremented somewhere) and the broader **external event-completion** graph (a
      quest completed by a `SCPT` other than its own — the single-kill cases with no counter). `PluginDatabase`
      aggregates both (re-keying each `ScriptFormId` into save space for Stage 2's script→actor map) and exposes
      `CounterGates`/`ExternalCompletions`. CLI `qgate <save>` dumps them + the headline.
    **DELIVERABLE (honest size of the prize, measured on both vanilla and the VNV-Extended Save-420 load order):**
    - **counter-gated completions (count N kills/events → complete, the Ghost Town Gunfight shape): 4** — VMS16
      Ghost Town Gunfight (`nGangerDeathCount >= 6`, vanilla; the mod bumps it to 7), VMS20 Boulder City Showdown
      (`nDeathsGreatKhans >= 6`), VNipton Booted (`NumHostages >= 2`), NVDLC03HQBuddy All My Friends Have Off
      Switches (`iComputer >= 1`). All externally (actor `OnDeath`) incremented. (3 more "gates" are do-once/timer
      flags — `doonce == 0` / `GotJessupNote == 0` / `fBorousTimer > 0` — correctly reported UNBOUND, not counters.)
    - **single-kill completions (one `OnDeath`/`OnHit` script SetStages/CompletesQuest, NO counter): 8** — I Fought
      the Law (VMS02), How Little We Know (VMS21), Ring-a-Ding-Ding! (VMQTops), That Lucky Old Sun (VMS03), We Are
      Legion (VDeadSea), Wheel of Fortune (VMS44), Crazy Crazy Crazy (VMS57), Return to Sender (VMS52).
    - **=> CLEAN kill-reachable prize (death-registry-recoverable): 12 quests.** The broader "completed by any
      external script" upper bound is 66, but the other 54 are `GameMode` world-polls / `OnActivate` / scripted DLC
      sequences whose triggers aren't a simple save signal — reachability varies, NOT counted as clean.
    - **Real recall on Save 420 specifically:** of the 13 quests currently mislabelled active-should-be-completed,
      exactly **3 are in the clean kill-reachable set** (I Fought the Law, Ring-a-Ding-Ding!, That Lucky Old Sun),
      so a perfect Stage 2 lifts Save 420 33→36 (+ the gtg controlled pair). The other mislabels (Guess Who I Saw
      Today, My Kind Of Town, Wang Dang Atomic Tango, …) complete via non-kill mechanisms (activators/dialogue) and
      are NOT kill-reachable. Honest framing confirmed: the bucket-C prize is real but modest. 8 synthetic tests pin
      the parsing + analyzer; full suite unchanged (the 8 pre-existing `Real_saves_*` inventory/SPECIAL failures are
      unrelated). Next: Stage 2 (re-derive the counter from the GlobalData type-2 death registry, prototype on gtg).
    **PROGRESS 2026-06-24 (cont.) — STAGE 2 SHIPPED: single-kill completion from the death registry (precision-safe
    recall, Save 122 15→16 + Save 420 33→34, 0 FP) — and the COUNTER path (Ghost Town Gunfight) characterised as a
    spawned-actor WALL.** Built the save-state half on the gtg controlled pair:
    - **`FalloutSave.StateChangedRefs()` / `DecodeStateChangedRefs()` (Stage 2a, committed separately):** decode
      the type-2 registry `[vsval count] + count×([refID:3] 7C [u16 status] 7C)`; `status 1 = death` (gtg pair: 0
      vs the 6 killed gangers). `DeadReferences()` + CLI `deaths`. Save 420 = 209 dead of 451 mixed entries.
    - **KEY DISCOVERY — the registry records a UNIQUE killed actor by its NPC_ BASE FormID, not a placed ref.**
      Grepping the esm, the 6 gtg "refs" (0x00104C67 etc.) are **NPC_ records**, and their `SCRI` is the
      increment/OnDeath SCPT (0x00104C69 / 0x00105D4D). So the binding the prior sessions called a wall is
      DIRECT: a dead registry entry IS a base actor; read `NPC_`/`CREA` `SCRI` (cheap — no worldspace descent;
      `TesPlugin.Read(readActors:true)` → `PluginDatabase.ActorScripts`, re-keyed) and a dead base that runs an
      `OnDeath` quest-completion script proves that script fired. (A placed-ACHR `CELL`/`WRLD` scan was built +
      measured — 200 ms, but the gtg bosses bind DIRECT, so it was removed; generic non-unique actors would need
      it, a future lever.)
    - **SHIPPED single-kill completion (`QuestPipboy`):** for an ALREADY-RUNNING player-facing quest with a ViaKill
      (`OnDeath`/`OnHit`) external completion whose script-bearing actor is DEAD, mark it completed. Gated to
      running ⇒ reclassify-only (never adds/drops a Pip-Boy entry ⇒ provably no new FP). **Counter-gated quests are
      EXCLUDED from this path** (critical fix: VMS16's OnDeath `SetStage 100` is guarded by `nGangerDeathCount >= 6`,
      so one ganger death must NOT complete it — a "killed 2, fled" save would be a FP). **Results: Save 57 = 7/7
      held; Save 122 15→16 (I Fought the Law); Save 420 33→34 (I Fought the Law); FP/precision unchanged
      everywhere.** 2 synthetic e2e tests pin it (a boss in a synthetic type-2 registry completes the quest; empty
      registry leaves it active) + the decode tests.
    - **COUNTER path (GTG) is a WALL — documented, not shipped.** Re-deriving `nGangerDeathCount` from dead
      ganger-bases gives **5 of 6**: the 6th Goodsprings ganger is a runtime-SPAWNED ref (`0x0015EAE9` — not in the
      masters, no change form, not in Created Objects type-4), so its base→script link is unreadable. 5 < 6, so the
      counter never reaches threshold — gtg-complete stays ACTIVE (the honest partial). **Verified the seam (the
      user asked which one is missing): it is NOT the named NPC. Joe Cobb's base NPC_ is `0x00104C67` (his ref
      `GSJoeCobbRef` 0x00104C68 NAMEs that base), which IS one of the bound 5 — Joe Cobb runs his own increment
      script `0x00104C69`; the other 4 share the generic ganger script `0x00105D4D`. So the 5 bound = Joe Cobb + 4
      editor-PLACED gangers, all bindable; the unbindable 6th is a generic RUNTIME-SPAWNED ganger (Goodsprings
      spawns extras via the trigger system — `GSJoeCobbTriggerRef` exists). The seam is placed-vs-spawned, not
      named-vs-generic.** Tempting-but-rejected heuristic: the 6 deaths sit at consecutive registry slots (refIds
      `0x21BC`–`0x21C1`, added together), so an unresolvable dead ref batched with bound gangers COULD be assumed a
      ganger and counted — but that's a guess (the next spawned ref elsewhere might be a brahmin/settler), a net-FP
      risk, so left out per precision-first. The counter pass was
      prototyped (`counterderive` CLI) but NOT wired into `QuestPipboy` (no win + it would ignore the OnDeath's
      objective-state guard = residual FP risk). So bucket-C counter-completion remains walled on spawned/leveled
      targets; single-kill unique-boss completion is the recoverable slice. **Net Stage 2: +1 Save 122, +1 Save 420,
      0 FP, gtg pair validates the mechanism (single-kill fires; counter 5/6 documented).** The other Save-420
      kill-mislabels (Ring-a-Ding-Ding!, That Lucky Old Sun) bind but their completing actor isn't in the registry
      (cleaned up / different mechanism) — partial coverage by design, as planned.
    **DEEP CONTROLLED-DIFF 2026-06-24 (cont., AFK — user pushed "the data IS there, the game obviously knows"):
    the prior two "wall" framings are BOTH corrected. (a) The 6th ganger IS bindable; (b) GTG completion is NOT
    persisted as quest state at all — the engine RE-DERIVES it from the 6 dead gangers, via engine-internal logic
    not present in readable Obscript.** Findings, each vetted on the gtg pair:
    - **REJECTED off-by-one:** the registry refId resolution is 0-BASED (confirmed: on Save 420, 0-based resolves
      51/451 entries to real records, 1-based 0/451). The gtg "clean 1-based run" was a coincidence (the 6 gangers
      sit at contiguous FormID-array indices). So the 6th 0-based entry `0x0015EAE9` is a genuine runtime-created
      form, the spawned ganger — matching the user's 6th corpse on the ground.
    - **The 6th ganger IS a scripted ganger (corrects "unbindable"):** the fight inserts a created reference
      `0xFF001334` (FormType 0, 58 B: a MOVE block + a template ref + a baseball cap) — the spawned ganger's
      record, which my earlier `cf 0x0015EAE9` missed (the corpse is keyed by its 0xFF created FormId, not the
      registry's array-form). It references template ACHR `0x00104C75`, whose base NPC_ is `0x00104C74` (`GSPGHM`),
      which **runs the generic ganger increment script `0x00105D4D`** (the "no SCRI" I first saw was a
      compressed-NPC_ read failure). So ALL 6 gangers run an increment script: 5 editor-placed bases in the
      registry + 1 runtime-created actor reachable via its created change-form → template → base.
    - **GTG completion has NO persisted quest state — exhaustively verified:** VMS16 (0x00104EAE, FormID-array
      index 1089) has NO change form (`cf` + `quests --raw` + `idiff` all agree; `cf` proven reliable on
      DeanBarkTimer iref 1090). NOT in formType-7 (`q7scan` identical both saves), NOT in Global Variables (type 3
      diff = only 2 time-floats), NOT in any GlobalData table (types 0–11 diffed: type-2 = the +6 kills, type-5
      `2219→36` = combat/havok process settling AFTER the fight, types 3/8/10 = time/counters), NOT in GlobalData3
      (empty). The only quest-completion trace is Misc Stats "Quests Completed" `2→3` (a bare counter). The 15
      inserted change forms are all fight SIDE-EFFECTS: 6 REFR ragdoll records (type 0x32), the created corpse, a
      REPU Goodsprings-reputation delta (`0x00104C22`), a CHAL challenge counter, INFO/IDLE, and 3 enable-markers.
      Notably the player's "People Killed" stat = 1 — the Goodsprings DEFENDERS killed the other 5 gangers.
    - **The readable scripts do NOT re-derive completion at load:** VMS16's GameMode gates on the quest-local
      `nGangerDeathCount` (incremented only in the gangers' `OnDeath`, which doesn't re-fire on load); both ganger
      scripts (`0x00104C69` Joe Cobb / `0x00105D4D` generic) are `OnDeath`-only with no GameMode re-count; the
      SGE controller `VFreeformGoodsprings` (0x00104C66) has no VMS16 logic. (Contrast VMS16b "Run Goodsprings Run",
      the opposite questline, whose GameMode DOES re-derive via `if SunnyRef.GetDead && EasyPeteRef.GetDead && …
      SetStage 100` — proving the engine pattern exists, just not for VMS16's kill-count path.)
    **CONCLUSION (fully vetted end-to-end):** the engine marks GTG complete on load by RE-EVALUATING the kill
    objective against the persisted dead actors with **engine-internal (C++) logic**, not via a persisted
    completion field and not via readable Obscript. The save contains the INPUT — 6 dead gangers, all bindable to
    scripted ganger bases — but neither the completion STATE nor the re-derivation RULE-at-load is in readable
    save+masters. **Static recovery is therefore possible only by REPLICATING the masters rule** (count dead
    ganger-script actors ≥ 6 → complete), which needs (1) re-adding the placed-ACHR base scan + a created-actor
    change-form decode (created ref → template ACHR → base NPC_ → SCRI) to bind the spawned 6th, and (2)
    re-enabling the counter pass gated to running. That's a sizable, somewhat fragile build touching the
    precision-critical path; proposed, not yet shipped (single-kill win stands). This also clarifies the earlier
    "reload re-derives from save data alone" note: TRUE (it re-derives from the dead gangers) but the rule is
    engine-internal, so a static evaluator must reconstruct it rather than read a stored flag.
    **PROGRESS 2026-06-24 (cont.) — SHIPPED: counter-gated completion incl. the runtime-SPAWNED ganger. Ghost Town
    Gunfight now flips to COMPLETED on `gtg-complete` (and stays ACTIVE on `gtg-active`), with ZERO oracle
    regression.** Built the static replication of the engine's re-derivation:
    - **`TesPlugin` re-adds the placed-ACHR scan** (`CELL`/`WRLD` descent → `PlacedActorBases`: placed ACHR →
      base NPC_), threaded through `PluginDatabase` (re-keyed). Needed to resolve a spawned actor's template.
    - **`FalloutSave.CreatedReferenceForms()`** enumerates runtime-created (`0xFF`) change forms and the FormIDs
      their data embeds (taking the last 3 bytes of each `0x7C`-delimited token, which recovers the template refID
      at the tail of the un-delimited MOVE block).
    - **`QuestPipboy` counter pass (re-enabled, gated to running):** for a counter-gate, count actors running the
      increment script that are dead = (a) dead registry **bases** (the 5 unique gangers) + (b) **created
      references** whose embedded template resolves (placed ACHR → base → script) to a ganger base (the 1 spawned
      ganger: `0xFF001334` → ACHR `0x00104C75` → NPC_ `0x00104C74` `GSPGHM` → script `0x00105D4D`). The two kinds
      are disjoint (a spawned ganger isn't a registry base), so they sum without double-count → **6 ≥ 6 →
      completed**. On `gtg-active` both are 0 → stays active. Single-kill quests stay on their own (non-counter) path.
    - **Validation:** `gtg-active` GTG ACTIVE, `gtg-complete` GTG COMPLETED; **Save 57 = 7/7, Save 122 = 16,
      Save 420 = 34, all FP counts unchanged** (the counter pass fires on nothing spurious — GTG isn't running on
      the modded oracles, and no other counter-gate's actors reach threshold). `counterderive` reports
      `derived=6 (5 placed + 1 spawned)`. 2 synthetic tests pin it (2 dead ganger-NPCs → counter-quest completed;
      1 dead → stays active). Cost: the worldspace ACHR scan adds ~1–2 s to `pipboy` (acceptable). **Net: GTG —
      the canonical bucket-C kill-count quest — is now recovered end-to-end by replicating the masters rule against
      the persisted dead actors, exactly what the engine does internally at load.**
    **CORPUS SPOT-CHECK 2026-06-24 (cont.) — the GTG counter completion is validated across the FULL VNV-Extended
    Courier playthrough (485 saves), not just the controlled pair.** Tracing Ghost Town Gunfight through the early
    Courier saves: Save 20–21 (Goodsprings/Saloon) not-yet-started; Save 22–23 (Gas Station, pre-fight) **active**,
    `derived=0`; **Save 24 (post-fight) → completed, `derived=7 (6 placed + 1 spawned)`**; Saves 25/26/28/122/420
    all completed (Save 28 `derived=9 (6 placed + 3 spawned)`). So GTG flips active→completed at exactly the save
    the fight ends and stays completed for the rest of the run — with **no false completion** on the pre-fight saves
    (`derived=0` → stays active). This also exercised two things the gtg pair never did: the **VNV mod bumps the
    threshold to 7** (the masters `>= N` is read per-load-order, so it's correct automatically) and **multiple
    spawned gangers** (1→3 created references), all bound via the created-reference→template→base chain. Oracle
    cross-check of the kill-reachable quests vs ground truth: **I Fought the Law completed correctly on Save 122 AND
    420; Return to Sender completed on 420; GTG completed on 122/420** (it also completes via the CTDA pass once a
    later GTG-referencing line is said — the counter pass uniquely covers the post-fight window before that). Still
    on the miss/mislabel tail (documented): Booted (counter quest, not surfaced/running → can't complete), How Little
    We Know / Crazy Crazy Crazy (not surfaced), Ring-a-Ding-Ding! / That Lucky Old Sun (surfaced active, their
    completing actor isn't in the death registry — cleaned up / different mechanism).
    **PROGRESS 2026-06-24 (cont.) — chased the two Save-420 kill-tail mislabels; one recovered, one walled.** `qfired`
    showed neither is actually kill-completed (the ViaKill detection was a red herring — VMS03's "OnDeath" is a
    conditional fail/complete edge case, VMQTops has none):
    - **Ring-a-Ding-Ding! (VMQTops) RECOVERED (Save 420 34→35, 0 FP):** a SAID INFO conditionally `SetStage VMQTops
      80` (its `QuestCompleted` stage) — present in the save, so the line fired, but the dialogue seed only applied
      NON-conditional SetStage, leaving the SGE quest stuck at its startup stage 5 ("Search the Strip…"). New
      precision-safe pass: a said-INFO's **conditional `SetStage` to a COMPLETING (QSDT-0x01) stage** of an
      ALREADY-RUNNING quest reclassifies it active→completed (no add ⇒ no new FP; distinct from the dropped B2 lever
      which applied conditional SetStage as a START and surfaced wrong quests). A completion line is conditional only
      on the player's chosen path, which — having said it — they're on. **Results: Save 57 = 7/7, Save 122 = 16
      (Ring-a-Ding-Ding! correctly stays not-surfaced there — its completion line wasn't said yet), Save 420 = 35
      (mislabelled 13→12), FP unchanged.** 2 synthetic tests pin it (conditional SetStage→complete completes a
      running quest; →non-completing stage does not).
    - **That Lucky Old Sun (VMS03) — initially called WALLED, then RECOVERED (the "walled" call was wrong; the user
      refused it).** HELIOS One completes by **activating the reflector console** — no kill, no said completion line,
      no QUST change form (its only change form is a REFR/alias stub). BUT the activation has permanent world
      effects: the completion script (`VHeliosArchimedesSCRIPT`, OnActivate `→ CompleteQuest VMS03`) **`Enable`s the
      HELIOS power FX refs** in a GameMode sequence, and an enabled ref PERSISTS in the save. Controlled-diff
      confirmed it (Save 122 HELIOS-not-done vs Save 420 done): `FXHeliosCollector1REF` (0x000A5AAB) and the god-ray
      refs gain a `CHANGE_FORM_FLAGS` change form whose new flags clear the `0x800` Initially-Disabled bit = enabled.
      **SHIPPED the activator/world-state-completion mechanism:** `FalloutSave.EnabledReferences()` (refs with a
      `0x800`-clearing FORM_FLAGS change / enable marker); `QuestScript.ParseEnableRefs` + `TesPlugin` harvests, per
      script that calls `CompleteQuest Q`, the `<Ref>.Enable` editor ids → Q's completion-enable refs, and a
      worldspace REFR-`EDID`→FormID scan resolves them; `PluginDatabase.CompletionEnableRefs` (quest → ref FormIDs);
      `QuestPipboy` completes a RUNNING quest when one of its completion-enable refs is enabled in the save (gated to
      running ⇒ reclassify-only, no add ⇒ no new FP). **Results: Save 420 35→36 (mislabelled 12→11), Save 57 = 7/7,
      Save 122 = 16, FP unchanged; gtg pair correct; pre-HELIOS saves keep VMS03 active (no false completion).** Cost:
      the REFR-EDID worldspace scan adds ~1 s to `pipboy`. Diagnostics: CLI `refenabled`, `qenable`. 2 unit tests pin
      the parser + the enabled-ref detector (full chain validated on the Save-420 ground truth). **Net: the
      activator-gated class is NOT unrecoverable — the completion's world-state side effects are the readable signal.**

    ### §6 #16 SYNTHESIS — the generalizable completion-evaluator pattern (READ THIS before declaring any quest "walled")

    Every recovery in this section turned out to be the SAME shape, and several were wrongly called "impossible"
    first. **The governing fact: the engine reconstructs the Pip-Boy from the save + masters ALONE at load — so a
    readable signal ALWAYS exists. The default belief must be "find it," not "it can't be done." Exhaust the search
    (and do a controlled diff: a save just-before vs just-after the event) before ever calling something walled.**

    **The principle:** a quest's state = the fixpoint of every script effect (`CompleteQuest` / `SetStage`-to-
    complete / `SetObjectiveCompleted` / `StartQuest`) whose **trigger fired** (visible as a persisted save trace)
    AND whose **guard holds** (against persisted state). The engine runs this live; we REPLICATE it from readable
    SCTX source + persisted traces — NO script interpretation needed. The only thing that varies per quest is the
    **trigger type**, and each leaves a specific kind of save trace:

    | Trigger (read from the script's `Begin <block>`) | Save signal we read | Shipped handler |
    |---|---|---|
    | `OnDeath` / kill | GlobalData type-2 death registry + created-ref corpse + ACHR lifestate | single-kill + counter passes (`DeadReferences`, `CreatedReferenceForms`, `ActorScripts`/`PlacedActorBases`) |
    | `OnActivate` / GameMode `.Enable` | enabled-ref state (FORM_FLAGS clears 0x800 / enable marker) | `EnabledReferences` + `CompletionEnableRefs` |
    | dialogue (INFO result script) | the INFO present as a change form (it was SAID) | dialogue seed + CTDA-on-said-INFO + conditional-SetStage-to-complete |
    | GameMode counter guard (`counter >= N`) | count of the persisted trigger events | `CounterGates` + the counter pass |
    | stage / objective set, SGE startup | QUST change form / formType-7 / masters SGE defaults | `QuestLog`, formType-7 seed, `ScriptStartup` |

    **What we have:** the META-pattern (reliable) + the ~6 handlers above. Each pass is precision-gated to
    ALREADY-RUNNING quests (reclassify-only ⇒ no added entry ⇒ no new FP), validated at every step against the 3
    oracles + the gtg/HELIOS controlled diffs.
    **What is NOT yet built (the path to making this automatic):**
    1. **A unified `CompletionRule` catalog** — today each pass independently scrapes its own effects (`CounterGates`,
       `CompletionEnableRefs`, dialogue effects, `ExternalQuestEffects`). They should be ONE per-quest list of
       `{Verb, TargetQuest, Stage/Obj, TriggerKind, Binding(actor/ref/info FormIds), Guard}`, harvested once.
    2. **More trigger types** (`OnHit`/`OnTrigger`/timers/package-done) — each NEW one needs a controlled diff first
       to LEARN its save trace, then a handler; once added it applies to ALL quests with that trigger.
    3. **A general guard/condition evaluator over the decoded save** (`GetDead`/`GetStage`/`GetObjectiveCompleted`/
       `GetItemCount`/globals/quest-vars) — the hard, FP-prone, inherently-partial piece. We currently SIDESTEP guards
       (the "line was said ⇒ on that path" proxy in the conditional-dialogue pass) or handle them ad hoc (counter
       thresholds, CTDA functions). A real evaluator would unlock many conditional effects at once.
    **Target architecture:** `CompletionRule` catalog → `SaveSignalEvaluator.TriggerFired(rule, save, db)` (dispatch
    on TriggerKind — we already have most of these checks, just scattered) → `GuardEvaluator.Holds(guard, save, db)`
    (the new build) → a fixpoint applying fired-and-held rules, gated to running.
    **Hard constraint:** validation is capped at 3 ground-truth oracles (Saves 57/122/420) + controlled pairs; a
    general framework needs MORE in-game Pip-Boy screenshots to validate broadly without sneaking in false positives.
    **Net score this phase:** Save 57 = 7/7, Save 122 = 16/24, Save 420 = 36/68, ~94% precision — and the "walled"
    classes (spawned-kill, conditional-dialogue, activator/world-state) are all now recoverable.

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
pattern + name the containing record), `irefscan <off> <len>` (resolve iref+count sites), two `diff`
modes: `diff a b cf` annotates each differing run with the change form that contains it, and
`idiff a b [clean]` aligns records by FormID across an insertion (drop/pickup) to surface the exact data change —
`clean` auto-hides the recurring per-reference game-time/havok churn (§4k.1 #7), e.g. 3,314 → 11 records on a notes diff;
and `fdiff a b [delta]` — a **float-aware** aligned diff that reads the full 4 bytes at every offset of each
same-length record and reports float32 fields that changed by ≈`delta` (a float change touches only its high
bytes, so it never shows as a clean byte-run delta in `diff`/`idiff`). `fdiff` is how karma/XP were found (§4j).

---

## 8. Reference sources
- `Nexus-Mods/node-gamebryo-savegames` — C++ parser (FO3/FNV/FO4/Skyrim). **Header-only**: stops at the
  plugin list, does not decode the body — the change-form/inventory format here was reverse-engineered locally.
- Vault-Tec Labs "FOS file format" (falloutmods wiki) — header + stats tables.
- UESP "Oblivion / Skyrim Save File Format" — change-record / FormID-array model NV mirrors. **The Skyrim pages
  are the best structural guide for the still-undecoded FNV body** and **validated much of our local RE** (see §8a):
  `Skyrim_Mod:Save_File_Format` (RefID 2-bit type; global-data type list; change-form header), `Skyrim_Mod:ChangeFlags`
  (every REFR/ACHR/QUST changeFlag bit + the REFR field-order layout + the per-item extra-data type→size catalog),
  `.../REFR_Changeform`, `.../QUST_Changeform`. **Fetch (UESP 403s WebFetch):** `curl` with a browser User-Agent on
  `https://en.uesp.net/w/index.php?title=<Page>&action=raw` returns raw wikitext (confirmed 2026-06).
- **Game ESM/ESP master files** (`<game>/Data/*.esm`) — the source for FormID → display name (§6.3). On
  this machine: `C:\Games\Steam\steamapps\common\Fallout New Vegas\Data` (`FalloutNV.esm` + DLC esms).
  Standard Bethesda TES4 plugin format; FNV stores `FULL` names **inline** (no `.STRINGS` localization
  files), so names are readable directly. UESP "Mod File Format" (TES4/FO3) documents the record/GRUP/subrecord layout.
- FNVEdit + GECK — resolve FormIDs/irefs while reverse-engineering.
- Note: those wikis 403 the WebFetch tool on `/wiki/` URLs. Fandom's `api.php?action=parse` works; **UESP works via
  `curl` + a browser User-Agent on `/w/index.php?title=<Page>&action=raw`** (the Bash tool, not WebFetch).

---

## 8a. Cross-reference with the UESP Skyrim/Gamebryo spec — validated matches + leads

The Skyrim save spec (§8) shares the **Gamebryo/Creation change-form model** FNV uses. **Caveat:** Skyrim ≠ FNV
byte-for-byte — FNV is FO3-era (pervasive `0x7C` delimiters, different form-type numbering, smaller global-data set,
some saves zlib-compress the player ACHR while FNV doesn't), so **every item below is an FNV-corpus hypothesis to
verify**, not a drop-in. That said, several of our independently-RE'd findings line up exactly, which is strong
validation of the method:

**Confirmed (our local RE matches the spec):**
- **MOVE block = 27 bytes** = the spec's REFR "Initial type 4" (`RefId cell/world` + `float pos[3]` + `rot[3]`). (§4i)
- **vsval `>>2` counts** — the spec's per-item `extra count` and inventory `count` are vsval, "shift right two bits
  to get the count" = our `b/4` propCount and `ReadVsval`. (§4i)
- **Per-stack extra-data sizes** — the spec's extraData catalog gives `0x1C`→3, `0x24`→2, `0x25`→4 ("appears to be a
  float"), independently matching our corpus-aligned sizes (§4i) and confirming `0x25` is a float.
- **`CHANGE_REFR_HAVOK_MOVE` = bit 2** and **`CHANGE_REFR_MOVE` = bit 1**, **`CHANGE_REFR_INVENTORY` = bit 5** —
  exactly our `FlagBitLabels`. (§4f/§4i)
- **Inventory item = `refId, count, vsval extraCount, extraData[…]`** — exactly our stack model (§4g/§4i).
- **Read-note marker** — the spec lists `CHANGE_NOTE_READ = 0x80000000`, matching our §4k marker `changeFlags` exactly.

**Leads (verify against FNV, then graduate to the §6 items noted):**
- **changeFlags bit labels** for REFR/ACHR — the spec names every bit (bit4 Scale, bit7 BaseObject; ACHR bit10
  LifeState, bit11 PackageData, bit22 OverrideModifiers, bit28 Animation, bit29 EncounterZone, bit31 GameOnly). Lets
  us replace our "label, don't guess" placeholders with confirmed names. → §6 #13.
- **Havok data is length-prefixed** — the spec's REFR layout has `Havok data = vsval count + uint8[count]` present
  *iff* `CHANGE_REFR_HAVOK_MOVE`. FNV's delimited preamble (`E1 10 7C 04 7C 4C 7C` / `49 11 7C 05 7C 4C 7C`) doesn't
  trivially decode as that size, so it's not a free win — but it's a concrete hypothesis to test that could turn the
  §10 anchor scan into a deterministic skip. → §6 #12.
- **Ordered REFR/ACHR field model** — Initial(MOVE) → Havok(if bit2) → Flags(if bit0) → BaseObject(if bit7) →
  Scale(if bit4) → ExtraData(if extra bits) → Inventory(if bit5) → Animation(if bit28). A structural blueprint to
  decode the *whole* player record (and reach the item list with zero heuristics). → §6 #14.
- **QUST change form** — quest **stages** live under `CHANGE_QUEST_STAGES` (bit31) as `vsval count` of
  `{sint16 stage, uint8 done}`; **objectives** under `CHANGE_QUEST_OBJECTIVES` (bit29). Direct blueprint for the
  quest log. → §6 #10.
- **RefID 2-bit type** — top 2 bits of the 3-byte refID: 0 = formID-array index (value−1, our `+1` rule), 1 = base
  ESM formID directly, 2 = created (0xFF), 3 = ?. A framework for refIDs that currently don't resolve. → §6 #15.
- **Global-data type labels** — spec types 5=Effects, 7=Audio, 8=SkyCells (FNV table 1 has types 0–11; 0–6 likely
  shared, 7–11 need FNV verification — FNV's set differs from Skyrim's 0–8 + 100+). → refines §4c.
- **More extra-data types to name** — the spec catalog labels several we haven't: `0x2a`=lock (level + KEYM refId),
  `0x70`=encounter-zone refId, `0x88`=QUST alias assignment, `0x8e`=outfit refId, `0x21`="could be owner". Candidate
  semantics for our sized-but-unlabelled types (§10).

## 9. Known limitations / risks
- Change-form **internals**: skills (§4e), inventory item stacks (§4g), and per-stack extra data —
  condition (`0x25`, editable), equipped (`0x16`), and the `0x21` ref (§4i) — are decoded; perks and most
  other per-record state are not yet decoded — needs more controlled diffs. The walker (§4f) makes these
  reachable record-by-record.
- The inventory walk is **deterministic** per-stack (exact extra-data lengths; no window — §4i) **and the list
  start is deterministic on all 607 real saves** (vanilla 30/30, base VNV 98/98, VNV Extended 479/479): MOVE skip
  → sized/anchored ExtraDataList → the `vsval` stack count → first item. The §4g scan is retained only as a
  never-needed safety net. The residual caveats (§10) are about *internal* decode, not the list start.
- The bit2/bit10 (`CHANGE_REFR_HAVOK_MOVE`) pre-list region is **located past, not byte-decoded**: it's a
  variable-length Havok physics blob (§4i), and the list is found by the self-validating ExtraDataList-header
  anchor instead of by sizing the blob. Exact blob decode is a logged follow-up; the list is correct without it.
- The **`vsval` reveals a benign over-read** it doesn't fully eliminate: on a few saves the decoder reads slightly
  *more* stacks than the engine's count (interspersed non-item over-reads the name filter already hides; **0
  under-reads across all 607** — never *fewer*). Sizing **every** per-stack property type (§4i — incl. the structured
  `0x0D`) **reduced** this with each fix strictly monotone: vanilla over-read 2 → **0**, base VNV 8 → 4, Extended
  318 → 314 (every changed save decreased; **0 became under-read**). The residual over-read on Extended comes from the
  bit2/bit10 havok-blob anchor path, **not** per-stack sizing (all per-stack types are now sized). Core keeps the full
  chain (truncating by position would drop real trailing items); dropping the rest needs the masters (CLI/GUI).
- The `0x21`/`0x1C`/`0x24`/`0x30`/`0x6E`/`0x0D` per-stack extra-data types are all **sized** (lengths right, walk fully
  deterministic — the ≤512 B resync is now a never-hit safety net) but their **semantics** aren't pinned: `0x21` is an
  attached weapon mod on weapons (reused for other linked refs, a VNV "Bill of Sale"); the rest (incl. `0x0D`'s
  `ref` + `(u32,f64)` pairs) were sized structurally by corpus alignment (§4i).
- Inventory editing targets the **first** stack of a given FormID; duplicate-FormID stacks (same item,
  different extra data) can't be disambiguated by FormID alone yet.
- SPECIAL locator relies on the player-name field appearing in the player base record (held on all 16
  saves); a save lacking it would return null (handled gracefully).
- Only **same-length** edits are supported by design (see §1). Length-changing edits are unsafe until
  full offset-fixup is implemented.
- Read notes (§4k) capture only notes the player has **opened** (each leaves a change-form marker); the **full**
  Pip-Boy list incl. **unread** notes is now decoded via `PipBoyNotes` (§4k.1 #4 — note refs in the player inventory
  record ∪ read markers). Two soft caveats: it needs the masters to tell which references are `NOTE` records (no
  masters → read markers only), and the ref-list's exact byte framing isn't individually parsed (the scan finds note
  refs directly, robust to framing) — neither affects the verified results.
- `findplayer`'s refID scan can report false positives in data; the player records are confirmed via
  the SPECIAL/name anchor, which is the reliable locator.

---

## 10. Accepted caveats (good enough now — fully fixable later)

Approximations we **deliberately ship** because they're verified-correct on every real save today, each with
a clear path to a fully-principled fix once more of the body is decoded. These aren't bugs or risks (those
live in §9) — they're "good enough, revisit when the RE catches up." **The throughline:** a `.fos` body is
deterministic engine output, so *every* one of these becomes exact once we decode enough of the surrounding
structure. None is a fundamental wall.

| Caveat | Why it's good enough today | The full fix (and what unblocks it) |
|---|---|---|
| **bit2/bit10 havok blob is located past, not byte-decoded** (§4i; VNV Extended only, 113 records). The list start is deterministic on all 607 saves, but on `CHANGE_REFR_HAVOK_MOVE` records the pre-list physics blob is skipped via the self-validating ExtraDataList-header anchor rather than by sizing the blob. | The anchor + "pick the real (longest) chain" rule lands the list correctly (and *fixed* 35 endgame saves that previously decoded to empty); the blob's bytes aren't needed to find the list. **Grammar now confirmed** (§4i: 7-byte preamble + 58-byte `pos/quat/03/vel/angvel` entries + truncated final + trailing variable `[4][7C]` slot array). | A full structural sizer is **intentionally not built**: the blob is genuinely variable-length with a truncated final entry, and its trailing slot array's values locally collide with the ExtraDataList header — so a sizer would *still* need the anchor's self-validation at the tail (zero correctness gain over the working anchor). `ReferenceChangeForm.HavokPhysicsEntryLength` (test-pinned) recognises one entry for any future exact decode. |
| **`vsval` over-read** — the decoder reads *more* stacks than the engine's `vsval` count (interspersed non-items the name filter hides); the full chain is kept rather than truncated (§9). **Measured: under-read 0 across all 607** (never drops items), benign over-read on some — now **reduced** by per-stack property sizing (§4i): 11 modded saves dropped phantom stacks, several to `vsval` exactly. | The extra stacks are hidden in display, and truncating by position would drop real trailing items. | Drop the residual `0x0D`-block over-reads — either by sizing `0x0D`, or using the `vsval` as the authoritative count (the name filter for that lives in the CLI/GUI, not `Core`; surface the vsval there as the cross-check). |
| **Inventory edits target the *first* stack** of a given FormID (§4g). | Duplicate-FormID stacks (same item, different extra data) are uncommon; the everyday case is unambiguous. | Address stacks by file offset / extra-data signature rather than FormID — straightforward once a UI/CLI affordance picks the specific stack. |
| **Per-stack extra-data semantics unpinned** — `0x21`/`0x1C`/`0x24`/`0x30`/`0x6E`/`0x0D` are all **sized** (by corpus alignment, §4i) but **unlabelled**. | Lengths are right so the per-stack walk is **fully deterministic** (every type sized; the ≤512 B resync is a never-hit safety net); sizes pinned across 607 saves (e.g. `0x6E` gap 2 on 929/929; `0x0D` = `12 + 14·(n/4)`). | Controlled diffs (attach a known weapon mod; inspect a modded weapon) to *name* each sized type — e.g. confirm what `0x0D`'s `(u32, f64)` pairs are (script vars? effects?). |
| **Skills are sparse** (only modified entries stored) and the absolute-vs-modifier semantics of small natural entries aren't pinned (§4e). | Reads/edits exactly what's stored; SPECIAL/skill sums verified across all 16 saves. | A single +3 skill-book controlled diff to confirm modifier vs absolute, then enumerate the full 13 from base + perks + tags. |

> **Is the inventory start *fully* deterministic now?** **Yes — on all 607 real saves** (vanilla 30/30, base VNV
> 98/98, VNV Extended 479/479). The vanilla path is a pure structural walk (MOVE + havok array + sized
> ExtraDataList + `vsval` anchor); modded saves use the same typed-entry walk (variable order + `0x1D`/`0x75`) plus
> a bounded post-entry resync; and bit2/bit10 records — whose pre-list region is a variable-length Havok physics
> blob, not a sized array — are located by the self-validating ExtraDataList-header anchor (choosing the real
> longest chain over the §4g scan). The §4g scan is now an unused safety net. The only thing not yet *byte*-decoded
> is the physics blob itself, which the list doesn't need. Verified **0 under-reads** and display **byte-identical**
> across all 607 except **35 strict corrections** (endgame inventories that previously decoded to empty).
