namespace FnvSaveExplorer.Core;

/// <summary>
/// Resolves the FormIDs that appear in a save to human-readable item names by reading the game's ESM/ESP
/// masters (see <see cref="TesPlugin"/>). Every plugin in the save's load order is indexed into the
/// <b>save's</b> FormID space, so a save FormID looks up directly.
///
/// <para><b>DLC renumbering.</b> Inside its own file a plugin numbers forms against <i>its own</i> master
/// list, so a form's high byte there rarely equals its high byte in this save's load order. For each plugin
/// we therefore build a remap: a local high byte <c>j &lt; masterCount</c> → that master's index in the
/// save load order (matched by file name); <c>j == masterCount</c> → the plugin itself. The form is then
/// re-keyed into save space. Plugins are processed in load order, so a later plugin's override of a name
/// wins — matching how the game resolves it.</para>
///
/// <para><b>Plugin discovery.</b> Plugins are located by file name. The game <c>Data</c> folder supplies the
/// base game + DLC; a <b>Mod Organizer 2</b> <c>mods\</c> folder (which keeps each mod's files in
/// <c>mods\&lt;Mod&gt;\</c> and only merges them into <c>Data</c> via a virtual filesystem at launch) supplies
/// the rest — each active plugin lives at the root of its mod folder. With no folders available the database
/// is simply empty and FormIDs keep displaying as hex.</para>
/// </summary>
public sealed class PluginDatabase
{
    private static readonly string[] PluginExtensions = [".esm", ".esp", ".esl"];

    private readonly Dictionary<uint, string> _names;
    private readonly Dictionary<uint, string> _types; // FormID -> record signature (WEAP/ARMO/ALCH/AMMO/MISC/…)
    private readonly Dictionary<uint, int> _noteTypes; // FormID -> NOTE media byte (0=Sound 1=Text 2=Image 3=Voice)
    private readonly Dictionary<uint, QuestDefinition> _quests; // FormID -> QUST stage/objective structure (§6 #10)

    /// <summary>The game <c>Data</c> folder the base/DLC names came from, or <c>null</c> when none was found.</summary>
    public string? DataFolder { get; }

    /// <summary>The MO2 <c>mods\</c> folder used for mod plugins, or <c>null</c> when none was used.</summary>
    public string? ModsFolder { get; }

    /// <summary>The load-order plugins that were found and successfully parsed.</summary>
    public IReadOnlyList<string> ResolvedPlugins { get; }

    /// <summary>FormID → display name, in the save's FormID space.</summary>
    public IReadOnlyDictionary<uint, string> Names => _names;

    public int Count => _names.Count;

    private readonly HashSet<uint> _dialogueStarted; // quest FormIDs an INFO StartQuest/SetStage-targets (Phase B)
    private readonly Dictionary<uint, IReadOnlyList<QuestScriptEffect>> _dialogueInfoEffects; // INFO FormID -> its result-script effects
    private readonly Dictionary<uint, IReadOnlyList<InfoCondition>> _dialogueInfoConditions; // INFO FormID -> its quest-state preconditions
    private readonly IReadOnlyList<CounterIncrement> _counterIncrements; // qualified `set Quest.counter` increments (§6 #16 Stage 1)
    private readonly IReadOnlyList<ExternalQuestEffect> _externalQuestEffects; // completing effects from any SCPT (§6 #16 Stage 1)
    private readonly IReadOnlyDictionary<uint, uint> _actorScripts; // base actor FormID -> SCRI script (§6 #16 Stage 2)
    private readonly IReadOnlyDictionary<uint, uint> _placedActorBases; // placed ACHR FormID -> base actor (§6 #16 Stage 2)
    private readonly IReadOnlyDictionary<uint, IReadOnlySet<uint>> _completionEnableRefs; // quest -> refs its completion enables (§6 #16 Stage 2)
    private IReadOnlyList<CounterGate>? _counterGates; // lazily-built counter-gated completion graph
    private IReadOnlyList<ExternalCompletion>? _externalCompletions; // lazily-built external event-completion graph

