using FnvSaveExplorer.Core;

if (args.Length < 2)
{
    Console.Error.WriteLine(
        """
        FNV Save Explorer CLI

        Usage:
          fnvsave dump  <save.fos>            Show header, screenshot info, and plugins
          fnvsave flt   <save.fos> [count]    Dump raw File Location Table uint32s (R&D)
          fnvsave check <save.fos>            Verify round-trip byte-identity
          fnvsave setlevel <in.fos> <out.fos> <level>   Safe edit demo (writes a new file)
          fnvsave probe <save.fos> [count]    Classify File Location Table + dump what offsets point to
          fnvsave hex   <save.fos> <offset> [len]   Hex-dump bytes at an absolute offset (0x.. ok)
          fnvsave globals <save.fos>          List global data table 1 records (types 0-11)
          fnvsave stats <save.fos>            Show decoded Misc Stats counters
          fnvsave formids <save.fos> [n]      Show the FormID array (iref -> FormID)
          fnvsave findplayer <save.fos>       Locate the player change forms via the FormID array
          fnvsave playerdump <save.fos>       Dump player change-form anchors + hex (R&D for skills)
          fnvsave irefscan <save.fos> <off> <len>   Scan a byte range for valid iref+count pairs (R&D for inventory)
          fnvsave special <save.fos>          Show the player's SPECIAL attributes
          fnvsave setspecial <in.fos> <out.fos> S P E C I A L   Edit SPECIAL (writes a new file)
          fnvsave skills <save.fos>           Show stored skill modifications (actor-value entries)
          fnvsave setskill <in.fos> <out.fos> <skill> <value>  Edit a stored skill (writes a new file)
          fnvsave inventory <save.fos> [dataDir]   Show the player's inventory (resolves item names from
                                              the game masters; dataDir overrides Data-folder auto-detect)
          fnvsave names <save.fos> [dataDir]  Report FormID -> name resolution status (which masters resolved)
          fnvsave notes <save.fos> [dataDir]  List the player's Pip-Boy Data -> Notes — READ and UNREAD (§4k/§4k.1)
          fnvsave perks <save.fos> [dataDir]  List the player's perks + traits (§4n; resolves PERK forms via masters)
          fnvsave pipboy <save.fos> [dataDir]   The COMPUTED in-game Pip-Boy quest list (§6 #16): interprets the
                                              masters' quest scripts (SGE startup + reached-stage effects + guard eval).
                                              active/completed + displayed objectives. (Only "Back in the Saddle" omitted.)
          fnvsave quests <save.fos> [dataDir] [--raw]   List quests whose state the save records (§6 #10): stages
                                              (done/time) + objectives ([active]/[done], from CHANGE_QUEST_OBJECTIVES).
                                              NOT the Pip-Boy list (use `pipboy` for that). --raw hex-dumps each form
          fnvsave notescan <dir>             Walk every .fos in a folder and aggregate the read-note markers:
                                              changeFlags values + whether each type-0x1F marker resolves to a
                                              NOTE record + inventory-reference confirmation (R&D §4k.1 #1-3)
          fnvsave resolve <save.fos> <formId> [dataDir]   Look up a FormID: record type + name + source plugin,
                                              and where it appears (FormID array iref / inventory / read note)
          fnvsave recid <save.fos> <formId> [formId...]   Identify the masters record SIGNATURE (REFR/DOOR/CHAL/…)
                                              + a placed ref's base form, for FormIDs the name index can't resolve (§6 #1)
          fnvsave findname <save.fos> "<substring>" [SIG]   Find a base form by NAME across the masters -> save-space
                                              FormID (SIG e.g. PERK restricts+speeds). R&D for locating where state lives (§6 #1)
          fnvsave setcount <in.fos> <out.fos> <formId> <count>  Edit a stack count (writes a new file)
          fnvsave setcondition <in.fos> <out.fos> <formId> <value>  Edit a stack's condition/health (new file)
          fnvsave caps <save.fos>             Show the player's caps (the 0x0000000F inventory stack)
          fnvsave setcaps <in.fos> <out.fos> <caps>  Edit the player's caps (writes a new file)
          fnvsave karma <save.fos>            Show the player's karma   (§4j)
          fnvsave xp <save.fos>               Show the player's XP      (§4j)
          fnvsave setkarma <in.fos> <out.fos> <value>  Edit the player's karma (writes a new file)
          fnvsave setxp <in.fos> <out.fos> <value>     Edit the player's XP    (writes a new file)
          fnvsave diff <a.fos> <b.fos> [body|cf]   Byte-diff two saves (best on controlled pairs); 'body'
                                              hides header/screenshot churn, 'cf' restricts to change forms
                                              and names the containing record for each differing run
          fnvsave walk <save.fos>             Walk all change-form records (validates the header format; R&D)
          fnvsave survey <save.fos|dir> [0xNN]   Decode-coverage survey (§6 #1a): per change-form FORM TYPE,
                                              the payload length/changeFlags distribution across the corpus;
                                              with a type byte, a per-offset constancy map per (flags,len) group
          fnvsave cfwalk <save.fos> <iref>|--type 0xNN [N]   Full walk (§6 #1b): render change form(s) as a
                                              labeled field tree with explicit unknown[n] gaps (sized types
                                              from §4l decoded; the rest shown as one honest gap)
          fnvsave refdump <save.fos> [iref]  Decode a reference change form (default: the player inventory
                                              record): changeFlags bits + the typed-entry ExtraDataList walk
                                              (bounded by the located first item) + 0x7C-field walk (R&D §4i)
          fnvsave edlscan <dir>              Walk every .fos in a folder and aggregate the ExtraDataList
                                              typed-entry grammar + per-stack extra-data types (R&D §4i ◑)
          fnvsave invsig <dir>              Print a per-save decoded-inventory signature (stack count + hash)
                                              for byte-identical-decode checks across decoder changes (R&D §4i)
          fnvsave fdiff <a.fos> <b.fos> [delta] [tol]   Float-aware aligned diff: report change-form float32
                                              fields that changed by ≈delta (default 50) — finds XP/karma (R&D §7)
          fnvsave idiff <a.fos> <b.fos> [clean]   Insertion-aware change-form diff (aligns by FormID across an
                                              add/remove); 'clean' hides recurring game-time/havok churn (R&D §7)
        """);
    return 1;
}

var command = args[0];
var path = args[1];

// Most commands take a save file; `edlscan`/`invsig`/`notescan` take a folder of saves.
if (command is "edlscan" or "invsig" or "notescan" or "q7corpus")
{
    if (!Directory.Exists(path))
    {
        Console.Error.WriteLine($"Directory not found: {path}");
        return 1;
    }
}
// `survey` accepts EITHER a single save or a folder (it aggregates the corpus when given a folder).
else if (command == "survey")
{
    if (!Directory.Exists(path) && !File.Exists(path))
    {
        Console.Error.WriteLine($"Not found: {path}");
        return 1;
    }
}
else if (!File.Exists(path))
{
    Console.Error.WriteLine($"File not found: {path}");
    return 1;
}

try
{
    switch (command)
    {
        case "dump":
            Dump(FalloutSave.Load(path));
            break;
        case "flt":
            Flt(FalloutSave.Load(path), args.Length > 2 ? int.Parse(args[2]) : 28);
            break;
        case "check":
            return Check(path);
        case "setlevel":
            return SetLevel(path, args[2], uint.Parse(args[3]));
        case "probe":
            Probe(FalloutSave.Load(path), args.Length > 2 ? int.Parse(args[2]) : 30);
            break;
        case "hex":
            Hex(FalloutSave.Load(path), ParseOffset(args[2]), args.Length > 3 ? int.Parse(args[3]) : 128);
            break;
        case "gddump":
            GlobalDataDump(FalloutSave.Load(path), path, (int)ParseOffset(args[2]),
                args.FirstOrDefault(a => !a.StartsWith("--") && a != command && a != path && a != args[2]));
            break;
        case "globals":
            Globals(FalloutSave.Load(path));
            break;
        case "deaths":
            Deaths(FalloutSave.Load(path), path,
                args.FirstOrDefault(a => !a.StartsWith("--") && a != command && a != path));
            break;
        case "refenabled":
        {
            var en = FalloutSave.Load(path).EnabledReferences();
            var probe = ParseOffset(args[2]);
            Console.WriteLine($"enabled refs: {en.Count};  0x{probe:X8} enabled = {en.Contains(probe)}");
            break;
        }
        case "qenable":
        {
            var s2 = FalloutSave.Load(path);
            var db2 = PluginDatabase.ForSave(s2, null, GameDataLocator.FindMo2Mods(path), withActors: true);
            var enabled = s2.EnabledReferences();
            Console.WriteLine($"CompletionEnableRefs: {db2.CompletionEnableRefs.Count} quests; PlacedRefEdids resolved; enabled refs in save: {enabled.Count}");
            var qf = args.Length > 2 ? ParseOffset(args[2]) : 0u;
            if (qf != 0 && db2.CompletionEnableRefs.TryGetValue(qf, out var refs))
            {
                Console.WriteLine($"  quest 0x{qf:X8} \"{db2.Quest(qf)?.Name}\" enables {refs.Count} ref(s) on completion:");
                foreach (var r in refs) Console.WriteLine($"     0x{r:X8}  enabled={enabled.Contains(r)}");
            }
            else if (qf != 0)
                Console.WriteLine($"  quest 0x{qf:X8} has NO completion-enable refs harvested.");
            break;
        }
        case "stats":
            Stats(FalloutSave.Load(path));
            break;
        case "setstat":
            return SetStat(path, args[2], int.Parse(args[3]), uint.Parse(args[4]));
        case "formids":
            FormIds(FalloutSave.Load(path), args.Length > 2 ? int.Parse(args[2]) : 24);
            break;
        case "findplayer":
            FindPlayer(FalloutSave.Load(path));
            break;
        case "playerdump":
            PlayerDump(FalloutSave.Load(path));
            break;
        case "irefscan":
            IrefScan(FalloutSave.Load(path), ParseOffset(args[2]), int.Parse(args[3]));
            break;
        case "walk":
            Walk(FalloutSave.Load(path));
            break;
        case "survey":
        {
            // ROADMAP §6 #1a: characterize each change-form type's payload across the corpus.
            // Optional 2nd arg = a form-type byte (0x.. or decimal) to deep-dive that one type.
            int? only = args.Length > 2 ? (int)ParseOffset(args[2]) : null;
            Survey(path, only);
            break;
        }
        case "cfwalk":
        {
            // ROADMAP §6 #1b: render change form(s) as a labeled field tree with explicit unknown[n] gaps.
            //   cfwalk <save> <iref>          one record (iref; 0x.. ok)
            //   cfwalk <save> --type 0xNN [N] first N records of a form type (default 3)
            var ti = Array.IndexOf(args, "--type");
            if (ti >= 0 && ti + 1 < args.Length)
            {
                var ft = (int)ParseOffset(args[ti + 1]);
                var n = args.Length > ti + 2 && int.TryParse(args[ti + 2], out var nn) ? nn : 3;
                CfWalk(FalloutSave.Load(path), path, type: ft, max: n);
            }
            else
                CfWalk(FalloutSave.Load(path), path, iref: (int)ParseOffset(args[2]));
            break;
        }
        case "refdump":
            RefDump(FalloutSave.Load(path), path, args.Length > 2 ? (int)ParseOffset(args[2]) : (int?)null);
            break;
        case "cf":
            CfByFormId(FalloutSave.Load(path), ParseOffset(args[2]));
            break;
        case "q7scan":
            Q7Scan(FalloutSave.Load(path), path,
                args.FirstOrDefault(a => !a.StartsWith("--") && a != command && a != path));
            break;
        case "q7corpus":
            Q7Corpus(path, args.Length > 2 ? args[2] : null);
            break;
        case "qfired":
            QuestFired(FalloutSave.Load(path), path,
                args.FirstOrDefault(a => !a.StartsWith("--") && a != command && a != path));
            break;
        case "qcond":
            QuestConditions(FalloutSave.Load(path), path,
                args.FirstOrDefault(a => !a.StartsWith("--") && a != command && a != path));
            break;
        case "qvars":
            QuestVars(FalloutSave.Load(path), path,
                args.FirstOrDefault(a => !a.StartsWith("--") && a != command && a != path));
            break;
        case "qguard":
            QuestGuard(FalloutSave.Load(path), path, args[2],
                args.FirstOrDefault(a => !a.StartsWith("--") && a != command && a != path && a != args[2]));
            break;
        case "qgate":
            QuestCounterGates(FalloutSave.Load(path), path,
                args.FirstOrDefault(a => !a.StartsWith("--") && a != command && a != path),
                args.Contains("--all"));
            break;
        case "counterderive":
            CounterDerive(FalloutSave.Load(path), path,
                args.FirstOrDefault(a => !a.StartsWith("--") && a != command && a != path));
            break;
        case "qaudit":
        {
            var whoIdx = Array.IndexOf(args, "--who");
            QuestAudit(FalloutSave.Load(path), path,
                args.FirstOrDefault(a => !a.StartsWith("--") && a != command && a != path),
                args.Contains("--list"), whoIdx >= 0 && whoIdx + 1 < args.Length ? args[whoIdx + 1] : null);
            break;
        }
        case "edlscan":
            EdlScan(path);
            break;
        case "invsig":
            InvSig(path);
            break;
        case "idiff":
            IDiff(path, args[2], args.Length > 3 ? args[3] : null);
            break;
        case "fdiff":
            FDiff(path, args[2], args.Length > 3 ? float.Parse(args[3]) : 50f, args.Length > 4 ? float.Parse(args[4]) : 0.05f);
            break;
        case "find":
            Find(FalloutSave.Load(path), args[2]);
            break;
        case "special":
            Special(FalloutSave.Load(path));
            break;
        case "diff":
            Diff(path, args[2], args.Length > 3 ? args[3] : null);
            break;
        case "setspecial":
            return SetSpecial(path, args[2], args[3..10]);
        case "skills":
            Skills(FalloutSave.Load(path));
            break;
        case "setskill":
            return SetSkill(path, args[2], args[3], float.Parse(args[4]));
        case "inventory":
            Inventory(FalloutSave.Load(path), path, args.Length > 2 ? args[2] : null);
            break;
        case "names":
            Names(FalloutSave.Load(path), path, args.Length > 2 ? args[2] : null);
            break;
        case "notes":
            Notes(FalloutSave.Load(path), path, args.Length > 2 ? args[2] : null);
            break;
        case "perks":
            Perks(FalloutSave.Load(path), path, args.Length > 2 ? args[2] : null);
            break;
        case "quests":
            Quests(FalloutSave.Load(path), path,
                args.FirstOrDefault(a => !a.StartsWith("--") && a != command && a != path),
                args.Contains("--raw"));
            break;
        case "notescan":
            NoteScan(path);
            break;
        case "qdbg":
            QuestDebug(FalloutSave.Load(path), path,
                args.FirstOrDefault(a => !a.StartsWith("--") && a != command && a != path));
            break;
        case "pipboy":
            Pipboy(FalloutSave.Load(path), path,
                args.FirstOrDefault(a => !a.StartsWith("--") && a != command && a != path));
            break;
        case "qscript":
            QuestScriptDump(FalloutSave.Load(path), path, args[2],
                args.FirstOrDefault(a => !a.StartsWith("--") && a != command && a != path && a != args[2]));
            break;
        case "qrec":
        {
            // qrec <plugin.esm> <localFormIdHex> — dump a QUST record's raw subrecords (R&D §6 #16).
            using var fs = File.OpenRead(path);
            var local = ParseOffset(args[2]);
            foreach (var (type, size, text) in TesPlugin.DumpQust(fs, local))
                Console.WriteLine($"  {type}  {size,5}{(text is not null ? "  \"" + text + "\"" : "")}");
            break;
        }
        case "resolve":
            Resolve(FalloutSave.Load(path), path, ParseOffset(args[2]), args.Length > 3 ? args[3] : null);
            break;
        case "recid":
            // R&D (§6 #1): identify the masters record SIGNATURE for one or more save FormIDs (REFR/DOOR/…),
            // for change forms the name index can't resolve. recid <save> <formId> [formId...]
            RecId(FalloutSave.Load(path), path, args[2..].Select(ParseOffset).Select(x => (uint)x).ToArray());
            break;
        case "findname":
            // R&D (§6 #1): find a base form by NAME across the save's masters → save-space FormID.
            // findname <save> "<substring>" [SIG]   (SIG e.g. PERK restricts + speeds the scan)
            FindName(FalloutSave.Load(path), path, args[2], args.Length > 3 ? args[3] : null);
            break;
        case "setcount":
            return SetCount(path, args[2], ParseOffset(args[3]), uint.Parse(args[4]));
        case "setcondition":
            return SetCondition(path, args[2], ParseOffset(args[3]), float.Parse(args[4]));
        case "caps":
            Caps(FalloutSave.Load(path));
            break;
        case "setcaps":
            return SetCaps(path, args[2], uint.Parse(args[3]));
        case "karma":
            PlayerStat(FalloutSave.Load(path), "Karma", s => s.Karma);
            break;
        case "xp":
            PlayerStat(FalloutSave.Load(path), "XP", s => s.Xp);
            break;
        case "setkarma":
            return SetPlayerStat(path, args[2], "Karma", float.Parse(args[3]), (s, v) => s.TrySetKarma(v), s => s.Karma);
        case "setxp":
            return SetPlayerStat(path, args[2], "XP", float.Parse(args[3]), (s, v) => s.TrySetXp(v), s => s.Xp);
        default:
            Console.Error.WriteLine($"Unknown command: {command}");
            return 1;
    }
}
catch (SaveFormatException ex)
{
    Console.Error.WriteLine($"Parse error: {ex.Message}");
    return 2;
}

return 0;

static void Dump(FalloutSave s)
{
    Console.WriteLine($"Variant         : {s.Variant}");
    Console.WriteLine($"Language        : {s.Language}");
    Console.WriteLine($"Save number     : {s.SaveNumber}");
    Console.WriteLine($"Player name     : {s.PlayerName}");
    Console.WriteLine($"Player title    : {s.PlayerTitle}");
    Console.WriteLine($"Player level    : {s.PlayerLevel}");
    Console.WriteLine($"Location        : {s.PlayerLocation}");
    Console.WriteLine($"Playtime        : {s.Playtime}");
    Console.WriteLine($"Screenshot      : {s.Screenshot.Width} x {s.Screenshot.Height} ({s.Screenshot.Rgb.Length:N0} bytes RGB)");
    Console.WriteLine($"Header size     : {s.SaveHeaderSize}  (version 0x{s.Version:X})");
    Console.WriteLine($"Plugins ({s.Plugins.Count}):");
    foreach (var p in s.Plugins)
        Console.WriteLine($"    {p}");
    Console.WriteLine($"Body starts at  : 0x{s.BodyOffset:X}  ({s.FileLength - s.BodyOffset:N0} bytes, undecoded)");
}

static void Flt(FalloutSave s, int count)
{
    Console.WriteLine($"File Location Table (raw uint32s from body @ 0x{s.BodyOffset:X}):");
    var values = s.PeekBodyUInt32(count);
    for (var i = 0; i < values.Length; i++)
        Console.WriteLine($"    [{i,2}]  {values[i],12:N0}   0x{values[i]:X8}");
}

static int Check(string path)
{
    var original = File.ReadAllBytes(path);
    var roundTripped = FalloutSave.Parse(original).ToBytes();
    var identical = original.AsSpan().SequenceEqual(roundTripped);
    Console.WriteLine(identical
        ? $"OK: round-trip is byte-identical ({original.Length:N0} bytes)."
        : "FAIL: round-trip differs from original!");
    return identical ? 0 : 3;
}

