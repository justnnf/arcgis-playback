using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Dialogs;
using NetworkChangePlaybackAddin.Services;

namespace NetworkChangePlaybackAddin.Buttons;

internal sealed class ClearPreviewButton : Button
{
    protected override async void OnClick()
    {
        await RecorderHost.Preview.ClearAsync();
        MessageBox.Show("Playback preview cleared from the map.", "ArcGIS Playback");
    }
}
