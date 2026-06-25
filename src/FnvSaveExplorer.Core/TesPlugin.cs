using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace FnvSaveExplorer.Core;

/// <summary>
/// A minimal, read-only reader for a Bethesda TES4-generation plugin (<c>.esm</c>/<c>.esp</c>) as used
/// by Fallout 3 / New Vegas. It extracts just what FormID → display-name resolution needs: the ordered
/// master list (from the <c>TES4</c> header's <c>MAST</c> subrecords) and, for item-bearing record types
/// (and quests), each form's FormID and display name (the <c>FULL</c> field, falling back to the editor id
/// <c>EDID</c>).
///
/// <para><b>Streaming + GRUP-skipping.</b> The file is read forward; top-level <c>GRUP</c>s whose record
/// type we don't care about are skipped by seeking past them — <c>FalloutNV.esm</c> is ~245 MB, so only
/// the comparatively tiny item groups are actually decoded. FO3/FNV use <b>24-byte</b> record and group
/// headers; a record's <c>DataSize</c> excludes its 24-byte header, a group's <c>GroupSize</c> includes it.</para>
///
/// <para>This is read-only against the game files; it never modifies them and is built on demand (it is not
/// part of <see cref="FalloutSave"/> parsing), so save handling stays fast and offline.</para>
/// </summary>
public sealed class TesPlugin
{
    private const uint CompressedFlag = 0x00040000; // record flag: data is [u32 decompSize][zlib]
    private const uint LocalizedFlag  = 0x00000080; // TES4 header flag: FULL is a string-table id, not inline text

    private const int HeaderSize = 24; // record + group header width on FO3/FNV

    /// <summary>Record types whose forms can appear in an inventory and so warrant a readable name.</summary>
    private static readonly HashSet<string> ItemTypes =
    [
        "WEAP", "ARMO", "ALCH", "AMMO", "MISC", "BOOK", "NOTE", "KEYM", "IMOD",
        "CCRD", "CHIP", "CMNY", "CDCK", // New Vegas: caravan card, casino chip, caps ("money"), caravan deck
    ];

    /// <summary>
    /// Record types we decode for FormID → name resolution: <see cref="ItemTypes"/> plus <c>QUST</c> and
    /// <c>PERK</c>. Neither is an inventory item, so they never reach the inventory-only Pip-Boy tab mapping
    /// (<see cref="PluginDatabase.PipBoyTab"/>); naming quest forms identifies quest change forms (ROADMAP §6 #10),
    /// and naming <c>PERK</c> forms (FNV stores traits as PERKs too) lets the player perk list be resolved
    /// (ROADMAP §4n) — so both are indexed here yet deliberately kept out of <see cref="ItemTypes"/>.
    /// </summary>
    private static readonly HashSet<string> NamedTypes = [.. ItemTypes, "QUST", "PERK"];

    /// <summary>The quest verbs that can complete/advance/end a quest — harvested from every <c>SCPT</c> to size the
    /// event-completion graph (ROADMAP §6 #16 Stage 1). Pure display verbs (<c>SetObjectiveDisplayed</c>,
    /// <c>StartQuest</c>) are excluded.</summary>
    private static readonly HashSet<QuestScriptVerb> CompletingVerbs =
    [
        QuestScriptVerb.CompleteQuest, QuestScriptVerb.CompleteAllObjectives, QuestScriptVerb.SetStage,
        QuestScriptVerb.SetObjectiveCompleted, QuestScriptVerb.StopQuest, QuestScriptVerb.FailQuest,
    ];

    /// <summary>The plugin's file name (e.g. <c>FalloutNV.esm</c>).</summary>
    public string FileName { get; }

    /// <summary>The plugin's masters, in order — index <c>i</c> is the plugin-local high byte that refers to it.</summary>
    public IReadOnlyList<string> Masters { get; }

    /// <summary>
    /// Named forms, keyed by <b>plugin-local</b> FormID (its high byte is an index into
    /// <see cref="Masters"/>, or == <c>Masters.Count</c> for forms the plugin defines itself). <c>Type</c>
    /// is the record signature (<c>WEAP</c>/<c>ARMO</c>/<c>ALCH</c>/<c>AMMO</c>/<c>MISC</c>/…, or <c>QUST</c>) —
    /// for item types it is the basis for the item's Pip-Boy tab (see <see cref="PluginDatabase.PipBoyTab"/>);
    /// <c>QUST</c> is indexed only so quests can be named/identified, not as inventory. <c>NoteType</c> is the
    /// <c>NOTE</c> record's <c>DATA</c> media byte (0=Sound, 1=Text, 2=Image, 3=Voice — the holodisk-vs-text
    /// distinction, ROADMAP §4k.1 #6), or <c>-1</c> for non-notes / notes with no <c>DATA</c>.
    /// </summary>
    public IReadOnlyList<(uint LocalFormId, string Name, string Type, int NoteType)> Forms { get; }

    /// <summary>The <c>QUST</c> records' decoded stage/objective structure, keyed by <b>plugin-local</b>
    /// FormID (re-keyed into save space by <see cref="PluginDatabase"/>) — the masters side of the quest-log
    /// reader (ROADMAP §6 #10). Empty unless the plugin defines quests.</summary>
    public IReadOnlyList<QuestDefinition> Quests { get; }

    /// <summary>Each dialogue <c>INFO</c> record (by <b>plugin-local</b> FormID) whose result-script source text
    /// (<c>SCTX</c>) carries quest-affecting calls (<c>StartQuest</c>/<c>SetStage</c>/<c>SetObjective…</c>),
    /// naming target quests by editor id (ROADMAP §6 #16 Phase B). Empty unless the plugin was read with
    /// <c>readDialogue: true</c> — dialogue is the largest group, so it is parsed only when the Pip-Boy interpreter
    /// needs it. The <b>INFO's FormID matters</b>: when the player says that line the engine writes a change form
    /// for the INFO, so its presence in the save is the "this dialogue actually fired" signal that distinguishes a
    /// truly-started quest from a background-initialized one (Phase B step 2).</summary>
    public IReadOnlyList<(uint InfoFormId, IReadOnlyList<QuestScriptEffect> Effects, IReadOnlyList<InfoCondition> Conditions)> DialogueInfos { get; }

    /// <summary>All dialogue result-script quest effects, flattened across <see cref="DialogueInfos"/>.</summary>
    public IEnumerable<QuestScriptEffect> DialogueEffects => DialogueInfos.SelectMany(i => i.Effects);

    /// <summary>Counter increments harvested from the FULL source text of every <c>SCPT</c> record — the
    /// <c>set &lt;Quest&gt;.&lt;counter&gt; to &lt;Quest&gt;.&lt;counter&gt; ± N</c> assignments that bump a quest's
    /// kill/collect counter from an actor <c>OnDeath</c>/event script (ROADMAP §6 #16 Stage 1). Only the qualified
    /// form (which names its quest by editor id) is kept here; <see cref="ScriptFormId"/> is plugin-local and
    /// re-keyed by <see cref="PluginDatabase"/>. The matching counter-gates are built by
    /// <see cref="CounterGatedQuests"/>.</summary>
    public IReadOnlyList<CounterIncrement> CounterIncrements { get; }