static void GlobalDataDump(FalloutSave s, string savePath, int type, string? dataDir)
{
    // ROADMAP §6 #16 bucket-C decode: dump a GlobalData table as 0x7C-delimited tokens, resolving any 3-byte
    // token as a refID -> FormID (+ masters name). Used to crack GlobalData type-2 ("TES"), which holds the
    // kill/death registry the engine re-derives kill-completed quests from.
    var g = s.GlobalDataTable1.FirstOrDefault(x => x.Type == (uint)type);
    if (g is null) { Console.WriteLine($"No GlobalData type {type}."); return; }
    var db = PluginDatabase.ForSave(s, dataDir, GameDataLocator.FindMo2Mods(savePath));
    var fields = ReferenceChangeForm.Tokenize(g.Data, g.DataOffset);
    Console.WriteLine($"GlobalData type {type}: {g.Data.Length} bytes @ 0x{g.DataOffset:X}, {fields.Count} tokens");
    for (var i = 0; i < fields.Count; i++)
    {
        var f = fields[i];
        var hex = string.Join(" ", f.Bytes.Select(b => b.ToString("X2")));
        var ann = "";
        if (f.AsRefId is { } raw && raw != 0)
        {
            var formId = s.ResolveRefId(raw);
            if (formId != 0) ann = $"   -> 0x{formId:X8} {db.Resolve(formId) ?? db.RecordType(formId) ?? ""}";
        }
        else if (f.AsUInt32 is { } v && v != 0) ann = $"   = {v}";
        Console.WriteLine($"  [{i,3}] ({f.Length}B) {hex,-14}{ann}");
    }
}

static void QuestVars(FalloutSave s, string savePath, string? questHex)
{
    // ROADMAP §6 #16 Stage B: dump the persisted quest script local-variable blocks (CHANGE_QUEST_SCRIPT, bit30):
    // [vsval count] count×(u32 SLSD index, f64 value). Names come from the masters when available (by index).
    var db = PluginDatabase.ForSave(s, null, GameDataLocator.FindMo2Mods(savePath));
    var vars = QuestScriptVars.Read(s);
    if (vars.Count == 0) { Console.WriteLine("No quest carries a persisted script-var block (CHANGE_QUEST_SCRIPT)."); return; }

    var filter = questHex is null ? (uint?)null : (uint)ParseOffset(questHex);
    Console.WriteLine($"Quests with a persisted script-var block: {vars.Count}\n");
    foreach (var (formId, vs) in vars.OrderBy(kv => kv.Key))
    {
        if (filter is { } f && formId != f) continue;
        var name = db.Resolve(formId);
        Console.WriteLine($"0x{formId:X8}  {name ?? "(unknown)"}  ({vs.Count} vars)");
        foreach (var v in vs.OrderBy(v => v.Index))
            Console.WriteLine($"    [{v.Index,3}] = {v.Value:g}");
    }
}

