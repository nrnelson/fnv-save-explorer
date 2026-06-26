# FNV Save Explorer — Roadmap & Status

A cold-start working document: where the project is, exactly what's been reverse-engineered, and
how to resume. For the user-facing overview see [README.md](README.md); for build/agent notes see
[CLAUDE.md](CLAUDE.md).

---

## 1. Goal & scope

**Primary objective: fully decode the `.fos` binary into labeled fields** — read the *whole* save
(header, GlobalData tables, every change-form type, the FormID array) into a structurally complete,
field-by-field model, with any not-yet-understood region marked as an explicit `unknown[n bytes]`
gap rather than skipped. Everything else is a *consumer* of that decode: the **analyze**/**edit**
tooling, and the **quest / Pip-Boy interpreter** (§6) in particular, are downstream of "the record
is fully parsed." Decode coverage — what's resolved vs `unknown` per section/record type — is tracked
in **[SPEC.md](SPEC.md)**, the validated byte-truth reference.

Chosen direction: C# / .NET 10, a reusable **Core library** plus a **WPF GUI** and a **CLI**. Method:
two complementary techniques — **corpus alignment** (line a record up across the 600+ real saves to
reveal field boundaries; autonomous) for *structure*, and **controlled diffs** (a save just-before vs
just-after one in-game change; §7) for *semantics*. Structure tells us where a field is; a controlled
diff tells us what it means.

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
`setcondition`, `names`, `notes`, `perks`, `reputation`, `setreputation`, `player`, `setlevel`, `caps`, `setcaps`, `karma`, `xp`, `setkarma`, `setxp`, `diff`, plus
R&D helpers `walk`, `survey`, `cfwalk`, `recid`, `findname`, `refdump`, `edlscan`, `invsig`, `notescan`, `resolve`, `idiff`, `fdiff`, `find`, `irefscan`.
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

→ Moved to **[SPEC.md](SPEC.md)** (the validated byte-truth + decode-coverage reference). Section numbering is preserved there, so existing `§4`/`§8a`/`§9`/`§10` cross-references in code and docs still resolve.

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
| Faction reputation decode (§4o) | ✅ fame/infamy per faction = a type-`0x2B` change form `[fame:f32][7C][infamy:f32][7C]` (len 10), keyed by the `REPU` record via `array[refID-1]` (the persisted-ref +1 convention). Cracked by a controlled diff (`rep4`: Goodsprings idolized→wiped, fame 100→0, found via whole-file `cmp`). `FalloutSave.Reputations(isRepuForm)` + CLI `reputation`; `REPU` now indexed by `TesPlugin`. Verified vanilla + Extended (NCR 80/2, Caesar's 12/100, …). **Editable** via `TrySetReputation`/`setreputation` (same-length float splice, round-trip tested). Corrects the earlier §4l `0x2B`=`[u32][7C][u32]` mis-read |
| Player perks + traits decode (§4n) | ✅ count-prefixed perk list in the player reference change form (iref = PlayerRef+1): `[count*4][7C]` + N×`[perkRef:3 BE][7C][rank][7C]`, perkRef = array index+1 → a `PERK` record (traits are PERKs too). Cracked via gtg-complete→level2-gunsbachelor (Confirmed Bachelor: absent→present, FormID appended to the array). `FalloutSave.PlayerPerks(isPerkForm)` + CLI `perks`; `PERK` now indexed by `TesPlugin`. Verified across saves/characters (no false positives); read-only (adding a perk is length-changing). New tool `findname` |
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
`0x0000000F` stack)**, **karma + XP (§4j)**, **faction reputation fame/infamy (§4o)** — all safe same-length splices.

---

## 6. Next steps — toward full decode (priority order)

The primary objective is the **full structural decode** of the save (§1); **[SPEC.md](SPEC.md)** holds
the validated byte-truth and tracks decode coverage. Completed work with verification is in §5;
approaches already ruled out are in **[docs/DECISIONS.md](docs/DECISIONS.md)** (don't re-grind them).

1. **Full per-type change-form payload decode (PRIMARY).** The skeleton is solved — we walk every
   record and read every header (corpus: 42,343/42,343 on Save 420, exact). The open frontier is the
   per-type *payload* internals. Extend the proven REFR/ACHR inventory walk to a complete,
   field-by-field parse of **every change-form type** present in the corpus (histogram: `0x00`/`0x01`/
   `0x02`/`0x07`/`0x08`/`0x09`/`0x0A`/`0x16`/`0x1F`/`0x22`/`0x32`/… plus the `0x40`/`0x80` flag
   variants). Deliverables: (a) a **decode-coverage survey** — bytes parsed vs `unknown[n]` per type
   across the corpus; (b) a **"full walk"** that renders any change form as a labeled field tree with
   explicit gaps; (c) the coverage map maintained in SPEC. This folds in the former #14 (the ordered
   REFR/ACHR field model per SPEC §8a) and, optionally, the byte-decode of the havok blob (former #12).
   **Status:** (a)+(b) shipped — the `survey` CLI (per-type length/`changeFlags` distribution + a per-offset
   constancy map) and the **coverage map in SPEC §4l**: the fixed-length types (`0x20`/`0x21`/`0x28`/
   `0x2B`/`0x32`/`0x0B`, plus the `0x08` zero-payload marker) are sized; the dominant `0x00` (script/
   animation/control state) and `0x0A` (actor/placement, float-heavy) are located, not field-decoded. The
   `cfwalk` CLI renders any change form as a labeled field tree with explicit `unknown[n]` gaps. Sized so far:
   the fixed types above + the count-prefixed list types `0x22`/`0x25`; REFR/ACHR are broken into their located
   spans (MOVE / havok-AV array / ExtraDataList+inventory). **Remaining is mostly SEMANTICS (needs controlled
   diffs):** name the sized types (`0x20`–`0x32`), decode the `0x00`/`0x0A` delimited script/actor fields, and
   fold the QUST stage/objective decode (§6 #3) into the walk. See the controlled-diff shopping list below.
2. **GlobalData full type coverage.** Several numbered types are still only partially decoded; finish
   them, and pin the type-2 registry status codes (only `1` = death is confirmed; 2–7 unknown).
3. **Quest / Pip-Boy interpreter** (former #16) — now a *consumer* of the decode, not bespoke probes.
   Remaining recall needs either the editor-ref→dead-instance binding (creature kills) or more
   ground-truth oracles; the walls are catalogued in DECISIONS.md. Smaller wins: map the QUST var
   block's SLSD index→source name so the decoded `QuestScriptVars` become usable by `GuardEvaluator`.
4. **Item condition maximums** (former #11): read the base-form Health so per-item condition shows a cap.
5. **Length-changing edits** (former #7): implement offset-fixup so renames / add / remove become safe
   (today only same-length splices are allowed — see DECISIONS.md).
6. **GUI/UX** (former #9): a raw-hex / full-walk field-tree viewer tab (surfacing deliverable 1b),
   screenshot export, backup management.

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

## 8. Reference sources · 8a. UESP cross-reference · 9. Limitations · 10. Accepted caveats

→ Moved to **[SPEC.md](SPEC.md)**.