    /// <summary>Quest-completing effects (<c>SetStage</c>/<c>CompleteQuest</c>/<c>CompleteAllObjectives</c>/
    /// <c>SetObjectiveCompleted</c>/<c>StopQuest</c>/<c>FailQuest</c>) harvested from the full source text of every
    /// <c>SCPT</c> record, targeting a quest by editor id (ROADMAP §6 #16 Stage 1). Includes the quests' own scripts;
    /// the analyzer (<see cref="CounterGatedQuests"/>) filters those out (by <see cref="QuestDefinition.ScriptFormId"/>)
    /// to find completions driven by an <b>external event script</b> (an actor <c>OnDeath</c>/<c>OnActivate</c>, a
    /// terminal, …) — the broader event-completion sizing alongside the counter-gated subset.</summary>
    public IReadOnlyList<ExternalQuestEffect> ExternalQuestEffects { get; }

    /// <summary>Base <c>NPC_</c>/<c>CREA</c> actor FormID → its attached <c>SCRI</c> script FormID (ROADMAP §6 #16
    /// Stage 2). The type-2 death registry records a UNIQUE killed actor by its base form, so this links a dead
    /// registry entry to the script (e.g. an <c>OnDeath</c> quest-completion). Plugin-local; re-keyed by
    /// <see cref="PluginDatabase"/>. Empty unless the plugin was read with <c>readActors: true</c>.</summary>
    public IReadOnlyList<(uint ActorFormId, uint ScriptFormId)> ActorScripts { get; }

    /// <summary>Placed <c>ACHR</c>/<c>ACRE</c> reference FormID → its <c>NAME</c> base actor FormID (ROADMAP §6 #16
    /// Stage 2), by descending the <c>CELL</c>/<c>WRLD</c> groups the normal reader skips. Needed to bind a
    /// runtime-SPAWNED actor: a created reference is instanced from a placed template <c>ACHR</c>, so resolving
    /// that template → base NPC_ → script recovers the spawned kill (e.g. the 6th Ghost Town Gunfight ganger).
    /// Plugin-local; re-keyed by <see cref="PluginDatabase"/>. Empty unless read with <c>readActors: true</c>.</summary>
    public IReadOnlyList<(uint RefFormId, uint BaseFormId)> PlacedActorBases { get; }

    /// <summary>Named placed reference <c>EDID</c> → its FormID (ROADMAP §6 #16 Stage 2) — lets a completion
    /// script's <c>&lt;Ref&gt;.Enable</c> editor id resolve to the reference a quest enables on completion.
    /// Plugin-local; re-keyed by <see cref="PluginDatabase"/>. Empty unless read with <c>readActors: true</c>.</summary>
    public IReadOnlyList<(string Edid, uint FormId)> PlacedRefEdids { get; }

    /// <summary>Each <c>(QuestEdid, RefEdid)</c> where a script block that calls <c>CompleteQuest &lt;Quest&gt;</c>
    /// also <c>Enable</c>s reference <c>RefEdid</c> (ROADMAP §6 #16 Stage 2). Finding such a ref enabled in the save
    /// proves the completion ran — the signal for activator/world-state-completed quests (e.g. That Lucky Old Sun,
    /// whose reflector-console completion enables the HELIOS One power FX). Empty unless read with the SCPT group.</summary>
    public IReadOnlyList<(string QuestEdid, string RefEdid)> CompletionEnables { get; }

    private TesPlugin(
        string fileName,
        IReadOnlyList<string> masters,
        IReadOnlyList<(uint, string, string, int)> forms,
        IReadOnlyList<QuestDefinition> quests,
        IReadOnlyList<(uint, IReadOnlyList<QuestScriptEffect>, IReadOnlyList<InfoCondition>)> dialogueInfos,
        IReadOnlyList<CounterIncrement> counterIncrements,
        IReadOnlyList<ExternalQuestEffect> externalQuestEffects,
        IReadOnlyList<(uint, uint)> actorScripts,
        IReadOnlyList<(uint, uint)> placedActorBases,
        IReadOnlyList<(string, uint)> placedRefEdids,
        IReadOnlyList<(string, string)> completionEnables)
    {
        FileName = fileName;
        Masters = masters;
        Forms = forms;
        Quests = quests;
        DialogueInfos = dialogueInfos;
        CounterIncrements = counterIncrements;
        ExternalQuestEffects = externalQuestEffects;
        ActorScripts = actorScripts;
        PlacedActorBases = placedActorBases;
        PlacedRefEdids = placedRefEdids;
        CompletionEnables = completionEnables;
    }

    public static TesPlugin Load(string path, bool readDialogue = false, bool readActors = false)
    {
        using var fs = File.OpenRead(path);
        return Read(fs, Path.GetFileName(path), readDialogue, readActors);
    }

