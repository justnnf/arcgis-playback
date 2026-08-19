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

        var sourceVersion = await QueuedTask.Run(GetActiveBranchVersion);
        var window = new StartRecordingWindow(sourceVersion) { Owner = System.Windows.Application.Current?.MainWindow };
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

    private static string GetActiveBranchVersion()
    {
        var sourceLayer = MapView.Active?.Map?.GetLayersAsFlattenedList().OfType<FeatureLayer>().FirstOrDefault();
        if (sourceLayer is null) return "SDE.DEFAULT";

        using var table = sourceLayer.GetTable();
        if (table?.GetDatastore() is not Geodatabase geodatabase) return "SDE.DEFAULT";
        using var versionManager = geodatabase.GetVersionManager();
        using var version = versionManager.GetCurrentVersion();
        return version.GetName();
    }
}
