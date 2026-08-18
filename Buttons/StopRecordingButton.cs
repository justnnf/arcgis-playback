using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Dialogs;
using NetworkChangePlaybackAddin.Services;

namespace NetworkChangePlaybackAddin.Buttons;

internal sealed class StopRecordingButton : Button
{
    protected override void OnClick() => StopAsync();

    private async void StopAsync()
    {
        if (RecorderHost.Recorder.ActivePackage is null)
        {
            MessageBox.Show("There is no active recording.", "Change Playback");
            return;
        }

        try
        {
            await RecorderHost.Capture.StopAsync();
            var path = RecorderHost.Recorder.StopAndSave();
            await RecorderHost.Indicator.HideAsync();
            MessageBox.Show($"Recording saved.\n\n{path}", "ArcGIS Playback");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not save recording: {ex.Message}", "ArcGIS Playback");
        }
    }
}
