using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Dialogs;
using NetworkChangePlaybackAddin.Dialogs;
using NetworkChangePlaybackAddin.Services;

namespace NetworkChangePlaybackAddin.Buttons;

internal sealed class PreviewPlaybackButton : Button
{
    protected override void OnClick() => PreviewAsync();

    private async void PreviewAsync()
    {
        var window = new PlaybackFileWindow { Owner = System.Windows.Application.Current?.MainWindow };
        if (window.ShowDialog() != true || window.FilePath is null) return;
        var stage = "reading the playback package";
        try
        {
            var package = PackageRecorder.Read(window.FilePath);
            stage = "zooming to the recorded extent";
            await new PlaybackService().ZoomToRecordedExtentAsync(package);
            stage = "drawing the preview overlays";
            await RecorderHost.Preview.ShowAsync(package);
            MessageBox.Show("Preview drawn on the map. Blue = add/update; red = delete. No edits were made. Use Clear Preview to remove it.", "ArcGIS Playback");
        }
        catch (Exception ex) { MessageBox.Show($"Could not preview playback while {stage}: {ex.Message}", "ArcGIS Playback"); }
    }
}