static void QuestGuard(FalloutSave s, string savePath, string questHex, string? dataDir)
{
    // ROADMAP §6 #16 Stage B: inspect GuardEvaluator's three-valued verdict on a quest's GameMode world-poll guards,
    // and the underlying GetDead resolution (does the ref editor id resolve, is it in the death registry?).
    var db = PluginDatabase.ForSave(s, dataDir, GameDataLocator.FindMo2Mods(savePath), withDialogue: true, withActors: true);
    if (db.Count == 0) { Console.WriteLine("Needs the game Data folder."); return; }
    var formId = (uint)ParseOffset(questHex);
    var def = db.Quest(formId);
    if (def is null || string.IsNullOrEmpty(def.GameModeScript)) { Console.WriteLine($"no quest / no GameMode for {questHex}"); return; }

    var pip = QuestPipboy.Compute(s, db);
    var pq = pip.Quests.ToDictionary(q => q.FormId);
    var byEdid = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
    foreach (var q in db.Quests.Values) if (!string.IsNullOrEmpty(q.Edid)) byEdid[q.Edid!] = q.FormId;
    var dead = s.DeadReferences();

    var guard = new GuardEvaluator(
        dead, db.PlacedReferenceEdids,
        e => byEdid.TryGetValue(e, out var f) ? f : null,
        f => pq.TryGetValue(f, out var q) ? q.State != PipboyQuestState.Completed : null,
        f => pq.TryGetValue(f, out var q) ? q.State == PipboyQuestState.Completed : false,
        _ => null,
        (f, n) => pq.TryGetValue(f, out var q) ? q.Objectives.Any(o => o.Index == n && o.Completed) : false,
        (f, n) => pq.TryGetValue(f, out var q) ? q.Objectives.Any(o => o.Index == n) : false);

    var computed = pq.TryGetValue(formId, out var cq) ? cq.State.ToString() : "(not shown)";
    Console.WriteLine($"{questHex}  {def.Name} [{def.Edid}]  computed={computed}  dead-refs={dead.Count}  placedRefEdids={db.PlacedReferenceEdids.Count}\n");

    // Resolve every distinct <ref>.GetDead ref the GameMode references.
    var refRx = new System.Text.RegularExpressions.Regex(@"([A-Za-z_][A-Za-z0-9_]*)\.GetDead", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    var refs = refRx.Matches(def.GameModeScript!).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    Console.WriteLine("--- GetDead refs ---");
    foreach (var r in refs)
    {
        var resolved = db.PlacedReferenceEdids.TryGetValue(r, out var rf);
        Console.WriteLine($"  {r,-28} {(resolved ? $"0x{rf:X8}" : "UNRESOLVED")}  {(resolved && dead.Contains(rf) ? "DEAD" : resolved ? "alive" : "")}");
    }

    Console.WriteLine("\n--- GameMode effects gated on GetDead ---");
    foreach (var e in QuestScript.Parse(def.GameModeScript))
    {
        if (!e.Guards.Any(g => g.Contains("getdead", StringComparison.OrdinalIgnoreCase)))
            continue;
        var verdict = e.Guards.All(g => guard.Holds(g) == true) ? "FIRE"
            : e.Guards.Any(g => guard.Holds(g) == false) ? "blocked" : "unknown";
        Console.WriteLine($"  [{verdict}] {e.Verb} {e.TargetQuestEdid} {e.Arg1}={e.Arg2}");
        foreach (var g in e.Guards)
            Console.WriteLine($"        {guard.Holds(g),-6} | {g.Trim()}");
    }
}

static void CounterDerive(FalloutSave s, string savePath, string? dataDir)
{
    // ROADMAP §6 #16 Stage 2: re-derive event-completed quests from the type-2 death registry, mirroring what
    // QuestPipboy does. A unique killed actor is recorded by its base FormID; a runtime-spawned one as a created
    // reference whose template (a placed ACHR) resolves to that base. Both bind to the script the base runs.
    var db = PluginDatabase.ForSave(s, dataDir, GameDataLocator.FindMo2Mods(savePath), withActors: true);
    if (db.Count == 0) { Console.WriteLine("Needs the game Data folder."); return; }

    var dead = s.DeadReferences();
    uint ScriptOf(uint f) =>
        db.ActorScripts.TryGetValue(f, out var sc) ? sc
        : db.PlacedActorBases.TryGetValue(f, out var b) && db.ActorScripts.TryGetValue(b, out var sc2) ? sc2 : 0;
    // Scripts run by a spawned (created) dead actor, one set per created reference.
    var createdScripts = s.CreatedReferenceForms()
        .Select(c => c.ReferencedFormIds.Select(ScriptOf).Where(x => x != 0).ToHashSet())
        .Where(set => set.Count > 0).ToList();
    Console.WriteLine($"dead references (type-2): {dead.Count};  scripted actors: {db.ActorScripts.Count};  placed actors: {db.PlacedActorBases.Count};  created refs w/ scripts: {createdScripts.Count}\n");
    int DeadCountRunningAnyOf(IReadOnlySet<uint> scripts) => dead.Count(d => scripts.Contains(ScriptOf(d)));

    Console.WriteLine("--- counter-gated (count N kills >= threshold; incl. spawned via created refs) ---");
    foreach (var gate in db.CounterGates.Where(g => g.Bound && g.ExternallyIncremented))
    {
        var inc = gate.IncrementScripts.ToHashSet();
        var directDead = DeadCountRunningAnyOf(inc);
        var spawnedDead = createdScripts.Count(set => set.Overlaps(inc));
        var derived = directDead + spawnedDead;
        Console.WriteLine($"  \"{gate.QuestName}\" [{gate.QuestEdid}]  {gate.Counter} >= {gate.Threshold}  derived={derived} ({directDead} placed + {spawnedDead} spawned)  => {(derived >= gate.Threshold ? "COMPLETE" : "not yet")}");
    }

    // Single-kill external completions: an OnDeath script on a unique actor completes the quest (the Save-420
    // prize, e.g. "I Fought the Law"). This is what QuestPipboy's Stage-2 pass ships.
    Console.WriteLine("\n--- single-kill external completions (OnDeath completes the quest) ---");
    foreach (var c in db.ExternalCompletions.Where(c => c.ViaKill && !c.ViaCounter).OrderBy(c => c.QuestName))
    {
        var killed = DeadCountRunningAnyOf(c.Scripts.ToHashSet()) > 0;
        Console.WriteLine($"  \"{c.QuestName}\" [{c.QuestEdid}]  {(killed ? "COMPLETE (script-bearing actor dead)" : "not yet")}");
    }
}

static void Deaths(FalloutSave s, string savePath, string? dataDir)
{
    // ROADMAP §6 #16 Stage 2: dump the GlobalData type-2 state-changed-reference registry, the kill signal a
    // counter/event-gated quest's completion is re-derived from. status 1 = dead (controlled-diff pinned on the
    // gtg pair); other codes are state changes (semantics not pinned).
    var refs = s.StateChangedRefs();
    var db = PluginDatabase.ForSave(s, dataDir, GameDataLocator.FindMo2Mods(savePath));
    var byStatus = refs.GroupBy(r => r.Status).OrderBy(g => g.Key);
    Console.WriteLine($"GlobalData type-2 registry: {refs.Count} state-changed reference(s)");
    Console.WriteLine($"  status histogram: {string.Join("  ", byStatus.Select(g => $"{g.Key}:{g.Count()}"))}");
    Console.WriteLine($"  dead (status 1): {refs.Count(r => r.Status == 1)}\n");
    foreach (var (formId, refId, status) in refs)
    {
        var name = db.Resolve(formId) ?? db.RecordType(formId) ?? "";
        var deadTag = status == 1 ? " [DEAD]" : "";
        Console.WriteLine($"  refId 0x{refId:X6} -> 0x{formId:X8}  status {status}{deadTag}  {name}");
    }
}

static void Globals(FalloutSave s)
{
    string[] typeNames =
        ["Misc Stats", "Player Location", "TES", "Global Variables", "Created Objects",
         "type 5", "Weather", "type 7", "type 8", "type 9", "type 10", "type 11"];
    var flt = s.Flt;
    Console.WriteLine($"globalDataTable1 @ 0x{flt.GlobalData1Offset:X} ({flt.GlobalData1Count} records), " +
                      $"changeForms @ 0x{flt.ChangeFormsOffset:X} ({flt.ChangeFormCount} forms), " +
                      $"FormID array count offset 0x{flt.FormIdArrayCountOffset:X}");
    foreach (var g in s.GlobalDataTable1)
    {
        var name = g.Type < typeNames.Length ? typeNames[g.Type] : $"type {g.Type}";
        Console.WriteLine($"  type {g.Type,2} ({name,-16})  {g.Data.Length,7:N0} bytes  @ 0x{g.Offset:X}");
    }
}

static void Stats(FalloutSave s)
{
    var stats = s.MiscStats;
    if (stats is null)
    {
        Console.WriteLine("No Misc Stats record found.");
        return;
    }
    Console.WriteLine($"Misc Stats ({stats.Stats.Count} counters; non-zero shown):");
    foreach (var st in stats.Stats)
        if (st.Value != 0)
        {
            var name = MiscStatNames.Get(st.Index) ?? $"[{st.Index}]";
            Console.WriteLine($"  [{st.Index,2}] {name,-26} = {st.Value,12:N0}   (edit offset 0x{st.ValueOffset:X})");
        }
}

static void FormIds(FalloutSave s, int n)
{
    var array = s.FormIdArray;
    var db = PluginDatabase.ForSave(s);
    Console.WriteLine($"FormID array: {array.Count:N0} entries");
    for (var i = 0; i < Math.Min(n, array.Count); i++)
    {
        var name = db.Resolve(array[i]);
        Console.WriteLine($"  iref [{i,5}] -> 0x{array[i]:X8}{(name is null ? "" : $"  {name}")}");
    }
}

static void Diff(string pathA, string pathB, string? filter = null)
{
    var a = File.ReadAllBytes(pathA);
    var b = File.ReadAllBytes(pathB);
    var save = FalloutSave.Parse(a);

    // Optional region filter: "body" hides the header/screenshot churn; "cf"/"changeforms" restricts to
    // the change-forms region and annotates each run with the change form that contains it (the key
    // signal for a controlled drop-item diff — it names the form whose inventory/count bytes moved).
    var onlyChangeForms = filter is "cf" or "changeforms";
    var minOffset = filter is "body" ? save.BodyOffset
                  : onlyChangeForms ? (int)save.Flt.ChangeFormsOffset
                  : 0;
    var maxOffset = onlyChangeForms ? (int)save.Flt.GlobalData3Offset : int.MaxValue;

    // Walk the change forms once so a body offset can be mapped to its containing record.
    var records = onlyChangeForms ? save.EnumerateChangeForms().ToArray() : [];
    string ContainingForm(int off)
    {
        if (records.Length == 0)
            return "";
        var lo = 0; var hi = records.Length - 1; var found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (records[mid].Offset <= off) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (found < 0)
            return "";
        var cf = records[found];
        if (off >= cf.Next)
            return "";
        var d = off - cf.DataOffset;
        var rel = d >= 0 ? $"data+0x{d:X}" : $"data-0x{-d:X}"; // negative = inside the record header
        return $"  [cf @0x{cf.Offset:X} iref {cf.Iref} -> 0x{cf.FormId:X8} type 0x{cf.TypeByte:X2} len {cf.DataLength}; {rel}]";
    }

    string Region(int off)
    {
        if (off < save.BodyOffset) return "header/screenshot/plugins";
        if (off < save.Flt.ChangeFormsOffset) return "globals (FLT/stats)";
        if (off < save.Flt.GlobalData3Offset) return "change forms";
        if (off < save.Flt.FormIdArrayCountOffset) return "gdt3";
        return "formID array/footer";
    }

    // Stable anchors (from A) so a change-forms hit reads as e.g. "playerBase+0x3C" — reusable across saves.
    var baseStart = save.PlayerBaseRecordStart;
    var specialOff = save.Special?.Offset;
    var refHits = save.PlayerAnchors().FirstOrDefault(x => x.Label == "playerRef").RecordStarts ?? [];
    string Anchors(int off)
    {
        // Only meaningful inside the change-forms region where the player records live.
        if (off < save.Flt.ChangeFormsOffset || off >= save.Flt.GlobalData3Offset)
            return "";
        var parts = new List<string>();
        void Add(string label, int anchor)
        {
            var d = off - anchor;
            parts.Add(d >= 0 ? $"{label}+0x{d:X}" : $"{label}-0x{-d:X}");
        }
        if (baseStart is { } b) Add("playerBase", b);
        if (specialOff is { } sp) Add("special", sp);
        if (refHits.Count > 0) Add("playerRef", refHits.MinBy(h => Math.Abs(h - off)));
        return parts.Count > 0 ? "  {" + string.Join(" ", parts) + "}" : "";
    }

    Console.WriteLine($"A: {a.Length:N0} bytes   B: {b.Length:N0} bytes" +
                      (a.Length != b.Length ? $"   (sizes differ by {Math.Abs(a.Length - b.Length):N0} — diff is only meaningful up to the first size-changing region)" : "   (same size — clean diff)"));

    var n = Math.Min(a.Length, b.Length);
    var runs = new List<(int Start, int Len)>();
    var i = 0;
    while (i < n)
    {
        if (a[i] != b[i]) { var s = i; while (i < n && a[i] != b[i]) i++; runs.Add((s, i - s)); }
        else i++;
    }

    var shown = runs.Where(r => r.Start >= minOffset && r.Start < maxOffset).ToList();
    var label = onlyChangeForms ? " in change forms" : filter is "body" ? " in body" : "";
    Console.WriteLine($"{runs.Count:N0} differing run(s); {shown.Count:N0} shown{label}:");
    foreach (var r in shown.Take(120))
    {
        var sa = string.Join(' ', a.Skip(r.Start).Take(Math.Min(r.Len, 10)).Select(x => x.ToString("X2")));
        var sb = string.Join(' ', b.Skip(r.Start).Take(Math.Min(r.Len, 10)).Select(x => x.ToString("X2")));
        var extra = onlyChangeForms ? ContainingForm(r.Start) : Anchors(r.Start);
        Console.WriteLine($"  @0x{r.Start:X} ({r.Len,4} B) [{Region(r.Start)}]  A: {sa}  B: {sb}{extra}");
    }
    if (shown.Count > 120)
        Console.WriteLine("  ... (truncated)");
}

static void Special(FalloutSave s)
{
    var sp = s.Special;
    if (sp is null)
        Console.WriteLine("SPECIAL not located.");
    else
        Console.WriteLine($"SPECIAL: {sp}  (sum {sp.Sum}) @ 0x{sp.Offset:X}");
}

static int SetSpecial(string inPath, string outPath, string[] values)
{
    var before = File.ReadAllBytes(inPath);
    var save = FalloutSave.Parse(before);
    var bytes = values.Select(byte.Parse).ToArray();
    if (!save.TrySetSpecial(bytes))
    {
        Console.WriteLine("FAIL: SPECIAL not located or not 7 values.");
        return 4;
    }
    save.Save(outPath, backup: false);

    var after = File.ReadAllBytes(outPath);
    var reloaded = FalloutSave.Parse(after);
    Console.WriteLine($"SPECIAL -> {reloaded.Special};  size {before.Length:N0} -> {after.Length:N0}");
    var ok = reloaded.Special is not null
             && reloaded.Special.Values.SequenceEqual(bytes)
             && before.Length == after.Length;
    Console.WriteLine(ok ? "OK: SPECIAL edit applied, size unchanged, re-parses." : "FAIL: did not verify.");
    return ok ? 0 : 4;
}

static void Skills(FalloutSave s)
{
    var skills = s.Skills;
    if (skills is null)
    {
        Console.WriteLine("No skill modifications stored (the engine computes skills from base+SPECIAL+perks;");
        Console.WriteLine("only deviations are written, and this save stores fewer than two).");
        return;
    }
    Console.WriteLine($"Stored skill modifications @ 0x{skills.Offset:X}  ({skills.Modifications.Count} actor-value entries):");
    foreach (var sk in skills.Skills.OrderBy(x => x.Name))
        Console.WriteLine($"  {sk.Name,-15} = {sk.Value,7:0.##}   (edit offset 0x{sk.ValueOffset:X})");
    var nonSkill = skills.Modifications.Count - skills.Skills.Count;
    if (nonSkill > 0)
        Console.WriteLine($"  (+{nonSkill} non-skill actor-value entries in the same block)");
}

static int SetSkill(string inPath, string outPath, string skill, float value)
{
    var before = File.ReadAllBytes(inPath);
    var save = FalloutSave.Parse(before);
    var old = save.Skills?.Skills.FirstOrDefault(x => x.Index == PlayerSkills.IndexForSkill(skill))?.Value;
    if (!save.TrySetSkill(skill, value))
    {
        Console.WriteLine($"FAIL: '{skill}' is not a known skill, or it has no stored entry to edit in this save.");
        return 4;
    }
    save.Save(outPath, backup: false);

    var after = File.ReadAllBytes(outPath);
    var reloaded = FalloutSave.Parse(after);
    var now = reloaded.Skills?.Skills.FirstOrDefault(x => x.Index == PlayerSkills.IndexForSkill(skill))?.Value;
    Console.WriteLine($"{skill}: {old} -> {now};  size {before.Length:N0} -> {after.Length:N0}");
    var ok = now == value && before.Length == after.Length;
    Console.WriteLine(ok ? "OK: skill edit applied, size unchanged, re-parses." : "FAIL: did not verify.");
    return ok ? 0 : 4;
}

static void Inventory(FalloutSave s, string savePath, string? dataDir)
{
    var inv = s.Inventory;
    if (inv is null)
    {
        Console.WriteLine("Inventory not located (no change form parsed as a recognisable item list).");
        return;
    }
    var db = PluginDatabase.ForSave(s, dataDir, GameDataLocator.FindMo2Mods(savePath));
    // With names available, hide entries that don't resolve to an item: on large records the decoder's
    // run can absorb a few non-item bytes that validate structurally but aren't real stacks.
    var items = db.Count > 0 ? inv.Items.Where(i => db.Resolve(i.FormId) is not null).ToList() : inv.Items;
    Console.WriteLine($"Player inventory @ 0x{inv.Offset:X}  ({items.Count} stacks, {items.Sum(i => (long)i.Count):N0} items):");
    if (db.Count == 0)
        Console.WriteLine("  (item names unavailable — game Data folder not found; pass it as the 2nd argument)");
    foreach (var item in items.OrderByDescending(i => i.Count))
    {
        var name = db.Resolve(item.FormId) ?? "?";
        var tab = db.Category(item.FormId) is { } t ? $"[{t}/{db.RecordType(item.FormId)}]" : "";
        var src = s.FriendlySourceForModIndex(item.ModIndex) ?? "?";
        var extra = "";
        if (item.Condition is { } c)
            extra += $"  cond {c:0.#}";
        if (item.Equipped)
            extra += "  [equipped]";
        if (item.ExtraRefIds.Count > 0)
            extra += $"  0x21-ref: {string.Join(", ", item.ExtraRefIds.Select(r => db.Resolve(s.ResolveIref(r)) ?? $"0x{s.ResolveIref(r):X8}"))}";
        Console.WriteLine($"  {name,-28}  {tab,-14} 0x{item.FormId:X8} (mod {item.ModIndex:X2})  {src,-22}  x{item.Count,-6} iref {item.Iref,-6} (edit 0x{item.CountValueOffset:X}){extra}");
    }
}

static void Names(FalloutSave s, string savePath, string? dataDir)
{
    var mods = GameDataLocator.FindMo2Mods(savePath);
    var db = PluginDatabase.ForSave(s, dataDir, mods);
    if (db.Count == 0 && db.DataFolder is null)
    {
        Console.WriteLine("Game Data folder not found. Pass it explicitly, e.g.:");
        Console.WriteLine("  fnvsave names <save.fos> \"C:\\...\\Fallout New Vegas\\Data\"");
        Console.WriteLine($"Save load order ({s.Plugins.Count} plugins): {string.Join(", ", s.Plugins)}");
        return;
    }
    Console.WriteLine($"Data folder : {db.DataFolder ?? "(not found)"}");
    if (db.ModsFolder is not null)
        Console.WriteLine($"MO2 mods    : {db.ModsFolder}");
    Console.WriteLine($"Resolved {db.ResolvedPlugins.Count}/{s.Plugins.Count} plugins; {db.Count:N0} named forms indexed.");
    var resolvedSet = db.ResolvedPlugins.ToHashSet(StringComparer.OrdinalIgnoreCase);
    foreach (var p in s.Plugins)
        Console.WriteLine($"  {(resolvedSet.Contains(p) ? "[ok]" : "[--]")} {p}");
}

static void Notes(FalloutSave s, string savePath, string? dataDir)
{
    // The player's Pip-Boy Data -> Notes list. READ notes are zero-length change-form markers (§4k); UNREAD
    // (bold) notes leave no marker but their reference sits in the player inventory record's note ref-list
    // (§4k.1 #4). Identifying which references are NOTE records needs the masters, so we pass that test into
    // PipBoyNotes; without masters we can only show the read markers (and not their names or the unread set).
    var db = PluginDatabase.ForSave(s, dataDir, GameDataLocator.FindMo2Mods(savePath));
    if (db.Count == 0)
    {
        var read = s.ReadNotes;
        Console.WriteLine($"Read notes ({read.Count}):  (names + the unread list need the game Data folder — pass it as the 2nd argument)");
        foreach (var n in read.Notes)
            Console.WriteLine($"  [read  ]  0x{n.FormId:X8} (mod {n.ModIndex:X2})");
        return;
    }

    var notes = s.PipBoyNotes(fid => db.RecordType(fid) == "NOTE");
    var readCount = notes.Count(n => n.Read);
    Console.WriteLine($"Pip-Boy notes ({notes.Count}: {readCount} read, {notes.Count - readCount} unread):");
    foreach (var n in notes
                 .OrderBy(n => n.Read)                                   // unread first
                 .ThenBy(n => db.Resolve(n.FormId) ?? "￿", StringComparer.OrdinalIgnoreCase))
    {
        var src = s.FriendlySourceForModIndex(n.ModIndex) ?? "?";
        var tag = n.Read ? "read  " : "UNREAD";
        var media = db.NoteMediaType(n.FormId) ?? "?";
        Console.WriteLine($"  [{tag}]  {db.Resolve(n.FormId) ?? "?",-40}  {media,-6}  0x{n.FormId:X8} (mod {n.ModIndex:X2})  {src}");
    }
}

static void Perks(FalloutSave s, string savePath, string? dataDir)
{
    // The player's perks + traits (ROADMAP §4n) — a count-prefixed list in the player reference change form,
    // each entry a perkRef (FormID-array index + 1) resolving to a PERK record. Identifying which references are
    // PERKs needs the masters, so we pass that test into PlayerPerks; without masters we can't name or filter them.
    var db = PluginDatabase.ForSave(s, dataDir, GameDataLocator.FindMo2Mods(savePath));
    if (db.Count == 0)
    {
        Console.WriteLine("Perks need the game Data folder to identify/name PERK forms — pass it as the 2nd argument.");
        return;
    }

    var perks = s.PlayerPerks(fid => db.RecordType(fid) == "PERK");
    Console.WriteLine($"Player perks & traits ({perks.Count}):");
    foreach (var p in perks.OrderBy(p => db.Resolve(p.FormId) ?? "￿", StringComparer.OrdinalIgnoreCase))
    {
        var modIndex = (int)(p.FormId >> 24);
        var src = s.FriendlySourceForModIndex(modIndex) ?? "?";
        var rank = p.Rank > 1 ? $"  (rank {p.Rank})" : "";
        Console.WriteLine($"  {db.Resolve(p.FormId) ?? "?",-40}{rank}  0x{p.FormId:X8} (mod {modIndex:X2})  {src}");
    }
}

static void Quests(FalloutSave s, string savePath, string? dataDir, bool raw)
{
    // The player's quest log (ROADMAP §6 #10). Classifying a change form as a quest needs the masters
    // (refID -> FormID -> QUST), as do stage/objective names, so the database is required.
    var db = PluginDatabase.ForSave(s, dataDir, GameDataLocator.FindMo2Mods(savePath));
    if (db.Count == 0)
    {
        Console.WriteLine("Quest log needs the game Data folder to classify quest change forms — pass it as the 2nd argument.");
        return;
    }

    if (raw)
    {
        // R&D: dump each quest change form's flags + bytes, to verify the stage-list layout against a save.
        foreach (var cf in s.EnumerateChangeForms())
        {
            if (db.RecordType(cf.FormId) != "QUST") continue;
            Console.WriteLine($"\n0x{cf.FormId:X8} {db.Resolve(cf.FormId) ?? "?"}  type 0x{cf.TypeByte:X2} (form 0x{cf.FormType:X2})  len {cf.DataLength}");
            Console.WriteLine($"  changeFlags 0x{cf.ChangeFlags:X8} = {ReferenceChangeForm.DescribeQuestFlags(cf.ChangeFlags)}");
            var bytes = s.ReadAt(cf.DataOffset, Math.Min(cf.DataLength, 256));
            Console.WriteLine("  " + Convert.ToHexString(bytes));
        }
        return;
    }

    var log = QuestLog.Read(s, db);
    Console.WriteLine($"Quests with state recorded in this save: {log.Quests.Count}\n" +
        "NOTE: this is NOT your Pip-Boy quest list and can't be made into one from the save alone. It includes\n" +
        "quests the engine background-initialized but you haven't started (e.g. DLC main quests like \"Welcome to\n" +
        "the Big Empty\" before you enter the DLC — identical save bytes to a started quest), and it omits quests\n" +
        "shown from masters defaults (no save delta). The real Pip-Boy list needs the quest-script interpreter — §6 #16.\n");
    foreach (var q in log.Quests.OrderBy(q => q.Name ?? "￿", StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine($"{q.Name ?? "?",-44} [{q.State}]  0x{q.FormId:X8}");
        foreach (var stage in q.Stages.OrderBy(st => st.Index))
        {
            var when = stage.CompletionTime is { } t ? $"  @t={t}" : "";
            var log2 = stage.LogText is { Length: > 0 } ? $"  \"{Truncate(stage.LogText, 70)}\"" : "";
            Console.WriteLine($"    stage {stage.Index,-3} {(stage.Done ? "[x]" : "[ ]")}{when}{log2}");
        }
        foreach (var o in q.Objectives.OrderBy(o => o.Index))
        {
            // Save-side display/complete status (bit0/bit1). Show only objectives the save marks displayed or
            // completed, plus those still unknown — a not-displayed objective is just clutter.
            var mark = (o.Displayed, o.Completed) switch
            {
                (true, true) => "[done]   ",
                (true, _) => "[active] ",
                (false, true) => "[done*]  ", // completed but no longer displayed
                (false, _) => null,            // not displayed — skip
                (null, _) => "[unknown]",
            };
            if (mark is null) continue;
            Console.WriteLine($"    obj {o.Index,-3} {mark} {Truncate(o.Text ?? "?", 60)}");
        }
    }

    static string Truncate(string v, int max) => v.Length <= max ? v : v[..(max - 1)] + "…";
}

// R&D (ROADMAP §6 #16): for every PLAYER-FACING quest in the masters (FULL name + >=1 objective), print its
// Start-Game-Enabled flag, whether the save carries a change form for it, and the save-side displayed/completed
// objective state. Lets us correlate masters+save fields against a known-ground-truth Pip-Boy list (Save 57).
static void QuestDebug(FalloutSave s, string savePath, string? dataDir)
{
    var db = PluginDatabase.ForSave(s, dataDir, GameDataLocator.FindMo2Mods(savePath));
    if (db.Count == 0) { Console.WriteLine("Needs the game Data folder."); return; }

    // Which quest FormIDs have a change form in this save (any kind)?
    var changeForms = new Dictionary<uint, FalloutSave.ChangeFormHeader>();
    foreach (var cf in s.EnumerateChangeForms())
        if (db.RecordType(cf.FormId) == "QUST")
            changeForms[cf.FormId] = cf;

    var log = QuestLog.Read(s, db);
    var byId = log.Quests.ToDictionary(q => q.FormId);

    var facing = db.Quests.Values.Where(q => q.IsPlayerFacing)
        .OrderByDescending(q => q.StartGameEnabled)
        .ThenBy(q => q.Name, StringComparer.OrdinalIgnoreCase).ToList();
    Console.WriteLine($"Player-facing quests in masters (FULL name + objectives): {facing.Count}");
    Console.WriteLine($"  of those with a change form in this save: {facing.Count(q => changeForms.ContainsKey(q.FormId))}");
    Console.WriteLine($"  Start-Game-Enabled: {facing.Count(q => q.StartGameEnabled)}\n");
    Console.WriteLine($"{"SGE",-4}{"CF",-4}{"DATA",-6}{"dispObj",-8}{"doneObj",-8}{"Name",-42}FormID");
    foreach (var q in facing)
    {
        var hasCf = changeForms.ContainsKey(q.FormId);
        byId.TryGetValue(q.FormId, out var decoded);
        var disp = decoded?.Objectives.Count(o => o.Displayed == true) ?? 0;
        var done = decoded?.Objectives.Count(o => o.Completed == true) ?? 0;
        Console.WriteLine($"{(q.StartGameEnabled ? "SGE" : ""),-4}{(hasCf ? "cf" : ""),-4}0x{q.DataFlags:X2}  {disp,-8}{done,-8}{Truncate(q.Name ?? "?", 40),-42}0x{q.FormId:X8}");
    }

    static string Truncate(string v, int max) => v.Length <= max ? v : v[..(max - 1)] + "…";
}

// R&D (ROADMAP §6 #16): for one quest, print its masters definition (EDID/name/SGE) and, per stage, the
// parsed quest-script effects (QuestScript.Parse over the stage's SCTX). Validates the whole extraction
// pipeline end-to-end on real masters data: TesPlugin SCTX -> QuestDefinition -> QuestScript effects.
// The computed in-game Pip-Boy quest list (ROADMAP §6 #16): masters quest-script interpretation.
static void Pipboy(FalloutSave s, string savePath, string? dataDir)
{
    var db = PluginDatabase.ForSave(s, dataDir, GameDataLocator.FindMo2Mods(savePath), withDialogue: true, withActors: true);
    if (db.Count == 0) { Console.WriteLine("Quest list needs the game Data folder — pass it as the 2nd argument."); return; }

    var pip = QuestPipboy.Compute(s, db);
    var active = pip.Quests.Count(q => q.State == PipboyQuestState.Active);
    var done = pip.Quests.Count(q => q.State == PipboyQuestState.Completed);
    var failed = pip.Quests.Count(q => q.State == PipboyQuestState.Failed);
    Console.WriteLine($"Computed Pip-Boy quests: {pip.Quests.Count} ({active} active, {done} completed, {failed} failed)");
    Console.WriteLine("Computed from masters quest scripts (ROADMAP §6 #16) — a high-coverage approximation, not yet a guaranteed mirror.\n");
    foreach (var q in pip.Quests)
    {
        var tag = q.State switch { PipboyQuestState.Completed => "[completed]", PipboyQuestState.Failed => "[failed]   ", _ => "[active]   " };
        Console.WriteLine($"{tag} {q.Name ?? "?",-44} 0x{q.FormId:X8}{(q.StartGameEnabled ? "  (SGE)" : "")}");
        foreach (var o in q.Objectives)
            Console.WriteLine($"      {(o.Completed ? "[x]" : "[ ]")} {(o.Optional ? "(opt) " : "")}{o.Text}");
    }
}

static void QuestScriptDump(FalloutSave s, string savePath, string formIdArg, string? dataDir)
{
    var db = PluginDatabase.ForSave(s, dataDir, GameDataLocator.FindMo2Mods(savePath));
    if (db.Count == 0) { Console.WriteLine("Needs the game Data folder."); return; }

    // Accept either a hex/numeric FormID or an editor id (EDID) — EDID is handy for controller quests like
    // CGTutorial whose FormID isn't known and whose record is zlib-compressed in the ESM (not greppable).
    var looksNumeric = formIdArg.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        || formIdArg.All(c => Uri.IsHexDigit(c));
    var q = looksNumeric ? db.Quest(ParseOffset(formIdArg)) : null;
    q ??= db.Quests.Values.FirstOrDefault(x => string.Equals(x.Edid, formIdArg, StringComparison.OrdinalIgnoreCase));
    if (q is null) { Console.WriteLine($"No QUST definition for '{formIdArg}' in the masters."); return; }

    Console.WriteLine($"0x{q.FormId:X8}  {q.Name ?? "(no name)"}  [EDID {q.Edid ?? "?"}]");
    Console.WriteLine($"  DATA 0x{q.DataFlags:X2}  StartGameEnabled={q.StartGameEnabled}  PlayerFacing={q.IsPlayerFacing}  " +
        $"stages={q.Stages.Count} objectives={q.Objectives.Count}");
    Console.WriteLine($"  LocalVars ({q.LocalVars?.Count ?? 0}): {string.Join(", ", q.LocalVars ?? [])}");
    if (q.GameModeScript is { } gm)
    {
        Console.WriteLine("  --- GameMode script (startup) ---");
        foreach (var ln in gm.Split('\n'))
            if (ln.Trim().Length > 0) Console.WriteLine($"  | {ln.TrimEnd()}");
        Console.WriteLine("  --- GameMode SetStage/StartQuest effects (fires = guards hold at startup) ---");
        var startup = ScriptStartup.Analyze(gm, q.LocalVars);
        foreach (var e in QuestScript.Parse(gm).Where(e => e.Verb is QuestScriptVerb.SetStage or QuestScriptVerb.StartQuest))
            Console.WriteLine($"      fires={e.Guards.All(startup.GuardHolds),-5} {e.Verb} {e.TargetQuestEdid} {(e.Verb == QuestScriptVerb.SetStage ? e.Arg1.ToString() : "")}  guards=[{string.Join(" ; ", e.Guards)}]");
    }
    Console.WriteLine();
    foreach (var st in q.Stages.OrderBy(x => x.Index))
    {
        var log = st.LogText is { Length: > 0 } ? $"  \"{st.LogText}\"" : "";
        Console.WriteLine($"  stage {st.Index,-3} (QSDT 0x{st.Flags:X2}){log}");
        foreach (var e in QuestScript.Parse(st.ScriptText))
        {
            var idx = e.Verb is QuestScriptVerb.StartQuest or QuestScriptVerb.StopQuest or QuestScriptVerb.CompleteQuest
                or QuestScriptVerb.FailQuest or QuestScriptVerb.CompleteAllObjectives ? "" : $" {e.Arg1}";
            var onOff = e.Verb is QuestScriptVerb.SetObjectiveDisplayed or QuestScriptVerb.SetObjectiveCompleted ? $" ={e.Arg2}" : "";
            Console.WriteLine($"      {(e.Conditional ? "?" : " ")} {e.Verb} {e.TargetQuestEdid}{idx}{onOff}");
        }
    }

    Console.WriteLine("\n  --- objectives (QOBJ index → NNAM text → QSTA target refs) ---");
    foreach (var o in q.Objectives.OrderBy(x => x.Index))
    {
        var targets = o.TargetFormIds.Count > 0
            ? string.Join(", ", o.TargetFormIds.Select(t => $"0x{t:X8}"))
            : "(none)";
        Console.WriteLine($"  obj {o.Index,-3} targets=[{targets}]  \"{o.Text}\"");
    }
}

static void NoteScan(string dir)
{
    // R&D aggregate for ROADMAP §4k.1 #1-3: across every .fos in `dir`, enumerate the change forms and
    // examine every type-0x1F record (the read-note marker form type). Answers, with corpus-wide data:
    //   #1  Is changeFlags ALWAYS exactly 0x80000000 on a read marker? (tally the distinct values)
    //   #2  Does every type-0x1F marker resolve (via the -1 index) to a NOTE record? (record-type tally)
    //   #3  Is the marker's refID the note's INVENTORY reference? (does FormIdArray[iref-1] appear as an
    //       inventory stack — i.e. some item's Iref == iref-1?)
    // Record-type resolution needs the masters; ForSave re-indexes the 245 MB FalloutNV.esm per call, so we
    // CACHE a database per distinct load order (saves in one profile usually share one, but an early save can
    // have a different — e.g. vanilla — load order, which must NOT be reused for the modded ones). The FormID
    // remap is a pure function of the load order, so a cached DB is valid for any save with the same one. Read-only.
    var files = Directory.EnumerateFiles(dir, "*.fos").OrderBy(f => f, StringComparer.Ordinal).ToList();
    Console.WriteLine($"Scanning {files.Count} .fos in {dir}\n");

    var dbCache = new Dictionary<string, PluginDatabase?>();
    int maxForms = 0;

    PluginDatabase? DbFor(FalloutSave s, string file)
    {
        var key = string.Join("|", s.Plugins);
        if (!dbCache.TryGetValue(key, out var db))
        {
            try { db = PluginDatabase.ForSave(s, null, GameDataLocator.FindMo2Mods(file)); }
            catch { db = null; }
            dbCache[key] = db;
            if (db is { Count: > 0 } && db.Count > maxForms) maxForms = db.Count;
        }
        return db;
    }

    int scanned = 0, parseFail = 0, markers = 0, invalidIref = 0;
    int noteType = 0, unknownType = 0, dbUnavailable = 0, inInventory = 0, invChecked = 0;
    var flagValues = new SortedDictionary<uint, int>();
    var otherTypes = new SortedDictionary<string, int>();
    var nonNoteExamples = new List<string>();

    foreach (var file in files)
    {
        FalloutSave s;
        try { s = FalloutSave.Load(file); }
        catch { parseFail++; continue; }
        scanned++;
        var name = Path.GetFileName(file);
        var db = DbFor(s, file);

        // The note FormIDs present in this save's inventory, by FormID-array index (item.Iref == array index).
        HashSet<int>? invIrefs = null;
        try
        {
            if (s.Inventory is { } inv)
                invIrefs = inv.Items.Select(i => i.Iref).ToHashSet();
        }
        catch { /* inventory decode is best-effort here */ }

        foreach (var cf in s.EnumerateChangeForms())
        {
            if (cf.FormType != 0x1F)
                continue;
            markers++;
            flagValues[cf.ChangeFlags] = flagValues.GetValueOrDefault(cf.ChangeFlags) + 1;

            if (cf.Iref <= 0)
            {
                invalidIref++;
                continue;
            }
            var noteFormId = s.ResolveIref(cf.Iref - 1);
            if (noteFormId == 0)
            {
                invalidIref++;
                continue;
            }

            if (invIrefs is not null)
            {
                invChecked++;
                if (invIrefs.Contains(cf.Iref - 1))
                    inInventory++;
            }

            if (db is not { Count: > 0 })
            {
                dbUnavailable++;
                continue;
            }
            var rt = db.RecordType(noteFormId);
            if (rt == "NOTE")
                noteType++;
            else if (rt is null)
                unknownType++;
            else
            {
                otherTypes[rt] = otherTypes.GetValueOrDefault(rt) + 1;
                if (nonNoteExamples.Count < 10)
                    nonNoteExamples.Add($"  {name}: iref {cf.Iref} (marker 0x{cf.FormId:X8}) -> note 0x{noteFormId:X8} = {rt}");
            }
        }
    }

    Console.WriteLine($"Saves scanned: {scanned} ({parseFail} parse failures)");
    Console.WriteLine($"Masters: {dbCache.Count} distinct load order(s); up to {maxForms} forms indexed"
        + (maxForms == 0 ? " — record-type (#2) tally unavailable" : ""));
    Console.WriteLine($"Type-0x1F markers: {markers}\n");

    Console.WriteLine("#1  changeFlags values on type-0x1F markers:");
    foreach (var (flag, n) in flagValues)
        Console.WriteLine($"      0x{flag:X8}: {n}");
    Console.WriteLine($"      -> {(flagValues.Count == 1 && flagValues.ContainsKey(0x80000000u) ? "ALWAYS exactly 0x80000000" : "MULTIPLE values — read marker is not a single flag")}\n");

    Console.WriteLine("#2  note record type (via the -1 index):");
    Console.WriteLine($"      NOTE:        {noteType}");
    foreach (var (t, n) in otherTypes)
        Console.WriteLine($"      {t,-12} {n}   <-- NOT a NOTE");
    Console.WriteLine($"      unknown:     {unknownType}   (resolves to a FormID the masters don't index)");
    Console.WriteLine($"      invalid iref:{invalidIref}   (iref<=0 or FormIdArray[iref-1] empty)");
    if (dbUnavailable > 0)
        Console.WriteLine($"      (masters unavailable for {dbUnavailable} markers)");
    if (nonNoteExamples.Count > 0)
    {
        Console.WriteLine("    non-NOTE examples:");
        foreach (var ex in nonNoteExamples)
            Console.WriteLine(ex);
    }
    Console.WriteLine();

    Console.WriteLine("#3  marker refID is the note's inventory reference:");
    Console.WriteLine($"      note present as an inventory stack: {inInventory}/{invChecked} markers (FormIdArray[iref-1] is a player item)");
}

static void Resolve(FalloutSave s, string savePath, uint formId, string? dataDir)
{
    // One-shot FormID lookup (ROADMAP §4k.1 #3 scratch helper): what IS this form, and where does it appear
    // in the save — the FormID array, the player inventory, the read-note markers? Read-only.
    var db = PluginDatabase.ForSave(s, dataDir, GameDataLocator.FindMo2Mods(savePath));
    var modIndex = (int)(formId >> 24);
    Console.WriteLine($"FormID 0x{formId:X8}  (mod index 0x{modIndex:X2})");
    if (db.Count == 0)
        Console.WriteLine("  masters unavailable — pass the game Data folder as the 3rd argument for names/types");
    else
        Console.WriteLine($"  name: {db.Resolve(formId) ?? "?"}   type: {db.RecordType(formId) ?? "?"}");
    Console.WriteLine($"  source plugin: {s.PluginForModIndex(modIndex) ?? "?"}  ({s.FriendlySourceForModIndex(modIndex) ?? "?"})");

    // FormID array: every iref whose entry equals this FormID.
    var irefs = new List<int>();
    var arr = s.FormIdArray;
    for (var i = 0; i < arr.Count; i++)
        if (arr[i] == formId)
            irefs.Add(i);
    Console.WriteLine($"  FormID array: {(irefs.Count == 0 ? "(absent)" : string.Join(", ", irefs.Take(8).Select(i => $"iref {i}")))}"
        + (irefs.Count > 8 ? $" … (+{irefs.Count - 8})" : ""));

    // Inventory: stacks of this base form (item.FormId == formId).
    var stacks = s.Inventory?.Items.Where(i => i.FormId == formId).ToList() ?? [];
    Console.WriteLine($"  inventory: {(stacks.Count == 0 ? "(not carried)" : string.Join(", ", stacks.Select(i => $"x{i.Count} (iref {i.Iref}, ref {i.Iref + 1})")))}");

    // Read notes: is this form the note of a read marker (note == formId), or a marker's own refID form?
    var asNote = s.ReadNotes.Notes.Where(n => n.FormId == formId).ToList();
    var asMarker = s.ReadNotes.Notes.Where(n => n.MarkerFormId == formId).ToList();
    if (asNote.Count > 0)
        Console.WriteLine($"  read note: YES — read marker(s) at iref {string.Join(", ", asNote.Select(n => $"{n.MarkerIref} (own refID 0x{n.MarkerFormId:X8})"))}");
    else
        Console.WriteLine("  read note: no marker names this form as a read note");
    if (asMarker.Count > 0)
        Console.WriteLine($"  this FormID is itself a read marker's refID (note = 0x{asMarker[0].FormId:X8})");
}

static int SetCount(string inPath, string outPath, uint formId, uint count)
{
    var before = File.ReadAllBytes(inPath);
    var save = FalloutSave.Parse(before);
    var old = save.Inventory?.Items.FirstOrDefault(i => i.FormId == formId)?.Count;
    if (!save.TrySetItemCount(formId, count))
    {
        Console.WriteLine($"FAIL: 0x{formId:X8} has no stack in this inventory (or inventory not located).");
        return 4;
    }
    save.Save(outPath, backup: false);

    var after = File.ReadAllBytes(outPath);
    var reloaded = FalloutSave.Parse(after);
    var now = reloaded.Inventory?.Items.FirstOrDefault(i => i.FormId == formId)?.Count;
    Console.WriteLine($"0x{formId:X8}: {old} -> {now};  size {before.Length:N0} -> {after.Length:N0}");
    var ok = now == count && before.Length == after.Length;
    Console.WriteLine(ok ? "OK: count edit applied, size unchanged, re-parses." : "FAIL: did not verify.");
    return ok ? 0 : 4;
}

static int SetCondition(string inPath, string outPath, uint formId, float value)
{
    var before = File.ReadAllBytes(inPath);
    var save = FalloutSave.Parse(before);
    var old = save.Inventory?.Items.FirstOrDefault(i => i.FormId == formId && i.ConditionValueOffset is not null)?.Condition;
    if (!save.TrySetItemCondition(formId, value))
    {
        Console.WriteLine($"FAIL: 0x{formId:X8} has no condition-bearing stack in this inventory (or inventory not located).");
        return 4;
    }
    save.Save(outPath, backup: false);

    var after = File.ReadAllBytes(outPath);
    var now = FalloutSave.Parse(after).Inventory?.Items.FirstOrDefault(i => i.FormId == formId && i.ConditionValueOffset is not null)?.Condition;
    Console.WriteLine($"0x{formId:X8}: condition {old} -> {now};  size {before.Length:N0} -> {after.Length:N0}");
    var ok = now == value && before.Length == after.Length;
    Console.WriteLine(ok ? "OK: condition edit applied, size unchanged, re-parses." : "FAIL: did not verify.");
    return ok ? 0 : 4;
}

static void Caps(FalloutSave s)
{
    if (s.Caps is { } caps)
        Console.WriteLine($"Caps: {caps:N0}");
    else
        Console.WriteLine("Caps: (no caps stack found — inventory not located, or this save carries no caps)");
}

static int SetCaps(string inPath, string outPath, uint caps)
{
    var before = File.ReadAllBytes(inPath);
    var save = FalloutSave.Parse(before);
    var old = save.Caps;
    if (!save.TrySetCaps(caps))
    {
        Console.WriteLine("FAIL: no caps stack in this inventory (or inventory not located).");
        return 4;
    }
    save.Save(outPath, backup: false);

    var after = File.ReadAllBytes(outPath);
    var now = FalloutSave.Parse(after).Caps;
    Console.WriteLine($"Caps: {old} -> {now};  size {before.Length:N0} -> {after.Length:N0}");
    var ok = now == caps && before.Length == after.Length;
    Console.WriteLine(ok ? "OK: caps edit applied, size unchanged, re-parses." : "FAIL: did not verify.");
    return ok ? 0 : 4;
}

static void PlayerStat(FalloutSave s, string label, Func<FalloutSave, float?> read)
{
    if (read(s) is { } v)
        Console.WriteLine($"{label}: {v:0.###}");
    else
        Console.WriteLine($"{label}: (not located — player reference record/slot not found in this save)");
}

static int SetPlayerStat(string inPath, string outPath, string label, float value,
                         Func<FalloutSave, float, bool> set, Func<FalloutSave, float?> read)
{
    var before = File.ReadAllBytes(inPath);
    var save = FalloutSave.Parse(before);
    var old = read(save);
    if (!set(save, value))
    {
        Console.WriteLine($"FAIL: {label} not located in this save (player reference record/slot not found).");
        return 4;
    }
    save.Save(outPath, backup: false);

    var after = File.ReadAllBytes(outPath);
    var now = read(FalloutSave.Parse(after));
    Console.WriteLine($"{label}: {old:0.###} -> {now:0.###};  size {before.Length:N0} -> {after.Length:N0}");
    var ok = now == value && before.Length == after.Length;
    Console.WriteLine(ok ? $"OK: {label} edit applied, size unchanged, re-parses." : "FAIL: did not verify.");
    return ok ? 0 : 4;
}

static void FindPlayer(FalloutSave s)
{
    foreach (var (formId, label) in new (uint, string)[] { (0x07u, "Player base (TESNPC_)"), (0x14u, "PlayerRef (ACHR)") })
    {
        var iref = s.FindIref(formId);
        if (iref < 0)
        {
            Console.WriteLine($"{label}: FormID 0x{formId:X8} not found in FormID array");
            continue;
        }
        var hits = s.FindRefIdInChangeForms(iref);
        var where = hits.Count > 0 ? string.Join(", ", hits.Select(h => $"0x{h:X}")) : "(no change form)";
        Console.WriteLine($"{label}: FormID 0x{formId:X8} = iref {iref}; change form @ {where}");
    }
}

static void PlayerDump(FalloutSave s)
{
    Console.WriteLine($"changeForms @ 0x{s.Flt.ChangeFormsOffset:X} .. 0x{s.Flt.GlobalData3Offset:X}");
    if (s.Special is { } sp)
        Console.WriteLine($"SPECIAL block        @ 0x{sp.Offset:X}   ({sp})");
    if (s.PlayerBaseRecordStart is { } baseStart)
    {
        var rel = s.Special is { } sp2 ? $"  (SPECIAL = playerBase+0x{sp2.Offset - baseStart:X})" : "";
        Console.WriteLine($"player base record    @ 0x{baseStart:X}{rel}");
    }
    foreach (var a in s.PlayerAnchors())
    {
        var starts = a.RecordStarts.Count == 0
            ? "(none)"
            : string.Join(", ", a.RecordStarts.Take(16).Select(h => $"0x{h:X}")) +
              (a.RecordStarts.Count > 16 ? $" … (+{a.RecordStarts.Count - 16} more)" : "");
        Console.WriteLine($"  {a.Label,-10} FormID 0x{a.FormId:X8} = iref {a.Iref}; refID hits [{a.RecordStarts.Count}]: {starts}");
    }

    var anchor = s.PlayerBaseRecordStart ?? s.Special?.Offset;
    if (anchor is { } at)
    {
        var from = Math.Max(0, at - 16);
        Console.WriteLine($"\nhex around player base record (0x{at:X}):");
        Hex(s, (uint)from, 320);
    }
}

static void Find(FalloutSave s, string hex)
{
    // R&D: find a byte pattern anywhere in the file; for change-forms hits, name the containing record
    // and show its data-relative offset (so an inventory item reference reads as "cf … data+0xNN").
    var pattern = Convert.FromHexString(hex.Replace(" ", "").Replace("0x", ""));
    var all = s.ReadAt(0, s.FileLength);
    var records = s.EnumerateChangeForms().ToArray();
    string Containing(int off)
    {
        var lo = 0; var hi = records.Length - 1; var found = -1;
        while (lo <= hi) { var m = (lo + hi) / 2; if (records[m].Offset <= off) { found = m; lo = m + 1; } else hi = m - 1; }
        if (found < 0 || off >= records[found].Next) return "";
        var cf = records[found];
        var d = off - cf.DataOffset;
        return $"  [cf iref {cf.Iref} -> 0x{cf.FormId:X8} type 0x{cf.TypeByte:X2} len {cf.DataLength}; data{(d >= 0 ? "+" : "-")}0x{Math.Abs(d):X}]";
    }
    var hits = 0;
    for (var i = 0; i + pattern.Length <= all.Length; i++)
    {
        var match = true;
        for (var k = 0; k < pattern.Length; k++)
            if (all[i + k] != pattern[k]) { match = false; break; }
        if (!match) continue;
        var ctx = string.Join(' ', all.Skip(Math.Max(0, i - 2)).Take(pattern.Length + 8).Select(x => x.ToString("X2")));
        Console.WriteLine($"  @0x{i:X}  …{ctx}…{Containing(i)}");
        if (++hits >= 100) { Console.WriteLine("  … (truncated at 100)"); break; }
    }
    Console.WriteLine($"{hits} hit(s) for [{Convert.ToHexString(pattern)}]");
}

static void IDiff(string pathA, string pathB, string? mode = null)
{
    // Insertion-aware change-form diff. Adding/removing an item shifts absolute offsets (a new change
    // form + a new FormID-array entry), so a positional byte diff is useless. Instead we walk both saves'
    // change forms and align them by record identity: records match until an insertion, after which B is
    // offset by one. This surfaces (1) the inserted/removed record and (2) the record whose DATA changed
    // in place — for a dropped stacked item that second record holds the inventory count.
    //
    // `clean` mode (ROADMAP §4k.1 #7): every save rewrites a recurring per-reference stamp (a game-time-like
    // field) into thousands of records, swamping the real signal. Rather than decode the stamp, we detect it by
    // FREQUENCY — a byte-run whose exact old→new value-pair recurs across many records is boilerplate — and hide
    // any DATA-CHANGED record whose runs are ALL boilerplate. Insertions/removals/length changes are always shown.
    var clean = string.Equals(mode, "clean", StringComparison.OrdinalIgnoreCase);
    var sa = FalloutSave.Parse(File.ReadAllBytes(pathA));
    var sb = FalloutSave.Parse(File.ReadAllBytes(pathB));
    var recsA = sa.EnumerateChangeForms().ToArray();
    var recsB = sb.EnumerateChangeForms().ToArray();
    Console.WriteLine($"A: {recsA.Length:N0} change forms   B: {recsB.Length:N0} change forms   (delta {recsB.Length - recsA.Length:+#;-#;0})");

    // Buffer events so `clean` can suppress game-time/havok churn once run frequencies are known. Each churn record
    // carries a few GLOBALLY-recurring stamp runs (identical old→new bytes in thousands of records) plus one
    // per-reference float run bracketed between them. So a run is "churn" if its value-pair recurs, or if it sits
    // adjacent to a recurring run (same cluster); a record is hidden only when every run is churn.
    var output = new List<(string Header, List<(int Start, byte[] Old, byte[] New)>? Runs, bool AlwaysShow)>();
    var valueCount = new Dictionary<string, int>();

    // Align by resolved FormID, not raw iref: creating a form (e.g. a dropped item) renumbers the FormID
    // array, so the same form can have a different iref in each save. FormIDs are stable, giving a clean
    // alignment that survives the renumbering. Allow single insertions/deletions on either side.
    int ia = 0, ib = 0, dataChanges = 0;
    while (ia < recsA.Length && ib < recsB.Length)
    {
        var ca = recsA[ia];
        var cb = recsB[ib];
        if (ca.FormId == cb.FormId && ca.TypeByte == cb.TypeByte)
        {
            // Same record in both — compare its data payload byte-for-byte.
            if (ca.DataLength == cb.DataLength)
            {
                var changes = new List<int>();
                for (var k = 0; k < ca.DataLength; k++)
                    if (sa.ReadAt(ca.DataOffset + k, 1)[0] != sb.ReadAt(cb.DataOffset + k, 1)[0])
                        changes.Add(k);
                if (changes.Count > 0)
                {
                    dataChanges++;
                    var runs = CoalesceRuns(sa, ca.DataOffset, sb, cb.DataOffset, changes);
                    foreach (var r in runs)
                        valueCount[RunSig(r.Old, r.New)] = valueCount.GetValueOrDefault(RunSig(r.Old, r.New)) + 1;
                    output.Add(($"\n~ DATA CHANGED  iref {ca.Iref} -> 0x{ca.FormId:X8}  type 0x{ca.TypeByte:X2}  len {ca.DataLength}  ({changes.Count} byte(s))", runs, false));
                }
            }
            else
                output.Add(($"\n~ LEN CHANGED   iref {ca.Iref} -> 0x{ca.FormId:X8}  type 0x{ca.TypeByte:X2}  {ca.DataLength} -> {cb.DataLength}", null, true));
            ia++; ib++;
        }
        else if (ib + 1 < recsB.Length && recsB[ib + 1].FormId == ca.FormId)
        {
            var bytes = sb.ReadAt(cb.DataOffset, Math.Min(cb.DataLength, 64));
            output.Add(($"\n+ INSERTED in B  iref {cb.Iref} -> 0x{cb.FormId:X8}  type 0x{cb.TypeByte:X2}  flags 0x{cb.ChangeFlags:X8}  len {cb.DataLength}  @0x{cb.Offset:X}"
                + $"\n    data: {HexBytes(bytes)}{(cb.DataLength > 64 ? " …" : "")}", null, true));
            ib++;
        }
        else if (ia + 1 < recsA.Length && recsA[ia + 1].FormId == cb.FormId)
        {
            output.Add(($"\n- REMOVED from B iref {ca.Iref} -> 0x{ca.FormId:X8}  type 0x{ca.TypeByte:X2}  len {ca.DataLength}  @0x{ca.Offset:X}", null, true));
            ia++;
        }
        else { ia++; ib++; } // unaligned single mismatch — skip both
    }

    // A run value-pair seen in this many records (≥0.5%, min 8) is a recurring game-time/havok stamp, not signal.
    var threshold = Math.Max(8, dataChanges / 200);
    const int adjacency = 0x40; // a non-recurring run within this many bytes of a recurring one is the same cluster
    int shown = 0, suppressed = 0;
    foreach (var (header, runs, always) in output)
    {
        if (clean && !always && runs is not null && AllChurn(runs))
        {
            suppressed++;
            continue;
        }
        Console.WriteLine(header);
        if (runs is not null)
            foreach (var r in runs)
                Console.WriteLine($"    data+0x{r.Start:X} ({r.Old.Length} B)  A: {HexBytes(r.Old)}  B: {HexBytes(r.New)}");
        shown++;
    }
    Console.WriteLine($"\n{dataChanges} record(s) changed data in place."
        + (clean ? $"  ({suppressed} hidden as recurring game-time/havok churn (≥{threshold}×); records with a unique change shown above)" : ""));
    return;

    // A record is pure churn when every run is either a globally-recurring stamp or sits next to one (same cluster).
    bool AllChurn(List<(int Start, byte[] Old, byte[] New)> runs)
    {
        bool Recurring((int Start, byte[] Old, byte[] New) r) => valueCount[RunSig(r.Old, r.New)] >= threshold;
        if (!runs.Any(Recurring))
            return false; // no recurring stamp at all -> a genuine change, always show
        foreach (var r in runs)
            if (!Recurring(r) && !runs.Any(o => Recurring(o) && Math.Abs(o.Start - r.Start) <= adjacency))
                return false; // a non-recurring run away from any stamp -> real signal
        return true;
    }
}

static List<(int Start, byte[] Old, byte[] New)> CoalesceRuns(FalloutSave sa, int aData, FalloutSave sb, int bData, List<int> changes)
{
    // Coalesce adjacent changed offsets into runs, capturing the actual A/B bytes (capped) for display + signature.
    var runs = new List<(int, byte[], byte[])>();
    var i = 0;
    while (i < changes.Count)
    {
        var startK = changes[i];
        var j = i;
        while (j + 1 < changes.Count && changes[j + 1] == changes[j] + 1) j++;
        var runLen = Math.Min(changes[j] - startK + 1, 16);
        runs.Add((startK, sa.ReadAt(aData + startK, runLen), sb.ReadAt(bData + startK, runLen)));
        i = j + 1;
    }
    return runs;
}

// A run's identity for the `clean`-mode frequency tally: its old→new byte values, ignoring the absolute offset
// (the same game-time/havok stamp lands at different data offsets in different record types).
static string RunSig(byte[] old, byte[] @new) => Convert.ToHexString(old) + ">" + Convert.ToHexString(@new);

static string HexBytes(byte[] bytes) => string.Join(' ', bytes.Select(x => x.ToString("X2")));

// R&D (§7): float-aware aligned diff. A scalar like XP/karma is likely a float32, and a float change
// (e.g. 100.0 -> 150.0) only alters its high bytes — the low bytes stay 0 — so it never surfaces as a
// clean byte-run delta in `idiff`. This aligns change forms by FormID (as `idiff` does) and, for every
// same-length aligned record, reads the FULL 4 bytes at each offset from both saves and reports offsets
// where the float32 changed by ≈ <delta>. Emits machine-parseable "iref|formid|data+off|fa|fb" lines so
// two transitions can be intersected.
static void FDiff(string pathA, string pathB, float delta, float tol)
{
    var sa = FalloutSave.Parse(File.ReadAllBytes(pathA));
    var sb = FalloutSave.Parse(File.ReadAllBytes(pathB));
    var recsA = sa.EnumerateChangeForms().ToArray();
    var recsB = sb.EnumerateChangeForms().ToArray();
    Console.Error.WriteLine($"A: {recsA.Length:N0} CF   B: {recsB.Length:N0} CF   target float delta {delta} ±{tol}");

    int ia = 0, ib = 0, hits = 0;
    while (ia < recsA.Length && ib < recsB.Length)
    {
        var ca = recsA[ia];
        var cb = recsB[ib];
        if (ca.FormId == cb.FormId && ca.TypeByte == cb.TypeByte)
        {
            if (ca.DataLength == cb.DataLength)
            {
                for (var k = 0; k + 4 <= ca.DataLength; k++)
                {
                    var fa = BitConverter.ToSingle(sa.ReadAt(ca.DataOffset + k, 4));
                    var fb = BitConverter.ToSingle(sb.ReadAt(cb.DataOffset + k, 4));
                    if (!float.IsFinite(fa) || !float.IsFinite(fb) || fa == fb)
                        continue;
                    if (Math.Abs(fb - fa) > 1e12f)
                        continue;
                    if (Math.Abs((fb - fa) - delta) <= tol)
                    {
                        hits++;
                        Console.WriteLine($"{ca.Iref}|0x{ca.FormId:X8}|data+0x{k:X}|{fa:R}|{fb:R}");
                    }
                }
            }
            ia++; ib++;
        }
        else if (ib + 1 < recsB.Length && recsB[ib + 1].FormId == ca.FormId) ib++;
        else if (ia + 1 < recsA.Length && recsA[ia + 1].FormId == cb.FormId) ia++;
        else { ia++; ib++; }
    }
    Console.Error.WriteLine($"{hits} float field(s) changed by ≈{delta}.");
}

static void Walk(FalloutSave s)
{
    // Validate the change-form header format: a correct walk yields exactly ChangeFormCount records
    // and lands precisely on GlobalData3Offset.
    var flt = s.Flt;
    var typeCounts = new SortedDictionary<int, int>();
    var count = 0;
    var last = (int)flt.ChangeFormsOffset;
    FnvSaveExplorer.Core.FalloutSave.ChangeFormHeader? player = null;
    foreach (var cf in s.EnumerateChangeForms())
    {
        count++;
        typeCounts[cf.FormType] = typeCounts.GetValueOrDefault(cf.FormType) + 1;
        last = cf.Next;
        if (cf.FormId == 0x14)
            player = cf;
    }
    Console.WriteLine($"changeForms 0x{flt.ChangeFormsOffset:X}..0x{flt.GlobalData3Offset:X};  walked {count:N0} records (FLT says {flt.ChangeFormCount:N0})");
    Console.WriteLine($"ended at 0x{last:X}  ->  {(last == (int)flt.GlobalData3Offset && count == flt.ChangeFormCount ? "EXACT MATCH (header format confirmed)" : "MISMATCH")}");
    Console.WriteLine("form-type histogram (type -> records):");
    foreach (var (t, n) in typeCounts)
        Console.WriteLine($"   0x{t:X2} : {n,6:N0}");
    if (player is { } p)
        Console.WriteLine($"\nPlayerRef (0x14): record @0x{p.Offset:X}  type 0x{p.TypeByte:X2} (form 0x{p.FormType:X2})  flags 0x{p.ChangeFlags:X8}  data @0x{p.DataOffset:X} len {p.DataLength}");

    Console.WriteLine("\nall change forms for player FormIDs 0x07 / 0x14:");
    foreach (var cf in s.EnumerateChangeForms())
        if (cf.FormId is 0x07 or 0x14)
            Console.WriteLine($"   0x{cf.FormId:X8}: @0x{cf.Offset:X} type 0x{cf.TypeByte:X2} flags 0x{cf.ChangeFlags:X8} len {cf.DataLength}");

    Console.WriteLine("\n12 largest change forms:");
    foreach (var cf in s.EnumerateChangeForms().OrderByDescending(c => c.DataLength).Take(12))
        Console.WriteLine($"   @0x{cf.Offset:X}  iref {cf.Iref,6} -> 0x{cf.FormId:X8}  type 0x{cf.TypeByte:X2}  flags 0x{cf.ChangeFlags:X8}  len {cf.DataLength,7:N0}");
}

// ROADMAP §6 #1a — decode-COVERAGE SURVEY. Aggregates, per change-form FORM TYPE (the low 6 bits of
// the type byte, §4f), the payload shape across the whole corpus: how many records, the length
// distribution (a single dominant length => a fixed-width struct, easy to decode; a spread => variable),
// the distinct changeFlags values (the flags GATE which sub-blocks are present, §8a's ordered model), and
// — when one type is deep-dived — a per-offset CONSTANCY MAP: across every record sharing a (changeFlags,
// length) group, which byte positions are CONSTANT (delimiters / type tags / structure) vs VARIABLE (data
// fields). Constant positions reveal field boundaries directly. Pure corpus alignment — no masters, no
// in-game saves, read-only. This is the autonomous structural microscope the full-decode work runs on.
static void Survey(string pathOrDir, int? onlyType)
{
    var files = Directory.Exists(pathOrDir)
        ? Directory.EnumerateFiles(pathOrDir, "*.fos").OrderBy(f => f).ToList()
        : [pathOrDir];
    Console.WriteLine($"Surveying {files.Count} save(s){(onlyType is { } t ? $" — deep-dive form type 0x{t:X2}" : "")}\n");

    // Per form type: counts + a length histogram + a changeFlags histogram. For a deep-dive we also keep,
    // per (changeFlags,length) group, a per-offset byte tally so we can report which positions are constant.
    var recCount = new SortedDictionary<int, long>();
    var lenHist = new SortedDictionary<int, SortedDictionary<int, int>>();   // type -> (len -> count)
    var flagHist = new SortedDictionary<int, Dictionary<uint, int>>();        // type -> (flags -> count)
    // Deep-dive constancy: (flags,len) -> per-offset [value -> count] for the leading bytes.
    const int ConstancyBytes = 96;   // cap how many leading payload bytes we profile per group
    var constancy = new Dictionary<(uint Flags, int Len), Dictionary<int, int>[]>();
    var groupCount = new Dictionary<(uint Flags, int Len), int>();
    var groupExample = new Dictionary<(uint Flags, int Len), string>();

    static void Bump<TK>(IDictionary<TK, int> d, TK k) => d[k] = d.TryGetValue(k, out var v) ? v + 1 : 1;

    int parsed = 0, failed = 0;
    foreach (var f in files)
    {
        FalloutSave s;
        try { s = FalloutSave.Load(f); } catch { failed++; continue; }
        parsed++;
        foreach (var cf in s.EnumerateChangeForms())
        {
            var ft = cf.FormType;
            recCount[ft] = recCount.GetValueOrDefault(ft) + 1;
            if (!lenHist.TryGetValue(ft, out var lh)) lenHist[ft] = lh = new SortedDictionary<int, int>();
            Bump(lh, cf.DataLength);
            if (!flagHist.TryGetValue(ft, out var fh)) flagHist[ft] = fh = new Dictionary<uint, int>();
            Bump(fh, cf.ChangeFlags);

            if (onlyType == ft)
            {
                var key = (cf.ChangeFlags, cf.DataLength);
                Bump(groupCount, key);
                if (!constancy.TryGetValue(key, out var perOff))
                {
                    perOff = new Dictionary<int, int>[Math.Min(cf.DataLength, ConstancyBytes)];
                    for (var i = 0; i < perOff.Length; i++) perOff[i] = new Dictionary<int, int>();
                    constancy[key] = perOff;
                    groupExample[key] = Path.GetFileNameWithoutExtension(f);
                }
                var n = Math.Min(cf.DataLength, perOff.Length);
                var bytes = s.ReadAt(cf.DataOffset, n);
                for (var i = 0; i < n; i++)
                    Bump(perOff[i], bytes[i]);
            }
        }
    }
    Console.WriteLine($"parsed {parsed} save(s){(failed > 0 ? $", {failed} failed to load" : "")}\n");

    if (onlyType is null)
    {
        // Summary table: one row per form type. "lens" = distinct payload lengths; a single dominant length
        // (shown as the modal length + its share) flags a fixed-width struct worth decoding first.
        Console.WriteLine("type  name      records   %empty  distinct-len  modal-len(share)   distinct-flags  top-flags(share)");
        foreach (var (ft, n) in recCount)
        {
            var lh = lenHist[ft];
            var fh = flagHist[ft];
            var empty = lh.GetValueOrDefault(0);
            var (modalLen, modalLenN) = lh.OrderByDescending(kv => kv.Value).First();
            var (topFlag, topFlagN) = fh.OrderByDescending(kv => kv.Value).First();
            Console.WriteLine(
                $"0x{ft:X2}  {FormTypeName(ft),-8}  {n,8:N0}  {(double)empty / n,6:P0}  {lh.Count,12:N0}  " +
                $"{modalLen,8} ({(double)modalLenN / n,4:P0})   {fh.Count,12:N0}  0x{topFlag:X8} ({(double)topFlagN / n,4:P0})");
        }
        Console.WriteLine("\n(deep-dive one type:  survey <path> 0xNN  — adds a per-offset constancy map per (flags,len) group)");
        return;
    }

    // Deep-dive: per (flags,len) group, the per-offset constancy map. '··' = constant byte (its value shown),
    // 'XX' = a position that varies across records in the group (a data field). Groups sorted by frequency.
    Console.WriteLine($"form type 0x{onlyType:X2} ({FormTypeName(onlyType.Value)}): {recCount.GetValueOrDefault(onlyType.Value):N0} records, " +
                      $"{groupCount.Count} (flags,len) groups\n");
    var shown = 0;
    foreach (var (key, gn) in groupCount.OrderByDescending(kv => kv.Value))
    {
        if (shown++ >= 16) { Console.WriteLine($"… {groupCount.Count - 16} more groups"); break; }
        Console.WriteLine($"flags 0x{key.Flags:X8}  len {key.Len}  × {gn:N0} records  (e.g. {groupExample[key]})");
        var perOff = constancy[key];
        var consts = 0;
        for (var i = 0; i < perOff.Length; i++) if (perOff[i].Count == 1) consts++;
        Console.WriteLine($"   leading {perOff.Length} bytes: {consts} constant, {perOff.Length - consts} variable");
        var sb = new System.Text.StringBuilder("   ");
        for (var i = 0; i < perOff.Length; i++)
        {
            if (i > 0 && i % 24 == 0) sb.Append("\n   ");
            sb.Append(perOff[i].Count == 1 ? perOff[i].Keys.First().ToString("X2") : "··");
            sb.Append(' ');
        }
        Console.WriteLine(sb.ToString());
        Console.WriteLine();
    }
}

// R&D (§6 #1): print the masters record SIGNATURE (REFR/DOOR/ACTI/STAT/…) for each save FormID, by opening
// the owning plugin and traversing its groups (header-only, TesPlugin.FindRecordSignatures). Identifies what a
// change form points at when the name index can't (world/reference records aren't indexed for naming).
static void RecId(FalloutSave s, string savePath, uint[] formIds)
{
    var dataFolder = GameDataLocator.FindDataFolder();
    var mo2 = GameDataLocator.FindMo2Mods(savePath);
    var byPlugin = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
    foreach (var f in formIds)
    {
        var plugin = s.PluginForModIndex((int)(f >> 24));
        if (plugin is null) { Console.WriteLine($"0x{f:X8}  (mod index 0x{f >> 24:X2} not in load order)"); continue; }
        (byPlugin.TryGetValue(plugin, out var l) ? l : byPlugin[plugin] = []).Add(f);
    }
    foreach (var (plugin, fs2) in byPlugin)
    {
        var file = FindPluginFile(plugin, dataFolder, mo2);
        if (file is null) { Console.WriteLine($"{plugin}: file not found (need the game Data folder / MO2 mods)"); continue; }
        using var stream = File.OpenRead(file);
        var wanted = fs2.Select(f => f & 0xFFFFFFu).ToHashSet();
        var found = TesPlugin.FindRecordSignatures(stream, wanted);
        foreach (var f in fs2)
        {
            if (!found.TryGetValue(f & 0xFFFFFF, out var rec)) { Console.WriteLine($"0x{f:X8}  (not found)  [{plugin}]"); continue; }
            var baseNote = rec.BaseFormId != 0 ? $"  base 0x{rec.BaseFormId:X8}" : "";
            var marker = rec.IsMapMarker ? "  [MAP MARKER]" : "";
            var name = rec.Name is { } n ? $"  \"{n}\"" : "";
            Console.WriteLine($"0x{f:X8}  {rec.Signature}{baseNote}{marker}{name}  [{plugin}]");
        }
    }
}

// R&D (§6 #1): locate a base form by NAME across the save's masters, reporting the SAVE-SPACE FormID
// (mod index from the save's load order). Used to find e.g. a perk's FormID, then trace where the save stores it.
static void FindName(FalloutSave s, string savePath, string substring, string? onlySig)
{
    var dataFolder = GameDataLocator.FindDataFolder();
    var mo2 = GameDataLocator.FindMo2Mods(savePath);
    var hitCount = 0;
    for (var modIndex = 0; modIndex < s.Plugins.Count; modIndex++)
    {
        var plugin = s.Plugins[modIndex];
        var file = FindPluginFile(plugin, dataFolder, mo2);
        if (file is null) continue;
        using var stream = File.OpenRead(file);
        List<(uint FormId, string Sig, string Name)> hits;
        try { hits = TesPlugin.FindByName(stream, substring, onlySig); } catch { continue; }
        foreach (var (local, sig, name) in hits)
        {
            var saveFormId = ((uint)modIndex << 24) | (local & 0xFFFFFF);
            Console.WriteLine($"0x{saveFormId:X8}  {sig}  \"{name}\"  [{plugin}]");
            hitCount++;
        }
    }
    if (hitCount == 0) Console.WriteLine($"No record name contains \"{substring}\"{(onlySig is null ? "" : $" (sig {onlySig})")}.");
}

static string? FindPluginFile(string plugin, string? dataFolder, string? mo2Mods)
{
    if (dataFolder is not null && File.Exists(Path.Combine(dataFolder, plugin)))
        return Path.Combine(dataFolder, plugin);
    if (mo2Mods is not null && Directory.Exists(mo2Mods))
        foreach (var mod in Directory.EnumerateDirectories(mo2Mods))
            if (File.Exists(Path.Combine(mod, plugin)))
                return Path.Combine(mod, plugin);
    return null;
}

// FNV change-form type-byte hints. NOTE: the type byte is a change-CATEGORY, NOT the changed form's record
// type (SPEC §4l — a `recid` census shows each type byte spans many record types and vice-versa). These are
// only the COMMON association for the player's own records (the usual cfwalk target), as a reading aid — use
// `recid` for a given record's actual masters record type. Most types stay "?" (decode by payload shape).
static string FormTypeName(int formType) => formType switch
{
    0x01 => "REFR?",   // the player inventory ref is 0x01; but 0x01 ≠ "REFR" in general (§4l)
    0x02 => "ACHR?",   // the PlayerRef is 0x02; not a record-type tag
    0x1F => "NOTE-rd",  // read-note marker; its −1-hopped base is uniformly NOTE (§4k.1)
    _ => "?",
};

// ROADMAP §6 #1b — the labeled "FULL WALK". Renders a change form's payload as a field tree, emitting
// labeled fields for the types whose structure SPEC §4l has pinned and an explicit `unknown[n]` gap for
// everything still undecoded — so coverage is always visible (never silently skipped). This is the
// consumer-facing surface of the decode: as a type graduates from "located" to "field-decoded", its
// emitter here replaces the `unknown[n]`. Semantics stay unlabelled per "size, don't guess".
static void CfWalk(FalloutSave s, string savePath, int? iref = null, int? type = null, int max = 3)
{
    var db = PluginDatabase.ForSave(s, null, GameDataLocator.FindMo2Mods(savePath));
    var shown = 0;
    foreach (var cf in s.EnumerateChangeForms())
    {
        if (iref is { } wi && cf.Iref != wi) continue;
        if (type is { } wt && cf.FormType != wt) continue;
        if (type is not null && shown >= max) break;
        shown++;

        var name = db.Count > 0 ? db.Resolve(cf.FormId) : null;
        Console.WriteLine($"change form @0x{cf.Offset:X}  iref {cf.Iref} -> 0x{cf.FormId:X8}{(name is null ? "" : $" {name}")}");
        Console.WriteLine($"  type 0x{cf.TypeByte:X2} (formType 0x{cf.FormType:X2} {FormTypeName(cf.FormType)})  " +
                          $"version 0x{cf.Version:X2}  changeFlags 0x{cf.ChangeFlags:X8}  len {cf.DataLength}");
        var data = s.ReadAt(cf.DataOffset, cf.DataLength);
        foreach (var line in WalkPayload(cf.FormType, cf.ChangeFlags, data))
            Console.WriteLine("    " + line);
        Console.WriteLine();
        if (iref is not null) break;
    }
    if (shown == 0) Console.WriteLine("No matching change form.");
}

// Emits the field-tree lines for one change-form payload. Returns "+0xOFF  label = value" for decoded
// fields and "+0xOFF  unknown[n]  <hex>" for gaps. The per-type emitters mirror the sized structures in
// SPEC §4l; anything not yet pinned is one honest `unknown[n]` over the whole payload.
static IEnumerable<string> WalkPayload(int formType, uint flags, byte[] d)
{
    if (d.Length == 0)
    {
        // 0x08 / 0x1F (and any other) zero-payload marker — the record's presence IS the state (§4l/§4k).
        yield return formType == 0x1F ? "(no payload — NOTE read marker, §4k)" : "(no payload — marker form, §4l)";
        yield break;
    }

    static string Hex(byte[] b, int off, int len) =>
        string.Join(' ', b.Skip(off).Take(len).Select(x => x.ToString("X2")));
    static uint U32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    static string Field(int off, string label, string val) => $"+0x{off:X3}  {label} = {val}";
    static string Gap(int off, byte[] b, int len)
    {
        const int preview = 48;   // keep the gap honest about its size without flooding on a big REFR payload
        var head = Hex(b, off, Math.Min(len, preview));
        return len <= preview ? $"+0x{off:X3}  unknown[{len}]  {head}"
                              : $"+0x{off:X3}  unknown[{len}]  {head} … (+{len - preview} more)";
    }

    switch (formType)
    {
        // Fixed-width sized types (SPEC §4l). Each field labelled u32/i32; the 0x7C delimiters shown inline.
        case 0x2B or 0x32 when d.Length == 10 && d[4] == 0x7C && d[9] == 0x7C:
            yield return Field(0, "u32[0]", U32(d, 0).ToString());
            yield return Field(5, "u32[1]", U32(d, 5).ToString());
            break;
        case 0x21 when d.Length == 20 && d[4] == 0x7C && d[9] == 0x7C && d[14] == 0x7C && d[19] == 0x7C:
            yield return Field(0, "u32[0]", U32(d, 0).ToString());
            yield return Field(5, "u32[1]", U32(d, 5).ToString());
            yield return Field(10, "u32[2]", U32(d, 10).ToString());
            yield return Field(15, "i32[3]", ((int)U32(d, 15)).ToString());   // observed always -1 (sentinel)
            break;
        case 0x20 when d.Length == 17 && d[16] == 0x7C:
            // Four packed u32 LE then a trailing delimiter (no internal 7C, §4l).
            yield return Field(0, "u32[0]", U32(d, 0).ToString());
            yield return Field(4, "u32[1]", U32(d, 4).ToString());
            yield return Field(8, "u32[2]", U32(d, 8).ToString());
            yield return Field(12, "u32[3]", U32(d, 12).ToString());
            break;
        case 0x28 when d.Length == 62 && d[0] == 0x10 && d[1] == 0x7C:
            // [10][7C] then 4× [u8 u8 u8][7C][u16][7C][u16][7C][f32][7C] (§4l).
            yield return Field(0, "tag", "0x10");
            for (var e = 0; e < 4; e++)
            {
                var o = 2 + e * 15;
                if (o + 15 > d.Length) { yield return Gap(o, d, d.Length - o); break; }
                // entry = [3 B][7C][u16][7C][u16][7C][f32][7C] (§4l): f32 (always 1.0) at o+10.
                var f = BitConverter.ToSingle(d, o + 10);
                yield return Field(o, $"entry[{e}]",
                    $"b={Hex(d, o, 3)} u16={(ushort)(d[o + 4] | (d[o + 5] << 8))} " +
                    $"u16={(ushort)(d[o + 7] | (d[o + 8] << 8))} f32={f}");
            }
            break;
        case 0x0B when d.Length == 25 && d[24] == 0x7C:
            // Constant 24-byte config block on vanilla (byte-identical); other variants on modded (§4l).
            yield return Field(0, "config[24]", Hex(d, 0, 24));
            break;
        // 0x25 — count-prefixed refID list: [u32 count][7C] then count × [ref:3 BE][7C] (§4l, len = 5+4·count).
        case 0x25 when d.Length >= 5 && d[4] == 0x7C && U32(d, 0) is var c25 &&
                       c25 <= 0x100000 && d.Length == 5 + 4 * (int)c25:
            yield return Field(0, "count", c25.ToString());
            for (var e = 0; e < (int)c25; e++)
            {
                var o = 5 + e * 4;
                yield return Field(o, $"ref[{e}]", Hex(d, o, 3));   // 3-byte BE refID; resolve via §4f if needed
            }
            break;
        // 0x22 — count-prefixed list: [n:u8][7C] then n/4 × [ref:3 BE][7C][u32][7C][u32][7C] (§4l, len = 2+14·n/4).
        case 0x22 when d.Length >= 2 && d[1] == 0x7C && (d[0] & 3) == 0 && d[0] / 4 is var c22 &&
                       d.Length == 2 + 14 * c22:
            yield return Field(0, "count*4", $"{d[0]} ({c22} entries)");
            for (var e = 0; e < c22; e++)
            {
                var o = 2 + e * 14;
                yield return Field(o, $"entry[{e}]",
                    $"ref={Hex(d, o, 3)} u32={U32(d, o + 4)} u32={U32(d, o + 9)}");
            }
            break;

        // Delimited script/animation/actor state — STRUCTURE known (0x7C-tokenized with embedded
        // [u16 len][7C][ascii][7C] strings), FIELDS not yet labelled (§4l: 0x00, 0x0A). Show the tokens so
        // the structure is visible while the whole payload remains honestly "not field-decoded".
        case 0x00 or 0x0A:
            yield return $"(0x7C-delimited {FormTypeName(formType)} state, §4l — tokens; fields not yet labelled)";
            foreach (var line in DelimitedTokens(d)) yield return line;
            break;

        // REFR/ACHR — break the payload into the deterministically-LOCATED structural spans (§4i/§4j): the
        // MOVE block, the havok/actor-value array, then the ExtraDataList + inventory. Each stays a labeled
        // span (located, not byte-decoded here); `refdump`/`inventory` give the byte-level walk + item list.
        case 0x01 or 0x02:
        {
            var hasMove = (flags & ReferenceChangeForm.ChangeRefrMove) != 0
                          && ReferenceChangeForm.MoveBlockLength < d.Length
                          && d[ReferenceChangeForm.MoveBlockLength] == ReferenceChangeForm.Delimiter;
            if (!hasMove)
            {
                yield return "(REFR/ACHR — no MOVE block to anchor; byte-level walk via `refdump`)";
                yield return Gap(0, d, d.Length);
                break;
            }
            yield return Field(0, "MOVE block[27]", Hex(d, 0, 27) + "  (cell ref + pos + rot, §4i)");
            var afterMove = ReferenceChangeForm.MoveBlockLength + 1;   // past the 0x7C delimiter
            var listStart = ReferenceChangeForm.InventorySearchStart(d, 0, flags);
            if (listStart > afterMove)
                yield return $"+0x{afterMove:X3}  unknown[{listStart - afterMove}]  havok/actor-value array (§4i/§4j; karma/XP at slots 100/101)";
            if (listStart < d.Length)
                yield return $"+0x{listStart:X3}  unknown[{d.Length - listStart}]  ExtraDataList + inventory (§4g–§4i; decode via `refdump`/`inventory`)";
            break;
        }

        default:
            yield return Gap(0, d, d.Length);
            break;
    }
}

// Splits a 0x7C-delimited payload into tokens, rendering each as the most plausible reading: an embedded
// length-prefixed ASCII string ([u16 len][7C][bytes]) prints as text; an all-printable run as text; a
// 4-byte run as u32/float; otherwise raw hex. Display-only (does not claim a field layout).
static IEnumerable<string> DelimitedTokens(byte[] d)
{
    var start = 0;
    var idx = 0;
    for (var i = 0; i <= d.Length; i++)
    {
        if (i != d.Length && d[i] != 0x7C) continue;
        var len = i - start;
        if (len > 0)
        {
            var seg = d.Skip(start).Take(len).ToArray();
            string render;
            if (seg.All(b => b >= 0x20 && b < 0x7F))
                render = $"\"{System.Text.Encoding.ASCII.GetString(seg)}\"";
            else if (len == 4)
            {
                var u = (uint)(seg[0] | (seg[1] << 8) | (seg[2] << 16) | (seg[3] << 24));
                render = $"u32={u} f32={BitConverter.ToSingle(seg, 0)}";
            }
            else
                render = string.Join(' ', seg.Select(x => x.ToString("X2")));
            yield return $"+0x{start:X3}  tok[{idx}] ({len}b) {render}";
        }
        idx++;
        start = i + 1;
    }
}

static void QuestFired(FalloutSave s, string savePath, string? dataDir)
{
    // ROADMAP §6 #16 Phase B step 2 probe: a dialogue INFO the player has said gets a change form in the save, so
    // intersecting the save's change-form FormIDs with the masters' quest-starting INFOs yields the quests whose
    // dialogue trigger ACTUALLY FIRED — the candidate "genuinely started (not background-init)" signal.
    var db = PluginDatabase.ForSave(s, dataDir, GameDataLocator.FindMo2Mods(savePath), withDialogue: true);
    if (db.Count == 0) { Console.WriteLine("Needs the game Data folder."); return; }

    var byEdid = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
    foreach (var q in db.Quests.Values)
        if (q.Edid is { } e) byEdid[e] = q.FormId;

    var present = s.EnumerateChangeForms().Select(cf => cf.FormId).ToHashSet();
    // quest FormId -> the said-INFO FormIds in this save that start/advance it
    var firedBy = new Dictionary<uint, List<uint>>();
    var effectDetail = new Dictionary<uint, List<string>>(); // quest -> "verb arg [?cond]" summaries
    foreach (var (infoFormId, effects) in db.DialogueInfoEffects)
        if (present.Contains(infoFormId))
            foreach (var e in effects)
                if (e.Verb is QuestScriptVerb.StartQuest or QuestScriptVerb.SetStage or QuestScriptVerb.StopQuest
                        or QuestScriptVerb.CompleteQuest or QuestScriptVerb.FailQuest
                    && byEdid.TryGetValue(e.TargetQuestEdid, out var q))
                {
                    (firedBy.TryGetValue(q, out var l) ? l : firedBy[q] = []).Add(infoFormId);
                    (effectDetail.TryGetValue(q, out var d) ? d : effectDetail[q] = [])
                        .Add($"{e.Verb}{(e.Verb == QuestScriptVerb.SetStage ? $" {e.Arg1}" : "")}{(e.Conditional ? " ?" : "")}");
                }

    Console.WriteLine($"Quest-starting INFOs in masters: {db.DialogueInfoEffects.Count}; change forms in save: {present.Count}.");
    Console.WriteLine($"Quests whose dialogue trigger FIRED (said-INFO present in save): {firedBy.Count}\n");
    foreach (var (questId, infos) in firedBy.OrderBy(kv => db.Quest(kv.Key)?.Name, StringComparer.OrdinalIgnoreCase))
    {
        var q = db.Quest(questId);
        var pf = q is { IsPlayerFacing: true } ? "" : "  (not player-facing)";
        Console.WriteLine($"  0x{questId:X8}  \"{q?.Name ?? "?"}\"{pf}  [{string.Join(", ", effectDetail[questId].Distinct())}]  <- INFO {string.Join(", ", infos.Distinct().Select(i => $"0x{i:X8}"))}");
    }
}

static void QuestConditions(FalloutSave s, string savePath, string? dataDir)
{
    // ROADMAP §6 #16 CTDA spike: a dialogue INFO the player SAID has a change form in the save, so its CTDA
    // conditions held when it fired. A said-INFO carrying a quest-state condition (GetStage/GetQuestCompleted X)
    // is therefore proof X reached that state. This probe lists, per player-facing quest, every such condition on
    // a said-INFO, and computes a conservative "implied completed" set (a condition that requires X completed /
    // X at >= its completing stage) to gauge whether this is a precision-safe completion signal.
    var db = PluginDatabase.ForSave(s, dataDir, GameDataLocator.FindMo2Mods(savePath), withDialogue: true);
    if (db.Count == 0) { Console.WriteLine("Needs the game Data folder."); return; }

    var present = s.EnumerateChangeForms().Select(cf => cf.FormId).ToHashSet();
    static string OpStr(byte op) => op switch { 0 => "==", 1 => "!=", 2 => ">", 3 => ">=", 4 => "<", 5 => "<=", _ => "?" };

    // quest FormId -> conditions seen on said-INFOs (deduped text)
    var perQuest = new Dictionary<uint, SortedSet<string>>();
    var impliedCompleted = new HashSet<uint>();
    var saidWithCond = 0;
    foreach (var (infoFormId, conditions) in db.DialogueInfoConditions)
    {
        if (!present.Contains(infoFormId))
            continue;
        saidWithCond++;
        foreach (var c in conditions)
        {
            if (db.Quest(c.QuestFormId) is not { IsPlayerFacing: true } q)
                continue;
            var p2 = c.Function == QuestFunction.GetStageDone ? $" {c.Param2}" : "";
            (perQuest.TryGetValue(c.QuestFormId, out var set) ? set : perQuest[c.QuestFormId] = [])
                .Add($"{c.Function}{p2} {OpStr(c.Op)} {c.CompareValue:g}");

            // Completion implication: a said-INFO whose condition proves X reached a completing stage (QSDT 0x01).
            // Use the MIN completing stage — reaching ANY complete-flagged stage finishes the quest (monotonic).
            var completeStage = q.Stages.Where(st => (st.Flags & 0x01) != 0).Select(st => (int?)st.Index).Min();
            var implies = c.Function switch
            {
                QuestFunction.GetQuestCompleted => c.Op is 0 or 3 && c.CompareValue >= 1,                       // == / >= 1
                QuestFunction.GetStage when completeStage is { } cs => c.Op is 0 or 2 or 3 && c.CompareValue >= cs, // == / > / >= a completing stage
                QuestFunction.GetStageDone when completeStage is { } cs => c.Param2 >= cs && c.Op is 0 or 3 && c.CompareValue >= 1,
                _ => false,
            };
            if (implies)
                impliedCompleted.Add(c.QuestFormId);
        }
    }

    Console.WriteLine($"said-INFOs carrying quest-state conditions: {saidWithCond}; player-facing quests they reference: {perQuest.Count}\n");
    foreach (var (questId, conds) in perQuest.OrderBy(kv => db.Quest(kv.Key)?.Name, StringComparer.OrdinalIgnoreCase))
    {
        var done = impliedCompleted.Contains(questId) ? "  => IMPLIED COMPLETED" : "";
        Console.WriteLine($"  0x{questId:X8}  \"{db.Quest(questId)?.Name}\"{done}");
        foreach (var c in conds)
            Console.WriteLine($"        {c}");
    }
    Console.WriteLine($"\nIMPLIED-COMPLETED player-facing quests ({impliedCompleted.Count}):");
    foreach (var id in impliedCompleted.OrderBy(i => db.Quest(i)?.Name, StringComparer.OrdinalIgnoreCase))
        Console.WriteLine($"  \"{db.Quest(id)?.Name}\"  0x{id:X8}");
}

static void QuestCounterGates(FalloutSave s, string savePath, string? dataDir, bool all)
{
    // ROADMAP §6 #16 Stage 1: the COUNTER-GATED COMPLETION GRAPH. A masters-only, save-independent analysis —
    // each player-facing quest whose GameMode/stage completes the quest (CompleteQuest / SetStage to a
    // QSDT-complete stage / CompleteAllObjectives) only inside an `if <counter> <op> N` guard, correlated with the
    // scripts that increment that counter (an actor OnDeath/event script, or the quest's own script). The headline
    // number is the honest size of the prize Stage 2's save-state evaluator can reach (the bucket-C event-completed
    // quests like Ghost Town Gunfight). `--all` also lists unbound gates (counter guard but NO increment found).
    var db = PluginDatabase.ForSave(s, dataDir, GameDataLocator.FindMo2Mods(savePath));
    if (db.Count == 0) { Console.WriteLine("Needs the game Data folder."); return; }

    var gates = db.CounterGates;
    var byQuest = gates.GroupBy(g => g.QuestFormId)
        .OrderBy(g => g.First().QuestName, StringComparer.OrdinalIgnoreCase)
        .ToList();

    Console.WriteLine($"Counter-gated completion graph: {gates.Count} gate(s) across {byQuest.Count} player-facing quest(s)\n");
    foreach (var grp in byQuest)
    {
        var name = grp.First().QuestName;
        var edid = grp.First().QuestEdid;
        var bound = grp.Any(g => g.Bound);
        if (!bound && !all)
            continue;
        Console.WriteLine($"  0x{grp.Key:X8}  \"{name}\" [{edid}]{(bound ? "" : "   (UNBOUND — counter never incremented)")}");
        foreach (var g in grp.OrderBy(g => g.Counter, StringComparer.OrdinalIgnoreCase))
        {
            var via = g.ExternallyIncremented
                ? $"{g.IncrementScripts.Count} ext script(s)" + (g.SelfIncremented ? " + self" : "")
                : g.SelfIncremented ? "self only" : "NONE";
            var stage = g.CompletingStage is { } cs ? $"SetStage {cs}" : "CompleteQuest";
            Console.WriteLine($"        if {g.Counter} {g.Op} {g.Threshold}  =>  {stage}    [increments: {via}]");
        }
    }

    // The broader event-completion graph: quests completed by a script other than their own. The script's
    // begin-block tells a kill-driven completion (OnDeath/OnHit = reachable from the save death registry) from a
    // per-frame world-poll (GameMode) or activation (OnActivate).
    var external = db.ExternalCompletions;
    var kill = external.Where(c => c.ViaKill && !c.ViaCounter).ToList();
    Console.WriteLine($"\nExternal event-completion graph (completed by a script other than the quest's own): {external.Count} quest(s)");
    Console.WriteLine($"  -- kill-driven (OnDeath/OnHit), the clean bucket-C shape beyond counters --");
    foreach (var c in kill)
        Console.WriteLine($"  0x{c.QuestFormId:X8}  \"{c.QuestName}\" [{c.QuestEdid}]   {c.Scripts.Count} script(s)  blocks=[{string.Join(",", c.Blocks)}]{(c.HasUnconditional ? "  unconditional" : "")}");

    var boundQuests = byQuest.Where(g => g.Any(x => x.Bound)).ToList();
    var externalQuests = byQuest.Where(g => g.Any(x => x.ExternallyIncremented)).ToList();
    var killCounter = byQuest.Count(g => g.Any(x => x.Bound)); // counter gates are kill/event counts by construction
    Console.WriteLine($"\n=== DELIVERABLE (size of the bucket-C prize) ===");
    Console.WriteLine($"counter-gated completions (count N kills/events, then complete — the Ghost Town Gunfight shape): {boundQuests.Count}");
    Console.WriteLine($"single-kill completions (one OnDeath/OnHit script completes the quest, NO counter): {kill.Count}");
    Console.WriteLine($"  => CLEAN kill-reachable prize (counter + single-kill, the death-registry-recoverable set): {killCounter + kill.Count}");
    Console.WriteLine($"other external completions (GameMode world-poll / OnActivate / scripted DLC — reachability varies): {external.Count - kill.Count - external.Count(c => c.ViaCounter)}");
    Console.WriteLine($"TOTAL player-facing quests completed by an external script (loose upper bound): {external.Count}");
    Console.WriteLine($"unbound counter guards (do-once/timer flags, not real counters): {byQuest.Count - boundQuests.Count}");
}

static void QuestAudit(FalloutSave s, string savePath, string? dataDir, bool list, string? who = null)
{
    // ROADMAP §6 #16 blind-spot audit: our Phase-A interpreter can only START a quest via (a) Start-Game-Enabled
    // startup or (b) a NON-conditional StartQuest/SetStage from another reachable quest's QUST script. Any
    // player-facing quest that is reachable by NEITHER is started only by something we don't read — dialogue
    // (DIAL/INFO) result scripts, activators, or if-guarded/world-gated triggers — i.e. the VCG02 "Back in the
    // Saddle" class. This audits the masters (no save state) to size that blind spot.
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var db = PluginDatabase.ForSave(s, dataDir, GameDataLocator.FindMo2Mods(savePath), withDialogue: true);
    sw.Stop();
    if (db.Count == 0) { Console.WriteLine("Needs the game Data folder."); return; }

    var byEdid = new Dictionary<string, QuestDefinition>(StringComparer.OrdinalIgnoreCase);
    foreach (var q in db.Quests.Values)
        if (!string.IsNullOrEmpty(q.Edid)) byEdid[q.Edid] = q;

    // --who <EDID>: list every QUST stage/GameMode script AND every dialogue INFO whose result script targets
    // <EDID> with ANY quest verb (Start/Set/Stop/Complete/Fail) — i.e. everything that controls that quest.
    if (who is not null)
    {
        var found = false;
        void Show(string src, IEnumerable<QuestScriptEffect> effects)
        {
            foreach (var e in effects.Where(e => string.Equals(e.TargetQuestEdid, who, StringComparison.OrdinalIgnoreCase)))
            {
                found = true;
                var arg = e.Verb is QuestScriptVerb.SetStage or QuestScriptVerb.SetObjectiveDisplayed or QuestScriptVerb.SetObjectiveCompleted ? $" {e.Arg1}" : "";
                Console.WriteLine($"  {src,-34} {(e.Conditional ? "?" : " ")}{e.Verb}{arg}");
            }
        }
        Console.WriteLine($"QUST scripts that control '{who}':");
        foreach (var q in db.Quests.Values)
        {
            foreach (var st in q.Stages)
                Show($"{q.Edid} stage {st.Index}", QuestScript.Parse(st.ScriptText));
            Show($"{q.Edid} GameMode", QuestScript.Parse(q.GameModeScript));
        }
        Console.WriteLine($"\nDialogue INFOs that control '{who}':");
        foreach (var (infoFormId, effects) in db.DialogueInfoEffects)
            Show($"INFO 0x{infoFormId:X8}", effects);
        if (!found) Console.WriteLine("  (nothing references it)");
        return;
    }

    // Per-quest cross-quest start edges (StartQuest / SetStage targeting ANOTHER quest), split by conditional.
    var nonCondTargets = new Dictionary<uint, HashSet<uint>>();
    var condTargetsOf = new HashSet<uint>();   // quests that are the target of at least one conditional start edge
    var anyTargetOf = new HashSet<uint>();      // quests referenced as a start target by any QUST script at all
    foreach (var q in db.Quests.Values)
    {
        var scripts = q.Stages.Select(st => st.ScriptText).Append(q.GameModeScript);
        foreach (var e in scripts.Where(t => t is not null).SelectMany(t => QuestScript.Parse(t)))
        {
            if (e.Verb is not (QuestScriptVerb.StartQuest or QuestScriptVerb.SetStage)) continue;
            if (byEdid.GetValueOrDefault(e.TargetQuestEdid) is not { } tgt || tgt.FormId == q.FormId) continue; // skip self
            anyTargetOf.Add(tgt.FormId);
            if (e.Conditional) condTargetsOf.Add(tgt.FormId);
            else (nonCondTargets.TryGetValue(q.FormId, out var set) ? set : nonCondTargets[q.FormId] = []).Add(tgt.FormId);
        }
    }

    // Reachability fixpoint: seed Start-Game-Enabled, expand over non-conditional cross-quest edges (same shape as
    // QuestPipboy's propagation, but ignoring per-save guards/seeds — so this OVER-estimates what we can reach, making
    // the "external-only" set a conservative LOWER bound on the blind spot).
    var reachable = new HashSet<uint>(db.Quests.Values.Where(q => q.StartGameEnabled).Select(q => q.FormId));
    var work = new Queue<uint>(reachable);
    while (work.Count > 0)
        foreach (var t in nonCondTargets.GetValueOrDefault(work.Dequeue()) ?? [])
            if (reachable.Add(t)) work.Enqueue(t);

    var dlg = db.DialogueStartedQuests;
    var pf = db.Quests.Values.Where(q => q.IsPlayerFacing).ToList();
    var computable = pf.Where(q => reachable.Contains(q.FormId)).ToList();
    var condOnly = pf.Where(q => !reachable.Contains(q.FormId) && condTargetsOf.Contains(q.FormId)).ToList();
    var externalOnly = pf.Where(q => !reachable.Contains(q.FormId) && !anyTargetOf.Contains(q.FormId)).ToList();
    // Phase B: of the QUST-unreachable quests, how many do we now have a dialogue (INFO) start trigger for?
    var atRisk = condOnly.Concat(externalOnly).ToList();
    var dialogueExplained = atRisk.Count(q => dlg.Contains(q.FormId));

    Console.WriteLine($"Player-facing quests in this load order: {pf.Count}  (masters: {db.Quests.Count} total QUST defs)");
    Console.WriteLine($"Masters read with dialogue (INFO) result scripts in {sw.ElapsedMilliseconds:N0} ms; {dlg.Count} quests have a dialogue start/advance trigger.\n");
    Console.WriteLine($"  computable      {computable.Count,4}  — SGE or reached by non-conditional QUST propagation (our interpreter can surface these)");
    Console.WriteLine($"  conditional-only{condOnly.Count,4}  — only ever an IF-guarded cross-quest QUST target; we skip conditional propagation -> AT RISK");
    Console.WriteLine($"  external-only   {externalOnly.Count,4}  — never a QUST-script start target at all (VCG02 class)");
    Console.WriteLine($"\n  Of the {atRisk.Count} at-risk (conditional-only + external-only) quests, {dialogueExplained} now have a known dialogue (INFO) trigger");
    Console.WriteLine($"  (Phase B foundation) — these can be gated on a save signal next; {atRisk.Count - dialogueExplained} remain with no QUST/dialogue trigger (activator/script-only).");
    Console.WriteLine($"\n  Note: 'computable' = CAN be surfaced if the save triggers it; it is not a claim that the quest is in any given Pip-Boy.");

    if (list)
    {
        void Dump(string label, List<QuestDefinition> qs)
        {
            Console.WriteLine($"\n=== {label} ({qs.Count}) ===");
            foreach (var q in qs.OrderBy(q => q.Name, StringComparer.OrdinalIgnoreCase))
                Console.WriteLine($"  0x{q.FormId:X8}  {(q.StartGameEnabled ? "SGE " : "    ")}{(dlg.Contains(q.FormId) ? "[dlg] " : "      ")}{q.Edid,-22} \"{q.Name}\"");
        }
        Dump("external-only (dialogue/activator-started — VCG02 class)", externalOnly);
        Dump("conditional-only (if-guarded cross-quest target)", condOnly);
    }
}

static void Q7Corpus(string dir, string? dataDir)
{
    // ROADMAP §6 #16 validation: across an entire corpus, tally every player-facing formType-7 quest whose change
    // form carries bit30 (the save-anchored seed's input). For each distinct quest report how many saves it appears
    // in, whether it has a completing (QSDT-0x01) stage (= the seed fires + marks it completed), and what that stage
    // propagates (StartQuest/SetStage to other quests = the seed's blast radius). A quest here that is NOT genuinely
    // completed would be a false positive of the bit30 generalization.
    var saves = Directory.EnumerateFiles(dir, "*.fos").OrderBy(x => x).ToList();
    var dbCache = new Dictionary<string, PluginDatabase>();
    var agg = new Dictionary<uint, (string? Name, bool Fires, int Saves, string Prop)>();
    int scanned = 0, skipped = 0;
    foreach (var f in saves)
    {
        FalloutSave s;
        try { s = FalloutSave.Load(f); } catch { skipped++; continue; }
        var key = string.Join("|", s.Plugins);
        if (!dbCache.TryGetValue(key, out var db))
            dbCache[key] = db = PluginDatabase.ForSave(s, dataDir, GameDataLocator.FindMo2Mods(f));
        if (db.Count == 0) { skipped++; continue; }
        scanned++;
        var seen = new HashSet<uint>();
        foreach (var cf in s.EnumerateChangeForms())
        {
            if (cf.FormType != 0x07 || (cf.ChangeFlags & 0xE0000000u) != 0xC0000000u) continue; // VCG01 "completed" pattern
            var q = db.Quest(cf.FormId);
            if (q is not { IsPlayerFacing: true } || !seen.Add(cf.FormId)) continue;
            var cap = q.Stages.Where(x => (x.Flags & 0x01) != 0).Select(x => (int?)x.Index).Max();
            var prop = "";
            if (cap is { } c)
            {
                var stage = q.Stages.FirstOrDefault(x => x.Index == c);
                if (stage is not null)
                    prop = string.Join(", ", QuestScript.Parse(stage.ScriptText)
                        .Where(e => e.Verb is QuestScriptVerb.StartQuest or QuestScriptVerb.SetStage)
                        .Select(e => $"{e.Verb} {e.TargetQuestEdid}{(e.Verb == QuestScriptVerb.SetStage ? $" {e.Arg1}" : "")}"));
            }
            var prev = agg.GetValueOrDefault(cf.FormId);
            agg[cf.FormId] = (q.Name, cap is not null, prev.Saves + 1, prop);
        }
    }
    Console.WriteLine($"Scanned {scanned} saves ({skipped} skipped, {dbCache.Count} distinct load orders) in {dir}\n");
    Console.WriteLine($"{"FormID",-12} {"saves",5} {"seed",5}  name / completing-stage propagation");
    foreach (var (formId, v) in agg.OrderByDescending(kv => kv.Value.Saves))
        Console.WriteLine($"0x{formId:X8}  {v.Saves,5} {(v.Fires ? "FIRES" : "  -  ")}  \"{v.Name}\"{(v.Prop.Length > 0 ? $"  ->[{v.Prop}]" : "")}");
}

static void Q7Scan(FalloutSave s, string savePath, string? dataDir)
{
    // R&D for ROADMAP §6 #16: list every formType-7 change form, flag whether bit30 (SCRIPT, 0x40000000) is set,
    // and resolve which are player-facing quests. Tests the candidate "bit30 = completing-stage ran" seed signal.
    var db = PluginDatabase.ForSave(s, dataDir, GameDataLocator.FindMo2Mods(savePath));
    foreach (var cf in s.EnumerateChangeForms())
    {
        if (cf.FormType != 0x07) continue;
        var q = db.Count > 0 ? db.Quest(cf.FormId) : null;
        var pf = q is { IsPlayerFacing: true };
        var bit30 = (cf.ChangeFlags & 0x40000000u) != 0;
        if (!pf && !bit30) continue;            // only show quests, or any bit30-set formType-7
        Console.WriteLine($"0x{cf.FormId:X8}  flags 0x{cf.ChangeFlags:X8} {(bit30 ? "[bit30]" : "       ")}  " +
            $"len {cf.DataLength,4}  {(pf ? $"QUEST \"{q!.Name}\" SGE={q.StartGameEnabled}" : "(not a player-facing quest)")}");
    }
}

static void CfByFormId(FalloutSave s, uint formId)
{
    // R&D: locate every change form whose RESOLVED FormID matches `formId` (refdump needs the iref, which
    // differs per save because creating forms renumbers the FormID array). Prints header + a hex dump of data.
    var hits = 0;
    foreach (var cf in s.EnumerateChangeForms())
    {
        if (cf.FormId != formId) continue;
        hits++;
        Console.WriteLine($"iref {cf.Iref} -> 0x{cf.FormId:X8}  type 0x{cf.TypeByte:X2} (formType 0x{cf.FormType:X2})  " +
            $"changeFlags 0x{cf.ChangeFlags:X8}  len {cf.DataLength}  @0x{cf.Offset:X}");
        var data = s.ReadAt(cf.DataOffset, cf.DataLength);
        for (var i = 0; i < data.Length; i += 32)
        {
            var slice = data.Skip(i).Take(32).ToArray();
            Console.WriteLine($"  +0x{i:X3}: {string.Join(' ', slice.Select(b => b.ToString("X2")))}");
        }
    }
    Console.WriteLine(hits == 0 ? $"No change form resolves to 0x{formId:X8}." : $"{hits} change form(s).");
}

static void RefDump(FalloutSave s, string savePath, int? iref)
{
    // R&D microscope for ROADMAP §6 ★: decode a reference change form (REFR/ACHR/container) into its
    // changeFlags bits + a 0x7C-delimited field walk, so the inventory list and per-stack extra data can
    // be cracked into a deterministic parse. Default target: the player inventory record (PlayerRef iref+1).
    FnvSaveExplorer.Core.FalloutSave.ChangeFormHeader? target = null;
    if (iref is { } want)
    {
        foreach (var c in s.EnumerateChangeForms())
            if (c.Iref == want) { target = c; break; }
        if (target is null) { Console.WriteLine($"No change form with iref {want}."); return; }
    }
    else
    {
        var playerRef = s.FindIref(0x14);
        if (playerRef < 0) { Console.WriteLine("PlayerRef (0x14) not in FormID array."); return; }
        foreach (var c in s.EnumerateChangeForms())
            if (c.Iref == playerRef + 1) { target = c; break; }
        if (target is null) { Console.WriteLine($"Player inventory record (iref {playerRef + 1}) not found."); return; }
    }
    var cf = target.Value;
    var db = PluginDatabase.ForSave(s, null, GameDataLocator.FindMo2Mods(savePath));

    // The player inventory record (iref = PlayerRef + 1) is an actor (ACHR), so the context-dependent
    // changeFlags bits use the actor meanings; other references stay Unknown (both meanings shown).
    var pr = s.FindIref(0x14);
    var kind = pr >= 0 && cf.Iref == pr + 1 ? ReferenceChangeForm.RefKind.Actor : ReferenceChangeForm.RefKind.Unknown;

    Console.WriteLine($"Reference change form @0x{cf.Offset:X}");
    Console.WriteLine($"  iref {cf.Iref} -> 0x{cf.FormId:X8} {db.Resolve(cf.FormId) ?? ""}");
    Console.WriteLine($"  typeByte 0x{cf.TypeByte:X2} (formType 0x{cf.FormType:X2})  version 0x{cf.Version:X2}  data @0x{cf.DataOffset:X} len {cf.DataLength}");
    Console.WriteLine($"  changeFlags 0x{cf.ChangeFlags:X8} = {ReferenceChangeForm.DescribeFlags(cf.ChangeFlags, kind)}");

    var data = s.ReadAt(cf.DataOffset, cf.DataLength);
    // The deterministic inventory start: skip the MOVE block + the fixed 1160-byte havok/float array
    // (ROADMAP §4i), landing on the reference ExtraDataList; the first valid stack chain after it is the
    // item list. Printed (with the array span) so the anchor can be eyeballed against the field walk and the
    // remaining ExtraDataList RE has its exact bounds.
    var searchStart = ReferenceChangeForm.InventorySearchStart(data, cf.DataOffset, cf.ChangeFlags);
    if ((cf.ChangeFlags & ReferenceChangeForm.ChangeRefrMove) != 0 && ReferenceChangeForm.MoveBlockLength < data.Length
        && data[ReferenceChangeForm.MoveBlockLength] == ReferenceChangeForm.Delimiter)
    {
        var arrayStart = cf.DataOffset + ReferenceChangeForm.MoveBlockLength + 1;
        Console.WriteLine($"  MOVE block @0x{cf.DataOffset:X}..0x{arrayStart:X}  fixed havok array @0x{arrayStart:X}..0x{arrayStart + ReferenceChangeForm.GatedArrayBlockLength:X} ({ReferenceChangeForm.GatedArraySlotCount} slots)");
    }
    Console.WriteLine($"  inventory search start (ExtraDataList, after MOVE + fixed array) @0x{searchStart:X}");
    // Deterministic items start: size the ExtraDataList + read the vsval stack count (ROADMAP §4i). `data` is
    // record-relative, so feed the relative ExtraDataList start and re-absolutise the result.
    if (ReferenceChangeForm.TryInventoryItemsStart(data, searchStart - cf.DataOffset, out var relItems, out var stackCount))
        Console.WriteLine($"  ExtraDataList sized -> first item @0x{cf.DataOffset + relItems:X}  vsval stackCount {stackCount} (deterministic vanilla path)");
    else
        Console.WriteLine("  ExtraDataList not recognised by the fixed vanilla parse -> live decoder falls back to the forward scan");

    // Generalised typed-entry ExtraDataList walk (ROADMAP §4i ◑): bound it by the first item the working
    // decoder located (the §4g fallback is correct on modded saves), then report how far the typed-entry
    // catalog gets — the lens for pinning the modded grammar. Only meaningful for the player inventory record.
    var playerRefIref = s.FindIref(0x14);
    if (playerRefIref >= 0 && cf.Iref == playerRefIref + 1 && s.Inventory?.FirstStackOffset is { } firstItemAbs)
    {
        var walk = ReferenceChangeForm.WalkExtraDataList(data, searchStart - cf.DataOffset, firstItemAbs - cf.DataOffset);
        Console.WriteLine($"\n  typed-entry ExtraDataList walk (bound: first item @0x{firstItemAbs:X}):");
        Console.WriteLine($"    header {(walk.HeaderOk ? "ok" : "MISSING")}  lead 0x{walk.LeadByte:X2}  entries {walk.Entries.Count}");
        foreach (var e in walk.Entries)
            Console.WriteLine($"      @0x{cf.DataOffset + e.Offset:X}  type 0x{e.Type:X2}  len {e.Length}");
        if (walk.FullyExplained)
            Console.WriteLine($"    FULLY EXPLAINED -> vsval {walk.Vsval} @0x{cf.DataOffset + walk.VsvalOffset:X} lands exactly on the first item");
        else if (walk.UnknownType is { } ut)
            Console.WriteLine($"    UNKNOWN type 0x{ut:X2} @0x{cf.DataOffset + walk.StopOffset:X}; {walk.UnexplainedBytes} bytes to the first item unaccounted -> "
                + string.Join(' ', s.ReadAt(cf.DataOffset + walk.StopOffset, Math.Min(32, walk.UnexplainedBytes)).Select(b => b.ToString("X2"))));
        else
            Console.WriteLine($"    NOT EXPLAINED (no unknown type framing); {walk.UnexplainedBytes} bytes to the first item @0x{cf.DataOffset + walk.StopOffset:X}");
    }

    var fields = ReferenceChangeForm.Tokenize(data, cf.DataOffset);
    Console.WriteLine($"\n0x7C field walk ({fields.Count} fields):");

    var zeroRun = 0;
    var zeroRunStart = 0;
    void FlushZeroRun()
    {
        if (zeroRun == 0) return;
        Console.WriteLine($"  @0x{zeroRunStart:X}  [x{zeroRun}] zero u32 fields (00 00 00 00)");
        zeroRun = 0;
    }

    for (var i = 0; i < fields.Count; i++)
    {
        var f = fields[i];
        // Collapse the long runs of zeroed u32 arrays so real structure stands out.
        if (f.Length == 4 && f.AsUInt32 == 0)
        {
            if (zeroRun++ == 0) zeroRunStart = f.Offset;
            continue;
        }
        FlushZeroRun();

        var hex = string.Join(' ', f.Bytes.Take(16).Select(b => b.ToString("X2")));
        if (f.Length > 16) hex += " …";
        var note = "";
        if (f.AsRefId is { } refId)
        {
            // An item entry is a 3-byte ref field immediately followed by a 4-byte count field; the ref is
            // the FormID-array index + 1 (§4g). Flag those, plus any bare iref that resolves.
            var item = refId > 0 ? s.ResolveIref(refId - 1) : 0;
            var nextIsCount = i + 1 < fields.Count && fields[i + 1].Length == 4;
            if (item != 0 && nextIsCount)
                note = $"  <- ITEM  0x{item:X8} {db.Resolve(item) ?? "?"}  x{fields[i + 1].AsUInt32}";
            else if (refId > 0 && s.ResolveIref(refId) is var bare && bare != 0)
                note = $"  refId {refId} -> 0x{bare:X8} {db.Resolve(bare) ?? ""}";
        }
        else if (f.Length == 4)
            note = $"  u32 {f.AsUInt32}  f32 {f.AsSingle:0.###}";
        else if (f.Length == 1)
            note = $"  u8 0x{f.Bytes[0]:X2} ({f.Bytes[0]})";
        else if (f.Length == 0)
            note = "  (empty)";

        Console.WriteLine($"  [{i,4}] @0x{f.Offset:X} len {f.Length,2}: {hex,-32}{note}");
    }
    FlushZeroRun();

    var end = fields.Count > 0 ? fields[^1].Offset + fields[^1].Length : cf.DataOffset;
    Console.WriteLine($"\nlast field ends @0x{end:X}; record data ends @0x{cf.DataOffset + cf.DataLength:X} (delta {cf.DataOffset + cf.DataLength - end}).");
}

static void InvSig(string dir)
{
    // Byte-identical-decode verification aid: for every .fos in `dir`, print a stable signature of the decoded
    // inventory (stack count + an FNV-1a hash over each stack's iref|count|countOffset|condition|equipped).
    // The signature captures the DECODE only (not which path located it), so it stays identical across decoder
    // refactors that don't change the result. One process per folder, no masters → fast. Read-only.
    // Usage: capture before a change (e.g. `git stash`), again after, and diff the two outputs.
    var files = Directory.EnumerateFiles(dir, "*.fos").OrderBy(f => f, StringComparer.Ordinal).ToList();
    foreach (var file in files)
    {
        string sig;
        try
        {
            var s = FalloutSave.Load(file);
            if (s.Inventory is not { } inv) { sig = "no-inventory"; }
            else
            {
                ulong h = 1469598103934665603UL; // FNV-1a 64
                void Mix(long v) { for (var i = 0; i < 8; i++) { h ^= (byte)(v >> (8 * i)); h *= 1099511628211UL; } }
                foreach (var it in inv.Items)
                {
                    Mix(it.Iref); Mix(it.Count); Mix(it.CountValueOffset);
                    Mix(BitConverter.SingleToInt32Bits(it.Condition ?? float.NaN));
                    Mix(it.Equipped ? 1 : 0);
                }
                sig = $"{inv.Items.Count,5} stacks  {h:X16}";
            }
        }
        catch (Exception e) { sig = "LOAD-FAIL: " + e.GetType().Name; }
        Console.WriteLine($"{Path.GetFileName(file)}\t{sig}");
    }
}

static void EdlScan(string dir)
{
    // R&D aggregate for ROADMAP §4i ◑: across every .fos in `dir`, walk the player inventory's
    // ExtraDataList as a GENERAL typed-entry sequence (ReferenceChangeForm.WalkExtraDataList), bounded by
    // the first item the working decoder located (the §4g fallback is correct on modded saves). Aggregates
    // the modded grammar — which entry-type orderings occur, which types are still unsized (ExtraDataList
    // AND per-stack), and whether the typed walk reaches the vsval cleanly. No name resolution (masters)
    // needed, so it stays fast. Read-only: nothing is written.
    var files = Directory.EnumerateFiles(dir, "*.fos").OrderBy(f => f).ToList();
    Console.WriteLine($"Scanning {files.Count} .fos in {dir}\n");

    int scanned = 0, parseFail = 0, noInv = 0, fully = 0, unknownEdl = 0, headerMissing = 0, otherFail = 0;
    int vsvalOver = 0, vsvalUnder = 0; // decoded stack count vs the engine's vsval (over = benign §9 over-read; under = lost items)
    int detStart = 0; // the LIVE decoder located the item-list start deterministically (vs the §4g scan fallback)
    var orderings = new Dictionary<string, int>();
    var edlTypeCounts = new SortedDictionary<byte, int>();
    var unknownEdlCount = new SortedDictionary<byte, int>();
    var unknownEdlEx = new SortedDictionary<byte, List<string>>();
    var perStackCount = new SortedDictionary<byte, int>();
    var perStackEx = new SortedDictionary<byte, List<string>>();
    // RE corpus-alignment sizer (ROADMAP §4i): per unsized per-stack property type, histogram the byte gap
    // from the property's [type][7C] header to the next valid stack start — a CLEAN payload measurement when
    // the unsized property is the block's LAST one (block ends → next stack), noisier otherwise. A fixed-length
    // type spikes at one gap (payload = gap==2 ? 0 : gap-3); a count-prefixed variable type spreads. lastGapHist
    // keeps only the clean last-property cases; allGapHist keeps every case for cross-check.
    var lastGapHist = new SortedDictionary<byte, SortedDictionary<int, int>>();
    var allGapHist = new SortedDictionary<byte, SortedDictionary<int, int>>();

    static void Bump(SortedDictionary<byte, SortedDictionary<int, int>> h, byte type, int gap)
    {
        if (!h.TryGetValue(type, out var inner)) h[type] = inner = new SortedDictionary<int, int>();
        inner[gap] = inner.GetValueOrDefault(gap) + 1;
    }

    static void Example(SortedDictionary<byte, List<string>> ex, byte key, string line)
    {
        if (!ex.TryGetValue(key, out var list)) ex[key] = list = new List<string>();
        if (list.Count < 5) list.Add(line);
    }

    foreach (var file in files)
    {
        FalloutSave s;
        try { s = FalloutSave.Load(file); }
        catch { parseFail++; continue; }
        scanned++;
        var name = Path.GetFileName(file);

        var playerRef = s.FindIref(0x14);
        FnvSaveExplorer.Core.FalloutSave.ChangeFormHeader? rec = null;
        if (playerRef >= 0)
            foreach (var c in s.EnumerateChangeForms())
                if (c.Iref == playerRef + 1) { rec = c; break; }

        if (rec is null || s.Inventory?.FirstStackOffset is not { } firstAbs) { noInv++; continue; }
        if (s.Inventory!.DeterministicStart) detStart++;
        var cf = rec.Value;
        var data = s.ReadAt(cf.DataOffset, cf.DataLength);
        var searchStart = ReferenceChangeForm.InventorySearchStart(data, cf.DataOffset, cf.ChangeFlags);
        var walk = ReferenceChangeForm.WalkExtraDataList(data, searchStart - cf.DataOffset, firstAbs - cf.DataOffset);

        foreach (var e in walk.Entries)
            edlTypeCounts[e.Type] = edlTypeCounts.GetValueOrDefault(e.Type) + 1;
        var order = string.Join(",", walk.Entries.Select(e => e.Type.ToString("X2")));
        orderings[order] = orderings.GetValueOrDefault(order) + 1;

        if (walk.FullyExplained)
        {
            fully++;
            var decoded = s.Inventory!.Items.Count;
            if (decoded > walk.Vsval) vsvalOver++;
            else if (decoded < walk.Vsval) vsvalUnder++;
        }
        else if (walk.UnknownType is { } ut)
        {
            unknownEdl++;
            unknownEdlCount[ut] = unknownEdlCount.GetValueOrDefault(ut) + 1;
            var win = s.ReadAt(cf.DataOffset + walk.StopOffset, Math.Min(24, Math.Max(0, walk.UnexplainedBytes)));
            Example(unknownEdlEx, ut, $"{name} +{walk.UnexplainedBytes}B: {string.Join(' ', win.Select(b => b.ToString("X2")))}");
        }
        else if (!walk.HeaderOk) headerMissing++; // search start mis-landed: variable havok-array size (§4i)
        else otherFail++;                          // header ok but no entry/vsval framing (lead or post-entry shape)

        foreach (var it in s.Inventory!.Items)
            if (it.UnknownExtraType is { } pt && it.UnknownExtraOffset is { } off)
            {
                perStackCount[pt] = perStackCount.GetValueOrDefault(pt) + 1;

                // Measure the gap from the unsized property's [type][7C] header to the next valid stack start,
                // and re-walk the block (starting 5 bytes past the stack's count, i.e. CountValueOffset + 5) to
                // learn whether this unsized property is the block's LAST one (propIndex == propCount-1).
                var win = s.ReadAt(off, 64); // local coords: win[0]=type, win[1]=0x7C
                var gap = -1;
                for (var q = 2; q + 9 <= win.Length; q++)
                    if (ReferenceChangeForm.LooksLikeStackStart(win, q)) { gap = q; break; }

                var isLast = false;
                var blk = s.ReadAt(it.CountValueOffset + 5, 256); // the per-stack extra block: [a][7C][b][7C] props…
                if (blk.Length >= 4 && blk[0] == 0x04 && blk[1] == ReferenceChangeForm.Delimiter
                    && blk[3] == ReferenceChangeForm.Delimiter)
                {
                    var propCount = blk[2] / 4;
                    var blockStart = it.CountValueOffset + 5;
                    var cur = 4; var idx = 0; var ok = true;
                    while (blockStart + cur < off && idx < propCount)
                    {
                        if (cur + 2 > blk.Length || blk[cur + 1] != ReferenceChangeForm.Delimiter) { ok = false; break; }
                        var plen = ReferenceChangeForm.FixedPropertyPayload(blk[cur]);
                        if (plen < 0) { ok = false; break; }
                        cur += 2 + (plen > 0 ? plen + 1 : 0);
                        idx++;
                    }
                    if (ok && blockStart + cur == off) isLast = idx == propCount - 1;
                }

                if (gap >= 0)
                {
                    Bump(allGapHist, pt, gap);
                    if (isLast) Bump(lastGapHist, pt, gap);
                }
                var raw = string.Join(' ', win.Take(Math.Min(24, win.Length)).Select(b => b.ToString("X2")));
                Example(perStackEx, pt, $"{name} 0x{it.FormId:X8} gap={(gap < 0 ? "?" : gap.ToString())} last={isLast}: {raw}");
            }
    }

    Console.WriteLine($"parsed {scanned} (failed {parseFail}); no inventory located: {noInv}");
    Console.WriteLine($"LIVE decoder: deterministic list start {detStart} / {scanned - noInv} (rest via the §4g scan fallback)");
    Console.WriteLine($"ExtraDataList typed-walk: fully explained {fully}, first-unknown-type {unknownEdl}, "
        + $"header mis-landed (variable havok array) {headerMissing}, other {otherFail}");
    Console.WriteLine($"of the fully-explained, decoded stacks vs engine vsval: over-read {vsvalOver}, under-read {vsvalUnder} (rest exact)\n");

    Console.WriteLine("ExtraDataList entry types seen (type x total occurrences across saves):");
    foreach (var (t, c) in edlTypeCounts) Console.WriteLine($"  0x{t:X2}  {c}");

    Console.WriteLine("\nExtraDataList entry-type orderings (sequence x saves):");
    foreach (var (o, c) in orderings.OrderByDescending(kv => kv.Value))
        Console.WriteLine($"  [{c,4}]  {(o.Length == 0 ? "(none)" : o)}");

    if (unknownEdlCount.Count > 0)
    {
        Console.WriteLine("\nUNKNOWN ExtraDataList types (first hit), with raw context to the first item:");
        foreach (var (t, c) in unknownEdlCount)
        {
            Console.WriteLine($"  0x{t:X2}  x{c}");
            foreach (var e in unknownEdlEx[t]) Console.WriteLine($"      {e}");
        }
    }

    if (perStackCount.Count > 0)
    {
        Console.WriteLine("\nUNSIZED per-stack extra-data types (0x25/0x16/0x21 already sized, so excluded):");
        foreach (var (t, c) in perStackCount)
        {
            Console.WriteLine($"  0x{t:X2}  x{c}");
            // Gap = bytes from the [type][7C] header to the next stack. When this property is the block's LAST,
            // gap is the property's own size; payload = gap==2 ? 0 : gap-3 (header [type][7C] + payload + [7C]).
            if (lastGapHist.TryGetValue(t, out var last))
                Console.WriteLine($"      last-prop gap→size histogram (gap×count): {FormatGapHist(last)}");
            if (allGapHist.TryGetValue(t, out var all))
                Console.WriteLine($"      all-cases  gap     histogram (gap×count): {FormatGapHist(all)}");
            foreach (var e in perStackEx[t]) Console.WriteLine($"      {e}");
        }
    }

    static string FormatGapHist(SortedDictionary<int, int> h) =>
        string.Join("  ", h.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key}×{kv.Value}"));
}

static void IrefScan(FalloutSave s, uint offset, int len)
{
    // R&D: walk a byte range and report every position where the next 3 bytes form a valid iref
    // (big-endian, index into the FormID array resolving to a non-zero FormID whose mod-index high
    // byte is a loaded plugin) plus the following 4 bytes read as a candidate stack count. Inventory
    // shows up as a dense run of such pairs; random data rarely resolves under both filters.
    var bytes = s.ReadAt((int)offset, len);
    var pluginCount = s.Plugins.Count;
    var hits = 0;
    for (var i = 0; i + 7 <= bytes.Length; i++)
    {
        var iref = (bytes[i] << 16) | (bytes[i + 1] << 8) | bytes[i + 2];
        var formId = s.ResolveIref(iref);
        if (formId == 0)
            continue;
        var modIndex = formId >> 24;
        if (modIndex >= (uint)pluginCount && modIndex != 0xFF) // 0xFF = save-created (in-game) form
            continue;
        var count = BitConverter.ToUInt32(bytes, i + 3);
        var countHex = string.Join(' ', bytes.Skip(i + 3).Take(4).Select(x => x.ToString("X2")));
        Console.WriteLine($"  @0x{offset + i:X}  iref {iref,6} -> 0x{formId:X8} (mod {modIndex:X2})  next4: {countHex} (={count})");
        if (++hits >= 200)
        {
            Console.WriteLine("  ... (truncated at 200)");
            break;
        }
    }
    Console.WriteLine($"{hits} candidate iref site(s) in 0x{offset:X}..0x{offset + len:X}");
}

static uint ParseOffset(string s) =>
    s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? Convert.ToUInt32(s[2..], 16)
        : uint.Parse(s);

static void Probe(FalloutSave s, int count)
{
    Console.WriteLine($"BodyOffset = 0x{s.BodyOffset:X} ({s.BodyOffset:N0}),  FileLength = {s.FileLength:N0}");
    Console.WriteLine("File Location Table:");
    var values = s.PeekBodyUInt32(count);
    for (var i = 0; i < values.Length; i++)
    {
        var v = values[i];
        if (s.IsBodyOffset(v))
        {
            var peek = s.ReadAt((int)v, 12);
            var hex = string.Join(' ', peek.Select(b => b.ToString("X2")));
            Console.WriteLine($"  [{i,2}] {v,12:N0}  0x{v:X8}  OFFSET -> {hex}");
        }
        else
        {
            Console.WriteLine($"  [{i,2}] {v,12:N0}  0x{v:X8}  (count/other)");
        }
    }
}

static void Hex(FalloutSave s, uint offset, int len)
{
    var bytes = s.ReadAt((int)offset, len);
    for (var i = 0; i < bytes.Length; i += 16)
    {
        var hex = ""; var asc = "";
        for (var j = 0; j < 16 && i + j < bytes.Length; j++)
        {
            var b = bytes[i + j];
            hex += $"{b:X2} ";
            asc += b is >= 32 and < 127 ? (char)b : '.';
        }
        Console.WriteLine($"{offset + i:X6}  {hex,-48} {asc}");
    }
}

static int SetStat(string inPath, string outPath, int index, uint value)
{
    var before = File.ReadAllBytes(inPath);
    var save = FalloutSave.Parse(before);
    var old = save.MiscStats?.Stats[index].Value;
    if (!save.TrySetMiscStat(index, value))
    {
        Console.WriteLine($"FAIL: could not set stat {index}.");
        return 4;
    }
    save.Save(outPath, backup: false);

    var after = File.ReadAllBytes(outPath);
    var reloaded = FalloutSave.Parse(after);
    var diff = 0;
    for (var i = 0; i < Math.Min(before.Length, after.Length); i++)
        if (before[i] != after[i]) diff++;

    var now = reloaded.MiscStats?.Stats[index].Value;
    Console.WriteLine($"Stat[{index}] {old} -> {now};  size {before.Length:N0} -> {after.Length:N0};  bytes changed: {diff}");
    Console.WriteLine(now == value && before.Length == after.Length
        ? "OK: stat edit applied, file size unchanged, still parses."
        : "FAIL: stat edit did not verify.");
    return now == value && before.Length == after.Length ? 0 : 4;
}

static int SetLevel(string inPath, string outPath, uint level)
{
    var before = File.ReadAllBytes(inPath);
    var save = FalloutSave.Parse(before);
    var oldLevel = save.PlayerLevel;
    save.SetPlayerLevel(level);
    save.Save(outPath, backup: false);

    // Re-parse the written file and measure exactly what changed.
    var after = File.ReadAllBytes(outPath);
    var reloaded = FalloutSave.Parse(after);
    var diff = 0;
    for (var i = 0; i < Math.Min(before.Length, after.Length); i++)
        if (before[i] != after[i]) diff++;

    Console.WriteLine($"Level {oldLevel} -> {reloaded.PlayerLevel}");
    Console.WriteLine($"File size: {before.Length:N0} -> {after.Length:N0} (delta {after.Length - before.Length})");
    Console.WriteLine($"Bytes changed: {diff}");
    Console.WriteLine(reloaded.PlayerLevel == level && before.Length == after.Length
        ? "OK: edit applied, file size unchanged, still parses."
        : "FAIL: edit did not verify.");
    return reloaded.PlayerLevel == level && before.Length == after.Length ? 0 : 4;
}
