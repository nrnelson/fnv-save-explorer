// Agent-tooling benchmark: measures the cost of FnvSaveExplorer's FormID->name resolution path
// (PluginDatabase.Build / TesPlugin.Load), which today re-parses every plugin master from scratch
// on EVERY CLI invocation that needs names (inventory, names, notes, perks, addictions, ...), with
// no cross-invocation cache. This models an agent/user running such a command repeatedly against
// the same save+Data folder in one working session - the realistic "slow/hanging tool" pattern.
//
// Deterministic: no network, fixed record counts/content, fixed temp paths, no RNG.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using FnvSaveExplorer.Core;

const int RecordsPerType = 3000;
const int Iterations = 15;
const int FillerPluginCount = 9;   // extra base/DLC-style masters directly in the Data folder
const int FillerRecordCount = 50;
const int ModCount = 40;           // MO2-style mods\<Mod>\<Mod>.esp, one plugin per mod folder
const int ModRecordCount = 20;
string[] RecordTypes = ["WEAP", "ARMO", "ALCH", "AMMO", "MISC", "BOOK"];
const string MainPluginFileName = "FalloutNV.esm"; // must match GameDataLocator.MasterEsm
var expectedNames = RecordsPerType * RecordTypes.Length + FillerPluginCount * FillerRecordCount + ModCount * ModRecordCount;

var root = Path.Combine(Path.GetTempPath(), "fnv-agent-tooling-bench");
if (Directory.Exists(root))
    Directory.Delete(root, recursive: true);
var dataDir = Path.Combine(root, "Data");
var modsDir = Path.Combine(root, "mods");
Directory.CreateDirectory(dataDir);
Directory.CreateDirectory(modsDir);

try
{
    var loadOrder = new List<string> { MainPluginFileName };
    File.WriteAllBytes(Path.Combine(dataDir, MainPluginFileName), BuildPlugin(RecordTypes, RecordsPerType));

    // Realistic Data folder: FalloutNV.esm + several more base/DLC-style masters sitting alongside it -
    // CollectPlugins enumerates every file in dataDir regardless of what's actually in the load order.
    for (var i = 0; i < FillerPluginCount; i++)
    {
        var name = $"Filler{i:D2}.esm";
        File.WriteAllBytes(Path.Combine(dataDir, name), BuildPlugin(["MISC"], FillerRecordCount));
        loadOrder.Add(name);
    }

    // Realistic MO2 layout: each active mod gets its own folder under mods\, merged into Data only at
    // launch via MO2's virtual filesystem - CollectPlugins has to walk every mod folder to find them.
    for (var i = 0; i < ModCount; i++)
    {
        var modName = $"Mod{i:D2}";
        var modDir = Path.Combine(modsDir, modName);
        Directory.CreateDirectory(modDir);
        var pluginName = $"{modName}.esp";
        File.WriteAllBytes(Path.Combine(modDir, pluginName), BuildPlugin(["MISC"], ModRecordCount));
        loadOrder.Add(pluginName);
    }

    var saveBytes = BuildSyntheticSave(loadOrder);
    var save = FalloutSave.Parse(saveBytes);

    // Warm up the JIT/file-system caches once, outside the measured loop (mirrors a real agent
    // session: the FIRST invocation is never free regardless of caching strategy; what should be
    // optimized away is invocations 2..N).
    var warmup = PluginDatabase.ForSave(save, dataFolderOverride: dataDir, modsFolder: modsDir);
    if (warmup.Count != expectedNames)
    {
        Console.Error.WriteLine($"FAIL: warmup resolved {warmup.Count} names, expected {expectedNames}");
        return 1;
    }

    var sw = Stopwatch.StartNew();
    var lastCount = 0;
    for (var i = 0; i < Iterations; i++)
    {
        var db = PluginDatabase.ForSave(save, dataFolderOverride: dataDir, modsFolder: modsDir);
        lastCount = db.Count;
    }
    sw.Stop();

    if (lastCount != expectedNames)
    {
        Console.Error.WriteLine($"FAIL: resolved {lastCount} names, expected {expectedNames}");
        return 1;
    }

    var totalMs = sw.Elapsed.TotalMilliseconds;
    var avgMs = totalMs / Iterations;

    Console.WriteLine($"METRIC redundant_reparse_ms={totalMs:F2}");
    Console.WriteLine($"METRIC avg_reparse_ms={avgMs:F2}");
    Console.WriteLine($"METRIC iterations={Iterations}");
    Console.WriteLine($"METRIC names_resolved={lastCount}");
    return 0;
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
}

