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
    public string? Name { get; init; }
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

    /// <summary>The Pip-Boy tab the item appears under (Weapons / Apparel / Aid / Ammo / Misc / Notes),
    /// derived from the base form's record type; empty when names aren't resolved.</summary>
    public string Category { get; init; } = "";

    /// <summary>The base-form record signature (WEAP/ARMO/ALCH/AMMO/MISC/…), or empty if unknown.</summary>
    public string RecordType { get; init; } = "";

    /// <summary>Tab + record type for display, e.g. "Aid (BOOK)".</summary>
    public string Tab => string.IsNullOrEmpty(RecordType) ? Category : $"{Category} ({RecordType})";

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

    /// <summary>True when the stack is equipped/worn (read-only display).</summary>
    public bool Equipped { get; init; }

    /// <summary>The stack's condition when loaded, or null if it carries no condition extra-data.</summary>
    public float? OriginalCondition { get; init; }

    private float? _condition;
    /// <summary>The stack's editable condition/health (a same-length float splice), or null for stacks
    /// (ammo/aid/misc) that carry none — those reject edits.</summary>
    public float? Condition
    {
        get => _condition;
        set { if (!Nullable.Equals(_condition, value)) { _condition = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Condition))); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>One note in the player's Pip-Boy Data → Notes list; read-only display (ROADMAP §4k / §4k.1 #4).</summary>
public sealed class NoteRow
{
    public uint FormId { get; init; }
    public int ModIndex { get; init; }
    public string Note => $"0x{FormId:X8} (mod {ModIndex:X2})";

    /// <summary>"Read" once viewed (a marker exists) or "Unread" (acquired but never opened — bold in-game).</summary>
    public bool Read { get; init; }
    public string Status => Read ? "Read" : "Unread";

    /// <summary>The note's media type — Text / Voice / Sound / Image — read from the base form (§4k.1 #6).</summary>
    public string Media { get; init; } = "";

    /// <summary>Display name resolved from the game masters; empty until/unless a Data folder is found.</summary>
    public string Name { get; init; } = "";

    /// <summary>The plugin (ESM/ESP) the note's mod index points at — its source mod/DLC.</summary>
    public string Source { get; init; } = "";
}

/// <summary>One quest in the read-only quest-log view (ROADMAP §6 #10). Its decoded stages and objectives
/// are flattened into <see cref="Lines"/> for the master-detail row, with summary columns alongside.</summary>
public sealed class QuestRow
{
    public uint FormId { get; init; }
    public string FormIdHex => $"0x{FormId:X8}";
    public string Name { get; init; } = "";

    /// <summary>Active / Completed / Unknown — see <see cref="QuestState"/>.</summary>
    public string State { get; init; } = "";

    public int StageCount { get; init; }
    public int DoneStageCount { get; init; }

    /// <summary>"3/6 stages" when the quest stores a decoded stage list, else blank (state lives in the
    /// undecoded packed form, ROADMAP §6 #10).</summary>
    public string Progress => StageCount > 0 ? $"{DoneStageCount}/{StageCount} stages" : "";

    /// <summary>The quest's stage + objective detail lines, shown in the expandable row-details panel.</summary>
    public IReadOnlyList<string> Lines { get; init; } = [];
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private FalloutSave? _save;
    private string? _loadedPath;
    private PluginDatabase? _questDb; // database last used for the Quests tab, so the show-all toggle can re-populate

    public ObservableCollection<string> Plugins { get; } = [];
    public ObservableCollection<FltRow> FileLocationTable { get; } = [];
    public ObservableCollection<SpecialAttr> Special { get; } = [];
    public ObservableCollection<SkillRow> Skills { get; } = [];
    public ObservableCollection<InventoryRow> Inventory { get; } = [];
    public ObservableCollection<NoteRow> Notes { get; } = [];
    public ObservableCollection<QuestRow> Quests { get; } = [];
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

    private string _notesInfo = "";
    public string NotesInfo { get => _notesInfo; private set => Set(ref _notesInfo, value); }

    private string _questsInfo = "";
    public string QuestsInfo { get => _questsInfo; private set => Set(ref _questsInfo, value); }

