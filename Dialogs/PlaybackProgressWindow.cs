using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetworkChangePlaybackAddin.Services;

namespace NetworkChangePlaybackAddin.Dialogs;

internal sealed class PlaybackProgressWindow : Window
{
    private readonly TextBlock _summary = new() { Foreground = DialogAppearance.Foreground, TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _entries = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = DialogAppearance.Foreground };

    internal PlaybackProgressWindow()
    {
        Title = "ArcGIS Playback - Progress";
        Width = 560;
        Height = 430;
        MinWidth = 480;
        MinHeight = 330;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = DialogAppearance.Background;
        Foreground = DialogAppearance.Foreground;
        var panel = new Grid { Margin = new Thickness(22, 20, 22, 18) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.Children.Add(DialogAppearance.SectionTitle("Playback activity"));
        _summary.Text = "Preparing playback…";
        _summary.Margin = new Thickness(0, 8, 0, 14);
        Grid.SetRow(_summary, 1);
        panel.Children.Add(_summary);
        Grid.SetRow(_entries, 2);
        panel.Children.Add(_entries);
        Content = DialogAppearance.WithChrome(this, "Playback Progress", panel);
    }

    internal void Report(PlaybackProgress progress) => Dispatcher.BeginInvoke(() =>
    {
        var operation = progress.Operation;
        var text = operation is null
            ? progress.Detail ?? progress.State
            : $"#{operation.Sequence}  {progress.State}  {operation.Type} — {operation.LayerName ?? operation.Association?.AssociationType ?? "association"}";
        _summary.Text = text;
        _entries.Items.Add(text + (string.IsNullOrWhiteSpace(progress.Detail) ? string.Empty : $"\n{progress.Detail}"));
        _entries.ScrollIntoView(_entries.Items[^1]);
    });
}
