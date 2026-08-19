using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ArcGIS.Desktop.Mapping;
using NetworkChangePlaybackAddin.Models;

namespace NetworkChangePlaybackAddin.Services;

/// <summary>Non-editing, screen-space sketch of the package's recorded point and line geometry.</summary>
internal sealed class PlaybackPreviewOverlay
{
    private MapViewOverlayControl? _overlay;
    private MapView? _mapView;

    internal Task ShowAsync(ChangePackage package)
    {
        Clear();
        var mapView = MapView.Active ?? throw new InvalidOperationException("Open and activate a map before previewing playback.");
        var shapes = package.Operations.Where(operation => operation.Type is ChangeOperationType.AddFeature or ChangeOperationType.UpdateFeature or ChangeOperationType.DeleteFeature)
            .SelectMany(ToShapes).ToList();
        var canvas = new Canvas { Width = 245, Height = 180, Background = new SolidColorBrush(Color.FromArgb(235, 31, 35, 40)) };
        canvas.Children.Add(new TextBlock { Text = $"PREVIEW  •  {shapes.Count} feature geometries", Foreground = Brushes.White, FontWeight = FontWeights.Bold, Margin = new Thickness(10, 8, 0, 0) });
        Draw(canvas, shapes);
        var border = new Border { Child = canvas, BorderBrush = new SolidColorBrush(Color.FromRgb(0, 122, 194)), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(5) };
        _overlay = new MapViewOverlayControl(border, true, true, true, OverlayControlRelativePosition.TopRight, .02, .02);
        _mapView = mapView;
        mapView.AddOverlayControl(_overlay);
        return Task.CompletedTask;
    }

    internal void Clear()
    {
        if (_overlay is not null && _mapView is not null) _mapView.RemoveOverlayControl(_overlay);
        _overlay = null;
        _mapView = null;
    }

    private static IEnumerable<PreviewShape> ToShapes(ChangeOperation operation)
    {
        var attributes = operation.After ?? operation.Before;
        if (attributes is null) yield break;
        foreach (var node in attributes.Select(pair => pair.Value).Where(node => node is JsonObject))
        {
            var points = ExtractPoints(node!).ToList();
            if (points.Count == 0) continue;
            yield return new PreviewShape(points, operation.Type);
        }
    }

    private static IEnumerable<Point> ExtractPoints(JsonNode node)
    {
        if (node is JsonObject obj && obj["x"] is not null && obj["y"] is not null &&
            double.TryParse(obj["x"]!.ToString(), out var x) && double.TryParse(obj["y"]!.ToString(), out var y))
        {
            yield return new Point(x, y);
            yield break;
        }
        if (node is JsonArray array)
        {
            if (array.Count >= 2 && double.TryParse(array[0]?.ToString(), out x) && double.TryParse(array[1]?.ToString(), out y))
            {
                yield return new Point(x, y);
                yield break;
            }
            foreach (var child in array.Where(child => child is not null))
                foreach (var point in ExtractPoints(child!)) yield return point;
        }
        else if (node is JsonObject objectNode)
        {
            foreach (var child in objectNode.Where(pair => pair.Value is not null).Select(pair => pair.Value!))
                foreach (var point in ExtractPoints(child)) yield return point;
        }
    }

    private static void Draw(Canvas canvas, IReadOnlyList<PreviewShape> shapes)
    {
        var points = shapes.SelectMany(shape => shape.Points).ToList();
        if (points.Count == 0) return;
        var minX = points.Min(point => point.X); var maxX = points.Max(point => point.X);
        var minY = points.Min(point => point.Y); var maxY = points.Max(point => point.Y);
        var width = Math.Max(maxX - minX, 1); var height = Math.Max(maxY - minY, 1);
        Point Scale(Point point) => new(14 + (point.X - minX) / width * 215, 38 + (maxY - point.Y) / height * 126);
        foreach (var shape in shapes)
        {
            var color = shape.Operation switch { ChangeOperationType.DeleteFeature => Colors.IndianRed, ChangeOperationType.UpdateFeature => Colors.Gold, _ => Colors.DeepSkyBlue };
            var scaled = shape.Points.Select(Scale).ToList();
            if (scaled.Count > 1)
            {
                var polyline = new Polyline { Stroke = new SolidColorBrush(color), StrokeThickness = 2.5 };
                foreach (var point in scaled) polyline.Points.Add(point);
                canvas.Children.Add(polyline);
            }
            foreach (var point in scaled)
            {
                var marker = new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(color), Stroke = Brushes.White, StrokeThickness = 1 };
                Canvas.SetLeft(marker, point.X - 3.5); Canvas.SetTop(marker, point.Y - 3.5); canvas.Children.Add(marker);
            }
        }
    }

    private sealed record PreviewShape(IReadOnlyList<Point> Points, ChangeOperationType Operation);
}
