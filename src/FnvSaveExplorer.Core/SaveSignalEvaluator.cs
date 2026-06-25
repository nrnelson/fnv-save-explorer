namespace FnvSaveExplorer.Core;

/// <summary>
/// Decides whether a <see cref="CompletionRule"/>'s trigger has fired in a given save (ROADMAP §6 #16, Stage A).
/// This is the unified dispatcher for the per-trigger save checks that <see cref="QuestPipboy"/> used to run as
/// separate inline passes — the bodies here are lifted verbatim from those passes. The save-derived sets each pass
/// recomputed (the said-INFO set, the death registry + actor→script map, created-reference corpses, enabled refs)
/// are built <b>once</b> in the constructor, so <see cref="TriggerFired"/> is a cheap lookup. Read-only.
/// </summary>
public sealed class SaveSignalEvaluator
{
    private readonly HashSet<uint> _saidInfoPresent = [];
    private readonly IReadOnlySet<uint> _dead;
    private readonly IReadOnlySet<uint> _enabled;
    private readonly HashSet<uint> _firedScripts = [];
    private readonly List<HashSet<uint>> _createdScripts = [];
    private readonly PluginDatabase _db;

    public SaveSignalEvaluator(FalloutSave save, PluginDatabase db)
    {
        _db = db;

        // An INFO the player SAID gets a change form written for it — so a present change form == the line fired.
        foreach (var cf in save.EnumerateChangeForms())
            _saidInfoPresent.Add(cf.FormId);

        _dead = save.DeadReferences();
        _enabled = save.EnabledReferences();

        // Scripts run by a DEAD actor (an OnDeath block that has fired): the single-kill path + the counter count.
        foreach (var d in _dead)
            if (ScriptOf(d) is var s && s != 0)
                _firedScripts.Add(s);

        // Created references (runtime-spawned corpses) → the scripts their resolved base actors run.
        foreach (var (_, referenced) in save.CreatedReferenceForms())
        {
            var scripts = new HashSet<uint>();
            foreach (var f in referenced)
                if (ScriptOf(f) is var s && s != 0)
                    scripts.Add(s);
            if (scripts.Count > 0)
                _createdScripts.Add(scripts);
        }
    }

    /// <summary>The script a base actor (NPC_/CREA) runs, for a save-space FormID that is either that base itself
    /// or a placed ACHR whose NAME base it is; 0 when none.</summary>
    private uint ScriptOf(uint formId) =>
        _db.ActorScripts.TryGetValue(formId, out var s) ? s
        : _db.PlacedActorBases.TryGetValue(formId, out var b) && _db.ActorScripts.TryGetValue(b, out var s2) ? s2
        : 0;

    /// <summary>True when <paramref name="rule"/>'s trigger has fired in the save. The caller is responsible for
    /// gating to already-running quests (so a fired rule only ever reclassifies active → completed).</summary>
    public bool TriggerFired(CompletionRule rule) => rule.Trigger switch
    {
        CompletionTrigger.Kill => rule.Scripts.Any(_firedScripts.Contains),
        CompletionTrigger.Counter => CounterReached(rule),
        CompletionTrigger.Activate => rule.EnableRefs.Any(_enabled.Contains),
        CompletionTrigger.DialogueSetStage or CompletionTrigger.CtdaProof => _saidInfoPresent.Contains(rule.InfoFormId),
        _ => false,
    };

    /// <summary>Counter gate: count how many counter-incrementing actors are dead. A unique ganger is a dead
    /// registry base; a spawned ganger is a created-reference corpse whose resolved base runs the increment script.
    /// The two kinds are disjoint, so they sum without double-counting.</summary>
    private bool CounterReached(CompletionRule rule)
    {
        var inc = rule.IncrementScripts.ToHashSet();
        var directDead = _dead.Count(d => inc.Contains(ScriptOf(d)));   // unique ganger bases
        var spawnedDead = _createdScripts.Count(s => s.Overlaps(inc));  // spawned (created) gangers
        return directDead + spawnedDead >= rule.Threshold;
    }
}
