namespace FnvSaveExplorer.Core;

/// <summary>One counter increment harvested from a script's source text (ROADMAP §6 #16 Stage 1): a
/// <c>set &lt;Quest&gt;.&lt;Counter&gt; to &lt;Quest&gt;.&lt;Counter&gt; ± Delta</c> assignment, naming the quest
/// the counter belongs to by editor id (<see cref="QuestEdid"/>). The canonical case is an actor's <c>OnDeath</c>
/// script bumping a kill counter (<c>set VMS16.nGangerDeathCount to (VMS16.nGangerDeathCount + 1)</c>).
/// <see cref="ScriptFormId"/> is the FormID of the <c>SCPT</c> record the increment lives in — re-keyed into save
/// space by <see cref="PluginDatabase"/> so Stage 2 can map it to the actors that run the script (and count how
/// many are dead in the save). Increments whose owning quest can't be named (the bare
/// <c>set counter to counter ± N</c> form) are not represented here; a quest's own-script bare increments are
/// recovered directly from its <see cref="QuestDefinition.GameModeScript"/>/stage scripts by
/// <see cref="CounterGatedQuests"/>.</summary>
public sealed record CounterIncrement(uint ScriptFormId, string QuestEdid, string Counter, int Delta);

/// <summary>A quest-completing/advancing call harvested from a script's source text (ROADMAP §6 #16 Stage 1):
/// <see cref="Verb"/> targets quest <see cref="TargetEdid"/> from the <c>SCPT</c> record <see cref="ScriptFormId"/>
/// (plugin-local; re-keyed by <see cref="PluginDatabase"/>). When the script is NOT the quest's own
/// (<see cref="QuestDefinition.ScriptFormId"/>), it's an external event source (actor <c>OnDeath</c>, terminal, …)
/// — the broader event-completion graph that sizes the bucket-C prize beyond the counter-gated subset.</summary>
public sealed record ExternalQuestEffect(
    uint ScriptFormId, string TargetEdid, QuestScriptVerb Verb, int Arg1, bool Conditional, string Block);

/// <summary>A player-facing quest completed by an <b>external event script</b> (ROADMAP §6 #16 Stage 1): one or
/// more <c>SCPT</c> records other than the quest's own that complete it (<c>CompleteQuest</c>,
/// <c>CompleteAllObjectives</c>, or a <c>SetStage</c> to a QSDT-complete stage). <see cref="HasUnconditional"/> is
/// true when at least one such call is unguarded (the cleanest reachability); <see cref="ViaCounter"/> marks the
/// subset that is ALSO a <see cref="CounterGate"/> (e.g. Ghost Town Gunfight). Reachability for Stage 2 depends on
/// the triggering event leaving a readable save signal (a dead actor does; a hacked terminal may not).</summary>
public sealed record ExternalCompletion(
    uint QuestFormId, string? QuestName, string? QuestEdid,
    IReadOnlyList<uint> Scripts, bool HasUnconditional, bool ViaCounter, IReadOnlyList<string> Blocks)
{
    /// <summary>The completion is driven by a kill/destroy event block (<c>OnDeath</c>/<c>OnHit</c>/
    /// <c>OnDestroyed</c>) — so it's reachable from the save's death registry (the high-confidence Stage-2 case,
    /// the Ghost Town Gunfight shape generalised to the no-counter single-kill quests).</summary>
    public bool ViaKill => Blocks.Any(b => b is "ondeath" or "onhit" or "ondestroyed" or "onkill");
}

/// <summary>A quest whose completion is gated on a runtime counter (ROADMAP §6 #16 Stage 1): the quest's GameMode
/// (or a stage result-script) completes the quest — <c>CompleteQuest</c>, <c>CompleteAllObjectives</c>, or a
/// <c>SetStage</c> to a QSDT-complete stage — only inside an <c>if &lt;Counter&gt; &lt;Op&gt; &lt;Threshold&gt;</c>
/// guard, and that counter is incremented elsewhere (an actor <c>OnDeath</c>/event script, or the quest's own
/// script). This is the static half of the bucket-C signal: the completion logic is fully readable; what's
/// missing is the counter's runtime value, which Stage 2 re-derives from save state.
/// <para><see cref="IncrementScripts"/> are the <c>SCPT</c> FormIDs that bump the counter from <b>other</b>
/// scripts (the event sources — kills/activations); <see cref="SelfIncremented"/> is true when the quest's own
/// script also bumps it. A gate is <see cref="Bound"/> (reachable by this approach) when the counter is
/// incremented at all; <see cref="ExternallyIncremented"/> singles out the kill/event-count shape that Stage 2
/// targets.</para></summary>
public sealed record CounterGate(
    uint QuestFormId, string? QuestName, string? QuestEdid, string Counter,
    string Op, int Threshold, int? CompletingStage, bool CompletesQuest,
    IReadOnlyList<uint> IncrementScripts, bool SelfIncremented)
{
    /// <summary>The counter is incremented somewhere, so its value is in principle re-derivable from save state —
    /// i.e. this completion is reachable by the Stage-2 evaluator.</summary>
    public bool Bound => IncrementScripts.Count > 0 || SelfIncremented;

    /// <summary>The counter is bumped by a script OTHER than the quest's own — the kill/activation-event shape
    /// (the bucket-C target). Self-only gates are state machines internal to the quest.</summary>
    public bool ExternallyIncremented => IncrementScripts.Count > 0;
}