    private bool _showAllQuests;
    /// <summary>When set, the Quests tab also lists internal/tracker quests that have stage state but no
    /// player-facing log line (the player-facing gate is bypassed — see <see cref="QuestLog"/>). Toggling
    /// re-populates the grid.</summary>
    public bool ShowAllQuests
    {
        get => _showAllQuests;
        set
        {
            if (_showAllQuests == value) return;
            Set(ref _showAllQuests, value);
            if (_save is not null && _questDb is not null)
                PopulateQuests(_save, _questDb);
        }
    }

    // ---- Edit fields (two-way bound) --------------------------------------
    private string _editName = "";
    public string EditName { get => _editName; set => Set(ref _editName, value); }

    private string _editLevel = "";
    public string EditLevel { get => _editLevel; set => Set(ref _editLevel, value); }

    private string _editSaveNumber = "";
    public string EditSaveNumber { get => _editSaveNumber; set => Set(ref _editSaveNumber, value); }

    private string _editCaps = "";
    /// <summary>The player's caps (the <c>0x0000000F</c> inventory stack, §6.4). Blank when the save carries
    /// no caps stack; editing it is a same-length count splice via <see cref="FalloutSave.TrySetCaps"/>.</summary>
    public string EditCaps { get => _editCaps; set => Set(ref _editCaps, value); }

    private string _editKarma = "";
    /// <summary>The player's karma (a float in the player reference's actor-value array, §4j). Blank when
    /// not locatable; edited same-length via <see cref="FalloutSave.TrySetKarma"/>.</summary>
    public string EditKarma { get => _editKarma; set => Set(ref _editKarma, value); }

    private string _editXp = "";
    /// <summary>The player's XP (a float, §4j). Blank when not locatable; edited same-length via
    /// <see cref="FalloutSave.TrySetXp"/>.</summary>
    public string EditXp { get => _editXp; set => Set(ref _editXp, value); }

    private string _editDataFolder = "";
    /// <summary>Optional override for the game Data folder used to resolve item names.</summary>
    public string EditDataFolder { get => _editDataFolder; set => Set(ref _editDataFolder, value); }

    private string _editModsFolder = "";
    /// <summary>Optional Mod Organizer 2 <c>mods\</c> folder (or MO2 root) for resolving mod item names.</summary>
    public string EditModsFolder { get => _editModsFolder; set => Set(ref _editModsFolder, value); }

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
            EditCaps = save.Caps?.ToString() ?? "";
            EditKarma = save.Karma?.ToString("0.###") ?? "";
            EditXp = save.Xp?.ToString("0.###") ?? "";

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

            var mods = GameDataLocator.FindMo2Mods(path, string.IsNullOrWhiteSpace(EditModsFolder) ? null : EditModsFolder);
            var invDb = PluginDatabase.ForSave(save, string.IsNullOrWhiteSpace(EditDataFolder) ? null : EditDataFolder, mods);
            if (invDb.DataFolder is not null && string.IsNullOrWhiteSpace(EditDataFolder))
                EditDataFolder = invDb.DataFolder;
            if (invDb.ModsFolder is not null && string.IsNullOrWhiteSpace(EditModsFolder))
                EditModsFolder = invDb.ModsFolder;
            PopulateInventory(save, invDb);
            PopulateNotes(save, invDb);
            PopulateQuests(save, invDb);

