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
          fnvsave setcount <in.fos> <out.fos> <formId> <count>  Edit a stack count (writes a new file)
          fnvsave setcondition <in.fos> <out.fos> <formId> <value>  Edit a stack's condition/health (new file)
          fnvsave diff <a.fos> <b.fos> [body|cf]   Byte-diff two saves (best on controlled pairs); 'body'
                                              hides header/screenshot churn, 'cf' restricts to change forms
                                              and names the containing record for each differing run
          fnvsave walk <save.fos>             Walk all change-form records (validates the header format; R&D)
          fnvsave refdump <save.fos> [iref]  Decode a reference change form (default: the player inventory
                                              record): changeFlags bits + 0x7C-field walk (R&D for §6 inventory)
        """);
    return 1;
}

var command = args[0];
var path = args[1];

if (!File.Exists(path))
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
        case "globals":
            Globals(FalloutSave.Load(path));
            break;
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
        case "refdump":
            RefDump(FalloutSave.Load(path), path, args.Length > 2 ? (int)ParseOffset(args[2]) : (int?)null);
            break;
        case "idiff":
            IDiff(path, args[2]);
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
        case "setcount":
            return SetCount(path, args[2], ParseOffset(args[3]), uint.Parse(args[4]));
        case "setcondition":
            return SetCondition(path, args[2], ParseOffset(args[3]), float.Parse(args[4]));
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
            Console.WriteLine($"  [{st.Index,2}] = {st.Value,12:N0}   (edit offset 0x{st.ValueOffset:X})");
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

static void IDiff(string pathA, string pathB)
{
    // Insertion-aware change-form diff. Adding/removing an item shifts absolute offsets (a new change
    // form + a new FormID-array entry), so a positional byte diff is useless. Instead we walk both saves'
    // change forms and align them by record identity: records match until an insertion, after which B is
    // offset by one. This surfaces (1) the inserted/removed record and (2) the record whose DATA changed
    // in place — for a dropped stacked item that second record holds the inventory count.
    var sa = FalloutSave.Parse(File.ReadAllBytes(pathA));
    var sb = FalloutSave.Parse(File.ReadAllBytes(pathB));
    var recsA = sa.EnumerateChangeForms().ToArray();
    var recsB = sb.EnumerateChangeForms().ToArray();
    Console.WriteLine($"A: {recsA.Length:N0} change forms   B: {recsB.Length:N0} change forms   (delta {recsB.Length - recsA.Length:+#;-#;0})");

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
                    Console.WriteLine($"\n~ DATA CHANGED  iref {ca.Iref} -> 0x{ca.FormId:X8}  type 0x{ca.TypeByte:X2}  len {ca.DataLength}  ({changes.Count} byte(s))");
                    PrintDataChanges(sa, ca.DataOffset, sb, cb.DataOffset, changes);
                }
            }
            else
            {
                Console.WriteLine($"\n~ LEN CHANGED   iref {ca.Iref} -> 0x{ca.FormId:X8}  type 0x{ca.TypeByte:X2}  {ca.DataLength} -> {cb.DataLength}");
            }
            ia++; ib++;
        }
        else if (ib + 1 < recsB.Length && recsB[ib + 1].FormId == ca.FormId)
        {
            Console.WriteLine($"\n+ INSERTED in B  iref {cb.Iref} -> 0x{cb.FormId:X8}  type 0x{cb.TypeByte:X2}  flags 0x{cb.ChangeFlags:X8}  len {cb.DataLength}  @0x{cb.Offset:X}");
            DumpRecord(sb, cb);
            ib++;
        }
        else if (ia + 1 < recsA.Length && recsA[ia + 1].FormId == cb.FormId)
        {
            Console.WriteLine($"\n- REMOVED from B iref {ca.Iref} -> 0x{ca.FormId:X8}  type 0x{ca.TypeByte:X2}  len {ca.DataLength}  @0x{ca.Offset:X}");
            ia++;
        }
        else { ia++; ib++; } // unaligned single mismatch — skip both
    }
    Console.WriteLine($"\n{dataChanges} record(s) changed data in place.");
}

static void PrintDataChanges(FalloutSave sa, int aData, FalloutSave sb, int bData, List<int> changes)
{
    // Coalesce adjacent changed offsets into runs and print A/B bytes side by side with the data-relative offset.
    var i = 0;
    while (i < changes.Count)
    {
        var startK = changes[i];
        var j = i;
        while (j + 1 < changes.Count && changes[j + 1] == changes[j] + 1) j++;
        var runLen = changes[j] - startK + 1;
        var av = string.Join(' ', sa.ReadAt(aData + startK, Math.Min(runLen, 12)).Select(x => x.ToString("X2")));
        var bv = string.Join(' ', sb.ReadAt(bData + startK, Math.Min(runLen, 12)).Select(x => x.ToString("X2")));
        Console.WriteLine($"    data+0x{startK:X} ({runLen} B)  A: {av}  B: {bv}");
        i = j + 1;
    }
}

static void DumpRecord(FalloutSave s, FalloutSave.ChangeFormHeader cf)
{
    var bytes = s.ReadAt(cf.DataOffset, Math.Min(cf.DataLength, 64));
    var hex = string.Join(' ', bytes.Select(x => x.ToString("X2")));
    Console.WriteLine($"    data: {hex}{(cf.DataLength > 64 ? " …" : "")}");
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

    Console.WriteLine($"Reference change form @0x{cf.Offset:X}");
    Console.WriteLine($"  iref {cf.Iref} -> 0x{cf.FormId:X8} {db.Resolve(cf.FormId) ?? ""}");
    Console.WriteLine($"  typeByte 0x{cf.TypeByte:X2} (formType 0x{cf.FormType:X2})  version 0x{cf.Version:X2}  data @0x{cf.DataOffset:X} len {cf.DataLength}");
    Console.WriteLine($"  changeFlags 0x{cf.ChangeFlags:X8} = {ReferenceChangeForm.DescribeFlags(cf.ChangeFlags)}");

    var data = s.ReadAt(cf.DataOffset, cf.DataLength);
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
