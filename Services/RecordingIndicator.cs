using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;

namespace NetworkChangePlaybackAddin.Services;

internal sealed class RecordingIndicator
{
    private IDisposable? _extentGraphic;
    private MapViewOverlayControl? _label;
    private MapView? _mapView;

    internal async Task ShowAsync()
    {
        await HideAsync();
        var mapView = MapView.Active ?? throw new InvalidOperationException("Open and activate a map before recording.");
        _mapView = mapView;
        await QueuedTask.Run(() =>
        {
            var outline = SymbolFactory.Instance.ConstructStroke(ColorFactory.Instance.RedRGB, 3.0, SimpleLineStyle.Solid);
            var symbol = SymbolFactory.Instance.ConstructPolygonSymbol(null, outline).MakeSymbolReference();
            _extentGraphic = mapView.AddOverlay(PolygonBuilderEx.CreatePolygon(mapView.Extent), symbol, -1, 1);
        });

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

    internal async Task HideAsync()
    {
        if (_label is not null && _mapView is not null) _mapView.RemoveOverlayControl(_label);
        _label = null;
        _mapView = null;
        if (_extentGraphic is not null)
        {
            await QueuedTask.Run(() => _extentGraphic.Dispose());
            _extentGraphic = null;
        }
    }
}
