using System.Windows;
using System.Windows.Controls;
using System.IO;
using Microsoft.Win32;

namespace NetworkChangePlaybackAddin.Dialogs;

internal sealed class PlaybackFileWindow : Window
{
    private readonly TextBox _filePath = new() { MinWidth = 360 };
    internal string? FilePath { get; private set; }

    internal PlaybackFileWindow()
    {
        Title = "Playback Recording";
        Width = 560;
        Height = 170;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = "Playback file", FontWeight = FontWeights.SemiBold });
        var fileRow = new Grid { Margin = new Thickness(0, 6, 0, 14) };
        fileRow.ColumnDefinitions.Add(new ColumnDefinition());
        fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fileRow.Children.Add(_filePath);
        var browse = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0), MinWidth = 78 };
        browse.Click += (_, _) => Browse();
        Grid.SetColumn(browse, 1);
        fileRow.Children.Add(browse);
        Grid.SetRow(fileRow, 1);
        root.Children.Add(fileRow);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", MinWidth = 82, Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => Close();
        var play = new Button { Content = "Replay", MinWidth = 82, IsDefault = true };
        play.Click += (_, _) => Confirm();
        actions.Children.Add(cancel);
        actions.Children.Add(play);
        Grid.SetRow(actions, 3);
        root.Children.Add(actions);
        Content = root;
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