            MiscStats.Clear();
            if (save.MiscStats is { } ms)
                foreach (var stat in ms.Stats)
                    MiscStats.Add(new MiscStatRow { Index = stat.Index, Name = MiscStatNames.Get(stat.Index), Value = stat.Value });

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
        var mods = GameDataLocator.FindMo2Mods(_loadedPath, string.IsNullOrWhiteSpace(EditModsFolder) ? null : EditModsFolder);
        var db = PluginDatabase.ForSave(_save, string.IsNullOrWhiteSpace(EditDataFolder) ? null : EditDataFolder, mods);
        if (db.DataFolder is not null)
            EditDataFolder = db.DataFolder;
        if (db.ModsFolder is not null)
            EditModsFolder = db.ModsFolder;
        PopulateInventory(_save, db);
        PopulateNotes(_save, db);
        PopulateQuests(_save, db);
    }

    /// <summary>
    /// Fills the inventory grid from the save. When item names are available, entries that don't resolve to
    /// an item are hidden — on large records the decoder's run can absorb a few non-item bytes that validate
    /// structurally but aren't real stacks; offline (no names), everything is shown so nothing is lost.
    /// </summary>
    private void PopulateInventory(FalloutSave save, PluginDatabase db)
    {
        Inventory.Clear();
        if (save.Inventory is not { } inv)
        {
            InventoryInfo = "Inventory not located in this save (no change form parsed as a recognisable item list).";
            return;
        }
        var items = db.Count > 0 ? inv.Items.Where(i => db.Resolve(i.FormId) is not null) : inv.Items;
        foreach (var item in items.OrderByDescending(i => i.Count))
            Inventory.Add(new InventoryRow
            {
                FormId = item.FormId,
                ModIndex = item.ModIndex,
                Name = db.Resolve(item.FormId) ?? "",
                Source = save.FriendlySourceForModIndex(item.ModIndex) ?? "",
                Category = db.Category(item.FormId) ?? "",
                RecordType = db.RecordType(item.FormId) ?? "",
                Count = item.Count,
                OriginalCount = item.Count,
                Equipped = item.Equipped,
                Condition = item.Condition,
                OriginalCondition = item.Condition,
            });
        InventoryInfo = DescribeInventory(Inventory.Count, db);
    }

    /// <summary>Fills the Notes grid with the player's full Pip-Boy Data → Notes list — read <em>and</em> unread
    /// (§4k / §4k.1 #4) — resolving names from the masters. The unread set needs the masters (it's identified by
    /// which references in the player inventory record are NOTE records), so without a Data folder we fall back to
    /// just the read markers. Read-only — toggling read state is length-changing.</summary>
    private void PopulateNotes(FalloutSave save, PluginDatabase db)
    {
        Notes.Clear();
        if (db.Count > 0)
        {
            var notes = save.PipBoyNotes(fid => db.RecordType(fid) == "NOTE");
            foreach (var n in notes
                         .OrderBy(n => n.Read)                                   // unread first
                         .ThenBy(n => db.Resolve(n.FormId) ?? "￿", StringComparer.OrdinalIgnoreCase))
                Notes.Add(new NoteRow
                {
                    FormId = n.FormId,
                    ModIndex = n.ModIndex,
                    Read = n.Read,
                    Media = db.NoteMediaType(n.FormId) ?? "",
                    Name = db.Resolve(n.FormId) ?? "",
                    Source = save.FriendlySourceForModIndex(n.ModIndex) ?? "",
                });
            var unread = Notes.Count(r => !r.Read);
            NotesInfo = $"{Notes.Count} note(s) — {Notes.Count - unread} read, {unread} unread (Pip-Boy Data → Notes). " +
                        "Read-only — toggling read state is a length-changing edit.";
        }
        else
        {
            foreach (var n in save.ReadNotes.Notes)
                Notes.Add(new NoteRow { FormId = n.FormId, ModIndex = n.ModIndex, Read = true });
            NotesInfo = $"{Notes.Count} note(s) read. Names + the unread list need the game's Data folder " +
                        "(FalloutNV.esm) — set it on the Edit tab.";
        }
    }

    /// <summary>Fills the read-only Quests grid with the player's quest log (ROADMAP §6 #10): each tracked quest
    /// with its decoded stages (done + completion time) and objectives (active state + target enable-state),
    /// flattened into per-quest detail lines. Classifying a change form as a quest, and naming its stages and
    /// objectives, all need the masters, so without a Data folder the grid stays empty.</summary>
    private void PopulateQuests(FalloutSave save, PluginDatabase db)
    {
        Quests.Clear();
        _questDb = db;
        if (db.Count == 0)
        {
            QuestsInfo = "The quest log needs the game's Data folder (FalloutNV.esm) to classify and name " +
                         "quests — set it on the Edit tab.";
            return;
        }

        var log = QuestLog.Read(save, db, includeAll: ShowAllQuests);
        foreach (var q in log.Quests
                     .OrderBy(q => q.State == QuestState.Unknown)                  // decoded-progress quests first
                     .ThenBy(q => q.Name ?? "￿", StringComparer.OrdinalIgnoreCase))
        {
            var lines = new List<string>();
            foreach (var s in q.Stages.OrderBy(s => s.Index))
            {
                var when = s.CompletionTime is { } t ? $"  (t={t})" : "";
                var text = s.LogText is { Length: > 0 } ? $"  {s.LogText}" : "";
                lines.Add($"stage {s.Index,-3} {(s.Done ? "[x]" : "[ ]")}{when}{text}");
            }
            foreach (var o in q.Objectives.OrderBy(o => o.Index))
            {
                // Save-side display/complete status (bit0/bit1); skip objectives the save marks not-displayed.
                var mark = (o.Displayed, o.Completed) switch
                {
                    (true, true) => "done",
                    (true, _) => "active",
                    (false, true) => "done*",
                    (false, _) => (string?)null,
                    (null, _) => "—",
                };
                if (mark is null) continue;
                lines.Add($"obj {o.Index,-3} [{mark}]  {o.Text ?? "?"}");
            }

            Quests.Add(new QuestRow
            {
                FormId = q.FormId,
                Name = q.Name ?? "",
                State = q.State.ToString(),
                StageCount = q.Stages.Count,
                DoneStageCount = q.Stages.Count(s => s.Done),
                Lines = lines,
            });
        }

        QuestsInfo = (ShowAllQuests
            ? $"{Quests.Count} quest(s) with any recorded progress, including internal/tracker quests with stage " +
              "state but no player-facing log line. Untick to show only the player-facing log. "
            : $"{Quests.Count} quest(s) in the player's log — displayed objectives + completed log stages the save " +
              "records. NOT the full Pip-Boy list: Start-Game-Enabled quests at their masters default (e.g. the DLC " +
              "intros) leave no save delta and aren't shown — tick “Show tracker quests” for everything decodable. ")
            + "(ROADMAP §6 #10.) Read-only — quest edits are length-changing.";
    }

    private static string DescribeInventory(int stacks, PluginDatabase db) =>
        db.Count > 0
            ? $"Player inventory ({stacks} stacks). Names resolved from the game masters in {db.DataFolder}. " +
              "Count and Condition (weapon/armor health) edit in place (same-length); edit a value and Apply, then Save As."
            : $"Player inventory ({stacks} stacks). Item names need the game's Data folder (FalloutNV.esm) — " +
              "not found automatically. Set the Data folder below and click Resolve names. Count and Condition " +
              "edit in place (same-length); edit a value and Apply, then Save As.";

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
        {
            if (row.Count != row.OriginalCount && !_save.TrySetItemCount(row.FormId, row.Count))
                messages.Add($"could not apply count for 0x{row.FormId:X8}");
            if (row.Condition is { } c && !Nullable.Equals(row.Condition, row.OriginalCondition)
                && !_save.TrySetItemCondition(row.FormId, c))
                messages.Add($"could not apply condition for 0x{row.FormId:X8}");
        }

        // Caps (the 0x0000000F stack) — staged after the inventory loop so the dedicated field wins if the
        // same stack was also edited in the grid. Only when changed and the save actually carries caps.
        if (!string.IsNullOrWhiteSpace(EditCaps))
        {
            if (uint.TryParse(EditCaps, out var caps))
            {
                if (caps != _save.Caps && !_save.TrySetCaps(caps))
                    messages.Add("could not apply caps (no caps stack in this inventory)");
            }
            else messages.Add("caps must be a whole number");
        }

        // Karma / XP (floats in the player reference record, §4j) — staged only when changed and parseable.
        if (!string.IsNullOrWhiteSpace(EditKarma))
        {
            if (float.TryParse(EditKarma, out var k))
            {
                if (k != _save.Karma && !_save.TrySetKarma(k))
                    messages.Add("could not apply karma (player reference record/slot not located)");
            }
            else messages.Add("karma must be a number");
        }
        if (!string.IsNullOrWhiteSpace(EditXp))
        {
            if (float.TryParse(EditXp, out var xp))
            {
                if (xp != _save.Xp && !_save.TrySetXp(xp))
                    messages.Add("could not apply XP (player reference record/slot not located)");
            }
            else messages.Add("XP must be a number");
        }

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
