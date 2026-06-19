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
          fnvsave special <save.fos>          Show the player's SPECIAL attributes
          fnvsave setspecial <in.fos> <out.fos> S P E C I A L   Edit SPECIAL (writes a new file)
          fnvsave skills <save.fos>           Show stored skill modifications (actor-value entries)
          fnvsave setskill <in.fos> <out.fos> <skill> <value>  Edit a stored skill (writes a new file)
          fnvsave diff <a.fos> <b.fos>        Byte-diff two saves (best on controlled same-size pairs)
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
        case "special":
            Special(FalloutSave.Load(path));
            break;
        case "diff":
            Diff(path, args[2]);
            break;
        case "setspecial":
            return SetSpecial(path, args[2], args[3..10]);
        case "skills":
            Skills(FalloutSave.Load(path));
            break;
        case "setskill":
            return SetSkill(path, args[2], args[3], float.Parse(args[4]));
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
    Console.WriteLine($"FormID array: {array.Count:N0} entries");
    for (var i = 0; i < Math.Min(n, array.Count); i++)
        Console.WriteLine($"  iref [{i,5}] -> 0x{array[i]:X8}");
}

static void Diff(string pathA, string pathB)
{
    var a = File.ReadAllBytes(pathA);
    var b = File.ReadAllBytes(pathB);
    var save = FalloutSave.Parse(a);
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

    Console.WriteLine($"{runs.Count:N0} differing run(s):");
    foreach (var r in runs.Take(80))
    {
        var sa = string.Join(' ', a.Skip(r.Start).Take(Math.Min(r.Len, 10)).Select(x => x.ToString("X2")));
        var sb = string.Join(' ', b.Skip(r.Start).Take(Math.Min(r.Len, 10)).Select(x => x.ToString("X2")));
        Console.WriteLine($"  @0x{r.Start:X} ({r.Len,4} B) [{Region(r.Start)}]  A: {sa}  B: {sb}{Anchors(r.Start)}");
    }
    if (runs.Count > 80)
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
