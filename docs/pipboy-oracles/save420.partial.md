# Save 420 — VNV Extended "Courier", Mojave Wasteland (late-game) — PARTIAL

**This is NOT a runnable oracle.** The full 68-quest Pip-Boy list was transcribed in a prior
session's reconciliation harness (user screenshot, 2026-06-24) but only the *summary* survived in
ROADMAP §6 #16 — the per-quest ground-truth list was lost with the ephemeral `/tmp/gt`. To make
this a measurable oracle, the user must re-supply the Pip-Boy screenshot so the 68 names + their
active/completed split can be transcribed into a `save420.oracle` in the format of the others.

```
save = C:\Modding\Viva New Vegas\profiles\Viva New Vegas Extended\saves\Save 420   Courier  Mojave Wasteland  50 52 28.fos
```

## Documented reconciliation (ROADMAP §6 #16, 2026-06-24)

- Ground truth: **68 quests = 13 active + 55 completed**.
- Computed: **50** (27 active + 23 completed).
- Result: **28 correct, 19 mislabelled, 3 false positives, 21 missed.**
- **Precision 94%** (47 of 50 computed are genuinely in the Pip-Boy).

## What is recoverable without the screenshot

**The 3 false positives** (computed, NOT in the Pip-Boy) — stable across Save 122 and Save 420:
`Happy Trails Expedition`, `Sierra Madre Grand Opening!` (SGE DLC-radios suppressed by VNV
Extended's DLC-delay mod), `The Finger of Suspicion` (formType-7 quest that completed then dropped
off the Pip-Boy). So the other **47 computed quests below are confirmed in the Pip-Boy**, but our
active/completed state is wrong for 19 of them (the event-completed class — shown active, actually
completed). Examples of those mislabels (from the ROADMAP): Ghost Town Gunfight, Come Fly With Me,
I Fought the Law, Guess Who I Saw Today, My Kind Of Town, Can You Find It in Your Heart?.

**Named missed quests** (in the Pip-Boy, NOT computed; ~5 of the 21 named in the ROADMAP — the
rest are ~16 event-completed quests we can't surface): Wild Card series, The House Always Wins II,
Beware the Wrath of Caesar!, Heartache by the Number.

### Computed list captured this session (50; `*` = false positive)

Active (27): Aba Daba Honeymoon, Can You Find It in Your Heart?, Come Fly With Me, Don't Make a
Beggar of Me, ED-E My Love, For the Republic Part 2, Ghost Town Gunfight, Guess Who I Saw Today,
Happy Trails Expedition*, I Fought the Law, Kings' Gambit, Midnight Science-Fiction Feature!,
My Kind Of Town, Pressing Matters, Render Unto Caesar, Restoring Hope, Ring-a-Ding-Ding!,
Sierra Madre Grand Opening!*, Someone to Watch Over Me, Talent Pool, That Lucky Old Sun,
The Coyotes, The House Always Wins I, The Reunion, They Went That-a-Way, Wang Dang Atomic Tango,
You Can Depend on Me.

Completed (23): Ain't That a Kick in the Head, Ant Misbehavin', Back in the Saddle, Bleed Me Dry,
Classic Inspiration, Climb Ev'ry Mountain, Eyesight to the Blind, Flags of Our Foul-Ups,
G.I. Blues, I Don't Hurt Anymore, Keep Your Eyes on the Prize, Medical Mystery,
Nothin' But a Hound Dog, Oh My Papa, Return to Sender, Still in the Dark, Sunshine Boogie,
The Finger of Suspicion*, The White Wash, Things That Go Boom, Three-Card Bounty,
Unfriendly Persuasion, Volare!.
