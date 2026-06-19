namespace FnvSaveExplorer.Core;

/// <summary>
/// Locates the Fallout: New Vegas <c>Data</c> folder (where the ESM/ESP masters live) so FormIDs can be
/// resolved to display names. It tries an explicit override first, then the known local install path and a
/// few common Steam/GOG/Bethesda locations, returning the first folder that exists and contains
/// <see cref="MasterEsm"/>. Returns <c>null</c> when none is found, so callers gracefully fall back to
/// showing raw-hex FormIDs.
/// </summary>
public static class GameDataLocator
{
    /// <summary>The base-game master; its presence marks a valid Data folder.</summary>
    public const string MasterEsm = "FalloutNV.esm";

    public static string? FindDataFolder(string? overridePath = null)
    {
        foreach (var candidate in Candidates(overridePath))
            if (HasMaster(candidate))
                return candidate;
        return null;
    }

    private static bool HasMaster(string folder) =>
        !string.IsNullOrWhiteSpace(folder) && File.Exists(Path.Combine(folder, MasterEsm));

    private static IEnumerable<string> Candidates(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            // Accept either the Data folder itself or the game root that contains it.
            yield return overridePath;
            yield return Path.Combine(overridePath, "Data");
        }

        var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string?[] roots =
        [
            @"C:\Games\Steam\steamapps\common\Fallout New Vegas",        // known local install (project notes)
            @"C:\Games\Steam\steamapps\common\Fallout New Vegas English",
            Join(x86, "Steam", "steamapps", "common", "Fallout New Vegas"),
            Join(x86, "Steam", "steamapps", "common", "Fallout New Vegas English"),
            Join(pf,  "Steam", "steamapps", "common", "Fallout New Vegas"),
            Join(x86, "GOG Galaxy", "Games", "Fallout New Vegas"),
            Join(x86, "GalaxyClient", "Games", "Fallout New Vegas"),
            Join(x86, "Bethesda.net Launcher", "games", "Fallout New Vegas"),
        ];
        foreach (var root in roots)
            if (!string.IsNullOrEmpty(root))
                yield return Path.Combine(root, "Data");
    }

    private static string? Join(string root, params string[] parts) =>
        string.IsNullOrEmpty(root) ? null : Path.Combine([root, .. parts]);
}
