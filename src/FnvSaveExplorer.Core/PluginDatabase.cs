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

    /// <summary>The game <c>Data</c> folder the base/DLC names came from, or <c>null</c> when none was found.</summary>
    public string? DataFolder { get; }

    /// <summary>The MO2 <c>mods\</c> folder used for mod plugins, or <c>null</c> when none was used.</summary>
    public string? ModsFolder { get; }

    /// <summary>The load-order plugins that were found and successfully parsed.</summary>
    public IReadOnlyList<string> ResolvedPlugins { get; }

    /// <summary>FormID → display name, in the save's FormID space.</summary>
    public IReadOnlyDictionary<uint, string> Names => _names;

    public int Count => _names.Count;

    private PluginDatabase(Dictionary<uint, string> names, string? dataFolder, string? modsFolder, IReadOnlyList<string> resolved)
    {
        _names = names;
        DataFolder = dataFolder;
        ModsFolder = modsFolder;
        ResolvedPlugins = resolved;
    }

    /// <summary>An empty database; every <see cref="Resolve"/> returns <c>null</c>.</summary>
    public static readonly PluginDatabase Empty = new([], null, null, []);

    /// <summary>
    /// Builds a database for <paramref name="save"/>, auto-detecting the game <c>Data</c> folder (or using an
    /// override) and, when given, an MO2 <paramref name="modsFolder"/> for mod plugins.
    /// </summary>
    public static PluginDatabase ForSave(FalloutSave save, string? dataFolderOverride = null, string? modsFolder = null)
    {
        var folder = GameDataLocator.FindDataFolder(dataFolderOverride);
        var paths = CollectPlugins(folder, modsFolder);
        return paths.Count == 0 ? Empty : Build(save.Plugins, paths, folder, modsFolder);
    }

    /// <summary>Builds a database from a load order and an explicit game <c>Data</c> folder.</summary>
    public static PluginDatabase Build(IReadOnlyList<string> loadOrder, string dataFolder)
        => Build(loadOrder, CollectPlugins(dataFolder, null), dataFolder, null);

    /// <summary>Builds a database from a load order and a plugin-name → file-path map (used in tests).</summary>
    public static PluginDatabase Build(IReadOnlyList<string> loadOrder, IReadOnlyDictionary<string, string> pluginPaths)
        => Build(loadOrder, pluginPaths, null, null);

    private static PluginDatabase Build(
        IReadOnlyList<string> loadOrder, IReadOnlyDictionary<string, string> pluginPaths, string? dataFolder, string? modsFolder)
    {
        var names = new Dictionary<uint, string>();
        var resolved = new List<string>();

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
                plugin = TesPlugin.Load(file);
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

            foreach (var (localFormId, name) in plugin.Forms)
            {
                var saveHigh = remap[(int)(localFormId >> 24)];
                if (saveHigh < 0)
                    continue; // references a master that isn't in this save's load order
                var saveFormId = ((uint)saveHigh << 24) | (localFormId & 0x00FFFFFF);
                names[saveFormId] = name; // later (load-order) plugin overrides win
            }

            resolved.Add(loadOrder[i]);
        }

        return new PluginDatabase(names, dataFolder, modsFolder, resolved);
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
}