    /// <summary>Parses a plugin from an arbitrary (seekable) stream — used by tests with an in-memory plugin.
    /// <paramref name="readDialogue"/> additionally descends into the <c>DIAL</c> group to collect <c>INFO</c>
    /// result-script quest effects (off by default — it is the heaviest group; ROADMAP §6 #16 Phase B).
    /// <paramref name="readActors"/> reads <c>NPC_</c>/<c>CREA</c> <c>SCRI</c> links for the Stage-2 kill-completion
    /// binding (cheap — base-actor groups only, no worldspace descent).</summary>
    public static TesPlugin Read(Stream fs, string fileName, bool readDialogue = false, bool readActors = false)
    {
        // ---- TES4 header record (always first) ----
        var sig = ReadSignature(fs) ?? throw new SaveFormatException($"{fileName}: empty file", 0);
        if (sig != "TES4")
            throw new SaveFormatException($"{fileName}: not a TES4 plugin (found '{sig}')", 0);
        var headerDataSize = ReadU32(fs);
        var headerFlags = ReadU32(fs);
        ReadExactly(fs, 12); // FormID + version-control / form-version
        var localized = (headerFlags & LocalizedFlag) != 0;
        var headerData = ReadExactly(fs, (int)headerDataSize);

        var masters = new List<string>();
        foreach (var (type, data) in ParseSubrecords(headerData))
            if (type == "MAST")
                masters.Add(ZString(data));

        // ---- top-level groups ----
        var forms = new List<(uint, string, string, int)>();
        var quests = new List<QuestDefinition>();
        var dialogueInfos = new List<(uint, IReadOnlyList<QuestScriptEffect>, IReadOnlyList<InfoCondition>)>();
        // SCPT FormID -> (GameMode block source, declared local-variable names) (ROADMAP §6 #16)
        var scripts = new Dictionary<uint, (string GameMode, List<string> Locals)>();
        var counterIncrements = new List<CounterIncrement>(); // qualified `set Quest.counter to counter ± N` (Stage 1)
        var externalQuestEffects = new List<ExternalQuestEffect>(); // completing effects from any SCPT (Stage 1)
        var actorScripts = new Dictionary<uint, uint>(); // base NPC_/CREA FormID -> SCRI script (Stage 2)
        var placedBases = new Dictionary<uint, uint>(); // placed ACHR/ACRE FormID -> NAME base actor (Stage 2)
        var placedEdids = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase); // placed ref EDID -> FormID (Stage 2)
        var completionEnables = new List<(string QuestEdid, string RefEdid)>(); // quest -> refs its completion enables (Stage 2)
        while (true)
        {
            var gsig = ReadSignature(fs);
            if (gsig is null)
                break; // clean EOF
            if (gsig != "GRUP")
                throw new SaveFormatException($"{fileName}: expected GRUP at top level, found '{gsig}'", (int)fs.Position - 4);

            var groupSize = ReadU32(fs);
            var label = ReadExactly(fs, 4);
            var groupType = ReadU32(fs);
            ReadExactly(fs, 8); // stamp + version + unknown
            var contentSize = (long)groupSize - HeaderSize;
            if (contentSize < 0)
                throw new SaveFormatException($"{fileName}: bad GRUP size {groupSize}", (int)fs.Position);

            var labelType = Encoding.ASCII.GetString(label);
            if (groupType == 0 && (NamedTypes.Contains(labelType) || labelType == "SCPT"))
                ReadRecords(fs, contentSize, localized, forms, quests, scripts, counterIncrements, externalQuestEffects, completionEnables);
            else if (groupType == 0 && labelType == "DIAL" && readDialogue)
                ReadInfoEffects(fs, contentSize, dialogueInfos); // descend DIAL -> Topic Children -> INFO (Phase B)
            else if (groupType == 0 && labelType is "NPC_" or "CREA" && readActors)
                ReadActorScripts(fs, contentSize, actorScripts); // base actor -> SCRI (Stage 2 kill-completion)
            else if (groupType == 0 && labelType is "CELL" or "WRLD" && readActors)
                ReadPlacedActors(fs, contentSize, placedBases, placedEdids); // placed ACHR->base + ref EDID->FormID (Stage 2)
            else
                fs.Seek(contentSize, SeekOrigin.Current); // skip the whole group without reading its bytes
        }

        // Link each quest to its SCPT script's GameMode block + local-variable names (the startup code; ROADMAP
        // §6 #16). The QUST and its SCPT live in the same plugin, so this is a plain plugin-local lookup.
        if (scripts.Count > 0)
            for (var i = 0; i < quests.Count; i++)
                if (quests[i].ScriptFormId != 0 && scripts.TryGetValue(quests[i].ScriptFormId, out var s))
                    quests[i] = quests[i] with { GameModeScript = s.GameMode, LocalVars = s.Locals };

