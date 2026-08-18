using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Dialogs;
using NetworkChangePlaybackAddin.Dialogs;
using NetworkChangePlaybackAddin.Services;

namespace NetworkChangePlaybackAddin.Buttons;

internal sealed class PlaybackRecordingButton : Button
{
    protected override void OnClick() => ReplayAsync();

    private async void ReplayAsync()
    {
        var window = new PlaybackFileWindow { Owner = System.Windows.Application.Current?.MainWindow };
        if (window.ShowDialog() != true || window.FilePath is null) return;
        try
        {
            var package = PackageRecorder.Read(window.FilePath);
            var result = await new PlaybackService().PlayAsync(package);
            var message = result.Skipped.Count == 0
                ? $"Playback complete. {result.Queued} operation(s) applied."
                : $"Playback completed. {result.Queued} operation(s) applied; {result.Skipped.Count} skipped.\n\nFirst issue: {result.Skipped[0]}";
            MessageBox.Show(message, "ArcGIS Playback");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Playback failed: {ex.Message}", "ArcGIS Playback");
        }
    }
}
