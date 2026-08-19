using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Framework.Threading.Tasks;
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
        var preview = await QueuedTask.Run(() =>
        {
            var mapView = MapView.Active ?? throw new InvalidOperationException("Open and activate a map before previewing playback.");
            var graphics = package.Operations
                .Where(operation => operation.Type is ChangeOperationType.AddFeature or ChangeOperationType.UpdateFeature or ChangeOperationType.DeleteFeature)
                .Select(operation =>
                {
                    var geometry = GeometryFromOperation(operation);
                    return geometry is null ? null : new PreviewGraphic(geometry, SymbolFor(operation.Type, geometry).MakeSymbolReference());
                })
                .Where(graphic => graphic is not null)
                .Cast<PreviewGraphic>()
                .ToList();

            foreach (var graphic in graphics) _graphics.Add(mapView.AddOverlay(graphic.Geometry, graphic.Symbol));
            return new Preview(mapView, graphics.Count);
        });

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _mapView = preview.MapView;
            _label = new MapViewOverlayControl(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(235, 31, 35, 40)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0, 122, 194)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(9, 5, 9, 5),
                Child = new TextBlock { Text = $"PREVIEW ACTIVE  •  {preview.Count} feature geometries\nBlue add/update  •  Red delete", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold }
            }, true, true, true, OverlayControlRelativePosition.TopLeft, .02, .08);
            preview.MapView.AddOverlayControl(_label);
        });
    }

    internal async Task ClearAsync()
    {
        await QueuedTask.Run(() =>
        {
            foreach (var graphic in _graphics) graphic.Dispose();
            _graphics.Clear();
        });
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
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

    private sealed record PreviewGraphic(ArcGIS.Core.Geometry.Geometry Geometry, CIMSymbolReference Symbol);
    private sealed record Preview(MapView MapView, int Count);
}
