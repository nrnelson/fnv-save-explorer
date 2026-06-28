# DECISIONS.md — ruled-out approaches (don't re-grind)

Conclusions reached and dead-ends hit during reverse-engineering, distilled to one line each so a
future session doesn't repeat the work. Full blow-by-blow lives in `git log` and the commit messages.
The governing rule still holds — *the engine rebuilds everything from save + masters at load, so a
readable signal always exists* — so "walled" here means **"the signal isn't in the form/field we key
on; here's why,"** not "impossible."

## Quest / Pip-Boy interpreter (former ROADMAP §6 #16)

- **Event-completed quests have no own-changeform "completed" flag.** A completed quest's QUST change
  form is byte-identical to an active one (or absent entirely). Recover completion from the *trigger's*
  persisted side effect — kill registry, enabled ref, said-INFO — not the quest's own record. (Angle 4:
  no distinguishing change-form flag exists. Confirmed on the full Save 420 oracle.)
- **Creature deaths are NOT editor-ref-keyed** (fih controlled diff, 2026-06-25). Killing 7 critters
  added exactly +7 status-1 entries to the GlobalData type-2 registry, but only 2 resolve to the
  editor placed-refs the script's `<Ref>.GetDead` checks; the rest live in the critter's own ACHR
  change form or under a runtime-instance id ≠ the editor ref. So `DeadReferences()` (registry-only)
  under-reports creature deaths. **The kill-poll is limited to NPC-kill quests** (gangers ARE
  registry-keyed by base FormID — the Ghost Town Gunfight case). Generic-creature quests need the
  editor-ref→dead-instance binding (a deeper ACHR decode).
  - *Update (2026-06-28):* the type-2 `Status` is a **bitfield**, bit 0 = dead (ROADMAP §6 #2, `killloot`
    diff). `DeadReferences` now tests the dead **bit** not `== 1`, so it no longer drops dead refs that also
    carry another change bit (status 3/5/7). This recovers *some* of the under-reported deaths but NOT the
    editor-ref→runtime-instance binding gap above — that still blocks generic-creature kill quests.
- **The stuck quests don't persist their gate vars.** The QUST script-var block is decoded
  (`CHANGE_QUEST_SCRIPT`, bit30 — `QuestScriptVars`), but only quests with a bit30 change form expose
  vars. Wang Dang (`doonce`) has no change form, "They Went That-a-Way"/VMQ01 (`GotJessupNote`) is a
  REFR/alias stub, the DLC-radio FP quests (`nEnableDLC`) have no change form. Their gate state isn't
  saved — the engine recomputes it at load. (Var-block presence is also NOT a "started" discriminator:
  ~103 quests carry one but only ~13 are active.)
- **DLC-radio false positives** (Happy Trails Expedition / Sierra Madre Grand Opening! / The Reunion):
  the DLC-delay mod gates them on `nEnableDLC`, an unpersisted quest-local var — no save signal, and
  indistinguishable from a genuinely-shown SGE radio quest (e.g. Midnight Science-Fiction Feature!).
- **The Finger of Suspicion false positive:** a formType-7 quest that completed and *dropped off* the
  Pip-Boy; no decoded "removed from Pip-Boy" flag, so it stays computed.
- **Back in the Saddle (VCG02) miss:** empty ref-style template; completion in no decodable field.
- **Conditional-dialogue START (the "B2" lever):** applying a said-INFO's conditional SetStage as a
  quest *start* is a ~1:1 recall-for-precision wash unconditionally; gating it on `GuardEvaluator`
  (only fire when the start-guard provably holds) fires on **nothing** — the start guards reference
  quest-local vars / globals the evaluator returns `unknown` for. Not shipped.
