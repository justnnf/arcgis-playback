using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.IO;
using Microsoft.Win32;

namespace NetworkChangePlaybackAddin.Dialogs;

internal sealed class PlaybackFileWindow : Window
{
    private readonly TextBox _filePath = new();
    internal string? FilePath { get; private set; }

    internal PlaybackFileWindow()
    {
        Title = "Playback Recording";
        Width = 680;
        Height = 300;
        MinWidth = 560;
        MinHeight = 270;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = DialogAppearance.Background;
        Foreground = DialogAppearance.Foreground;
        var root = new Grid { Margin = new Thickness(22, 20, 22, 18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 18 });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        heading.Children.Add(DialogAppearance.SectionTitle("Playback a recorded change package"));
        heading.Children.Add(new TextBlock { Text = "Select the .unplayback.json file to apply to the active production map.", Foreground = DialogAppearance.Foreground, Opacity = .72, Margin = new Thickness(0, 4, 0, 0) });
        root.Children.Add(heading);
        var fileRow = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        fileRow.ColumnDefinitions.Add(new ColumnDefinition());
        fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fileRow.Children.Add(_filePath);
        var browse = DialogAppearance.SecondaryButton("Browse…", 92);
        browse.Margin = new Thickness(8, 0, 0, 0);
        browse.Click += (_, _) => Browse();
        Grid.SetColumn(browse, 1);
        fileRow.Children.Add(browse);
        Grid.SetRow(fileRow, 1);
        root.Children.Add(fileRow);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = DialogAppearance.SecondaryButton("Cancel", 82);
        cancel.Margin = new Thickness(0, 0, 8, 0);
        cancel.Click += (_, _) => Close();
        var play = DialogAppearance.PrimaryButton("Replay", 82);
        play.IsDefault = true;
        play.Click += (_, _) => Confirm();
        actions.Children.Add(cancel);
        actions.Children.Add(play);
        Grid.SetRow(actions, 3);
        root.Children.Add(actions);
        _filePath.Background = DialogAppearance.InputBackground;
        _filePath.Foreground = DialogAppearance.Foreground;
        _filePath.BorderBrush = DialogAppearance.Border;
        Content = DialogAppearance.WithChrome(this, "Playback Recording", root);
    }

    private void Browse()
    {
        var dialog = new OpenFileDialog { Filter = "ArcGIS playback package (*.unplayback.json)|*.unplayback.json|JSON files (*.json)|*.json" };
        if (dialog.ShowDialog(this) == true) _filePath.Text = dialog.FileName;
    }

    private void Confirm()
    {
        if (!File.Exists(_filePath.Text))
        {
            MessageBox.Show(this, "Choose an existing playback package.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        FilePath = _filePath.Text;
        DialogResult = true;
    }
}
