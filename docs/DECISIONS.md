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
- **Map-marker "discovered" state is NOT cleanly isolated by an approach diff** (`primm-prediscover` →
  `primm-postdiscover`, 2026-06-25). Discovering Primm fires a coupled NPC encounter (an NPC runs up + dialogue),
  so the diff conflates the two: the discrete changes were a `0x32` counter `1→2` on a container REFR (the
  approach trigger, not the marker), two `0x08` markers on a said INFO + a NAVM (the NPC dialogue), and the NPC
  ACHR — none is a clean map-marker visibility flag. The discovered-marker list likely lives in GlobalData (not a
  per-form change form); a no-NPC discovery would isolate it. The pair DID pin `0x32` (per-form counter) and `0x08`
  (said-INFO/marker) — see SPEC §4l.

- **Havok physics blob (bit2/bit10 `CHANGE_REFR_HAVOK_MOVE` records):** deliberately *located, not
  byte-decoded* (SPEC §10). Variable-length with a truncated final entry and a trailing slot array
  whose values locally collide with the ExtraDataList header — so a structural sizer would *still* need
  the self-validating anchor at the tail (zero correctness gain). The inventory list is found by the
  anchor instead. `HavokPhysicsEntryLength` recognises one entry for any future exact decode.
- **Length-changing edits** (rename, add/remove items/plugins): unsafe until full offset-fixup, because
  the File Location Table stores *absolute* offsets. Same-length splices only, by design.
- **`vsval` inventory over-read:** a few modded saves decode slightly *more* stacks than the engine's
  `vsval` count (hidden by the name filter); **0 under-reads across all 607 saves**. The residual comes
  from the havok-anchor path, not per-stack sizing (every per-stack type is sized).
