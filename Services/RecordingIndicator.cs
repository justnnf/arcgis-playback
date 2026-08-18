using ArcGIS.Desktop.Mapping;

namespace NetworkChangePlaybackAddin.Services;

internal sealed class RecordingIndicator
{
    private MapViewOverlayControl? _label;
    private MapView? _mapView;

    internal async Task ShowAsync()
    {
        await HideAsync();
        var mapView = MapView.Active ?? throw new InvalidOperationException("Open and activate a map before recording.");
        _mapView = mapView;

        var label = new System.Windows.Controls.Border
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(230, 180, 0, 0)),
            BorderBrush = System.Windows.Media.Brushes.White,
            BorderThickness = new System.Windows.Thickness(1),
            CornerRadius = new System.Windows.CornerRadius(4),
            Padding = new System.Windows.Thickness(10, 5, 10, 5),
            Child = new System.Windows.Controls.TextBlock
            {
                Text = "●  RECORDING",
                FontWeight = System.Windows.FontWeights.Bold,
                Foreground = System.Windows.Media.Brushes.White
            }
        };
        _label = new MapViewOverlayControl(label, true, true, true, OverlayControlRelativePosition.TopLeft, 0.02, 0.02);
        mapView.AddOverlayControl(_label);
    }

    internal Task HideAsync()
    {
        if (_label is not null && _mapView is not null) _mapView.RemoveOverlayControl(_label);
        _label = null;
        _mapView = null;
        return Task.CompletedTask;
    }
}
