using ArcGIS.Core.Data;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Dialogs;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using NetworkChangePlaybackAddin.Dialogs;
using NetworkChangePlaybackAddin.Services;

namespace NetworkChangePlaybackAddin.Buttons;

internal sealed class StartRecordingButton : Button
{
    protected override void OnClick() => StartAsync();

    private async void StartAsync()
    {
        if (RecorderHost.Recorder.ActivePackage is not null)
        {
            MessageBox.Show("A recording is already active. Save it before starting another one.", "Change Playback");
            return;
        }

        var recordingContext = await QueuedTask.Run(RecordingContextService.Get);
        var window = new StartRecordingWindow(recordingContext.SourceVersion, recordingContext.ExtentJson) { Owner = System.Windows.Application.Current?.MainWindow };
        if (window.ShowDialog() != true || window.Metadata is null || window.FilePath is null) return;

        try
        {
            await RecorderHost.Capture.StartAsync();
            RecorderHost.Recorder.Start(window.Metadata, window.FilePath);
            await RecorderHost.Indicator.ShowAsync();
            MessageBox.Show($"Recording started. The package is being saved to:\n{window.FilePath}", "ArcGIS Playback");
        }
        catch (Exception ex)
        {
            await RecorderHost.Capture.StopAsync();
            MessageBox.Show($"Recording did not start: {ex.Message}", "ArcGIS Playback");
        }
    }
}