        return new TesPlugin(fileName, masters, forms, quests, dialogueInfos, counterIncrements, externalQuestEffects,
            actorScripts.Select(kv => (kv.Key, kv.Value)).ToList(),
            placedBases.Select(kv => (kv.Key, kv.Value)).ToList(),
            placedEdids.Select(kv => (kv.Key, kv.Value)).ToList(),
            completionEnables);
    }

    /// <summary>R&D (full-decode, ROADMAP §6 #1): finds the record SIGNATURE (REFR/DOOR/STAT/NAVM/ACTI/…) for
    /// the given plugin-LOCAL FormIDs (low 24 bits; the high byte is ignored) by traversing every group in the
    /// plugin and reading only record/group <i>headers</i> — record data is seeked past, so it is cheap even on
    /// the 245&#160;MB <c>FalloutNV.esm</c>. This identifies what a change form points at when the name index
    /// can't (REFR/CELL/world records aren't indexed for naming), e.g. to confirm which record type a given
    /// change-form type byte corresponds to. Returns localId → signature for those found; stops early once all
    /// wanted ids are seen.</summary>
    /// <summary>A located record: its signature (REFR/DOOR/…), the <c>NAME</c> base form a placed reference
    /// instances (0 if none), its <c>FULL</c> display name (null if none), and whether it carries an <c>XMRK</c>
    /// subrecord — i.e. it is a <b>map marker</b> reference (how FO3/FNV mark world-map locations).</summary>
    public readonly record struct LocatedRecord(string Signature, uint BaseFormId, string? Name, bool IsMapMarker);

    public static Dictionary<uint, LocatedRecord> FindRecordSignatures(Stream fs, IReadOnlySet<uint> wantedLocalIds)
    {
        var found = new Dictionary<uint, LocatedRecord>();
        if (wantedLocalIds.Count == 0)
            return found;
        fs.Seek(0, SeekOrigin.Begin);

        var sig = ReadSignature(fs) ?? throw new SaveFormatException("empty file", 0);
        if (sig != "TES4")
            throw new SaveFormatException($"not a TES4 plugin (found '{sig}')", 0);
        var headerDataSize = ReadU32(fs);
        ReadExactly(fs, 16);                         // flags + FormID + version-control / form-version
        fs.Seek(headerDataSize, SeekOrigin.Current); // skip the TES4 header data

        void Walk(long end)
        {
            while (fs.Position < end && found.Count < wantedLocalIds.Count)
            {
                var s = ReadSignature(fs);
                if (s is null)
                    break;
                var size = ReadU32(fs);
                if (s == "GRUP")
                {
                    ReadExactly(fs, 16);             // rest of the 24-byte group header; GRUP size INCLUDES it
                    Walk(fs.Position + ((long)size - HeaderSize));
                }
                else
                {
                    var flags = ReadU32(fs);
                    var formId = ReadU32(fs) & 0xFFFFFF; // low 24 bits = plugin-local id
                    ReadExactly(fs, 8);              // version-control / form-version
                    if (wantedLocalIds.Contains(formId))
                    {
                        // For a wanted record (few), read its data to pull the NAME base form + FULL name, and to
                        // detect an XMRK subrecord (= a map marker reference). Cheap (only matched records).
                        uint baseForm = 0;
                        string? full = null;
                        var isMapMarker = false;
                        var data = ReadExactly(fs, (int)size);
                        if ((flags & CompressedFlag) != 0)
                            data = Decompress(data);
                        foreach (var (t, sub) in ParseSubrecords(data))
                        {
                            if (t == "NAME" && sub.Length >= 4) baseForm = BinaryPrimitives.ReadUInt32LittleEndian(sub);
                            else if (t == "FULL") full = ZString(sub);
                            else if (t == "XMRK") isMapMarker = true; // map-marker data subrecord
                        }
                        found.TryAdd(formId, new LocatedRecord(s, baseForm, full, isMapMarker));
                    }
                    else
                        fs.Seek(size, SeekOrigin.Current); // record `size` excludes the 24-byte header
                }
            }
        }
        Walk(fs.Length);
        return found;
    }

    /// <summary>R&D (full-decode): finds records whose <c>FULL</c> (or <c>EDID</c>) name contains
    /// <paramref name="substring"/> (case-insensitive), optionally restricted to one record signature
    /// (<paramref name="onlySig"/>, e.g. "PERK") for speed — non-matching records are seeked past without
    /// reading their data. Returns (localFormId, signature, name). Header-only traversal except for matched-sig
    /// records. Used to locate a named base form (e.g. a perk) when reverse-engineering where the save stores it.</summary>
    public static List<(uint FormId, string Sig, string Name)> FindByName(Stream fs, string substring, string? onlySig = null)
    {
        var hits = new List<(uint, string, string)>();
        fs.Seek(0, SeekOrigin.Begin);
        var sig0 = ReadSignature(fs) ?? throw new SaveFormatException("empty file", 0);
        if (sig0 != "TES4") throw new SaveFormatException($"not a TES4 plugin (found '{sig0}')", 0);
        var hdr = ReadU32(fs);
        ReadExactly(fs, 16);
        fs.Seek(hdr, SeekOrigin.Current);

        void Walk(long end)
        {
            while (fs.Position < end)
            {
                var s = ReadSignature(fs);
                if (s is null) break;
                var size = ReadU32(fs);
                if (s == "GRUP")
                {
                    ReadExactly(fs, 16);
                    Walk(fs.Position + ((long)size - HeaderSize));
                }
                else
                {
                    var flags = ReadU32(fs);
                    var formId = ReadU32(fs) & 0xFFFFFF;
                    ReadExactly(fs, 8);
                    if (onlySig is not null && s != onlySig)
                    {
                        fs.Seek(size, SeekOrigin.Current);
                        continue;
                    }
                    var data = ReadExactly(fs, (int)size);
                    if ((flags & CompressedFlag) != 0) data = Decompress(data);
                    string? full = null, edid = null;
                    foreach (var (t, sub) in ParseSubrecords(data))
                    {
                        if (t == "FULL") full = ZString(sub);
                        else if (t == "EDID") edid = ZString(sub);
                    }
                    var name = full ?? edid;
                    if (name is not null && name.Contains(substring, StringComparison.OrdinalIgnoreCase))
                        hits.Add((formId, s, name));
                }
            }
        }
        Walk(fs.Length);
        return hits;
    }

    /// <summary>Descends a dialogue (<c>DIAL</c>) group — which nests <c>INFO</c> records inside per-topic
    /// "Topic Children" sub-groups — collecting the quest-affecting calls in each <c>INFO</c>'s result-script
    /// source text (<c>SCTX</c>). Recurses through nested <c>GRUP</c>s and skips non-<c>INFO</c> records (the
    /// <c>DIAL</c> topics themselves). ROADMAP §6 #16 Phase B.</summary>
    private static void ReadInfoEffects(
        Stream fs, long contentSize, List<(uint, IReadOnlyList<QuestScriptEffect>, IReadOnlyList<InfoCondition>)> infos)
    {
        var end = fs.Position + contentSize;
        while (fs.Position < end)
        {
            var sig = ReadSignature(fs);
            if (sig is null)
                break;
            var size = ReadU32(fs);
            if (sig == "GRUP")
            {
                ReadExactly(fs, 16); // rest of the 24-byte group header
                ReadInfoEffects(fs, (long)size - HeaderSize, infos); // recurse into the nested (Topic Children) group
                continue;
            }

            var flags = ReadU32(fs);
            var formId = ReadU32(fs);
            ReadExactly(fs, 8); // version-control / form-version
            var data = ReadExactly(fs, (int)size);
            if (sig != "INFO")
                continue; // a DIAL topic (or other) record — only INFO carries the result scripts
            if ((flags & CompressedFlag) != 0)
                data = Decompress(data);
            var effects = new List<QuestScriptEffect>();
            var conditions = new List<InfoCondition>();
            foreach (var (type, sub) in ParseSubrecords(data))
            {
                if (type == "SCTX" && ZString(sub) is { Length: > 0 } src)
                    effects.AddRange(QuestScript.Parse(src));
                else if (type == "CTDA" && ParseQuestCondition(sub) is { } c)
                    conditions.Add(c);
            }
            // Keep an INFO if it carries quest effects (Phase B seed) OR a quest-state precondition (CTDA spike) —
            // its presence as a save change form means the line was SAID, so both its effects fired and its
            // conditions held at that time.
            if (effects.Count > 0 || conditions.Count > 0)
                infos.Add((formId, effects, conditions)); // keyed by INFO FormID so the said-INFO change form matches
        }
    }

    /// <summary>Decodes a 28-byte FNV <c>CTDA</c> subrecord, returning the condition only when it is one of the
    /// quest-state functions (<see cref="QuestFunction"/>) whose first parameter is a quest FormID — the signal
    /// the precondition spike needs. Returns null for any other function (skipped). Plugin-local FormID; re-keyed
    /// by <see cref="PluginDatabase"/>.</summary>
    private static InfoCondition? ParseQuestCondition(byte[] ctda)
    {
        if (ctda.Length < 28)
            return null;
        var span = ctda.AsSpan();
        var op = (byte)(ctda[0] >> 5);                                       // comparison operator (high 3 bits)
        var compare = BinaryPrimitives.ReadSingleLittleEndian(span[4..]);    // value compared against
        var func = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);       // function opcode
        var param1 = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);    // quest FormID (for these functions)
        var param2 = BinaryPrimitives.ReadUInt32LittleEndian(span[16..]);    // stage (GetStageDone)
        if (!Enum.IsDefined(typeof(QuestFunction), (int)func))
            return null;
        return new InfoCondition((QuestFunction)func, param1, op, compare, (int)param2);
    }

    /// <summary>Reads the records (and defensively skips any nested groups) inside one top-level item group.</summary>
    private static void ReadRecords(
        Stream fs, long contentSize, bool localized,
        List<(uint, string, string, int)> forms, List<QuestDefinition> quests,
        Dictionary<uint, (string GameMode, List<string> Locals)> scripts,
        List<CounterIncrement> counterIncrements,
        List<ExternalQuestEffect> externalQuestEffects,
        List<(string QuestEdid, string RefEdid)> completionEnables)
    {
        var end = fs.Position + contentSize;
        while (fs.Position < end)
        {
            var sig = ReadSignature(fs);
            if (sig is null)
                break;
            var size = ReadU32(fs);
            if (sig == "GRUP")
            {
                ReadExactly(fs, 16); // rest of the 24-byte group header
                fs.Seek((long)size - HeaderSize, SeekOrigin.Current);
                continue;
            }

            var flags = ReadU32(fs);
            var formId = ReadU32(fs);
            ReadExactly(fs, 8); // version-control / form-version
            var data = ReadExactly(fs, (int)size);
            if ((flags & CompressedFlag) != 0)
                data = Decompress(data);

            var subs = ParseSubrecords(data);

            if (sig == "SCPT")
            {
                // A script record: keep its GameMode block + declared local-variable names (from the source text,
                // SCTX), keyed by FormID, for the quest link above. SCDA is compiled bytecode (ignored). Also harvest
                // counter increments (`set Quest.counter to counter ± N`) from the FULL script — these live in actor
                // OnDeath/event scripts (no GameMode block of their own), so they must be scanned here, not just on
                // the quest-linked GameMode (ROADMAP §6 #16 Stage 1).
                foreach (var (type, sub) in subs)
                    if (type == "SCTX" && ZString(sub) is { } src)
                    {
                        if (ExtractGameMode(src) is { } gm)
                            scripts[formId] = (gm, ExtractLocals(src));
                        foreach (var (questEdid, counter, delta) in QuestScript.ParseCounterIncrements(src))
                            if (questEdid.Length > 0) // qualified — names its quest; bare ones are ambiguous here
                                counterIncrements.Add(new CounterIncrement(formId, questEdid, counter, delta));
                        foreach (var (block, body) in SplitBlocks(src))
                            foreach (var e in QuestScript.Parse(body))
                                if (CompletingVerbs.Contains(e.Verb) && !string.IsNullOrEmpty(e.TargetQuestEdid))
                                    externalQuestEffects.Add(new ExternalQuestEffect(formId, e.TargetQuestEdid, e.Verb, e.Arg1, e.Conditional, block));
                        // Completion-enable (ROADMAP §6 #16 Stage 2): a SCRIPT that calls CompleteQuest Q also Enables
                        // specific refs — finding one enabled in the save proves the completion ran (the activator/
                        // world-state signal, e.g. HELIOS One's reflector console completes VMS03 in OnActivate and
                        // powers on the FX in a GameMode sequence). Whole-script scope ties enables across blocks (the
                        // OnActivate completion and the GameMode FX sequence it triggers belong to the same completion).
                        var completes = QuestScript.Parse(src)
                            .Where(e => e.Verb == QuestScriptVerb.CompleteQuest && !string.IsNullOrEmpty(e.TargetQuestEdid))
                            .Select(e => e.TargetQuestEdid).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        if (completes.Count > 0)
                            foreach (var refEdid in QuestScript.ParseEnableRefs(src))
                                foreach (var q in completes)
                                    completionEnables.Add((q, refEdid));
                    }
                continue;
            }

            string? edid = null, full = null;
            var noteType = -1;
            foreach (var (type, sub) in subs)
            {
                if (type == "EDID") edid = ZString(sub);
                else if (type == "FULL" && !localized) full = ZString(sub);
                else if (type == "DATA" && sig == "NOTE" && sub.Length >= 1) noteType = sub[0]; // 0=Sound 1=Text 2=Image 3=Voice
            }
            var name = full ?? edid;
            if (!string.IsNullOrEmpty(name))
                forms.Add((formId, name, sig, noteType)); // sig is the record type (WEAP/ARMO/ALCH/AMMO/MISC/…)
            if (sig == "QUST")
                quests.Add(ParseQuest(formId, subs, localized, full, edid));
        }
    }

    /// <summary>
    /// Decodes a <c>QUST</c> record's ordered subrecords into its <see cref="QuestDefinition"/> (ROADMAP §6 #10).
    /// FO3/FNV lay out a quest as a <b>stages block</b> then an <b>objectives block</b>:
    /// <list type="bullet">
    /// <item>A stage begins with <c>INDX</c> (the stage index); its <c>QSDT</c> carries the stage flag byte and
    /// <c>CNAM</c> the Pip-Boy log-entry text. (The exact <c>QSDT</c> bit meaning is kept raw — see
    /// <see cref="QuestStageDef.Flags"/>.)</item>
    /// <item>An objective begins with <c>QOBJ</c> (the objective index), <c>NNAM</c> is its display text, and
    /// one or more <c>QSTA</c> name its target reference(s) — the placed refs whose enable-state encodes
    /// objective progress in the save (ROADMAP §6 #10). A <c>QSTA</c>'s leading u32 is the target FormID.</item>
    /// </list>
    /// Parsing is defensive: a malformed/short subrecord is skipped rather than throwing.
    /// </summary>
    private static QuestDefinition ParseQuest(uint formId, List<(string Type, byte[] Data)> subs, bool localized, string? full, string? edid)
    {
        var stages = new List<QuestStageDef>();
        var objectives = new List<QuestObjectiveDef>();
        byte dataFlags = 0; // QUST DATA byte 0: bit0 = Start Game Enabled (ROADMAP §6 #16)
        uint scriptFormId = 0; // SCRI: the FormID of the quest's SCPT script (linked to GameMode text later)

        int? pendingStage = null;
        byte pendingFlags = 0;
        string? pendingLog = null;
        string? pendingScript = null; // SCTX result-script source text for the stage (ROADMAP §6 #16)

        void FlushStage()
        {
            if (pendingStage is { } idx)
                stages.Add(new QuestStageDef(idx, pendingFlags, pendingLog, pendingScript));
            pendingStage = null;
            pendingFlags = 0;
            pendingLog = null;
            pendingScript = null;
        }

        int curObjIndex = 0;
        string? curObjText = null;
        var curTargets = new List<uint>();
        var inObjective = false;

        void FlushObjective()
        {
            if (inObjective)
                objectives.Add(new QuestObjectiveDef(curObjIndex, curObjText, curTargets));
            curObjIndex = 0;
            curObjText = null;
            curTargets = [];
            inObjective = false;
        }

        foreach (var (type, sub) in subs)
        {
            switch (type)
            {
                case "DATA" when sub.Length >= 1:
                    dataFlags = sub[0]; // QUST DATA: byte 0 = quest flags (bit0 = Start Game Enabled)
                    break;
                case "SCRI" when sub.Length >= 4:
                    scriptFormId = BinaryPrimitives.ReadUInt32LittleEndian(sub); // the quest's SCPT script FormID
                    break;
                case "INDX":
                    FlushStage();
                    pendingStage = sub.Length >= 2 ? BinaryPrimitives.ReadInt16LittleEndian(sub) : sub.Length == 1 ? sub[0] : 0;
                    break;
                case "QSDT" when pendingStage is not null && sub.Length >= 1:
                    pendingFlags = sub[0];
                    break;
                case "CNAM" when pendingStage is not null && !localized:
                    pendingLog ??= ZString(sub);
                    break;
                case "SCTX" when pendingStage is not null:
                    // A stage may carry several log-entry result scripts; concatenate so the interpreter scans them all.
                    pendingScript = pendingScript is null ? ZString(sub) : pendingScript + "\n" + ZString(sub);
                    break;
                case "QOBJ":
                    FlushStage();
                    FlushObjective();
                    inObjective = true;
                    curObjIndex = sub.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(sub)
                        : sub.Length >= 2 ? BinaryPrimitives.ReadInt16LittleEndian(sub) : 0;
                    break;
                case "NNAM" when inObjective && !localized:
                    curObjText ??= ZString(sub);
                    break;
                case "QSTA" when inObjective && sub.Length >= 4:
                    curTargets.Add(BinaryPrimitives.ReadUInt32LittleEndian(sub)); // leading u32 = target reference FormID
                    break;
            }
        }
        FlushStage();
        FlushObjective();

        return new QuestDefinition(formId, stages, objectives, dataFlags, full, edid, scriptFormId);
    }

    /// <summary>Extracts the <c>Begin GameMode … End</c> block(s) from a script's source text (ROADMAP §6 #16) —
    /// the code the engine runs each frame, where a Start-Game-Enabled quest sets its own startup stage. Other
    /// block types (<c>MenuMode</c>, <c>OnActivate</c>, …) and the variable declarations are dropped. Returns
    /// null when the script has no GameMode block. A block ends at a standalone <c>End</c> (distinct from
    /// <c>endif</c>, which the interpreter's own scan handles).</summary>
    private static string? ExtractGameMode(string scriptText)
    {
        if (string.IsNullOrEmpty(scriptText))
            return null;
        var sb = new StringBuilder();
        var inBlock = false;
        foreach (var raw in scriptText.Split('\n'))
        {
            var line = raw;
            var semi = line.IndexOf(';');
            if (semi >= 0) line = line[..semi];
            var trimmed = line.Trim();
            if (!inBlock)
            {
                if (trimmed.StartsWith("begin", StringComparison.OrdinalIgnoreCase) &&
                    trimmed[5..].TrimStart().StartsWith("gamemode", StringComparison.OrdinalIgnoreCase))
                    inBlock = true;
            }
            else if (trimmed.Equals("end", StringComparison.OrdinalIgnoreCase))
            {
                inBlock = false;
                sb.Append('\n');
            }
            else
            {
                sb.Append(raw).Append('\n');
            }
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    /// <summary>Reads base <c>NPC_</c>/<c>CREA</c> records' <c>SCRI</c> (their attached script FormID) — the
    /// actor↔script link the Stage-2 kill-completion binding needs (ROADMAP §6 #16). A unique killed actor is
    /// recorded in the save's type-2 death registry by its base form, so this maps that base to the script whose
    /// <c>OnDeath</c> block completes a quest.</summary>
    private static void ReadActorScripts(Stream fs, long contentSize, Dictionary<uint, uint> actorScripts)
    {
        var end = fs.Position + contentSize;
        while (fs.Position < end)
        {
            var sig = ReadSignature(fs);
            if (sig is null) break;
            var size = ReadU32(fs);
            if (sig == "GRUP") { ReadExactly(fs, 16); fs.Seek((long)size - HeaderSize, SeekOrigin.Current); continue; }
            var flags = ReadU32(fs);
            var formId = ReadU32(fs);
            ReadExactly(fs, 8);
            var data = ReadExactly(fs, (int)size);
            if ((flags & CompressedFlag) != 0) data = Decompress(data);
            foreach (var (type, sub) in ParseSubrecords(data))
                if (type == "SCRI" && sub.Length >= 4)
                {
                    actorScripts[formId] = BinaryPrimitives.ReadUInt32LittleEndian(sub);
                    break;
                }
        }
    }

    /// <summary>Recursively descends a <c>CELL</c>/<c>WRLD</c> group, recording each placed <c>ACHR</c>/<c>ACRE</c>
    /// reference's <c>NAME</c> (its base actor FormID — the runtime-spawned-actor binding) and every NAMED placed
    /// reference's <c>EDID</c> → FormID (so a completion-script <c>&lt;Ref&gt;.Enable</c> editor id resolves to the
    /// ref a quest enables on completion). A <c>REFR</c>'s <c>EDID</c>, when present, is its first subrecord, so it
    /// is read cheaply (header + the EDID) without parsing the rest; anonymous refs (the bulk) are skipped by seek.
    /// ROADMAP §6 #16 Stage 2.</summary>
    private static void ReadPlacedActors(Stream fs, long contentSize, Dictionary<uint, uint> placedBases, Dictionary<string, uint> placedEdids)
    {
        var end = fs.Position + contentSize;
        while (fs.Position < end)
        {
            var sig = ReadSignature(fs);
            if (sig is null) break;
            var size = ReadU32(fs);
            if (sig == "GRUP")
            {
                ReadExactly(fs, 16);
                ReadPlacedActors(fs, (long)size - HeaderSize, placedBases, placedEdids); // recurse into block/subblock/children
                continue;
            }
            var flags = ReadU32(fs);
            var formId = ReadU32(fs);
            ReadExactly(fs, 8);

            if (sig is "ACHR" or "ACRE") // placed actor: read NAME (base) + EDID, both from the full data
            {
                var data = ReadExactly(fs, (int)size);
                if ((flags & CompressedFlag) != 0) data = Decompress(data);
                foreach (var (type, sub) in ParseSubrecords(data))
                {
                    if (type == "NAME" && sub.Length >= 4) placedBases[formId] = BinaryPrimitives.ReadUInt32LittleEndian(sub);
                    else if (type == "EDID" && ZString(sub) is { Length: > 0 } e) placedEdids[e] = formId;
                }
            }
            else if (sig == "REFR" && (flags & CompressedFlag) == 0 && size >= 6) // named placed ref: EDID is the first subrecord
            {
                var headerBytes = ReadExactly(fs, 6); // [type:4][size:u16]
                if (Encoding.ASCII.GetString(headerBytes, 0, 4) == "EDID")
                {
                    var esize = BinaryPrimitives.ReadUInt16LittleEndian(headerBytes.AsSpan(4, 2));
                    var estr = ReadExactly(fs, esize);
                    if (ZString(estr) is { Length: > 0 } e) placedEdids[e] = formId;
                    fs.Seek((long)size - 6 - esize, SeekOrigin.Current);
                }
                else
                    fs.Seek((long)size - 6, SeekOrigin.Current); // no leading EDID — anonymous ref, skip the rest
            }
            else
                fs.Seek(size, SeekOrigin.Current);
        }
    }

    /// <summary>Splits a script's source text into its <c>Begin &lt;Type&gt; … End</c> blocks, yielding each block's
    /// lower-cased type keyword (<c>gamemode</c>, <c>ondeath</c>, <c>onactivate</c>, …) and its body text (ROADMAP
    /// §6 #16 Stage 1). The block type is what tells a kill-driven completion (<c>OnDeath</c>) from a per-frame
    /// world-poll (<c>GameMode</c>). A block ends at a standalone <c>End</c> (not <c>endif</c>).</summary>
    private static IEnumerable<(string Block, string Text)> SplitBlocks(string? scriptText)
    {
        if (string.IsNullOrEmpty(scriptText))
            yield break;
        string? block = null;
        var sb = new StringBuilder();
        foreach (var raw in scriptText.Split('\n'))
        {
            var line = raw;
            var semi = line.IndexOf(';');
            if (semi >= 0) line = line[..semi];
            var trimmed = line.Trim();
            if (block is null)
            {
                if (trimmed.StartsWith("begin", StringComparison.OrdinalIgnoreCase) &&
                    (trimmed.Length == 5 || char.IsWhiteSpace(trimmed[5])))
                {
                    var rest = trimmed[5..].Trim();
                    var sp = rest.IndexOfAny([' ', '\t']);
                    block = (sp >= 0 ? rest[..sp] : rest).ToLowerInvariant();
                    sb.Clear();
                }
            }
            else if (trimmed.Equals("end", StringComparison.OrdinalIgnoreCase))
            {
                yield return (block, sb.ToString());
                block = null;
            }
            else
            {
                sb.Append(raw).Append('\n');
            }
        }
    }

    /// <summary>The local-variable type keywords a FNV script declares variables with (case-insensitive).</summary>
    private static readonly HashSet<string> VarTypes =
        new(StringComparer.OrdinalIgnoreCase) { "short", "int", "long", "float", "ref", "reference", "string_var", "array_var" };

    /// <summary>Extracts a script's declared local-variable names from its source text — declaration lines
    /// <c>&lt;type&gt; &lt;name&gt;</c> (e.g. <c>short DoOnceMessage</c>, <c>float StartTimer</c>) that precede the
    /// first <c>Begin</c> block (ROADMAP §6 #16). The interpreter treats these as script-internal (do-once / timer)
    /// state, distinct from globals and world-query functions.</summary>
    private static List<string> ExtractLocals(string scriptText)
    {
        var locals = new List<string>();
        foreach (var raw in scriptText.Split('\n'))
        {
            var line = raw;
            var semi = line.IndexOf(';');
            if (semi >= 0) line = line[..semi];
            var tokens = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;
            if (tokens[0].Equals("Begin", StringComparison.OrdinalIgnoreCase)) break; // declarations precede the blocks
            if (tokens.Length >= 2 && VarTypes.Contains(tokens[0]))
                locals.Add(tokens[1]);
        }
        return locals;
    }

    /// <summary>R&amp;D (ROADMAP §6 #16): dump the raw subrecords of one <c>QUST</c> record from a plugin file,
    /// matched by <b>plugin-local</b> FormID. Returns each subrecord's signature, length, and (for text fields)
    /// decoded text — used to discover whether FNV masters retain stage result-script <b>source text</b>
    /// (<c>SCTX</c>) and conditions (<c>CTDA</c>) we can statically scan, or only compiled bytecode (<c>SCDA</c>).</summary>
    public static IReadOnlyList<(string Type, int Size, string? Text)> DumpQust(Stream fs, uint localFormId)
    {
        if (ReadSignature(fs) != "TES4")
            throw new SaveFormatException("not a TES4 plugin", 0);
        var headerDataSize = ReadU32(fs);
        ReadExactly(fs, 16); // flags(4) + formId(4) + version-control(8)
        ReadExactly(fs, (int)headerDataSize);

        while (true)
        {
            var gsig = ReadSignature(fs);
            if (gsig is null) break;
            if (gsig != "GRUP") break;
            var groupSize = ReadU32(fs);
            var label = Encoding.ASCII.GetString(ReadExactly(fs, 4));
            ReadU32(fs);
            ReadExactly(fs, 8);
            var contentSize = (long)groupSize - HeaderSize;
            if (label != "QUST") { fs.Seek(contentSize, SeekOrigin.Current); continue; }

            var end = fs.Position + contentSize;
            while (fs.Position < end)
            {
                var sig = ReadSignature(fs);
                if (sig is null) break;
                var size = ReadU32(fs);
                if (sig == "GRUP") { ReadExactly(fs, 16); fs.Seek((long)size - HeaderSize, SeekOrigin.Current); continue; }
                var flags = ReadU32(fs);
                var formId = ReadU32(fs);
                ReadExactly(fs, 8);
                var data = ReadExactly(fs, (int)size);
                if (formId != localFormId) continue;
                if ((flags & CompressedFlag) != 0) data = Decompress(data);
                var result = new List<(string, int, string?)>();
                foreach (var (type, sub) in ParseSubrecords(data))
                {
                    string? text = type is "SCTX" or "CNAM" or "NNAM" or "FULL" or "EDID" ? ZString(sub) : null;
                    result.Add((type, sub.Length, text));
                }
                return result;
            }
            break;
        }
        return [];
    }

    /// <summary>
    /// Splits a record's data block into <c>[type:4][size:u16][data]</c> subrecords. Handles the <c>XXXX</c>
    /// escape, where an <c>XXXX</c> subrecord carries a u32 holding the real (over-u16) size of the next field.
    /// </summary>
    private static List<(string Type, byte[] Data)> ParseSubrecords(byte[] data)
    {
        var result = new List<(string, byte[])>();
        var p = 0;
        var oversize = -1;
        while (p + 6 <= data.Length)
        {
            var type = Encoding.ASCII.GetString(data, p, 4);
            int size = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 4, 2));
            p += 6;
            if (type == "XXXX")
            {
                if (p + 4 <= data.Length)
                    oversize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p, 4));
                p += size;
                continue;
            }
            var realSize = oversize >= 0 ? oversize : size;
            oversize = -1;
            if (realSize < 0 || p + realSize > data.Length)
                realSize = data.Length - p; // truncated/garbled — take what's left rather than throwing
            result.Add((type, data.AsSpan(p, realSize).ToArray()));
            p += realSize;
        }
        return result;
    }

    /// <summary>Inflates a compressed record's data: <c>[u32 decompressedSize][zlib stream]</c>.</summary>
    private static byte[] Decompress(byte[] data)
    {
        if (data.Length < 4)
            return data;
        var size = BinaryPrimitives.ReadUInt32LittleEndian(data);
        using var src = new MemoryStream(data, 4, data.Length - 4);
        using var z = new ZLibStream(src, CompressionMode.Decompress);
        var outBuf = new byte[size];
        var read = 0;
        while (read < outBuf.Length)
        {
            var r = z.Read(outBuf, read, outBuf.Length - read);
            if (r <= 0) break;
            read += r;
        }
        return outBuf;
    }

    /// <summary>Reads a zero-terminated Latin1/Windows-1252 string (names/editor ids are stored this way).</summary>
    private static string ZString(byte[] data)
    {
        var n = Array.IndexOf(data, (byte)0);
        if (n < 0) n = data.Length;
        return Encoding.Latin1.GetString(data, 0, n);
    }

    // ---- stream primitives (little-endian; throw SaveFormatException with offset on truncation) ----

    private static string? ReadSignature(Stream s)
    {
        var buf = new byte[4];
        var n = s.Read(buf, 0, 4);
        if (n == 0) return null; // clean EOF
        if (n < 4) throw new SaveFormatException("truncated 4-byte signature", (int)s.Position);
        return Encoding.ASCII.GetString(buf);
    }

    private static uint ReadU32(Stream s) => BinaryPrimitives.ReadUInt32LittleEndian(ReadExactly(s, 4));

    private static byte[] ReadExactly(Stream s, int count)
    {
        if (count < 0)
            throw new SaveFormatException($"negative length {count}", (int)s.Position);
        var buf = new byte[count];
        var off = 0;
        while (off < count)
        {
            var r = s.Read(buf, off, count - off);
            if (r <= 0)
                throw new SaveFormatException($"unexpected EOF: needed {count} byte(s)", (int)s.Position);
            off += r;
        }
        return buf;
    }
}

