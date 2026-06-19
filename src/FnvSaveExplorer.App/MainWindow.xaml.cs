using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace FnvSaveExplorer.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private static string DefaultSaveDirectory
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "My Games", "FalloutNV", "Saves");
            return Directory.Exists(dir) ? dir : Environment.CurrentDirectory;
        }
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open Fallout: New Vegas save",
            Filter = "Fallout saves (*.fos)|*.fos|All files (*.*)|*.*",
            InitialDirectory = DefaultSaveDirectory,
        };
        if (dialog.ShowDialog(this) == true)
            _vm.Load(dialog.FileName);
    }

    private void OnApplyClick(object sender, RoutedEventArgs e) => _vm.ApplyEdits();

    private void OnResolveNamesClick(object sender, RoutedEventArgs e) => _vm.ReresolveNames();

    private void OnBrowseDataFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select the Fallout: New Vegas Data folder (contains FalloutNV.esm)",
            InitialDirectory = Directory.Exists(_vm.EditDataFolder) ? _vm.EditDataFolder : DefaultSaveDirectory,
        };
        if (dialog.ShowDialog(this) == true)
        {
            _vm.EditDataFolder = dialog.FolderName;
            _vm.ReresolveNames();
        }
    }

    private void OnSaveAsClick(object sender, RoutedEventArgs e)
    {
        var suggested = _vm.SuggestedSavePath;
        var dialog = new SaveFileDialog
        {
            Title = "Save edited .fos (writes a new file)",
            Filter = "Fallout saves (*.fos)|*.fos|All files (*.*)|*.*",
            InitialDirectory = suggested is not null ? Path.GetDirectoryName(suggested) : DefaultSaveDirectory,
            FileName = suggested is not null
                ? Path.GetFileNameWithoutExtension(suggested) + " (edited).fos"
                : "edited.fos",
        };
        if (dialog.ShowDialog(this) == true)
            _vm.SaveAs(dialog.FileName);
    }
}
