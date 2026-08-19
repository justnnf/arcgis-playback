using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Dialogs;
using NetworkChangePlaybackAddin.Services;

namespace NetworkChangePlaybackAddin.Buttons;

internal sealed class ClearPreviewButton : Button
{
    protected override void OnClick()
    {
        RecorderHost.Preview.Clear();
        MessageBox.Show("Playback preview cleared from the map.", "ArcGIS Playback");
    }
}
