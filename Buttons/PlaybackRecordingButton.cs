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
            var player = new PlaybackService();
            var result = await player.PlayAsync(package);
            while (result.PausedIssue is not null)
            {
                var choice = System.Windows.MessageBox.Show(
                    $"Playback paused after {result.Queued} operation(s) were applied.\n\n{result.PausedIssue}\n\nYes: retry after correcting the target data.\nNo: skip this operation and continue.\nCancel: stop playback and retain work already applied.",
                    "ArcGIS Playback - Review Required", System.Windows.MessageBoxButton.YesNoCancel, System.Windows.MessageBoxImage.Warning);
                result = await player.ContinueAsync(choice switch
                {
                    System.Windows.MessageBoxResult.Yes => PlaybackContinuation.Retry,
                    System.Windows.MessageBoxResult.No => PlaybackContinuation.Skip,
                    _ => PlaybackContinuation.Stop
                });
            }
            var message = result.Stopped
                ? $"Playback stopped. {result.Queued} operation(s) applied; {result.Skipped.Count} skipped."
                : result.Skipped.Count == 0
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
