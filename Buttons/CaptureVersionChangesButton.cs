using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Dialogs;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using NetworkChangePlaybackAddin.Dialogs;
using NetworkChangePlaybackAddin.Services;

namespace NetworkChangePlaybackAddin.Buttons;

internal sealed class CaptureVersionChangesButton : Button
{
    protected override void OnClick() => CaptureAsync();

    private static async void CaptureAsync()
    {
        if (RecorderHost.Recorder.ActivePackage is not null)
        {
            MessageBox.Show("Save the active recording before capturing version changes.", "ArcGIS Playback");
            return;
        }

        var context = await QueuedTask.Run(RecordingContextService.Get);
        var window = new StartRecordingWindow(context.SourceVersion, context.ExtentJson, captureVersionDelta: true)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        if (window.ShowDialog() != true || window.Metadata is null || window.FilePath is null) return;

        try
        {
            var result = await QueuedTask.Run(() => new VersionDifferenceCapture().Capture(MapView.Active?.Map
                ?? throw new InvalidOperationException("Activate a map before capturing version changes."), window.Metadata));
            PackageRecorder.WritePackage(window.FilePath, result.Package);
            var skipped = result.SkippedSources.Count == 0 ? string.Empty : $"\n\nSkipped sources:\n{string.Join("\n", result.SkippedSources)}";
            MessageBox.Show($"Captured {result.Package.Operations.Count} final-state operation(s).\n\n{window.FilePath}{skipped}", "ArcGIS Playback");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not capture version changes: {ex.Message}", "ArcGIS Playback");
        }
    }
}
