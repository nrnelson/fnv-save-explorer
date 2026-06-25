# Pip-Boy quest oracles (ROADMAP §6 #16)

Ground-truth Pip-Boy quest lists, transcribed from in-game screenshots, used to measure the
accuracy of the computed Pip-Boy (`QuestPipboy.Compute`, exposed by the CLI `pipboy` command and
the GUI Quests tab). These let every session re-measure recall/precision without re-screenshotting.

## Oracles

| Oracle | Save | Quests | Documented result |
|--------|------|--------|--------------------|
| `save57.oracle`  | vanilla "Nathan", Prospector Saloon (early) | 7 (5 active + 2 completed) | **7/7 EXACT, 0 FP** |
| `fih-submitted.oracle` | vanilla, just after turning in "Can You Find It in Your Heart?" | 10 (6 active + 4 completed) | **9/10, 0 FP, 1 missed** (Back in the Saddle) |
| `save122.oracle` | VNV Extended, New Vegas Strip (mid) | 24 (10 active + 14 completed) | 16/24 correct, 4 FP, 5 missed |
| `save420.oracle` | VNV Extended, Mojave Wasteland (late) | 68 (13 active + 55 completed) | 36/68 correct, 11 mislabelled, 3 FP, 21 missed (94% precision) |

`save57` is the regression floor: any change MUST keep it 7/7, 0 FP. `fih-submitted` is the second
vanilla floor and a kill/turn-in **controlled-diff endpoint** (its companion saves
`fih-accepted`/`close`/`onedead`/`complete` show the quest ACTIVE — it greys only on the Ranger
Jackson turn-in, not the kill, proving the completion is dialogue-driven). `save122` (mid) and
`save420` (late) are the modded-save measures — all four reproduce the results above exactly.

## Running

From the repo root (so `dotnet run` resolves the CLI project):

```bash
bash docs/pipboy-oracles/reconcile.sh docs/pipboy-oracles/save57.oracle
bash docs/pipboy-oracles/reconcile.sh docs/pipboy-oracles/fih-submitted.oracle
bash docs/pipboy-oracles/reconcile.sh docs/pipboy-oracles/save122.oracle
bash docs/pipboy-oracles/reconcile.sh docs/pipboy-oracles/save420.oracle
# Override the save path (e.g. a moved save folder) with a 2nd arg:
bash docs/pipboy-oracles/reconcile.sh docs/pipboy-oracles/save122.oracle "/path/to/Save 122 ….fos"
```

Output: `correct  mislabelled  falsePos  missed  [computed, truth, precision]`, then the FP and
missed names. **correct** = right quest, right active/completed state. **mislabelled** = quest is
in the Pip-Boy and computed, but our active/completed state is wrong (almost always an
event-completed quest we show active). **falsePos** = we compute it, it's not in the Pip-Boy.
**missed** = it's in the Pip-Boy, we don't compute it.

## Oracle file format

Plain text. `save = <path>` (machine-specific; overridable by the 2nd CLI arg), then one
`active = <quest name>` or `completed = <quest name>` per Pip-Boy entry. Names must match the
CLI's printed `FULL` name exactly (the harness strips the `[state]` tag, the trailing FormID and
any `(SGE)` marker, then compares by name). `#` lines are comments.

## The accuracy boundary (why it's not 100%)

`QuestPipboy` is exact for quest state the save *records* — SGE startup at masters defaults,
the formType-7 completion flag, and said-INFO dialogue (start/advance/complete/stop). It cannot
recover state the engine *recomputes from world events at load*: event-completed quests
(kills/activators) show active-instead-of-completed, and a few dialogue/event-started quests are
missed entirely. Precision stays ~94% at all playthrough lengths; recall degrades with length
because more quests have completed via these unrecoverable events. See ROADMAP §6 #16 for the
full evidence (change-form flags, completion dialogue, dead-ref counts, and globals all checked).
