using System.Globalization;
using System.Text.Json.Nodes;
using ArcGIS.Core.Data;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Editing;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using NetworkChangePlaybackAddin.Models;

namespace NetworkChangePlaybackAddin.Services;

internal sealed class PlaybackService
{
    internal async Task<PlaybackResult> PlayAsync(ChangePackage package)
    {
        return await QueuedTask.Run(() => Play(package));
    }

    private static PlaybackResult Play(ChangePackage package)
    {
        var map = MapView.Active?.Map ?? throw new InvalidOperationException("Activate the production map before playback.");
        var result = new PlaybackResult();
        var edit = new EditOperation { Name = $"Playback: {package.Metadata.Name}", SelectModifiedFeatures = true };

        foreach (var operation in Coalesce(package.Operations))
        {
            if (operation.Type is ChangeOperationType.AddAssociation or ChangeOperationType.DeleteAssociation)
            {
                result.Skipped.Add($"#{operation.Sequence}: association playback is not yet enabled.");
                continue;
            }
            var layer = map.GetLayersAsFlattenedList().OfType<FeatureLayer>()
                .FirstOrDefault(candidate => IsSourceFeatureClass(candidate, operation.LayerName));
            if (layer is null)
            {
                result.Skipped.Add($"#{operation.Sequence}: layer '{operation.LayerName}' is not in the active map.");
                continue;
            }

            try
            {
                if (operation.Type == ChangeOperationType.AddFeature)
                {
                    var attributes = EditableAttributes(layer, operation.After);
                    edit.Create(layer, attributes);
                    result.Queued++;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(operation.FacilityId))
                {
                    result.Skipped.Add($"#{operation.Sequence}: {operation.Type} needs FacilityID to locate the production feature.");
                    continue;
                }
                var target = FindByFacilityId(layer, operation.FacilityId);
                if (target is null)
                {
                    result.Skipped.Add($"#{operation.Sequence}: no {operation.LayerName} with FacilityID '{operation.FacilityId}' was found.");
                    continue;
                }
                using (target)
                {
                    if (operation.Type == ChangeOperationType.DeleteFeature)
                        edit.Delete(layer, target.GetObjectID());
                    else
                        edit.Modify(layer, target.GetObjectID(), EditableAttributes(layer, operation.After));
                    result.Queued++;
                }
            }
            catch (Exception ex)
            {
                result.Skipped.Add($"#{operation.Sequence}: {ex.Message}");
            }
        }

        if (result.Queued > 0 && !edit.Execute())
            throw new InvalidOperationException(edit.ErrorMessage ?? "ArcGIS Pro rejected the playback edit operation.");
        return result;
    }

    // Source GlobalID is only a temporary package correlation key; it is never used to match production data.
    private static IEnumerable<ChangeOperation> Coalesce(IEnumerable<ChangeOperation> operations)
    {
        foreach (var group in operations.OrderBy(operation => operation.Sequence).GroupBy(operation =>
                     string.IsNullOrWhiteSpace(operation.SourceGlobalId) ? $"operation:{operation.OperationId}" : $"source:{operation.SourceGlobalId}"))
        {
            var ordered = group.ToList();
            var last = ordered[^1];
            var createdInPackage = ordered.FirstOrDefault(operation => operation.Type == ChangeOperationType.AddFeature);
            if (createdInPackage is null)
            {
                yield return last;
                continue;
            }

            // A source GlobalID only joins consecutive source events inside this package.
            // The production player still ignores it completely when locating target data.
            if (last.Type == ChangeOperationType.DeleteFeature) continue; // add then delete: no net target change
            yield return last with
            {
                Type = ChangeOperationType.AddFeature,
                After = last.After ?? createdInPackage.After,
                FacilityId = last.FacilityId ?? createdInPackage.FacilityId
            };
        }
    }

    private static Row? FindByFacilityId(FeatureLayer layer, string facilityId)
    {
        using var table = layer.GetTable();
        var facilityField = table.GetDefinition().GetFields().FirstOrDefault(field =>
            string.Equals(field.Name, "facilityid", StringComparison.OrdinalIgnoreCase));
        if (facilityField is null) throw new InvalidOperationException($"Layer '{layer.Name}' has no FacilityID field.");
        var escaped = facilityId.Replace("'", "''");
        using var cursor = table.Search(new QueryFilter { WhereClause = $"{facilityField.Name} = '{escaped}'" }, false);
        if (!cursor.MoveNext()) return null;
        return cursor.Current;
    }

    private static bool IsSourceFeatureClass(FeatureLayer layer, string? sourceName)
    {
        if (string.IsNullOrWhiteSpace(sourceName)) return false;
        if (string.Equals(layer.Name, sourceName, StringComparison.OrdinalIgnoreCase)) return true;
        using var table = layer.GetTable();
        return string.Equals(table.GetDefinition().GetName(), sourceName, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, object> EditableAttributes(FeatureLayer layer, JsonObject? attributes)
    {
        if (attributes is null) throw new InvalidOperationException("The package operation has no feature attributes.");
        using var table = layer.GetTable();
        var fields = table.GetDefinition().GetFields().ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in attributes)
        {
            if (!fields.TryGetValue(pair.Key, out var field) || !field.IsEditable) continue;
            if (string.Equals(field.Name, "GLOBALID", StringComparison.OrdinalIgnoreCase)) continue;
            if (field.FieldType == FieldType.Geometry)
            {
                if (pair.Value is not null) result[field.Name] = GeometryFromJson(pair.Value.ToJsonString(), layer.ShapeType);
                continue;
            }
            result[field.Name] = ConvertValue(pair.Value, field.FieldType)!;
        }
        return result;
    }

    private static object? ConvertValue(JsonNode? value, FieldType type)
    {
        if (value is null) return null;
        var text = value.ToString();
        return type switch
        {
            FieldType.SmallInteger => short.Parse(text, CultureInfo.InvariantCulture),
            FieldType.Integer => int.Parse(text, CultureInfo.InvariantCulture),
            FieldType.BigInteger => long.Parse(text, CultureInfo.InvariantCulture),
            FieldType.Single => float.Parse(text, CultureInfo.InvariantCulture),
            FieldType.Double => double.Parse(text, CultureInfo.InvariantCulture),
            FieldType.Date => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal),
            _ => text
        };
    }

    private static Geometry GeometryFromJson(string json, esriGeometryType shapeType) => shapeType switch
    {
        esriGeometryType.esriGeometryPoint => MapPointBuilderEx.FromJson(json),
        esriGeometryType.esriGeometryPolyline => PolylineBuilderEx.FromJson(json),
        esriGeometryType.esriGeometryPolygon => PolygonBuilderEx.FromJson(json),
        esriGeometryType.esriGeometryMultipoint => MultipointBuilderEx.FromJson(json),
        _ => throw new InvalidOperationException("The target layer geometry type is not supported.")
    };
}

internal sealed class PlaybackResult
{
    internal int Queued { get; set; }
    internal List<string> Skipped { get; } = [];
}
