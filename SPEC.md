# SPEC.md — the validated `.fos` byte-truth + decode-coverage map

The authoritative reference for the Fallout: New Vegas `.fos` save format **as reverse-engineered and
verified against the real-save corpus** (vanilla + base VNV + VNV Extended; 600+ saves). This is the
backbone for the project's primary objective — *fully decoding the binary into labeled fields* (see
[ROADMAP.md](ROADMAP.md) §1). Every claim here is corpus-validated; not-yet-understood regions are
called out explicitly as gaps rather than guessed.

- **Method.** *Structure* (where each field is, its size/type) comes from **corpus alignment**;
  *semantics* (what a field means) comes from **controlled diffs** (ROADMAP §7). A region can be
  structurally located ("a 1-byte enum here") before its meaning is pinned.
- **Decode-coverage convention.** As the full-decode work proceeds, each save section / change-form
  type records what is resolved vs left as `unknown[n bytes]`. The skeleton (header iteration, every
  change-form header, refID→FormID resolution) is fully solved on the corpus; the open frontier is the
  per-type *payload* internals (most change-form types beyond the REFR/ACHR inventory path).
- **Section numbering** is preserved from its original home in ROADMAP (§4, §8, §8a, §9, §10) so the
  many `§4i`/`§8a`/`§10` cross-references already in the code comments and docs still resolve here.
- For the user-facing summary see [README.md](README.md); for ruled-out approaches see
  [docs/DECISIONS.md](docs/DECISIONS.md).

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
Because these five offsets are *absolute*, inserting/removing body bytes shifts every section after the edit.
`FalloutSave.RebuildWithBodyEdits` makes that safe: it applies a set of `BodySplice`s (insert/remove in original-file
coordinates) and recomputes the five offsets — each shifts by the summed net delta of the splices before it (strictly
before for a *prepend*, at-or-before for an *append*; this boundary rule is the whole subtlety) — plus the three
counts. The result re-parses cleanly: the change-form walker still lands exactly on `GlobalData3Offset` and the FormID
array re-parses with its new count. The no-op case (no splices) is byte-identical to the input. This lifts the
former same-length-only limit; the first consumer is add-reputation (§4o).

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
**1 → 2 → 1**; that entry's `ref` resolved to *Antivenom* only through the `- 1` index.
**Count sentinel `0xFFFFFFFF` (-1):** an equipped base/quest item that can't be dropped stores its count as
`0xFFFFFFFF` — observed on the starting **Pip-Boy 3000 + Pip-Boy Glove**, which sit as the first two stacks
(controlled saves q1/q2). It is a genuine single-item stack, so the decoder accepts it (the `count <= 1,000,000`
sane-count cap is only a misalignment heuristic and explicitly excludes `0xFFFFFFFF`), it counts as **1** toward
the item total (`InventoryItem.IsCountSentinel`), and the CLI shows it as `x-1`. Earlier corpora never surfaced
it because those saves were past the early state where the Pip-Boy leads the list. The `- 1` fix
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

> **Note:** only a few slots of this actor-value array are decoded so far (karma 100, XP 101, plus the skill
> modifiers below). The rest stay labelled as the undecoded havok/float array — more controlled diffs can
> graduate further slots (e.g. carry weight, action points) the same way.

