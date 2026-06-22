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
`setcondition`, `names`, `setlevel`, `caps`, `setcaps`, `karma`, `xp`, `setkarma`, `setxp`, `diff`, plus R&D
helpers `walk`, `refdump`, `edlscan`, `invsig`, `idiff`, `fdiff`, `find`, `irefscan`.
Run with no args to list them. (`edlscan <dir>` aggregates the modded ExtraDataList grammar + a deterministic-path
tally across a save folder; `invsig <dir>` prints a per-save decoded-inventory signature for byte-identical-decode checks — §4i.)

---

## 3. Architecture & key files

- **`src/FnvSaveExplorer.Core`** (`net10.0`, UI-agnostic)
  - `FalloutSave.cs` — parser, retention writer, all decoders + same-length editors, change-form walker.
  - `ByteReader.cs` — little-endian cursor; throws `SaveFormatException` with the failing offset.
  - `FileLocationTable.cs`, `GlobalData.cs`, `MiscStats.cs`, `PlayerSpecial.cs`, `PlayerSkills.cs`, `PlayerInventory.cs`, `SaveScreenshot.cs`.
  - `ReferenceChangeForm.cs` — reference (REFR/ACHR) change-form helpers: the `0x7C` field tokenizer, `changeFlags` describer, the per-stack extra-data catalog/decoder (`TryReadStackExtra`) behind the deterministic inventory walk, and the generalised typed-entry ExtraDataList walk (`WalkExtraDataList`/`ExtraEntryLength` — modded-grammar RE, inspection-only) (§4i).
  - `TesPlugin.cs`, `PluginDatabase.cs`, `GameDataLocator.cs` — FormID → display-name resolution from the game's ESM/ESP masters (§4h / §6.3).
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
- `0x25` **ExtraCondition** — 4-byte LE float (weapon/armor health). The repair moved exactly this float
  `52.5 → 67.5`; it appears only on degradable gear. **Editable** as a same-length splice (`TrySetItemCondition`).
- `0x16` **ExtraEquipped** — 0-byte flag; its presence means the stack is equipped/worn. It *appeared* on the
  pistol when equipped (Save 31→32), and is present on the always-worn Pip-Boy / worn armor.
- `0x21` — a 3-byte BE refID. On a weapon this is an attached **weapon mod**; the type is reused for other
  linked refs (a VNV "Bill of Sale" note appears on a consumable), so the general semantics aren't pinned.
- `0x0D` / `0x18` / `0x24` / `0x6E` — longer/structured or mod-added; payload length not yet pinned. When the
  walk hits an un-sized type it falls back to a **bounded 512-byte resync** to the next valid stack (rare;
  modded weapons only) — far tighter than the old window, and the list stays contiguous.

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
   blob.** Late-game records set `changeFlags` bit2 (`CHANGE_REFR_HAVOK_MOVE`) and/or bit10, and the region
   between MOVE and the ExtraDataList is then **active physics state**, not the vanilla 232-slot `[u32][7C]` array
   (the "~214 slots" first guess was wrong). Its serialization is a preamble (`[E1 10][7C][04][7C][4C][7C]`-ish)
   then ~58-byte entries `[pos:3×f32][7C][quat:4×f32][7C][03][7C][vel:3×f32][7C][angvel:3×f32][7C]` with a
   per-entry type byte (`03` full / `02` truncated) and a truncated final entry — so it can't be byte-sized by a
   fixed stride. Rather than decode the physics, `FalloutSave.ScanForExtraDataListAnchor` locates the list by the
   **first ExtraDataList header that self-validates** (typed entries → sane vsval → real stack chain), and
   `WalkInventory` chooses the **real (longest) chain** between that anchor and the §4g scan (a 2× gap separates a
   genuine 180–214-stack endgame list from the short coincidental chains either locator can otherwise latch onto;
   neither locator alone suffices — the anchor finds lists the scan misses, and the scan finds the bit10 lists the
   anchor has no header for). **Closed — and it fixed 35 Extended endgame inventories that previously decoded to
   empty** (the §4g scan had been returning name-unresolvable garbage from the havok blob, hidden by the name
   filter). The blob's exact byte decode is a logged follow-up (not needed for the list).

