using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Dialogs;
using NetworkChangePlaybackAddin.Dialogs;
using NetworkChangePlaybackAddin.Services;

namespace NetworkChangePlaybackAddin.Buttons;

internal sealed class PreviewPlaybackButton : Button
{
    private static readonly PlaybackPreviewOverlay Preview = new();

    protected override void OnClick() => PreviewAsync();

    private async void PreviewAsync()
    {
        var window = new PlaybackFileWindow { Owner = System.Windows.Application.Current?.MainWindow };
        if (window.ShowDialog() != true || window.FilePath is null) return;
        try
        {
            var package = PackageRecorder.Read(window.FilePath);
            await new PlaybackService().ZoomToRecordedExtentAsync(package);
            await Preview.ShowAsync(package);
            MessageBox.Show("Preview drawn. Blue = add, gold = update, red = delete. No edits were made.", "ArcGIS Playback");
        }
        catch (Exception ex) { MessageBox.Show($"Could not preview playback: {ex.Message}", "ArcGIS Playback"); }
    }
}