    private PluginDatabase(Dictionary<uint, string> names, Dictionary<uint, string> types, Dictionary<uint, int> noteTypes, Dictionary<uint, QuestDefinition> quests, string? dataFolder, string? modsFolder, IReadOnlyList<string> resolved, HashSet<uint>? dialogueStarted = null, Dictionary<uint, IReadOnlyList<QuestScriptEffect>>? dialogueInfoEffects = null, Dictionary<uint, IReadOnlyList<InfoCondition>>? dialogueInfoConditions = null, IReadOnlyList<CounterIncrement>? counterIncrements = null, IReadOnlyList<ExternalQuestEffect>? externalQuestEffects = null, IReadOnlyDictionary<uint, uint>? actorScripts = null, IReadOnlyDictionary<uint, uint>? placedActorBases = null, IReadOnlyDictionary<uint, IReadOnlySet<uint>>? completionEnableRefs = null)
    {
        _names = names;
        _types = types;
        _noteTypes = noteTypes;
        _quests = quests;
        DataFolder = dataFolder;
        ModsFolder = modsFolder;
        ResolvedPlugins = resolved;
        _dialogueStarted = dialogueStarted ?? [];
        _dialogueInfoEffects = dialogueInfoEffects ?? [];
        _dialogueInfoConditions = dialogueInfoConditions ?? [];
        _counterIncrements = counterIncrements ?? [];
        _externalQuestEffects = externalQuestEffects ?? [];
        _actorScripts = actorScripts ?? new Dictionary<uint, uint>();
        _placedActorBases = placedActorBases ?? new Dictionary<uint, uint>();
        _completionEnableRefs = completionEnableRefs ?? new Dictionary<uint, IReadOnlySet<uint>>();
    }

    /// <summary>Quest FormIDs (save-space) that a dialogue <c>INFO</c> result script <c>StartQuest</c>/<c>SetStage</c>
    /// targets — the quests the player triggers through conversation rather than a quest script (ROADMAP §6 #16
    /// Phase B). Empty unless the database was built with <c>withDialogue: true</c>.</summary>
    public IReadOnlySet<uint> DialogueStartedQuests => _dialogueStarted;

    /// <summary>Dialogue <c>INFO</c> FormIDs (save-space) → that INFO's result-script quest effects (targeting quests
    /// by editor id). The save writes a change form for an INFO the player has said, so an INFO from this map that is
    /// also present in the save is a dialogue trigger that ACTUALLY FIRED — the Phase B step-2 "genuinely started
    /// (not background-init)" signal the Pip-Boy interpreter seeds from. Empty unless built with
    /// <c>withDialogue: true</c>.</summary>
    public IReadOnlyDictionary<uint, IReadOnlyList<QuestScriptEffect>> DialogueInfoEffects => _dialogueInfoEffects;

    /// <summary>Dialogue <c>INFO</c> FormIDs (save-space) → that INFO's quest-state <c>CTDA</c> preconditions
    /// (ROADMAP §6 #16 CTDA spike). An INFO present in the save was SAID, so its conditions held when it fired;
    /// a <c>GetQuestCompleted X</c>/<c>GetStage X &gt;= N</c> condition is therefore proof X reached that state.
    /// Empty unless built with <c>withDialogue: true</c>.</summary>
    public IReadOnlyDictionary<uint, IReadOnlyList<InfoCondition>> DialogueInfoConditions => _dialogueInfoConditions;

    /// <summary>Counter increments (qualified <c>set Quest.counter to counter ± N</c>) across the load order, with
    /// each <see cref="CounterIncrement.ScriptFormId"/> re-keyed into save space (ROADMAP §6 #16 Stage 1).</summary>
    public IReadOnlyList<CounterIncrement> CounterIncrements => _counterIncrements;

    /// <summary>The counter-gated completion graph (built once on first use): the player-facing quests whose
    /// completion is gated on a runtime counter, with the scripts that increment it. The size of the bucket-C
    /// prize Stage 2's save-state evaluator targets (ROADMAP §6 #16 Stage 1).</summary>
    public IReadOnlyList<CounterGate> CounterGates =>
        _counterGates ??= CounterGatedQuests.Build(_quests.Values, _counterIncrements);