// ---- Minimal synthetic TES4 plugin builder (subset of tests/FnvSaveExplorer.Tests/PluginDatabaseTests.cs
// EsmBuilder, duplicated here so this benchmark has zero dependency on the test assembly). ----

static byte[] BuildPlugin(IReadOnlyList<string> recordTypes, int recordsPerType)
{
    var b = new List<byte>();
    b.AddRange(Header());
    uint formId = 1;
    foreach (var type in recordTypes)
    {
        var records = new List<byte[]>();
        for (var i = 0; i < recordsPerType; i++)
        {
            records.Add(Record(type, formId++, $"TEST_{type}_{i:D5}", $"Test {type} Item {i}"));
        }
        b.AddRange(Group(type, records));
    }
    return [.. b];
}

static byte[] Header()
{
    var fields = new List<byte>();
    var hedr = new byte[12];
    BinaryPrimitives.WriteSingleLittleEndian(hedr.AsSpan(0, 4), 1.34f);
    fields.AddRange(Sub("HEDR", hedr));
    fields.AddRange(Sub("CNAM", ZStr("bench")));
    // No MAST records: single master-less plugin, form IDs resolve directly into its own save index.
    return RecordHeader("TES4", flags: 0, formId: 0, [.. fields]);
}

static byte[] Record(string type, uint formId, string edid, string full)
{
    var fields = new List<byte>();
    fields.AddRange(Sub("EDID", ZStr(edid)));
    fields.AddRange(Sub("FULL", ZStr(full)));
    return RecordHeader(type, flags: 0, formId, [.. fields]);
}

static byte[] RecordHeader(string type, uint flags, uint formId, byte[] data)
{
    var b = new List<byte>();
    b.AddRange(Encoding.ASCII.GetBytes(type));
    b.AddRange(U32((uint)data.Length));
    b.AddRange(U32(flags));
    b.AddRange(U32(formId));
    b.AddRange(new byte[8]);
    b.AddRange(data);
    return [.. b];
}

static byte[] Group(string label, IEnumerable<byte[]> records)
{
    var content = new List<byte>();
    foreach (var r in records) content.AddRange(r);
    var b = new List<byte>();
    b.AddRange(Encoding.ASCII.GetBytes("GRUP"));
    b.AddRange(U32((uint)(24 + content.Count)));
    b.AddRange(Encoding.ASCII.GetBytes(label));
    b.AddRange(U32(0));
    b.AddRange(new byte[8]);
    b.AddRange(content);
    return [.. b];
}

static byte[] Sub(string type, byte[] data)
{
    var b = new List<byte>();
    b.AddRange(Encoding.ASCII.GetBytes(type));
    var sz = new byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(sz, (ushort)data.Length);
    b.AddRange(sz);
    b.AddRange(data);
    return [.. b];
}

static byte[] ZStr(string s)
{
    var d = Encoding.Latin1.GetBytes(s);
    var r = new byte[d.Length + 1];
    Array.Copy(d, r, d.Length);
    return r;
}

static byte[] U32(uint v)
{
    var t = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(t, v);
    return t;
}

// ---- Minimal synthetic .fos save builder (subset of tests/FnvSaveExplorer.Tests/FalloutSaveTests.cs
// SyntheticSave, parametrized with an explicit plugin list). ----

static byte[] BuildSyntheticSave(IReadOnlyList<string> plugins)
{
    var b = new List<byte>();
    void Str(string s) => b.AddRange(Encoding.Latin1.GetBytes(s));
    void Delim() => b.Add(0x7C);
    void U16(ushort v) { var t = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(t, v); b.AddRange(t); }
    void U32f(uint v) => b.AddRange(U32(v));
    void StringField(string s) { U16((ushort)s.Length); Delim(); Str(s); Delim(); }

    Str("FO3SAVEGAME");
    var headerSizeAt = b.Count;
    U32f(0);
    U32f(0x30);
    Delim();
    Str("ENGLISH");
    Delim();
    U32f(4); Delim();
    U32f(2); Delim();
    U32f(7); Delim();
    StringField("Bench");
    StringField("Bench");
    U32f(3); Delim();
    StringField("Vault1");
    StringField("000.01.02");

    var screenshotStart = b.Count;
    for (var i = 0; i < 4 * 2 * 3; i++) b.Add((byte)i);

    b.Add(0x15);
    U32f(0);
    b.Add((byte)plugins.Count);
    Delim();
    foreach (var p in plugins) StringField(p);

    for (var i = 0; i < 32; i++) b.Add((byte)(0xF0 + (i & 0x0F)));

    var data = b.ToArray();
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(headerSizeAt, 4), (uint)(screenshotStart - 15));
    return data;
}