> **Skill-book bonuses are MODIFIER floats in this same array (controlled diff, resolves §10).** `skillbook-pre →
> skillbook-science` (read the Big Book of Science, +3 Science) flipped exactly **one slot `0.0 → 3.0`** in this
> `[float32][7C]` array (the player **reference** record, iref = PlayerRef + 1) — `3.0` is the book's **+3
> contribution**, i.e. a **modifier**, not the absolute Science total (~15–40). So skill values are composed from a
> base + these per-skill modifier slots; a permanent book bonus is `+3` (or `+4` with Comprehension) added to the
> skill's slot. The change form's length grew (the book was consumed from inventory in the same record), which is
> why `fdiff` missed it — `fdiff` only compares **same-length** records, and this is a length-changed one. The
> sparse `0x7C`-tagged AV-mod list of **§4e is a *different* structure and did NOT change** (the read skill stayed
> `0` there), so book bonuses live in this dense slot array, not the §4e list. Also: the **"Books Read" Misc Stat**
> increments per book read.
>
> **Slot map now PINNED to absolute indices** (from the four sequential books `science → repair → guns →
> lockpick`). The skill modifiers are in the **same record + array as karma/XP** — the player reference record
> (iref = PlayerRef + 1), the standard post-MOVE array at **data + 28** that the §4j locator already uses
> (`PlayerStatSlotOffset`). Anchoring on karma (200.0 @ slot 100) and XP (547.0 @ slot 101), each skill-modifier
> slot is **`slot = AV-index + 77`**, verified byte-exact (guns save: slots 116/117/118 = Repair/Science/Guns =
> `3.0`, slot 113 Lockpick still `0` since it was read after):
>
> | Skill | AV idx | slot | Skill | AV idx | slot |
> |---|---|---|---|---|---|
> | Barter | 0x20 | 109 | Repair | 0x27 | 116 |
> | Energy Weapons | 0x22 | 111 | Science | 0x28 | 117 |
> | Explosives | 0x23 | 112 | Guns | 0x29 | 118 |
> | Lockpick | 0x24 | 113 | Sneak | 0x2A | 119 |
> | Medicine | 0x25 | 114 | Speech | 0x2B | 120 |
> | Melee Weapons | 0x26 | 115 | Survival | 0x2C | 121 |
> | | | | Unarmed | 0x2D | 122 |
>
> (`0x21` Big Guns = slot 110, unused in FNV → stays 0.) So the dense AV array's per-slot meaning is now decoded
> for karma (100), XP (101), and the full skill-modifier block (109–122) — each holding the **permanent modifier**
> (book/console bonus), `0` until modified. A skill *total* = base (SPECIAL + tag + level) + this modifier + perks;
> the array stores the modifier portion, so a "skills total" reader still needs the base composition.
>
> **This dense array is a *different* structure from the §4e sparse skills list.** In `skillbook-pre` the dense
> skill slots (114 Medicine, 115 Melee, 116 Repair, …) are **all `0.0`**, while the §4e `[count*4][7C]` +
> `[avIndex][7C][float][7C]` list reports Medicine 4 / Melee 6 / Repair 3 — so the two hold different values. The
> dense array is the **book/permanent-modifier** store (what skill-book reads write); what the §4e sparse list's
> values represent (a separate AV-deviation/cache, or temporary effects) is still unpinned and would need its own
> controlled diff (e.g. a `player.modav`/chem/implant change) to separate from this array. So the §4e `skills`
> command and this dense block are **not** two views of the same data; don't conflate them.

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

