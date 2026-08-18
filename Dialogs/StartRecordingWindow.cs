using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.IO;
using Microsoft.Win32;
using NetworkChangePlaybackAddin.Models;

namespace NetworkChangePlaybackAddin.Dialogs;

internal sealed class StartRecordingWindow : Window
{
    private readonly TextBox _name = new() { MinWidth = 280 };
    private readonly TextBox _workOrder = new() { MinWidth = 280 };
    private readonly TextBox _sourceVersion = new() { Text = "SDE.DEFAULT", MinWidth = 280 };
    private readonly TextBox _filePath = new() { MinWidth = 220 };
    private readonly TextBox _description = new() { MinWidth = 280, MinHeight = 70, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };

    internal PackageMetadata? Metadata { get; private set; }
    internal string? FilePath { get; private set; }

    internal StartRecordingWindow()
    {
        Title = "Start Change Recording";
        Width = 460;
        Height = 610;
        MinWidth = 460;
        MinHeight = 530;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = DialogAppearance.Background;
        Foreground = DialogAppearance.Foreground;
        Content = BuildContent();
    }

    private UIElement BuildContent()
    {
        var panel = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
        panel.Children.Add(DialogAppearance.SectionTitle("Record a pre-production work package"));
        panel.Children.Add(new TextBlock { Text = "Enter the package attribution before starting the active-map recording.", Foreground = DialogAppearance.Foreground, Opacity = .72, Margin = new Thickness(0, 4, 0, 18) });
        AddField(panel, "Package name *", _name);
        AddField(panel, "Work order / reference", _workOrder);
        AddField(panel, "Source branch version *", _sourceVersion);
        panel.Children.Add(new TextBlock { Text = "Save location and filename *", Margin = new Thickness(0, 0, 0, 4) });
        var fileRow = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        fileRow.ColumnDefinitions.Add(new ColumnDefinition());
        fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fileRow.Children.Add(_filePath);
        var browse = DialogAppearance.SecondaryButton("Browse…", 82);
        browse.Margin = new Thickness(8, 0, 0, 0);
        browse.Click += (_, _) => BrowseForFile();
        Grid.SetColumn(browse, 1);
        fileRow.Children.Add(browse);
        panel.Children.Add(fileRow);
        AddField(panel, "Description", _description);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = DialogAppearance.SecondaryButton("Cancel", 88);
        cancel.Margin = new Thickness(0, 0, 8, 0);
        cancel.Click += (_, _) => Close();
        var start = DialogAppearance.PrimaryButton("Start recording", 116);
        start.IsDefault = true;
        start.Click += (_, _) => Start();
        buttons.Children.Add(cancel);
        buttons.Children.Add(start);
        panel.Children.Add(buttons);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = panel };
        return DialogAppearance.WithChrome(this, "Start Recording", scroll);
    }

    private static void AddField(Panel panel, string label, Control input)
    {
        panel.Children.Add(new TextBlock { Text = label, Foreground = DialogAppearance.Foreground, Margin = new Thickness(0, 0, 0, 4) });
        input.Margin = new Thickness(0, 0, 0, 10);
        input.Background = DialogAppearance.InputBackground;
        input.Foreground = DialogAppearance.Foreground;
        input.BorderBrush = DialogAppearance.Border;
        panel.Children.Add(input);
    }

    private void Start()
    {
        if (string.IsNullOrWhiteSpace(_name.Text) || string.IsNullOrWhiteSpace(_sourceVersion.Text) || string.IsNullOrWhiteSpace(_filePath.Text))
        {
            MessageBox.Show(this, "Package name, source branch version, and save location are required.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Metadata = new PackageMetadata
        {
            Name = _name.Text.Trim(),
            SourceBranchVersion = _sourceVersion.Text.Trim(),
            WorkOrder = EmptyToNull(_workOrder.Text),
            Description = EmptyToNull(_description.Text)
        };
        FilePath = _filePath.Text.Trim();
        DialogResult = true;
    }

    private void BrowseForFile()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save recorded change package",
            Filter = "ArcGIS playback package (*.unplayback.json)|*.unplayback.json|JSON files (*.json)|*.json",
            DefaultExt = ".unplayback.json",
            FileName = SafeFileName(_name.Text) + ".unplayback.json"
        };
        if (dialog.ShowDialog(this) == true) _filePath.Text = dialog.FileName;
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string SafeFileName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
