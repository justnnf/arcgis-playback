using ArcGIS.Desktop.Framework.Contracts;
using NetworkChangePlaybackAddin.Views;

namespace NetworkChangePlaybackAddin.Buttons;

internal sealed class ShowPlaybackPaneButton : Button
{
    protected override void OnClick() => PlaybackWindow.ShowPlayback();
}
