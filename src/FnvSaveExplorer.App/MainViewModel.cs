using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FnvSaveExplorer.Core;

namespace FnvSaveExplorer.App;

public sealed class FltRow
{
    public int Index { get; init; }
    public uint Value { get; init; }
    public string Hex => $"0x{Value:X8}";
}

public sealed class MiscStatRow
{
    public int Index { get; init; }
    public uint Value { get; init; }
}

/// <summary>One editable SPECIAL attribute row for the GUI grid.</summary>
public sealed class SpecialAttr : INotifyPropertyChanged
{
    public required string Name { get; init; }

    private byte _value;
    public byte Value
    {
        get => _value;
        set { if (_value != value) { _value = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class SkillRow : INotifyPropertyChanged
{
    public required string Name { get; init; }

    private float _value;
    public float Value
    {
        get => _value;
        set { if (_value != value) { _value = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class InventoryRow : INotifyPropertyChanged
{
    public uint FormId { get; init; }
    public int ModIndex { get; init; }
    public uint OriginalCount { get; init; }
    public string Item => $"0x{FormId:X8} (mod {ModIndex:X2})";

    /// <summary>The plugin (ESM/ESP) the item's mod index points at — its source mod/DLC.</summary>
    public string Source { get; init; } = "";

    private string _name = "";
    /// <summary>Display name resolved from the game masters; empty until/unless a Data folder is found.</summary>
    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name))); } }
    }

    private uint _count;
    public uint Count
    {
        get => _count;
        set { if (_count != value) { _count = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count))); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private FalloutSave? _save;
    private string? _loadedPath;

    public ObservableCollection<string> Plugins { get; } = [];
    public ObservableCollection<FltRow> FileLocationTable { get; } = [];
    public ObservableCollection<SpecialAttr> Special { get; } = [];
    public ObservableCollection<SkillRow> Skills { get; } = [];
    public ObservableCollection<InventoryRow> Inventory { get; } = [];
    public ObservableCollection<MiscStatRow> MiscStats { get; } = [];

    private static readonly string[] SpecialNames =
        ["Strength", "Perception", "Endurance", "Charisma", "Intelligence", "Agility", "Luck"];

    // ---- Display state -----------------------------------------------------
    private string _windowTitle = "FNV Save Explorer";
    public string WindowTitle { get => _windowTitle; private set => Set(ref _windowTitle, value); }

    private string _status = "Open a .fos save to begin.";
    public string Status { get => _status; private set => Set(ref _status, value); }

    private ImageSource? _screenshot;
    public ImageSource? Screenshot { get => _screenshot; private set => Set(ref _screenshot, value); }

    private bool _hasSave;
    public bool HasSave { get => _hasSave; private set => Set(ref _hasSave, value); }

    private string _playerName = "";
    public string PlayerName { get => _playerName; private set => Set(ref _playerName, value); }

    private string _playerTitle = "";
    public string PlayerTitle { get => _playerTitle; private set => Set(ref _playerTitle, value); }

    private string _language = "";
    public string Language { get => _language; private set => Set(ref _language, value); }

    private string _location = "";
    public string Location { get => _location; private set => Set(ref _location, value); }

    private string _playtime = "";
    public string Playtime { get => _playtime; private set => Set(ref _playtime, value); }

    private uint _level;
    public uint Level { get => _level; private set => Set(ref _level, value); }

    private uint _saveNumber;
    public uint SaveNumber { get => _saveNumber; private set => Set(ref _saveNumber, value); }

    private string _bodyInfo = "";
    public string BodyInfo { get => _bodyInfo; private set => Set(ref _bodyInfo, value); }

    private string _skillsInfo = "";
    public string SkillsInfo { get => _skillsInfo; private set => Set(ref _skillsInfo, value); }

    private string _inventoryInfo = "";
    public string InventoryInfo { get => _inventoryInfo; private set => Set(ref _inventoryInfo, value); }

    // ---- Edit fields (two-way bound) --------------------------------------
    private string _editName = "";
    public string EditName { get => _editName; set => Set(ref _editName, value); }

    private string _editLevel = "";
    public string EditLevel { get => _editLevel; set => Set(ref _editLevel, value); }

    private string _editSaveNumber = "";
    public string EditSaveNumber { get => _editSaveNumber; set => Set(ref _editSaveNumber, value); }

    private string _editDataFolder = "";
    /// <summary>Optional override for the game Data folder used to resolve item names.</summary>
    public string EditDataFolder { get => _editDataFolder; set => Set(ref _editDataFolder, value); }

    // ---- Operations --------------------------------------------------------
    public void Load(string path)
    {
        try
        {
            var save = FalloutSave.Load(path);
            _save = save;
            _loadedPath = path;

            PlayerName = save.PlayerName;
            PlayerTitle = save.PlayerTitle;
            Language = save.Language;
            Location = save.PlayerLocation;
            Playtime = save.Playtime;
            Level = save.PlayerLevel;
            SaveNumber = save.SaveNumber;

            EditName = save.PlayerName;
            EditLevel = save.PlayerLevel.ToString();
            EditSaveNumber = save.SaveNumber.ToString();

            Screenshot = BuildScreenshot(save.Screenshot);

            Plugins.Clear();
            foreach (var p in save.Plugins)
                Plugins.Add(p);

            FileLocationTable.Clear();
            var flt = save.PeekBodyUInt32(28);
            for (var i = 0; i < flt.Length; i++)
                FileLocationTable.Add(new FltRow { Index = i, Value = flt[i] });

            Special.Clear();
            if (save.Special is { } sp)
                for (var i = 0; i < SpecialNames.Length; i++)
                    Special.Add(new SpecialAttr { Name = SpecialNames[i], Value = sp.Values[i] });

            Skills.Clear();
            if (save.Skills is { } sk)
            {
                foreach (var s in sk.Skills.OrderBy(s => s.Name))
                    Skills.Add(new SkillRow { Name = s.Name, Value = s.Value });
                SkillsInfo = "Stored skill modifications (actor-value entries). Only skills the game has " +
                             "modified from their computed base are stored, so this may be a subset; values " +
                             "edit in place (same-length).";
            }
            else
            {
                SkillsInfo = "This save stores no editable skill modifications. The engine computes skills " +
                             "from base + SPECIAL + perks and only writes deviations (e.g. from a console " +
                             "setav, an implant, or certain effects), of which this save has fewer than two.";
            }

            Inventory.Clear();
            if (save.Inventory is { } inv)
            {
                var db = PluginDatabase.ForSave(save, string.IsNullOrWhiteSpace(EditDataFolder) ? null : EditDataFolder);
                if (db.DataFolder is not null && string.IsNullOrWhiteSpace(EditDataFolder))
                    EditDataFolder = db.DataFolder;
                foreach (var item in inv.Items.OrderByDescending(i => i.Count))
                    Inventory.Add(new InventoryRow
                    {
                        FormId = item.FormId,
                        ModIndex = item.ModIndex,
                        Name = db.Resolve(item.FormId) ?? "",
                        Source = save.FriendlySourceForModIndex(item.ModIndex) ?? "",
                        Count = item.Count,
                        OriginalCount = item.Count,
                    });
                InventoryInfo = DescribeInventory(inv.Items.Count, db);
            }
            else
            {
                InventoryInfo = "Inventory not located in this save (no change form parsed as a recognisable item list).";
            }

            MiscStats.Clear();
            if (save.MiscStats is { } ms)
                foreach (var stat in ms.Stats)
                    MiscStats.Add(new MiscStatRow { Index = stat.Index, Value = stat.Value });

            var bodyBytes = save.FileLength - save.BodyOffset;
            BodyInfo =
                $"Body begins at offset 0x{save.BodyOffset:X} ({save.BodyOffset:N0}).\n" +
                $"{bodyBytes:N0} bytes of globals, change forms and the FormID array follow — " +
                "preserved verbatim and not yet decoded.";

            HasSave = true;
            WindowTitle = $"FNV Save Explorer — {Path.GetFileName(path)}";
            Status = $"Loaded {Path.GetFileName(path)} ({save.FileLength:N0} bytes).";
        }
        catch (SaveFormatException ex)
        {
            Status = $"Could not parse save: {ex.Message}";
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Re-resolves inventory item names using the current <see cref="EditDataFolder"/> override (or
    /// auto-detection when it's blank), updating rows in place so staged count edits are preserved.
    /// </summary>
    public void ReresolveNames()
    {
        if (_save is null)
            return;
        var db = PluginDatabase.ForSave(_save, string.IsNullOrWhiteSpace(EditDataFolder) ? null : EditDataFolder);
        if (db.DataFolder is not null)
            EditDataFolder = db.DataFolder;
        foreach (var row in Inventory)
            row.Name = db.Resolve(row.FormId) ?? "";
        InventoryInfo = DescribeInventory(Inventory.Count, db);
    }

    private static string DescribeInventory(int stacks, PluginDatabase db) =>
        db.Count > 0
            ? $"Player inventory ({stacks} stacks). Names resolved from the game masters in {db.DataFolder}. " +
              "Stack counts edit in place (same-length); edit a Count and Apply, then Save As."
            : $"Player inventory ({stacks} stacks). Item names need the game's Data folder (FalloutNV.esm) — " +
              "not found automatically. Set the Data folder below and click Resolve names. Stack counts edit " +
              "in place (same-length); edit a Count and Apply, then Save As.";

    /// <summary>Stages the edit-tab values onto the loaded save (same-length / fixed-width only).</summary>
    public void ApplyEdits()
    {
        if (_save is null)
            return;

        var messages = new List<string>();

        if (uint.TryParse(EditLevel, out var lvl))
        {
            _save.SetPlayerLevel(lvl);
            Level = lvl;
        }
        else messages.Add("level must be a whole number");

        if (uint.TryParse(EditSaveNumber, out var num))
        {
            _save.SetSaveNumber(num);
            SaveNumber = num;
        }
        else messages.Add("save number must be a whole number");

        if (EditName != PlayerName)
        {
            if (_save.TrySetPlayerName(EditName))
                PlayerName = EditName;
            else
                messages.Add($"name must stay {PlayerName.Length} characters (same-length edits only)");
        }

        if (Special.Count == 7 && !_save.TrySetSpecial(Special.Select(a => a.Value).ToArray()))
            messages.Add("could not apply SPECIAL");

        foreach (var skill in Skills)
            if (!_save.TrySetSkill(skill.Name, skill.Value))
                messages.Add($"could not apply skill {skill.Name}");

        // Only stage stacks whose count actually changed (editing by FormID targets the first matching
        // stack, so applying unchanged rows would be needless and could disturb duplicate-FormID stacks).
        foreach (var row in Inventory)
            if (row.Count != row.OriginalCount && !_save.TrySetItemCount(row.FormId, row.Count))
                messages.Add($"could not apply count for 0x{row.FormId:X8}");

        Status = messages.Count == 0
            ? "Edits staged. Use \"Save As…\" to write a new .fos."
            : "Some edits were skipped: " + string.Join("; ", messages);
    }

    public void SaveAs(string path)
    {
        if (_save is null)
            return;
        try
        {
            _save.Save(path, backup: true);
            Status = $"Saved to {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            Status = $"Save failed: {ex.Message}";
        }
    }

    public string? SuggestedSavePath => _loadedPath;

    private static ImageSource BuildScreenshot(SaveScreenshot shot)
    {
        var bgra = shot.ToBgra32();
        var bmp = BitmapSource.Create(
            shot.Width, shot.Height, 96, 96, PixelFormats.Bgra32, null, bgra, shot.Width * 4);
        bmp.Freeze();
        return bmp;
    }

    // ---- INotifyPropertyChanged -------------------------------------------
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
