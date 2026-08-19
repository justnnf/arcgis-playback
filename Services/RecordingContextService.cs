using ArcGIS.Core.Data;
using ArcGIS.Desktop.Mapping;

namespace NetworkChangePlaybackAddin.Services;

internal static class RecordingContextService
{
    internal static RecordingContext Get()
    {
        var extentJson = MapView.Active?.Extent?.ToJson();
        var sourceLayer = MapView.Active?.Map?.GetLayersAsFlattenedList().OfType<FeatureLayer>().FirstOrDefault();
        if (sourceLayer is null) return new RecordingContext("SDE.DEFAULT", extentJson);

        using var table = sourceLayer.GetTable();
        if (table?.GetDatastore() is not Geodatabase geodatabase) return new RecordingContext("SDE.DEFAULT", extentJson);
        try
        {
            using var versionManager = geodatabase.GetVersionManager();
            using var version = versionManager.GetCurrentVersion();
            return new RecordingContext(version.GetName(), extentJson);
        }
        catch (InvalidOperationException)
        {
            return new RecordingContext("LOCAL (unversioned)", extentJson);
        }
    }
}

internal sealed record RecordingContext(string SourceVersion, string? ExtentJson);
