using System.Text.Json.Nodes;
using ArcGIS.Core.Data;
using ArcGIS.Core.Events;
using ArcGIS.Desktop.Editing.Events;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using NetworkChangePlaybackAddin.Models;

namespace NetworkChangePlaybackAddin.Services;

/// <summary>Captures row-level Pro edits from the map that was active when recording started.</summary>
internal sealed class ProEditCapture
{
    private readonly PackageRecorder _recorder;
    private readonly List<(SubscriptionToken Create, SubscriptionToken Change, SubscriptionToken Delete)> _subscriptions = [];
    private readonly HashSet<string> _capturedRows = new(StringComparer.Ordinal);

    internal ProEditCapture(PackageRecorder recorder) => _recorder = recorder;

    internal async Task StartAsync()
    {
        await StopAsync();
        await QueuedTask.Run(() =>
        {
            _capturedRows.Clear();
            var map = MapView.Active?.Map ?? throw new InvalidOperationException("Open and activate a map before recording.");
            var tables = new List<Table>();
            tables.AddRange(map.GetLayersAsFlattenedList().OfType<FeatureLayer>().Select(layer => layer.GetTable()));
            tables.AddRange(map.StandaloneTables.Select(table => table.GetTable()));

            foreach (var table in tables)
            {
                _subscriptions.Add((
                    RowCreatedEvent.Subscribe(OnRowCreated, table, true),
                    RowChangedEvent.Subscribe(OnRowChanged, table, true),
                    RowDeletedEvent.Subscribe(OnRowDeleted, table, true)));
            }
        });
    }

    internal async Task StopAsync()
    {
        if (_subscriptions.Count == 0) return;
        await QueuedTask.Run(() =>
        {
            foreach (var subscription in _subscriptions)
            {
                RowCreatedEvent.Unsubscribe(subscription.Create);
                RowChangedEvent.Unsubscribe(subscription.Change);
                RowDeletedEvent.Unsubscribe(subscription.Delete);
            }
            _subscriptions.Clear();
        });
    }

    private void OnRowCreated(RowChangedEventArgs args) => Record(args, ChangeOperationType.AddFeature);
    private void OnRowChanged(RowChangedEventArgs args) => Record(args, ChangeOperationType.UpdateFeature);
    private void OnRowDeleted(RowChangedEventArgs args) => Record(args, ChangeOperationType.DeleteFeature);

    private void Record(RowChangedEventArgs args, ChangeOperationType type)
    {
        if (_recorder.ActivePackage is null) return;
        try
        {
            var row = args.Row;
            // Pro can raise several row notifications while one EditOperation builds a feature.
            // One package operation per feature per edit operation is the portable replay unit.
            var captureKey = $"{args.Guid:N}|{row.GetTable().GetName()}|{row.GetObjectID()}";
            if (!_capturedRows.Add(captureKey)) return;
            var attributes = Attributes(row);
            _recorder.Record(new ChangeOperation
            {
                Type = type,
                LayerName = row.GetTable().GetName(),
                SourceObjectId = row.GetObjectID(),
                SourceGlobalId = FieldValue(row, "GLOBALID"),
                FacilityId = FieldValue(row, "FACILITYID"),
                After = type == ChangeOperationType.DeleteFeature ? null : attributes,
                Before = type == ChangeOperationType.DeleteFeature ? attributes : null
            });
        }
        catch
        {
            // Editing must never be interrupted because recording an audit entry failed.
        }
    }

    private static string? FieldValue(Row row, string fieldName)
    {
        var field = row.GetTable().GetDefinition().GetFields()
            .FirstOrDefault(candidate => string.Equals(candidate.Name, fieldName, StringComparison.OrdinalIgnoreCase));
        if (field is null) return null;
        var value = row[field.Name];
        return value is null || value is DBNull ? null : value.ToString();
    }

    private static JsonObject Attributes(Row row)
    {
        var values = new JsonObject();
        foreach (var field in row.GetTable().GetDefinition().GetFields())
        {
            if (field.FieldType is FieldType.Blob or FieldType.Raster) continue;
            var value = row[field.Name];
            if (value is null || value is DBNull)
            {
                values[field.Name] = null;
                continue;
            }

            if (field.FieldType == FieldType.Geometry && row is Feature feature)
            {
                values[field.Name] = JsonNode.Parse(feature.GetShape().ToJson());
                continue;
            }

            values[field.Name] = JsonValue.Create(value.ToString());
        }
        return values;
    }
}