    /// <summary>The broader external event-completion graph (built once on first use): player-facing quests
    /// completed by a script other than their own (the single-kill/activation completions), with the counter-gated
    /// subset flagged. Sizes the full bucket-C prize (ROADMAP §6 #16 Stage 1).</summary>
    public IReadOnlyList<ExternalCompletion> ExternalCompletions =>
        _externalCompletions ??= CounterGatedQuests.BuildExternalCompletions(
            _quests.Values, _externalQuestEffects, CounterGates.Where(g => g.Bound).Select(g => g.QuestFormId).ToHashSet());

    /// <summary>Base actor FormID (save-space) → its <c>SCRI</c> script FormID — the link used to bind a dead
    /// registry entry (a unique killed actor) to the script that completes a quest (ROADMAP §6 #16 Stage 2).
    /// Empty unless the database was built with <c>withActors: true</c>.</summary>
    public IReadOnlyDictionary<uint, uint> ActorScripts => _actorScripts;

    /// <summary>Placed <c>ACHR</c> reference FormID (save-space) → its base actor FormID — used to bind a
    /// runtime-spawned actor through its placed template (ROADMAP §6 #16 Stage 2). Empty unless built with
    /// <c>withActors: true</c>.</summary>
    public IReadOnlyDictionary<uint, uint> PlacedActorBases => _placedActorBases;

    /// <summary>Quest FormID (save-space) → the reference FormIDs its completion script <c>Enable</c>s (ROADMAP
    /// §6 #16 Stage 2). A running quest with one of these refs enabled in the save is completed (the
    /// activator/world-state-completion signal). Empty unless built with <c>withActors: true</c>.</summary>
    public IReadOnlyDictionary<uint, IReadOnlySet<uint>> CompletionEnableRefs => _completionEnableRefs;

    /// <summary>An empty database; every <see cref="Resolve"/> returns <c>null</c>.</summary>
    public static readonly PluginDatabase Empty = new([], [], [], [], null, null, []);

    /// <summary>
    /// Builds a database for <paramref name="save"/>, auto-detecting the game <c>Data</c> folder (or using an
    /// override) and, when given, an MO2 <paramref name="modsFolder"/> for mod plugins.
    /// </summary>
    public static PluginDatabase ForSave(FalloutSave save, string? dataFolderOverride = null, string? modsFolder = null, bool withDialogue = false, bool withActors = false)
    {
        var folder = GameDataLocator.FindDataFolder(dataFolderOverride);
        var paths = CollectPlugins(folder, modsFolder);
        return paths.Count == 0 ? Empty : Build(save.Plugins, paths, folder, modsFolder, withDialogue, withActors);
    }

    /// <summary>Builds a database from a load order and an explicit game <c>Data</c> folder.</summary>
    public static PluginDatabase Build(IReadOnlyList<string> loadOrder, string dataFolder, bool withDialogue = false, bool withActors = false)
        => Build(loadOrder, CollectPlugins(dataFolder, null), dataFolder, null, withDialogue, withActors);

    /// <summary>Builds a database from a load order and a plugin-name → file-path map (used in tests).</summary>
    public static PluginDatabase Build(IReadOnlyList<string> loadOrder, IReadOnlyDictionary<string, string> pluginPaths, bool withDialogue = false, bool withActors = false)
        => Build(loadOrder, pluginPaths, null, null, withDialogue, withActors);

    private static PluginDatabase Build(
        IReadOnlyList<string> loadOrder, IReadOnlyDictionary<string, string> pluginPaths, string? dataFolder, string? modsFolder, bool withDialogue = false, bool withActors = false)
    {
        var names = new Dictionary<uint, string>();
        var types = new Dictionary<uint, string>();
        var noteTypes = new Dictionary<uint, int>();
        var quests = new Dictionary<uint, QuestDefinition>();
        var resolved = new List<string>();
        var dialogueTargetEdids = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // INFO StartQuest/SetStage targets
        var infoEffects = new Dictionary<uint, IReadOnlyList<QuestScriptEffect>>(); // save-space INFO FormID -> effects
        var infoConditions = new Dictionary<uint, IReadOnlyList<InfoCondition>>(); // save-space INFO FormID -> quest-state preconditions
        var counterIncrements = new List<CounterIncrement>(); // qualified counter increments, ScriptFormId re-keyed (§6 #16 Stage 1)
        var externalQuestEffects = new List<ExternalQuestEffect>(); // completing effects from any SCPT, ScriptFormId re-keyed
        var actorScripts = new Dictionary<uint, uint>(); // base actor FormID -> SCRI, both re-keyed to save space (§6 #16 Stage 2)
        var placedActorBases = new Dictionary<uint, uint>(); // placed ACHR FormID -> base actor, both re-keyed (§6 #16 Stage 2)
        var placedRefEdids = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase); // placed ref EDID -> save-space FormID
        var completionEnables = new List<(string QuestEdid, string RefEdid)>(); // quest -> refs its completion enables (editor ids)