/// <summary>One stage of a <c>QUST</c> definition: its <paramref name="Index"/> (the <c>INDX</c> stage index),
/// the raw <c>QSDT</c> <paramref name="Flags"/> byte (kept raw — its completion/fail bit semantics are not yet
/// FNV-verified, ROADMAP §6 #10), the Pip-Boy log-entry text (<c>CNAM</c>), and the stage's <b>result-script
/// source text</b> (<c>SCTX</c>, all of the stage's log-entry scripts concatenated) — the literal
/// <c>SetObjectiveDisplayed</c>/<c>SetStage</c>/<c>CompleteQuest</c> calls the Pip-Boy interpreter scans
/// (ROADMAP §6 #16). All are null when the stage records none.</summary>
public sealed record QuestStageDef(int Index, byte Flags, string? LogText, string? ScriptText = null);

/// <summary>One objective of a <c>QUST</c> definition: its <paramref name="Index"/> (<c>QOBJ</c>), display
/// <paramref name="Text"/> (<c>NNAM</c>), and the FormIDs of its target reference(s) (<c>QSTA</c>) — the placed
/// refs whose save-side enable-state encodes whether the objective is active (ROADMAP §6 #10).</summary>
public sealed record QuestObjectiveDef(int Index, string? Text, IReadOnlyList<uint> TargetFormIds);