**Remaining per-stack follow-up (not the list start):** beyond `0x25/0x16/0x21` the per-stack catalog still lacks
`0x0D` (×3120), `0x24` (×864), `0x6E` (×858 — modded weapons), `0x1C` (×68), `0x30` (×17) across the 479; these
force the ≤512 B per-stack resync but never lose items.

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
| Player inventory decode + edit (§4g) | ✅ located via PlayerRef iref+1; `[ref][7C][u32 count][7C]` entries with **ref = array index + 1**; same-length count edit round-trips; confirmed by a controlled diff (Saves 28/29/30: Antivenom 1→2→1) — every stack resolves, no spurious rows |
| Deterministic inventory walk + per-stack extra data (§4i) | ✅ 2048-byte window removed; extra-data catalog cracked by a controlled 3-save diff (31/32/33): `0x25`=condition float (**editable**, 52.5→67.5 on repair), `0x16`=equipped flag, `0x21`=ref (weapon mod). Save 31 = 36 stacks, VNV = 103 stacks/12,999 items; condition edit round-trips |
| FormID → display name (§4h / §6.3) | ✅ custom TES4 reader over the ESM/ESP masters; 10/10 plugins of a real save parse, 3,985 named forms; DLC renumbering + compressed records handled; inventory CLI + GUI show names + source mod (friendly name) |
| Mod Organizer 2 / modded saves (§4h) | ✅ auto-detects the MO2 `mods\` folder from an MO2 save path; a 43-plugin Viva New Vegas save resolves 43/43; large fragmented inventories reunited (a dropped 1,414-count stack recovered) |
| Pip-Boy item category / tab (§4h) | ✅ from the base form's record type (read from the masters, not the save): `RecordType`/`Category`/`PipBoyTab`; verified in-game (WEAP/ARMO/AMMO; ALCH+BOOK→Aid; KEYM→Misc/"Keyring"; NOTE→Data) |
| Caps decode + edit (§6.4) | ✅ caps are an inventory stack (FormID `0x0000000F`); `Caps`/`TrySetCaps` wrap the inventory path; CLI `caps`/`setcaps` + GUI Edit field; same-length edit round-trips |
| Karma + XP decode + edit (§4j) | ✅ two float32 actor-values in the player reference record (slot 100 = karma, slot 101 = XP), cracked via the new `fdiff` float-aware diff on controlled pairs (XP `10→60→110`, karma `0→100→200`) + confirmed on a 2nd character; `Karma`/`Xp` + `TrySetKarma`/`TrySetXp`; CLI `karma`/`xp`/`setkarma`/`setxp` + GUI; same-length float edit round-trips |
| WPF GUI (metadata, screenshot, plugins, stats, SPECIAL + skills + inventory + caps + karma/XP edit) | ✅ launches + builds |
| `diff` tool (pinpoints same-size changes) | ✅ Strength 5→6 = 1 byte; `cf` mode names the containing change form; `idiff` aligns records across an insertion |
| Tests | ✅ 457 xUnit, all green |
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
> type bytes + a truncated final entry) is partly RE'd in §4i for a future exact decode. Per-stack types
> `0x0D/0x24/0x6E/0x1C/0x30` are still unsized (the ≤512 B per-stack resync handles them).

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
5. **Notes / message log (Pip-Boy "Data → Notes")** — the collected notes/holotapes shown under Pip-Boy
   *Data → Notes* are **not inventory items**. The player's inventory change form (iref = PlayerRef+1)
   holds at most a stray carried holotape: a 40 h VNV save (Save 335) decodes only **one** `NOTE` stack
   while the Pip-Boy lists *many* (most marked viewed). So the notes log is a **separate, undecoded
   structure** — a different change form or a global-data table, keyed by note FormID. **Findings so far:**
   a known note is "Message: Khan Hospitality" (FormID `0x000CCFCB`, a `NOTE` record) present in Save 335;
   each entry carries a **viewed/unviewed** flag (dimmed vs bold in-game). **Ready-made controlled pair**
   for that toggle: in Save 335 "Message: Khan Hospitality" is *viewed* and the next entry "They Didn't
   Shoot The Deputy" is *unviewed* — diff/inspect to pin the flag (same method as the equipped flag, §4i).
   **Lead:** `find` a note's FormID/iref *outside* the inventory record to locate the structure. (Raised
   out of scope; logged for future us.)
6. ~~**General change-form record header**~~ — ✅ **DONE** (§4f). Walker (`EnumerateChangeForms`) reproduces
   all records exactly; CLI `walk` validates. Enables a future full change-form browser.
7. **Length-changing edits** (arbitrary rename, add/remove plugins, add/remove items) — requires rewriting every
   absolute offset in the File Location Table (and any internal absolute offsets). Deferred.
8. **Quick win (no new saves):** label the 43 Misc Stat indices by name (diff an early vs late save
   of the same character) so the GUI reads "Quests Completed: 4" instead of "[0]: 4".
9. **GUI/UX polish:** screenshot export (PNG), a raw hex viewer tab, backup management.

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
`idiff a b` aligns records by FormID across an insertion (drop/pickup) to surface the exact data change;
and `fdiff a b [delta]` — a **float-aware** aligned diff that reads the full 4 bytes at every offset of each
same-length record and reports float32 fields that changed by ≈`delta` (a float change touches only its high
bytes, so it never shows as a clean byte-run delta in `diff`/`idiff`). `fdiff` is how karma/XP were found (§4j).

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
- The **`vsval` reveals a benign over-read** it doesn't fix: on a few saves the decoder reads slightly *more*
  stacks than the engine's count (interspersed non-item over-reads the name filter already hides; **0 under-reads
  across all 607** — never *fewer*). Core keeps the full chain (truncating by position would drop real trailing
  items); dropping exactly the non-items needs the masters, which live in the CLI/GUI, not `Core`.
- The `0x21` extra-data type is decoded as a 3-byte ref (length is right) but its **semantics** aren't
  pinned — an attached weapon mod on weapons, but reused for other linked refs (a VNV "Bill of Sale"); a
  few structured/mod-added types (`0x0D`/`0x18`/`0x24`/`0x6E`) aren't sized, so the walk resyncs (≤512 B).
- Inventory editing targets the **first** stack of a given FormID; duplicate-FormID stacks (same item,
  different extra data) can't be disambiguated by FormID alone yet.
- SPECIAL locator relies on the player-name field appearing in the player base record (held on all 16
  saves); a save lacking it would return null (handled gracefully).
- Only **same-length** edits are supported by design (see §1). Length-changing edits are unsafe until
  full offset-fixup is implemented.
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
| **bit2/bit10 havok blob is located past, not byte-decoded** (§4i). The list start is deterministic on all 607 saves, but on `CHANGE_REFR_HAVOK_MOVE` records the pre-list physics blob is skipped via the self-validating ExtraDataList-header anchor rather than by sizing the blob. | The anchor + "pick the real (longest) chain" rule lands the list correctly (and *fixed* 35 endgame saves that previously decoded to empty); the blob's bytes aren't needed to find the list. | Byte-decode the blob's serialization (preamble + ~58-byte pos/quat/vel/angvel entries with `02`/`03` type bytes + truncated final entry) for a fully structural skip; `refdump` + the §4i notes have the layout. |
| **`vsval` over-read** — the decoder reads *more* stacks than the engine's `vsval` count (interspersed non-items the name filter hides); the full chain is kept rather than truncated (§9). **Measured: under-read 0 across all 607** (never drops items), benign over-read on some. | The extra stacks are hidden in display, and truncating by position would drop real trailing items. | Drop exactly the non-item over-reads using the `vsval` as the authoritative count — but the name filter lives in the CLI/GUI (`PluginDatabase`), not `Core`; surface the vsval there as the cross-check. |
| **Inventory edits target the *first* stack** of a given FormID (§4g). | Duplicate-FormID stacks (same item, different extra data) are uncommon; the everyday case is unambiguous. | Address stacks by file offset / extra-data signature rather than FormID — straightforward once a UI/CLI affordance picks the specific stack. |
| **`0x21` extra-data semantics unpinned**, and types `0x0D`/`0x18`/`0x24`/`0x6E` aren't sized → ≤512 B resync (§4i). | Lengths that matter are right, so the per-stack walk stays deterministic; only modded weapons hit the resync, which stays tight. | Controlled diffs (attach a known weapon mod; inspect a modded weapon) to pin each type's payload length + meaning, extending the `TryReadStackExtra` catalog. |
| **Skills are sparse** (only modified entries stored) and the absolute-vs-modifier semantics of small natural entries aren't pinned (§4e). | Reads/edits exactly what's stored; SPECIAL/skill sums verified across all 16 saves. | A single +3 skill-book controlled diff to confirm modifier vs absolute, then enumerate the full 13 from base + perks + tags. |

> **Is the inventory start *fully* deterministic now?** **Yes — on all 607 real saves** (vanilla 30/30, base VNV
> 98/98, VNV Extended 479/479). The vanilla path is a pure structural walk (MOVE + havok array + sized
> ExtraDataList + `vsval` anchor); modded saves use the same typed-entry walk (variable order + `0x1D`/`0x75`) plus
> a bounded post-entry resync; and bit2/bit10 records — whose pre-list region is a variable-length Havok physics
> blob, not a sized array — are located by the self-validating ExtraDataList-header anchor (choosing the real
> longest chain over the §4g scan). The §4g scan is now an unused safety net. The only thing not yet *byte*-decoded
> is the physics blob itself, which the list doesn't need. Verified **0 under-reads** and display **byte-identical**
> across all 607 except **35 strict corrections** (endgame inventories that previously decoded to empty).