        // Case-insensitive load-order index, for mapping a plugin's masters back to save indices.
        var indexOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < loadOrder.Count; i++)
            indexOf[loadOrder[i]] = i;

        for (var i = 0; i < loadOrder.Count; i++)
        {
            if (!pluginPaths.TryGetValue(loadOrder[i], out var file))
                continue; // plugin not found in the Data folder or the mods tree

            TesPlugin plugin;
            try
            {
                plugin = TesPlugin.Load(file, withDialogue, withActors);
            }
            catch (SaveFormatException)
            {
                continue; // skip an unreadable plugin rather than failing the whole build
            }

            // Map each plugin-local high byte to a save load-order index.
            var remap = new int[256];
            Array.Fill(remap, -1);
            for (var j = 0; j < plugin.Masters.Count && j < remap.Length; j++)
                remap[j] = indexOf.TryGetValue(plugin.Masters[j], out var idx) ? idx : -1;
            if (plugin.Masters.Count < remap.Length)
                remap[plugin.Masters.Count] = i; // the plugin's own forms

            foreach (var (localFormId, name, type, noteType) in plugin.Forms)
            {
                if (Remap(localFormId, remap) is not { } saveFormId)
                    continue; // references a master that isn't in this save's load order
                names[saveFormId] = name; // later (load-order) plugin overrides win
                types[saveFormId] = type;
                if (noteType >= 0)
                    noteTypes[saveFormId] = noteType;
            }

            // QUST definitions: re-key the quest's own FormID AND each objective's target-ref FormIDs through
            // the same remap, so a target ref looks up directly against the save's change forms (§6 #10).
            foreach (var q in plugin.Quests)
            {
                if (Remap(q.FormId, remap) is not { } questFormId)
                    continue;
                var objectives = new List<QuestObjectiveDef>(q.Objectives.Count);
                foreach (var o in q.Objectives)
                {
                    var targets = new List<uint>(o.TargetFormIds.Count);
                    foreach (var t in o.TargetFormIds)
                        if (Remap(t, remap) is { } st)
                            targets.Add(st);
                    objectives.Add(new QuestObjectiveDef(o.Index, o.Text, targets));
                }
                quests[questFormId] = new QuestDefinition(
                    questFormId, q.Stages, objectives, q.DataFlags, q.Name, q.Edid, q.ScriptFormId, q.GameModeScript, q.LocalVars);
            }

            // Dialogue INFO result-script effects target quests by editor id; collect the StartQuest/SetStage
            // targets (per INFO, re-keying the INFO's FormID into save space) so they resolve to quest FormIDs once
            // every plugin's quests are known (Phase B).
            foreach (var (infoFormId, effects, conditions) in plugin.DialogueInfos)
            {
                foreach (var e in effects)
                    if (e.Verb is QuestScriptVerb.StartQuest or QuestScriptVerb.SetStage && !string.IsNullOrEmpty(e.TargetQuestEdid))
                        dialogueTargetEdids.Add(e.TargetQuestEdid);
                if (Remap(infoFormId, remap) is not { } saveInfoFormId)
                    continue;
                if (effects.Count > 0)
                    infoEffects[saveInfoFormId] = effects; // later (load-order) plugin overrides win, like names
                // Re-key each quest-state precondition's target quest FormID into save space (CTDA spike).
                if (conditions.Count > 0)
                {
                    var rekeyed = new List<InfoCondition>(conditions.Count);
                    foreach (var c in conditions)
                        if (Remap(c.QuestFormId, remap) is { } q)
                            rekeyed.Add(c with { QuestFormId = q });
                    if (rekeyed.Count > 0)
                        infoConditions[saveInfoFormId] = rekeyed;
                }
            }

            // Counter increments target their quest by editor id (no FormID re-key needed), but the ScriptFormId is
            // a plugin-local form, so re-key it into save space for Stage 2's script→actor mapping (§6 #16 Stage 1).
            foreach (var inc in plugin.CounterIncrements)
                if (Remap(inc.ScriptFormId, remap) is { } saveScriptId)
                    counterIncrements.Add(inc with { ScriptFormId = saveScriptId });
            foreach (var e in plugin.ExternalQuestEffects)
                if (Remap(e.ScriptFormId, remap) is { } saveScriptId)
                    externalQuestEffects.Add(e with { ScriptFormId = saveScriptId });
            // Re-key both the base actor FormID and its script FormID into save space (Stage 2). Later plugins win.
            foreach (var (actorFormId, scriptFormId) in plugin.ActorScripts)
                if (Remap(actorFormId, remap) is { } a && Remap(scriptFormId, remap) is { } sc)
                    actorScripts[a] = sc;
            foreach (var (refFormId, baseFormId) in plugin.PlacedActorBases)
                if (Remap(refFormId, remap) is { } r && Remap(baseFormId, remap) is { } b)
                    placedActorBases[r] = b;
            foreach (var (edid, refFormId) in plugin.PlacedRefEdids)
                if (Remap(refFormId, remap) is { } r)
                    placedRefEdids[edid] = r; // EDID -> save-space ref FormID (later plugins win)
            completionEnables.AddRange(plugin.CompletionEnables); // (questEdid, refEdid) — editor ids, resolved below

            resolved.Add(loadOrder[i]);
        }

        // Resolve dialogue start/advance targets (by editor id) to quest FormIDs in save space (for diagnostics).
        var dialogueStarted = new HashSet<uint>();
        foreach (var q in quests.Values)
            if (q.Edid is { } edid && dialogueTargetEdids.Contains(edid))
                dialogueStarted.Add(q.FormId);

        // Resolve completion-enable pairs (questEdid, refEdid) -> quest FormID -> set of ref FormIDs (§6 #16 Stage 2).
        var questByEdid = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in quests.Values)
            if (!string.IsNullOrEmpty(q.Edid))
                questByEdid[q.Edid!] = q.FormId;
        var completionEnableRefs = new Dictionary<uint, IReadOnlySet<uint>>();
        foreach (var (questEdid, refEdid) in completionEnables)
            if (questByEdid.TryGetValue(questEdid, out var qFormId) && placedRefEdids.TryGetValue(refEdid, out var rFormId))
                ((HashSet<uint>)(completionEnableRefs.TryGetValue(qFormId, out var set) ? set : completionEnableRefs[qFormId] = new HashSet<uint>())).Add(rFormId);

        return new PluginDatabase(names, types, noteTypes, quests, dataFolder, modsFolder, resolved, dialogueStarted, infoEffects, infoConditions, counterIncrements, externalQuestEffects, actorScripts, placedActorBases, completionEnableRefs);
    }

    /// <summary>Re-keys a plugin-local FormID into save space using the plugin's high-byte → save-index
    /// <paramref name="remap"/>, or null when its master isn't in this save's load order.</summary>
    private static uint? Remap(uint localFormId, int[] remap)
    {
        var saveHigh = remap[(int)(localFormId >> 24)];
        return saveHigh < 0 ? null : ((uint)saveHigh << 24) | (localFormId & 0x00FFFFFF);
    }

    /// <summary>
    /// Builds a plugin-name → file-path map from the game <c>Data</c> folder (top-level) and, when given, an
    /// MO2 <c>mods\</c> folder (each mod's root). The Data folder wins, so the real base game / DLC files are
    /// preferred over any copy bundled inside a mod.
    /// </summary>
    public static Dictionary<string, string> CollectPlugins(string? dataFolder, string? modsFolder)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (dataFolder is not null && Directory.Exists(dataFolder))
            foreach (var f in EnumeratePlugins(dataFolder))
                paths.TryAdd(Path.GetFileName(f), f);
        if (modsFolder is not null && Directory.Exists(modsFolder))
            foreach (var mod in Directory.EnumerateDirectories(modsFolder))
                foreach (var f in EnumeratePlugins(mod)) // MO2 maps each mod's root into Data, so plugins sit here
                    paths.TryAdd(Path.GetFileName(f), f);
        return paths;
    }

    private static IEnumerable<string> EnumeratePlugins(string folder) =>
        Directory.EnumerateFiles(folder)
            .Where(f => PluginExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));

    /// <summary>The display name for a save FormID, <c>"(created)"</c> for runtime forms, or <c>null</c> if unknown.</summary>
    public string? Resolve(uint formId)
    {
        if ((formId >> 24) == 0xFF)
            return "(created)";
        return _names.TryGetValue(formId, out var name) ? name : null;
    }

    /// <summary>The base-form record signature for a save FormID (<c>WEAP</c>/<c>ARMO</c>/<c>ALCH</c>/
    /// <c>AMMO</c>/<c>MISC</c>/…), or <c>null</c> if unknown.</summary>
    public string? RecordType(uint formId) => _types.TryGetValue(formId, out var t) ? t : null;

    /// <summary>The decoded <c>QUST</c> definition (stages + objectives + target refs) for a save FormID, or
    /// <c>null</c> if the FormID isn't a known quest. The masters side of the quest-log reader (ROADMAP §6 #10).</summary>
    public QuestDefinition? Quest(uint formId) => _quests.TryGetValue(formId, out var q) ? q : null;

    /// <summary>All known quest definitions, keyed by save FormID.</summary>
    public IReadOnlyDictionary<uint, QuestDefinition> Quests => _quests;

    /// <summary>The media type of a <c>NOTE</c> form — <c>Text</c> / <c>Voice</c> / <c>Sound</c> / <c>Image</c>
    /// (the holodisk-vs-text distinction) — read from the base form's <c>DATA</c> byte, or <c>null</c> if the
    /// FormID isn't a known note. This is base-form metadata: the save stores only which notes are held and
    /// whether they're read, never the note's media type/text (ROADMAP §4k.1 #6).</summary>
    public string? NoteMediaType(uint formId) => _noteTypes.TryGetValue(formId, out var t)
        ? t switch { 0 => "Sound", 1 => "Text", 2 => "Image", 3 => "Voice", _ => $"Type{t}" }
        : null;

    /// <summary>The Pip-Boy tab a save FormID's item appears under, or <c>null</c> if its record type is
    /// unknown. The category is a pure function of the base form's record type (it is not stored in the
    /// save) — see <see cref="PipBoyTab"/>.</summary>
    public string? Category(uint formId) => RecordType(formId) is { } t ? PipBoyTab(t) : null;

    /// <summary>
    /// Maps a base-form record signature to its Pip-Boy tab (verified in-game on a real VNV save):
    /// <list type="bullet">
    /// <item><c>WEAP</c> → Weapons, <c>ARMO</c> → Apparel, <c>AMMO</c> → Ammo.</item>
    /// <item><c>ALCH</c> <b>and</b> <c>BOOK</c> → <b>Aid</b>. The Aid tab is "anything single-use with an
    /// effect": food/chems/stimpaks (<c>ALCH</c>), skill magazines (timed boost — also <c>ALCH</c>), and
    /// skill <b>books</b> (permanent, single-use — <c>BOOK</c>, e.g. "Duck and Cover!").</item>
    /// <item><c>NOTE</c> → "Notes" — shown under Pip-Boy <b>Data → Notes</b>, not an item tab. (Most notes
    /// aren't carried inventory at all; see the notes-log task in ROADMAP §6.)</item>
    /// <item>Everything else → <b>Misc</b>: <c>MISC</c>, currency <c>CMNY</c>, caravan cards
    /// <c>CCRD</c>/<c>CDCK</c>, casino chips <c>CHIP</c>, weapon mods <c>IMOD</c>, and <b>keys</b>
    /// (<c>KEYM</c> — the Pip-Boy collapses all keys into one "Keyring" pseudo-row, a UI grouping that is
    /// not stored in the save).</item>
    /// </list>
    /// </summary>
    public static string PipBoyTab(string recordType) => recordType switch
    {
        "WEAP" => "Weapons",
        "ARMO" => "Apparel",
        "ALCH" or "BOOK" => "Aid",
        "AMMO" => "Ammo",
        "NOTE" => "Notes",
        _ => "Misc",
    };
}