/// <summary>One quest-state <c>CTDA</c> condition on a dialogue <c>INFO</c> (ROADMAP §6 #16 CTDA spike). FNV
/// CTDA is 28 bytes: <c>[op+flags 1][unused 3][compare value f32 4][function index u32 4][param1 4][param2 4]
/// [run-on 4][reference 4]</c>. We keep only the quest-state functions whose first parameter is a quest:
/// <see cref="QuestFunction.GetStage"/> (param2 unused), <see cref="QuestFunction.GetStageDone"/> (param2 =
/// stage), <see cref="QuestFunction.GetQuestRunning"/>/<see cref="QuestFunction.GetQuestCompleted"/>.
/// <see cref="QuestFormId"/> is the param1 quest FormID (<b>plugin-local</b>; re-keyed into save space by
/// <see cref="PluginDatabase"/>). <see cref="Op"/> is the comparison operator (<c>type &gt;&gt; 5</c>:
/// 0=Eq 1=Ne 2=Gt 3=Ge 4=Lt 5=Le) and <see cref="CompareValue"/> the value it is compared against — so an
/// INFO the player SAID carrying <c>GetStage X &gt;= N</c> / <c>GetQuestCompleted X == 1</c> is proof X reached
/// that state when the line fired (monotonic for completion).</summary>
public sealed record InfoCondition(QuestFunction Function, uint QuestFormId, byte Op, float CompareValue, int Param2);