- **Quest-chain hand-off back-propagation (Angle 1):** completing stages self-complete
  (`CompleteQuest <self>`); they do *not* `StartQuest` their successor (that's dialogue-driven). A
  completing-stage→successor graph has ~no edges.
- **Distributed `type 0x08` enable markers → objective binding:** arbitrary script side-effects; the
  inserted markers don't match the completed objective's QSTA target refs. Dead end.
- **`0x0010D9F4`:** per-cell map/fog-of-war data, not objective progress.
- **formType-7 packed "thermometer" stage bitmask:** real (encodes progress) but the bit→stage mapping
  needs an in-game `setstage <quest> N` capture — and `setstage` is a no-op replay (only the target
  stage's result script runs), so natural-play controlled diffs are required, not console captures.
- **Validation cap:** only 4 ground-truth Pip-Boy oracles exist (vanilla Save 57 + fih-submitted; VNV
  Extended 122/420). Any general guard/seed lever needs more in-game screenshots to validate without
  sneaking in false positives. The GameMode kill-poll + GuardEvaluator are built but produce **zero**
  movement on these 4 oracles (their gaps are non-lethal/dialogue completions or unpersisted state).

## Save body / format

- **Change-form TYPE byte ≠ the changed form's record type.** A `recid` census (FormID → masters record signature)
  over type-0-refID change forms (whose `cf.FormId` is validated correct) shows each type byte's forms span many
  unrelated record types and one record type appears under many type bytes (e.g. `0x32` → PACK/CHAL/REPU/REFR/INFO;
  `0x09` → NPC_/QUST/ACHR). So the type byte is a change-CATEGORY (decode by payload shape, SPEC §4l), not a
  record-type tag — don't infer record type from it (use `recid`). Only `0x1F`'s −1-hopped base is uniformly NOTE.
- **Location discovery = the map-marker REFR's visibility flags (SOLVED, SPEC §4m), plus two running tallies.**
  Confirmed by THREE diffs (`canyonwreckage`/`nhps`/`primm`, 2026-06-25): the discovered location's **map-marker
  REFR** (base `0x10`) carries a **type-`0x00` len-6 change form** `[04][7C][2C][7C][flags][7C]` whose flags byte
  is **`0x01` (Visible) → `0x03` (Visible+Can-Travel-To)** on discovery; a brand-new marker (`nhps` → "Nevada
  Highway Patrol Station") gets the change form **created** at `0x03` with its FormID **appended** to the array. A
  location is discovered iff its marker REFR's change form has bit1 (`0x02`) set. *(Initial mis-call: I first said
  "no per-marker change form" because I filtered `type 0x4` only — it is **type `0x00`**, a "type byte ≠ record
  type" case found via `recid`.)* The "Locations Discovered" Misc Stat (GlobalData type 0) and a `0x32` per-event
  counter on a CONT ref (`1→2`→`2→3`→`3→4` across the three) are just tallies. The "you can now fast-travel"
  tutorial popup's seen-flag was **not isolable** (only position + game-time floats beyond the marker/count).
- **refID-convention discrepancy — UNRESOLVED.** Map-marker (and §4g/§4k inventory/note) change-form refIDs resolve
  via **`array[iref−1]`** (refID = array index + 1, "index 0 reserved"): proven on `nhps` (array grew 11580→11581,
  the new marker is `array[11580]`, change-form refID = 11581 = index+1) and `canyonwreckage` (`array[iref−1]` =
  "Canyon Wreckage" matches the save name; `array[iref]` = "Abandoned BoS Bunker" is the wrong neighbour). **But**
  pre-existing forms resolve `array[iref]` 0-based — the player base (`0x07`) and the "Ain't That a Kick" QUST
  (change-form iref 2 → `array[2]` = the quest, validated by the quest decoder) — and `cf.FormId`/the walker use
  that 0-based form. Both refIDs are 2-bit-type 0, so the type bit doesn't explain it. Net: `cf.FormId` is correct
  for pre-existing forms but **off-by-one for created/marker refs**; the governing rule (created-vs-existing? a
  count/base field?) isn't pinned. **Do NOT "fix" `cf.FormId` globally** — it would break the validated
  quest/player/notescan paths. Identify created/marker refs with `iref−1`; investigate the rule before relying on
  the `recid` census for created refs (the "type byte ≠ record type" §4l census used low-iref pre-existing forms,
  so it stands).
  - **Update — NOT a transient creation artifact (`nhps-resave` experiment, reload + immediate re-save, no
    movement).** The `+1` persists across a reload: the NHPS marker stays at `array[11580]` and its flags change
    form keeps **iref 11581** (= index+1). (The reload *appended* 6 refs, 11581→11587, re-persisting nearby refs,
    but the marker's index and its refID were unchanged.) So the split is **structural and stable**, not a
    save-session quirk — it correlates with **persisted-reference** change forms (map markers, note-read markers,
    inventory entries → `iref−1`, "index 0 reserved") vs **ESM-base-form** change forms (quest, player base/ref →
    0-based `iref`). The exact governing field still isn't pinned, but the rule of thumb holds: resolve
    persisted-reference change forms with `iref−1`, base-form change forms with `iref`.

- **Havok physics blob (bit2/bit10 `CHANGE_REFR_HAVOK_MOVE` records):** deliberately *located, not
  byte-decoded* (SPEC §10). Variable-length with a truncated final entry and a trailing slot array
  whose values locally collide with the ExtraDataList header — so a structural sizer would *still* need
  the self-validating anchor at the tail (zero correctness gain). The inventory list is found by the
  anchor instead. `HavokPhysicsEntryLength` recognises one entry for any future exact decode.
- **Length-changing edits** (rename, add/remove items/plugins): **now supported** via offset-fixup —
  `FalloutSave.RebuildWithBodyEdits` (§4b). It applies `BodySplice`s (insert/remove in original-file
  coordinates) and recomputes the File Location Table's five *absolute* offsets + three counts so the
  result re-parses (the change-form walker still lands exactly on `GlobalData3Offset`; the FormID array
  re-parses with its new count). No-op (no splices) stays byte-identical, preserving the retention
  invariant. Two design points that bit and are now settled:
  - **Boundary rule.** An FLT offset that *equals* a splice's position is the ambiguous case. A
    `BodySplice` carries `ShiftBoundaryOffset`: an **append** (new bytes belong to the preceding region)
    shifts a following region whose start equals the position (`≤`); a **prepend** (new bytes become the
    new start of the region at that position — e.g. the new change form at `ChangeFormsOffset`) does
    **not** move that region's own start offset (`<`). A single uniform `<` *or* `≤` rule is wrong — the
    append (FormID entry at the array end == the next region's start) and the prepend (change form at the
    region start) need *opposite* behaviour. Found by the synthetic append-path test walking 4 records
    instead of 2.
  - **Don't invent bytes.** The new `0x2B` record's `changeFlags` (`0x00000002`) and `version` (`0x1B`)
    are copied verbatim from real reputation records (`cfwalk --type 0x2B`), per `no-speculative-spec-code`.
  First consumer: add-reputation (§4o). Add-inventory-item / grant-perk are straightforward follow-ups.
- **`vsval` inventory over-read:** a few modded saves decode slightly *more* stacks than the engine's
  `vsval` count (hidden by the name filter); **0 under-reads across all 607 saves**. The residual comes
  from the havok-anchor path, not per-stack sizing (every per-stack type is sized).
- **AddPerk first-perk (zero-perk) write — mechanism cracked, locator walled (NOT shipped).** A clean
  controlled diff (`perk-pre`→`perk-post`: no-trait L1 char, console `player.addperk` only) proved the
  edit is trivial: a zero-perk save already holds an empty `00 7C` (count 0) perk list, so the first
  perk is the same `00`→`04` bump + 6-byte `[ref][7C][rank][7C]` insert as the ≥1 path (record +6 B,
  +4 B FormID append). **Walled on *locating* that empty `00 7C` slot:** the perk slots sit in the
  **undecoded trailing actor-data region after the inventory item stacks**, and resolving the
  neighbours proves that region is the actor's **volatile AI / animation / package state** with no
  character-invariant anchor: the `04`-mini-list ref immediately before the perk slot is
  **`0x001055C0` = an IDLE animation record** (the actor's current idle anim — moment-specific); the
  region "header" ref differs by save (HonestHearts **QUST** in perk-pre vs DeadMoney **GLOB** in q2);
  in a developed save (level2) the form right before the perk list is an **ACRE** creature ref. So the
  perk slot's position, neighbours, and ref *values* (array-index-dependent) all vary per
  character/moment — a fixed offset or specific-ref anchor would corrupt other saves. **Don't anchor on
  a ref or offset.** Unblock only by decoding the ACHR `CHANGE_ACTOR`/`ACTOR_PACKAGE_DATA` change-record
  grammar (bit11+ sections, between the inventory `vsval`/stacks and the record end), after which the
  perk slot is found deterministically; controlled diffs of these saves can't reveal it (volatile
  state, not a clean fixed structure). The q2 "havok phantom" was also a misread — q2's Hoarder is a genuine chargen
  trait (q1 has 0 perks + the FormID nowhere; q2 has the real `04 7C [Hoarder] 7C 01 7C` list), so
  `q1`→`q2` and `perk-pre`→`perk-post` are both valid 0→1 perk diffs (SPEC §4n).
