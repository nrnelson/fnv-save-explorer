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
/// <para>Building is opt-in: callers auto-detect or pass the Data folder (see
/// <see cref="GameDataLocator"/>). With no masters available the database is simply empty and FormIDs keep
/// displaying as hex.</para>
/// </summary>
public sealed class PluginDatabase
{
    private readonly Dictionary<uint, string> _names;

    /// <summary>The Data folder the names came from, or <c>null</c> when none was found.</summary>
    public string? DataFolder { get; }

    /// <summary>The load-order plugins that were found on disk and successfully parsed.</summary>
    public IReadOnlyList<string> ResolvedPlugins { get; }

    /// <summary>FormID → display name, in the save's FormID space.</summary>
    public IReadOnlyDictionary<uint, string> Names => _names;

    public int Count => _names.Count;

    private PluginDatabase(Dictionary<uint, string> names, string? dataFolder, IReadOnlyList<string> resolved)
    {
        _names = names;
        DataFolder = dataFolder;
        ResolvedPlugins = resolved;
    }

    /// <summary>An empty database (no Data folder); every <see cref="Resolve"/> returns <c>null</c>.</summary>
    public static readonly PluginDatabase Empty = new([], null, []);

    /// <summary>Builds a database for <paramref name="save"/>, auto-detecting the Data folder (or using an override).</summary>
    public static PluginDatabase ForSave(FalloutSave save, string? dataFolderOverride = null)
    {
        var folder = GameDataLocator.FindDataFolder(dataFolderOverride);
        return folder is null ? Empty : Build(save.Plugins, folder);
    }

    /// <summary>Builds a database from a load order and an explicit Data folder.</summary>
    public static PluginDatabase Build(IReadOnlyList<string> loadOrder, string dataFolder)
    {
        var names = new Dictionary<uint, string>();
        var resolved = new List<string>();

        // Case-insensitive load-order index, for mapping a plugin's masters back to save indices.
        var indexOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < loadOrder.Count; i++)
            indexOf[loadOrder[i]] = i;

        for (var i = 0; i < loadOrder.Count; i++)
        {
            var file = Path.Combine(dataFolder, loadOrder[i]);
            if (!File.Exists(file))
                continue;

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

        return new PluginDatabase(names, dataFolder, resolved);
    }

    /// <summary>The display name for a save FormID, <c>"(created)"</c> for runtime forms, or <c>null</c> if unknown.</summary>
    public string? Resolve(uint formId)
    {
        if ((formId >> 24) == 0xFF)
            return "(created)";
        return _names.TryGetValue(formId, out var name) ? name : null;
    }
}