/// <summary>
/// Builds the <b>counter-gated completion graph</b> (ROADMAP §6 #16 Stage 1): correlates each player-facing
/// quest's counter-guarded completion effects with the scripts that increment those counters. This is a static,
/// masters-only analysis — it reads quest/script <i>source text</i> (already parsed by <see cref="QuestScript"/>),
/// derives no save state, and is fully measurable on its own. Its deliverable is the honest count of quests whose
/// completion is counter/event-gated and thus reachable by the Stage-2 save-state evaluator, before any of that
/// harder work is invested. Read-only.
/// </summary>
public static class CounterGatedQuests
{
    /// <summary>The QSDT stage-flag bit marking a stage as completing its quest (FO3/FNV "Complete Quest"); matches
    /// <c>QuestPipboy.CompleteQuestStageFlag</c>.</summary>
    private const byte CompleteQuestStageFlag = 0x01;

    /// <summary>Builds every counter-gate across <paramref name="quests"/>, binding each to the increment sites in
    /// <paramref name="increments"/> (matched by quest editor id + counter name). Includes gates whose counter has
    /// no increment site (<see cref="CounterGate.Bound"/> = false) so a probe can surface the decode gap. Only
    /// player-facing quests (named + with objectives) are considered — the Pip-Boy population.</summary>
    public static IReadOnlyList<CounterGate> Build(
        IEnumerable<QuestDefinition> quests, IEnumerable<CounterIncrement> increments)
    {
        // External increment sites, indexed by (questEdid, counter) — both case-insensitive.
        var external = new Dictionary<(string Edid, string Counter), List<uint>>(EdidCounterComparer.Instance);
        foreach (var inc in increments)
        {
            if (string.IsNullOrEmpty(inc.QuestEdid))
                continue;
            var key = (inc.QuestEdid, inc.Counter);
            (external.TryGetValue(key, out var list) ? list : external[key] = []).Add(inc.ScriptFormId);
        }

        var gates = new List<CounterGate>();
        foreach (var q in quests)
        {
            if (!q.IsPlayerFacing || q.LocalVars is not { Count: > 0 })
                continue;
            var locals = new HashSet<string>(q.LocalVars, StringComparer.OrdinalIgnoreCase);

            // Counters this quest bumps in its OWN script (GameMode + every stage result-script) — the bare or
            // qualified-self increment form. These are unambiguous because we know whose script we're reading.
            var selfIncremented = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var src in OwnScripts(q))
                foreach (var inc in QuestScript.ParseCounterIncrements(src))
                    if ((inc.QuestEdid.Length == 0 || EdidEquals(inc.QuestEdid, q.Edid)) && locals.Contains(inc.Counter))
                        selfIncremented.Add(inc.Counter);

            // Find counter-guarded completing effects in the quest's own scripts.
            var seen = new HashSet<(string Counter, int Stage)>();
            foreach (var src in OwnScripts(q))
                foreach (var e in QuestScript.Parse(src))
                {
                    if (!CompletesSelf(e, q, out var completingStage) || !e.Conditional)
                        continue;
                    foreach (var g in e.Guards)
                    {
                        if (QuestScript.FindCounterComparison(g, locals) is not { } cmp)
                            continue;
                        if (!seen.Add((cmp.Counter, completingStage ?? -1)))
                            continue; // de-dupe identical gates across the GameMode + stage scans
                        var ext = external.TryGetValue((q.Edid ?? "", cmp.Counter), out var scripts)
                            ? (IReadOnlyList<uint>)scripts
                            : [];
                        gates.Add(new CounterGate(
                            q.FormId, q.Name, q.Edid, cmp.Counter, cmp.Op, cmp.Threshold,
                            completingStage, CompletesQuest: true, ext, selfIncremented.Contains(cmp.Counter)));
                    }
                }
        }
        return gates;
    }

    /// <summary>Builds the broader <b>external event-completion graph</b> (ROADMAP §6 #16 Stage 1): player-facing
    /// quests completed by a <c>SCPT</c> other than their own — the single-event (one-kill/one-activation)
    /// completions that have no counter but are still reachable from the same dead-actor/world save signal Stage 2
    /// reads. A SetStage counts only when its target stage carries the QSDT-complete flag. <paramref name="counterGatedQuestIds"/>
    /// marks which results overlap the (high-confidence) counter-gated set.</summary>
    public static IReadOnlyList<ExternalCompletion> BuildExternalCompletions(
        IEnumerable<QuestDefinition> quests, IEnumerable<ExternalQuestEffect> effects,
        IReadOnlySet<uint> counterGatedQuestIds)
    {
        var byEdid = new Dictionary<string, QuestDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in quests)
            if (q.IsPlayerFacing && !string.IsNullOrEmpty(q.Edid))
                byEdid[q.Edid!] = q;

        // quest FormId -> (scripts, anyUnconditional, blocks)
        var acc = new Dictionary<uint, (HashSet<uint> Scripts, bool Unconditional, HashSet<string> Blocks, QuestDefinition Def)>();
        foreach (var e in effects)
        {
            if (!byEdid.TryGetValue(e.TargetEdid, out var q) || e.ScriptFormId == q.ScriptFormId)
                continue; // unknown/non-player-facing target, or the quest's OWN script (not external)
            var completes = e.Verb switch
            {
                QuestScriptVerb.CompleteQuest or QuestScriptVerb.CompleteAllObjectives => true,
                QuestScriptVerb.SetStage => q.Stages.Any(s => s.Index == e.Arg1 && (s.Flags & CompleteQuestStageFlag) != 0),
                _ => false,
            };
            if (!completes)
                continue;
            ref var entry = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(acc, q.FormId, out var existed);
            if (!existed)
                entry = ([], false, [], q);
            entry.Scripts.Add(e.ScriptFormId);
            entry.Unconditional |= !e.Conditional;
            entry.Blocks.Add(e.Block);
        }

        return acc.Select(kv => new ExternalCompletion(
                kv.Key, kv.Value.Def.Name, kv.Value.Def.Edid, [.. kv.Value.Scripts],
                kv.Value.Unconditional, counterGatedQuestIds.Contains(kv.Key), [.. kv.Value.Blocks]))
            .OrderBy(c => c.QuestName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The quest's own script source: its GameMode block plus every stage result-script.</summary>
    private static IEnumerable<string> OwnScripts(QuestDefinition q)
    {
        if (!string.IsNullOrEmpty(q.GameModeScript))
            yield return q.GameModeScript!;
        foreach (var s in q.Stages)
            if (!string.IsNullOrEmpty(s.ScriptText))
                yield return s.ScriptText!;
    }

    /// <summary>True when <paramref name="e"/> completes quest <paramref name="q"/> itself — <c>CompleteQuest</c>,
    /// <c>CompleteAllObjectives</c>, or a <c>SetStage</c> to a QSDT-complete stage. <paramref name="completingStage"/>
    /// is the target stage for the SetStage form, else null.</summary>
    private static bool CompletesSelf(QuestScriptEffect e, QuestDefinition q, out int? completingStage)
    {
        completingStage = null;
        if (!EdidEquals(e.TargetQuestEdid, q.Edid))
            return false;
        switch (e.Verb)
        {
            case QuestScriptVerb.CompleteQuest:
            case QuestScriptVerb.CompleteAllObjectives:
                return true;
            case QuestScriptVerb.SetStage when q.Stages.Any(s => s.Index == e.Arg1 && (s.Flags & CompleteQuestStageFlag) != 0):
                completingStage = e.Arg1;
                return true;
            default:
                return false;
        }
    }

    private static bool EdidEquals(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private sealed class EdidCounterComparer : IEqualityComparer<(string Edid, string Counter)>
    {
        public static readonly EdidCounterComparer Instance = new();
        public bool Equals((string Edid, string Counter) x, (string Edid, string Counter) y) =>
            string.Equals(x.Edid, y.Edid, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Counter, y.Counter, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Edid, string Counter) o) =>
            HashCode.Combine(o.Edid.ToLowerInvariant(), o.Counter.ToLowerInvariant());
    }
}
