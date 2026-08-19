using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Mapping;
using NetworkChangePlaybackAddin.Models;

namespace NetworkChangePlaybackAddin.Services;

/// <summary>Temporary spatial graphics for a package; these never write target data.</summary>
internal sealed class PlaybackPreviewOverlay
{
    private readonly List<IDisposable> _graphics = [];
    private MapViewOverlayControl? _label;
    private MapView? _mapView;

    internal async Task ShowAsync(ChangePackage package)
    {
        await ClearAsync();
        var mapView = MapView.Active ?? throw new InvalidOperationException("Open and activate a map before previewing playback.");
        _mapView = mapView;
        var count = 0;
        foreach (var operation in package.Operations.Where(operation => operation.Type is ChangeOperationType.AddFeature or ChangeOperationType.UpdateFeature or ChangeOperationType.DeleteFeature))
        {
            var geometry = GeometryFromOperation(operation);
            if (geometry is null) continue;
            // The synchronous overlay API requires the map view's owning UI thread.
            // The asynchronous form marshals to that dispatcher for us, which is
            // important because ribbon button handlers are not guaranteed to run there.
            _graphics.Add(await mapView.AddOverlayAsync(geometry, SymbolFor(operation.Type, geometry).MakeSymbolReference()));
            count++;
        }
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _label = new MapViewOverlayControl(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(235, 31, 35, 40)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0, 122, 194)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(9, 5, 9, 5),
                Child = new TextBlock { Text = $"PREVIEW ACTIVE  •  {count} feature geometries\nBlue add/update  •  Red delete", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold }
            }, true, true, true, OverlayControlRelativePosition.TopLeft, .02, .08);
            mapView.AddOverlayControl(_label);
        });
    }

    internal async Task ClearAsync()
    {
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            foreach (var graphic in _graphics) graphic.Dispose();
            _graphics.Clear();
            if (_label is not null && _mapView is not null) _mapView.RemoveOverlayControl(_label);
            _label = null;
            _mapView = null;
        });
    }

    private static ArcGIS.Core.Geometry.Geometry? GeometryFromOperation(ChangeOperation operation)
    {
        var attributes = operation.After ?? operation.Before;
        if (attributes is null) return null;
        var geometry = attributes.Select(pair => pair.Value).OfType<JsonObject>()
            .FirstOrDefault(value => value["x"] is not null || value["paths"] is not null || value["rings"] is not null);
        if (geometry is null) return null;
        var json = geometry.ToJsonString();
        return geometry["x"] is not null ? MapPointBuilderEx.FromJson(json)
            : geometry["paths"] is not null ? PolylineBuilderEx.FromJson(json)
            : PolygonBuilderEx.FromJson(json);
    }

    private static CIMSymbol SymbolFor(ChangeOperationType type, ArcGIS.Core.Geometry.Geometry geometry)
    {
        var color = type switch
        {
            ChangeOperationType.DeleteFeature => ColorFactory.Instance.CreateRGBColor(211, 47, 47),
            _ => ColorFactory.Instance.CreateRGBColor(0, 122, 194)
        };
        return geometry switch
        {
            MapPoint => SymbolFactory.Instance.ConstructPointSymbol(color, 9, SimpleMarkerStyle.Circle),
            Polyline => SymbolFactory.Instance.ConstructLineSymbol(color, 3, SimpleLineStyle.Solid),
            Polygon => SymbolFactory.Instance.ConstructPolygonSymbol(color, SimpleFillStyle.Null, SymbolFactory.Instance.ConstructStroke(color, 3, SimpleLineStyle.Solid)),
            _ => SymbolFactory.Instance.ConstructPointSymbol(color, 9, SimpleMarkerStyle.Circle)
        };
    }
}