/// <summary>The FNV script-function opcodes (CTDA function index) that query another quest's state by FormID.</summary>
public enum QuestFunction { GetQuestRunning = 56, GetStage = 58, GetStageDone = 59, GetQuestCompleted = 397 }

/// <summary>A <c>QUST</c> record's decoded structure: its FormID (plugin-local as read from a
/// <see cref="TesPlugin"/>, re-keyed into save space by <see cref="PluginDatabase"/>) plus its stages and
/// objectives. This is the static quest <i>definition</i> from the masters; the player's progress against it
/// lives in the save's change forms (read by <c>QuestLog</c>).
/// <para><see cref="DataFlags"/> is the <c>DATA</c> subrecord's first byte (FO3/FNV quest flags): bit0
/// <c>0x01</c> = <b>Start Game Enabled</b> (the engine starts the quest at game load), bit2 <c>0x04</c> =
/// Allow repeated conversation topics, bit3 <c>0x08</c> = Allow repeated stages. <see cref="Name"/> is the
/// quest's <c>FULL</c> display name (null when the quest has none — a dialogue/script container that never
/// appears in the Pip-Boy); a player-facing quest has a name <i>and</i> objectives (ROADMAP §6 #16).
/// <see cref="Edid"/> is the quest's editor id (e.g. <c>VMQ01</c>): script calls like
/// <c>SetStage VMQ01 10</c> name their target quest by editor id, so the interpreter resolves them via
/// it. <see cref="ScriptFormId"/> is the quest's <c>SCRI</c> (the FormID of its <c>SCPT</c> script); after
/// linking, <see cref="GameModeScript"/> holds that script's <c>Begin GameMode … End</c> block source — the
/// code that runs at load and (for Start-Game-Enabled quests) sets the quest's startup stage (ROADMAP
/// §6 #16). <see cref="LocalVars"/> are the script's declared local variable names (do-once flags / timers); the
/// interpreter uses them to tell a startup guard (only local vars + constants) from a world-state-gated one
/// (functions / globals).</para></summary>
public sealed record QuestDefinition(
    uint FormId, IReadOnlyList<QuestStageDef> Stages, IReadOnlyList<QuestObjectiveDef> Objectives,
    byte DataFlags = 0, string? Name = null, string? Edid = null,
    uint ScriptFormId = 0, string? GameModeScript = null, IReadOnlyList<string>? LocalVars = null)
{
    /// <summary>The quest's <c>DATA</c> "Start Game Enabled" flag (bit0): the engine starts these at game load,
    /// so a Start-Game-Enabled quest with a displayed objective shows in the Pip-Boy with no save delta.</summary>
    public bool StartGameEnabled => (DataFlags & 0x01) != 0;

    /// <summary>A player-facing quest carries a display name and at least one objective; only these can appear in
    /// the Pip-Boy Quests list (dialogue/script-only quests have neither). The first-order Pip-Boy filter.</summary>
    public bool IsPlayerFacing => !string.IsNullOrEmpty(Name) && Objectives.Count > 0;
}