### 4l. Change-form payload decode-coverage map (per form type) — the full-decode frontier
The skeleton (§4f) walks every record and reads every header exactly. The open frontier is the per-**type**
*payload*. This map tracks, for each form-type byte (low 6 bits of the type byte), what is structurally resolved
vs left `unknown`. It is **regenerated/extended by the `survey` CLI** (ROADMAP §6 #1a): `survey <dir>` tabulates
per type the record count, payload-length distribution, and `changeFlags` distribution across the corpus; `survey
<dir> 0xNN` deep-dives one type with a **per-offset constancy map** — across every record of a `(changeFlags,
length)` group, which byte positions are **constant** (delimiters / type tags / structure) vs **variable** (data
fields). A single dominant length ⇒ a fixed-width struct (easy to decode); a length spread ⇒ variable/script-like.
Pure corpus alignment (no masters, no in-game saves). Counts below are illustrative (vanilla 77-save + base-VNV
98-save runs); regenerate with `survey`.

**The type byte is a change-CATEGORY, NOT the changed form's record type.** This was tested directly with the
`recid` probe (FormID → masters record signature, below): for change forms whose refID is a type-0 array index
(so `cf.FormId` is the §4f-resolved FormID — validated correct: iref 2 → `0x00104C1C` = the "Ain't That a Kick"
QUST), the **directly-named forms of a single type byte span many unrelated record types**, and one record type
appears under many type bytes:
- `0x00` → ACHR / SCPT / REFR / MISC   ·   `0x07` → QUST / SCPT / ALCH / CELL   ·   `0x08` → INFO / NAVM / PACK
- `0x09` → NPC_ / QUST / ACHR   ·   `0x0A` → ACRE / WEAP / ACHR / NPC_   ·   `0x22` → FACT / CHAL
- `0x2B` → INFO / IDLE   ·   `0x32` → PACK / CHAL / REPU / REFR(CONT) / INFO / ACHR

So a record type (e.g. ACHR) carries **several** change forms of different type bytes — each a different *kind* of
change — and the type byte cannot be read as "this is a REFR/ACHR/NPC_." (The earlier framing that named
`0x01`=REFR etc. was an over-generalization from the *player's* specific records — the player inventory REFR does
carry a `0x01` change form, but `0x01` is not "the REFR type".) The one near-exception is `0x1F`: its **−1-hopped
base** is uniformly NOTE (§4k.1 #2), though even there `cf.FormId` itself is the note's *reference object* (a REFR).
**Practical consequence:** decode a type by its PAYLOAD shape (the survey/constancy map), and use `recid` to get
the per-record target's record type when interpreting it — don't infer record type from the type byte.

**Fixed-length / single-flag types — payload SIZED by corpus alignment (semantics unlabelled per "size, don't
guess"):**
- `0x08` — **len 0, flags `0x80000000`** (100%, every save). A **zero-payload marker** change form (like the
  `0x1F` note-read marker but a different type). Very common (vanilla 7k, base VNV 273k). Its presence IS the
  state; the marked forms span types (`recid`: **INFO / NAVM / PACK**). A controlled diff (`primm-*discover`)
  inserted two — on a said **INFO** (a dialogue line) and a **NAVM** — when an NPC ran up and started dialogue, so
  on an INFO the `0x08` marker reads as "this dialogue line has been said"; the general meaning isn't fully pinned.
- `0x1F` — **len 0, flags `0x80000000`**. NOTE read marker — **fully DECODED** (§4k).
- `0x20` — **len 17, flags `0x80000000`**. Four packed `u32 LE` then a trailing `7C` (no internal delimiters):
  `[u32][u32][u32=0][u32][7C]`. SIZED.
- `0x21` — **len 20, flags `0x00000002`**. `[u32][7C][u32][7C][u32][7C][0xFFFFFFFF][7C]` (last field a `−1`
  sentinel). SIZED.
- `0x2B` — **len 10, flags `0x00000002`** = **FACTION REPUTATION** (§4o): `[fame:f32][7C][infamy:f32][7C]`, keyed by
  the faction's `REPU` record. **DECODED** (was mistakenly read as two `u32`). `0x32` — **len 10, flags
  `0x00000002`**, `[u32][7C][u32][7C]`. **`0x32` is a
  per-form COUNTER/value** (`[u32 value][7C][u32 = 0][7C]`; the 2nd `u32` is always 0): a controlled diff
  (`primm-prediscover` → `primm-postdiscover`) changed exactly one — value `1 → 2` on a `REFR`(base `CONT`,
  `0x001075F4`) — and the type's forms are countable across the board (`recid`: CHAL challenge-progress, REPU
  reputation, PACK, REFR, INFO), so `u32[0]` is that form's count/progress; its exact meaning is per target form.
  **Two discovery diffs confirm a per-EVENT counter:** that same `0x001075F4` CONT ref's `0x32` value tracks
  location discoveries in lock-step with the "Locations Discovered" Misc Stat — `1→2` on the Primm discovery and
  `2→3` on a second (canyon-wreckage, no-NPC) discovery — so it increments once per discovery event.
- `0x28` — **len 62, flags `0x80000000`**. `[10][7C]` then **4×** `[u8=00][u8=19][u8][7C][u16=1][7C][u16][7C]
  [f32=1.0][7C]`. SIZED (a 4-entry fixed list).
- `0x0B` — **len 25, flags `0x00000002`** on vanilla: a **fully constant** 24-byte block + `7C` (byte-identical on
  every vanilla save — a config/settings snapshot). NOT universal: modded corpora show other length variants, so
  it is fixed *per variant*, not globally.

**Count-prefixed list types — fully SIZED by corpus alignment (variable count, fixed entry stride; semantics
unlabelled):** both verified across vanilla + base VNV, the length matching the formula exactly on every record.
- `0x25` — **flags `0x80000000`**: `[u32 count][7C]` then `count` × `[ref:3 BE][7C]` (4-byte entries). `len = 5 +
  4·count` (count 0/1/3/5/7/9 → len 5/9/17/25/33/41). Entries are 3-byte BE refIDs (the §4f convention; often a
  consecutive iref run). A refID list — meaning unpinned.
- `0x22` — **flags `0x00000004`**: `[n:u8][7C]` then `n/4` × `[ref:3 BE][7C][u32][7C][u32][7C]` (14-byte entries,
  the `vsval`/ExtraDataList `b/4` count convention). `len = 2 + 14·(n/4)` (n = 4/8/0x0C/0x10/0xC4 → 1/2/3/4/49
  entries → len 16/30/44/58/688). A rarer `flags 0x80000006` variant has a different trailing shape (not this
  formula) — `cfwalk` correctly declines it to an `unknown[n]` gap rather than mis-parse.

**Variable / structured types — LOCATED, not yet field-decoded:**
- `0x00` — **DOMINANT** (vanilla 34k, base VNV 1.4M; the single most common change form). `0x7C`-delimited typed
  sub-records carrying embedded `[u16 len][7C][ascii][7C]` **strings** — animation/control names ("Idle",
  "SpecialIdle", "Forward", "Backward", "Close") — behind a leading tag byte; `changeFlags` selects the layout
  (≫100 distinct flag values, hundreds of lengths). Reads as **script/animation/control state**. Mostly not
  field-decoded — but one `0x00` sub-shape **IS decoded** (see §4m) and now **emitted labeled by `cfwalk`**: the
  **len-6 `[04][7C][2C][7C][flags:u8][7C]`** variant (flags `0x80000000`) is a per-form **flags/state** record —
  for a **map marker** REFR it holds the marker's visibility flags (Visible / Can-Travel-To), i.e. the
  **discovered-location** state.
- `0x0A` — `0x7C`-delimited, **float-heavy**, embeds NPC names (the player name; "Beagle" on a Primm save). The
  **dominant `0x020C`/len-58 variant (≥99% of all `0x0A`: vanilla 22.6k, base VNV 50.3k) is now SIZED** by `cfwalk`
  into its three `0x7C`-delimited spans (delimiters at `+0x07`/`+0x1C`/`+0x39`), with a **constant `0x32` tag at
  `+0x0B`** and **zero padding at `+0x2B..+0x38`**; the variable bytes are float-shaped placement/havok state, not
  yet field-named (needs a §7 controlled diff). Larger variants (len 706) hold the player name + a long float run
  (position/rotation/scale-shaped) and remain located, not field-decoded.
- `0x01` REFR / `0x02` ACHR — the reference path: MOVE block, havok/AV array, ExtraDataList, inventory, and the
  karma/XP slots are decoded (§4g–§4j); the rest of the actor-value array and most base state remain `unknown`.
  `cfwalk` breaks a MOVE-anchored record into its located spans — `MOVE block[27]`, `havok/actor-value
  array[n]`, `ExtraDataList + inventory[n]` — rather than one opaque gap; the byte-level field walk + item list
  stay in `refdump`/`inventory`. Records without the MOVE bit fall to a single gap (anchor via `refdump`).
- `0x09` NPC_ — player base: SPECIAL + name (§4d) decoded. The remaining records (flags `0x40000000`, 80–95%) are
  now **SIZED as one count-prefixed family**: `[n:u8][7C]` then `n/4` × 14-byte entry (`[u32][7C][8 B][7C]`) then a
  `[00][7C][00][7C]` trailer, `len = 6 + 14·(n/4)` (n = 0/4/8/12/16 → len 6/20/34/48/62; the modal len-6 stub is the
  n=0 case). Verified across vanilla + base VNV; `cfwalk` emits count + entries + trailer. Entry internals (a small
  `u32` + an 8-byte block, FaceGen-ish) not yet field-named.
- `0x07` — **HETEROGENEOUS** (not one record type): some are **QUST** change forms ("Ain't That a Kick in the
  Head" resolves; the packed formType-7 stage encoding the quest work decodes — §6 #3), but others are **cell /
  map-fog data** (e.g. `0x0010D9F4`, per the quest RE) and more. Disambiguate by the masters' record type, not the
  form-type byte. The two cross-corpus-stable fixed variants are now **SIZED**: **len 9 (flags `0x60000000`,
  dominant) = `[u32][u32][7C]`**, and **len 42 (flags `0xE0000000`) = that same 8-byte header + a 32-byte variable
  block (`unknown[32]`, float-heavy) + `7C`**. The large `0xC0000000` variant stays an `unknown[n]` gap (quest
  stages/objectives are surfaced by `quests`, §6 #3).
- `0x04` / `0x05` — large `0x7C`-delimited reference-like records (flags `0x20000000`/`0x20000002`), embedding the
  `04 7C 74 7C`-style ExtraDataList sub-blocks; variable, not field-decoded. (Modded-heavy.)
- `0x0B` — fixed-constant 24-byte config on vanilla (sized, above) but **variable on modded** (11 distinct lengths)
  — sized only per-variant.

**More single-value types SIZED (corpus-fixed across all three corpora; semantics unlabelled):**
- `0x0F` / `0x16` / `0x1A` — **`[u32][7C]`** (len 5 always; `0x16` is very common — 145k records on VNV Extended).
- `0x0D` — **`[u32][7C]`** (len 5) or **`[refID:3 BE][7C]`** (len 4, flags `0x00800000`). Two fixed variants.
  (Decoded by `cfwalk`; the `0x09`-prefixed `[u32]` value recurs — a small state/flag, meaning unpinned.)

Tooling: **`survey <save|dir> [0xNN]`** (the coverage survey above) and **`cfwalk <save> <iref>|--type 0xNN [N]`**
(the labeled **full walk**, §6 #1b): renders a change form's payload as a field tree — labeled fields for the sized
types above, `0x7C`-tokenized output for the still-undecoded `0x00`/`0x0A` variants, and a single explicit
`unknown[n]` gap (hex-capped) for everything else, so coverage is always visible and never silently skipped. The
decoder now lives in **`Core/ChangeFormPayload.Walk`** (pure bytes→lines, unit-tested on synthetic payloads, reused
by the planned GUI full-walk tab); as a type graduates from "located" to "field-decoded", its emitter there replaces
the gap. Still to fold in: the REFR/ACHR field tree (the `refdump` decode of §4g–§4j) and the ordered REFR/ACHR
model (§8a). **`recid <save>
<formId…>`** identifies the masters **record signature** (REFR/DOOR/CHAL/…) + a placed ref's base form for any
save FormID — by traversing the owning plugin header-only (`TesPlugin.FindRecordSignatures`) — so a change form
the name index can't resolve (world/reference records aren't indexed for naming) can still be classified; it is how
the "type byte ≠ record type" census above was run.

### 4m. Discovered map-locations — the map-marker visibility flags (controlled diff)
**How the save records which locations are discovered.** Each world-map location is a placed **map-marker REFR**
(base form **`0x00000010`** = "MapMarker") carrying a `FULL` display name. When the player encounters one, the save
holds a tiny **type-`0x00`, len-6 change form** on that REFR:
```
[04][7C] [2C][7C] [flags:u8][7C]      flags = map-marker visibility (GECK layout: bit0 0x01 = Visible, bit1 0x02 = Can Travel To)
```
A location is **"discovered" (fast-travelable) iff its map-marker REFR's change form has bit1 (`0x02`) set**; the
discovered *set* is exactly those markers. **Three marker states** (confirmed across the diffs): **unknown** = not
in the FormID array, no change form; **visible / "told-about"** (an NPC/note placed a greyed marker) = in the array
with a `0x01` (Visible) change form; **discovered** = flags `0x03` (Visible + Can-Travel-To). Discovery therefore
takes one of two forms in the save: a **DATA CHANGE** flipping an existing `0x01`→`0x03` (a told-about marker, e.g.
"Canyon Wreckage" — its FormID `0x00157F7E` was already in the PRE array), or an **INSERT** that **appends** the
marker's FormID to the array and creates the change form at `0x03` outright (a never-seen marker, e.g. NHPS
`0x00153403`, array 11580→11581). **Confirmed by three controlled diffs:**
- `canyonwreckage-*discover` (no NPC): an **existing** marker change form (iref 8163) flipped `flags 0x01 → 0x03`
  (Visible → Visible + Can-Travel-To). The marker is **"Canyon Wreckage"** (`array[iref−1] = array[8162] =
  0x00157F7E`) — matching the save name.
- `nhps-*discover`: a brand-new marker → the save **created** the change form (`flags 0x03` directly) **and
  appended the marker's FormID to the FormID array** (count 11580 → 11581). The marker is **"Nevada Highway Patrol
  Station"** (`array[iref−1] = array[11580] = 0x00153403`).
- `primm-*discover`: the same `[04][7C][2C][7C][flags][7C]` shape with `0x01→0x03` also appears on an `INFO`, so
  `0x2C` is a **generic per-form flags field** (Seen/Enabled-style), here read for the marker REFR.

**Marker change-form refID resolves via `array[iref−1]`** (the §4g/§4k "+1" convention: refID = array index + 1),
NOT `array[iref]` — so `cf.FormId` (which the walker computes as `array[iref]`) gives the **off-by-one neighbour**
for these records (e.g. it named the canyon marker `0x001558AA` "Abandoned BoS Bunker" = `array[8163]`, the wrong
one). This contradicts pre-existing forms (player base `0x07`, the "Ain't That a Kick" QUST at iref 2) whose target
*is* `array[iref]` — an **unresolved refID-convention discrepancy**, see [docs/DECISIONS.md]. The marker change
form is **type `0x00`** (a REFR carrying only a flags change) — a concrete "type byte ≠ record type" case (§4l),
initially missed by a `type 0x4`-only filter. Found via `recid` (now reports `[MAP MARKER]` + `FULL` name + base).

> **The "you can now fast-travel" tutorial popup** (first fast-travelable discovery): a "seen" flag for it was
> **not isolable** from the `nhps` pair — beyond the marker flip + the discovery count, the only other GlobalData
> changes are player-position and game-time/global-variable **floats** (no clean `0→1` boolean). Whether the popup
> is a persisted seen-flag or a one-shot scripted event needs a *no-movement* controlled pair around the popup.

### 4n. Player perks (and traits) — the perk list in the player reference change form
The player's **perks and traits** are a **count-prefixed list embedded in the player reference change form** (iref =
PlayerRef + 1 — the same record that holds inventory §4g, karma/XP §4j), inside its ExtraDataList region:
```
[count*4 : u8][7C]   then count × ( [perkRef : 3 BE][7C][rank : u8][7C] )      # 6 bytes per perk; count*4 = the b/4 vsval convention (§4i)
```
Each `perkRef` is a 3-byte big-endian refID = **FormID-array index + 1** (the §4g "+1" convention), resolving to a
**`PERK`** record (named via the masters; FNV stores **traits as PERK forms too**). `rank` is the perk rank (1 for
single-rank perks). **Cracked by a controlled diff** (`gtg-complete` → `level2-gunsbachelor`: level 1→2, 13 skill
points into Guns 15→28, **Confirmed Bachelor** perk taken): the saves are far apart (noisy change-form churn), so
the perk was isolated **structurally**, not by change-form diff — `findname` located the perk
(`Confirmed Bachelor = 0x001361B4`), which is **absent from the FormID array before and present after** (iref 8669),
and `find` located its refID inside the player reference record. The decoded block reads `08 7C` (count*4 = **2**)
then **Confirmed Bachelor** (rank 1) + **Hoarder** (the player's trait, also a PERK, rank 1) — exactly the
character's perk + trait. **Taking a perk** therefore (a) **appends the perk's FormID to the FormID array** and
(b) adds a `[perkRef][7C][rank][7C]` entry (count*4 += 4) — a length-changing edit, so this is **read-decodable but
not same-length-editable** (like notes §4k). **Reader shipped:** `FalloutSave.PlayerPerks(isPerkForm)` scans the
player reference record for `7C [ref:3] 7C` entries whose `FormIdArray[ref−1]` is a `PERK` (the masters test is
injected by the caller, as for notes) and reads each `rank`; **CLI `perks`** names them via the masters (`PERK` is
now indexed by `TesPlugin`). **Verified** across saves/characters: gtg-complete = Hoarder + Companion Suite (no
Confirmed Bachelor); level2-gunsbachelor = Confirmed Bachelor + Hoarder + Companion Suite; a vanilla mid-game save =
Built to Destroy + Fast Shot + Swift Learner + Companion Suite (the always-granted engine perk) — no false
positives; controlled + read-only-invariant tests pin it. Tooling: **`findname <save> "<text>" [SIG]`** (find a base
form by name → save-space FormID; how the perk was located) + `recid`/`find`.

### 4o. Faction reputation (fame / infamy) — the type-0x2B change forms
The player's **reputation with each faction** is a **type-`0x2B` change form** (len 10):
```
[fame : f32 LE][7C][infamy : f32 LE][7C]      # both 0–100; a faction with both 0 shows no standing in the Pip-Boy
```
keyed by the faction's **`REPU`** record (FNV has one REPU per playable faction). The change form's refID is the
REPU's FormID-array index **+ 1** (the §4g/§4k persisted-reference convention — confirmed stable across reload by
`nhps-resave`, see [docs/DECISIONS.md]), so the faction is `FormIdArray[refID − 1]` and resolves to a `REPU`
(named via the masters; `REPU` is now indexed by `TesPlugin`). **Cracked by a controlled diff** (`rep4-pre →
rep4-post`: Goodsprings set from idolized to nothing via `setreputation 104c22 … 0` — fame `100.0 → 0.0`, removing
it from the Pip-Boy). The change is a clean float splice found by a **whole-file byte diff** (`cmp`): the only
non-churn change was two bytes `C8 42 → 00 00` (i.e. `100.0 → 0.0`) inside the Goodsprings `0x2B` record. **Verified**:
rep4-pre = Goodsprings 100/0 + Powder Gangers 0/12; rep4-post = Goodsprings 0/0 (gone) + Powder Gangers unchanged;
a late VNV Extended save reads 12 factions with sane values (NCR 80/2, Caesar's Legion 12/100, Boomers 50/0, …).
**Why it was missed earlier:** `0x2B` was mistaken for `[u32][7C][u32][7C]` (§4l) and `cf.FormId` (`array[iref]`)
gave the off-by-one neighbour, not the REPU — both fixed. `FalloutSave.Reputations(isRepuForm)` (read) + `TrySetReputation(faction, fame, infamy)` (**editable** — a
same-length float splice, like karma/XP; matches the faction by FormID so no masters needed for the edit); CLI
`reputation` + `setreputation`. The earlier `0x001558E6` `0x32`
sighting was a *separate* per-form counter on the Powder Gangers REPU, not its reputation (which is the `0x2B`).

**Lifecycle (standard change-form behaviour):** a faction's `0x2B` record is **created the first time its reputation
deviates from the default 0**, and is **never removed** — setting it back to `0/0` zeroes the floats in place (the
file stays the same size; confirmed `rep4-pre → rep4-post`: Goodsprings `100/0 → 0/0`, record still present at iref
8656). The **Pip-Boy hides any faction with both fame and infamy 0**, so a wiped faction disappears in-game but its
`0x2B` record persists in the save — which is why `reputation`/the GUI still list it at `0/0` (faithful to the save,
not the Pip-Boy's display filter), and why `setreputation` can restore a wiped faction without re-creating the record.

**Adding a record (length-changing).** For a faction with *no* `0x2B` record yet, `FalloutSave.AddReputation`
creates one — the first consumer of the offset-fixup core (§4b). It appends the faction's `REPU` FormID to the
FormID array if absent (array + count grow; new iref = old count), builds the 20-byte record
`[refID:3 BE = iref+1][changeFlags:u32 = 0x00000002][type 0x2B][version 0x1B][len 10][fame:f32][7C][infamy:f32][7C]`
(`changeFlags`/`version` copied verbatim from real `0x2B` records — not guessed), prepends it to the change-forms
region (`ChangeFormCount++`), and fixes up every absolute offset via `RebuildWithBodyEdits`. CLI `addreputation`.
Verified on a real 1.86 MB save: Goodsprings added to a save with none → size +24 B (20 record + 4 FormID entry),
change forms 4134→4135, FormID array 8108→8109, the walk still lands exactly on `GlobalData3Offset`, and the new
record reads back. Refuses (use `setreputation`) when the faction already has a record.

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
| **Skills are sparse** (only modified entries stored) in the §4e `0x7C`-tagged AV list; **modifier-vs-absolute now RESOLVED — modifier** (§4j skill-book diff). | Reads/edits exactly what's stored; SPECIAL/skill sums verified across all 16 saves. | A skill-book diff (`skillbook-pre→science`, §4j) proved book bonuses are **+3 modifier floats** in the dense AV slot array (not the §4e list). Remaining: map each AV slot → skill and reconcile the §4e list with the dense array, then enumerate the full 13 from base + perks + tags. |

> **Is the inventory start *fully* deterministic now?** **Yes — on all 607 real saves** (vanilla 30/30, base VNV
> 98/98, VNV Extended 479/479). The vanilla path is a pure structural walk (MOVE + havok array + sized
> ExtraDataList + `vsval` anchor); modded saves use the same typed-entry walk (variable order + `0x1D`/`0x75`) plus
> a bounded post-entry resync; and bit2/bit10 records — whose pre-list region is a variable-length Havok physics
> blob, not a sized array — are located by the self-validating ExtraDataList-header anchor (choosing the real
> longest chain over the §4g scan). The §4g scan is now an unused safety net. The only thing not yet *byte*-decoded
> is the physics blob itself, which the list doesn't need. Verified **0 under-reads** and display **byte-identical**
> across all 607 except **35 strict corrections** (endgame inventories that previously decoded to empty).
