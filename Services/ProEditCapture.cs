using System.Text.Json.Nodes;
using ArcGIS.Core.Data;
using ArcGIS.Core.Events;
using ArcGIS.Core.Data.UtilityNetwork;
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
    private readonly Dictionary<string, string> _packageFeatureIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AssociationReference> _associationSnapshot = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AssociationEndpoint> _associationEndpoints = new(StringComparer.Ordinal);
    private SubscriptionToken? _editCompletedSubscription;
    private UtilityNetworkLayer? _utilityNetworkLayer;

    internal ProEditCapture(PackageRecorder recorder) => _recorder = recorder;

    internal async Task StartAsync()
    {
        await StopAsync();
        await QueuedTask.Run(() =>
        {
            _capturedRows.Clear();
            _packageFeatureIds.Clear();
            var map = MapView.Active?.Map ?? throw new InvalidOperationException("Open and activate a map before recording.");
            _utilityNetworkLayer = map.GetLayersAsFlattenedList().OfType<UtilityNetworkLayer>().FirstOrDefault();
            var tables = new List<Table>();
            tables.AddRange(map.GetLayersAsFlattenedList().OfType<FeatureLayer>().Select(layer => layer.GetTable()));
            tables.AddRange(map.GetStandaloneTablesAsFlattenedList().Select(table => table.GetTable()));

            foreach (var table in tables)
            {
                _subscriptions.Add((
                    RowCreatedEvent.Subscribe(OnRowCreated, table, true),
                    RowChangedEvent.Subscribe(OnRowChanged, table, true),
                    RowDeletedEvent.Subscribe(OnRowDeleted, table, true)));
            }
            CaptureAssociationSnapshot(recordChanges: false);
            _editCompletedSubscription = EditCompletedEvent.Subscribe(OnEditCompleted, true);
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
            if (_editCompletedSubscription is not null) EditCompletedEvent.Unsubscribe(_editCompletedSubscription);
            _editCompletedSubscription = null;
            _associationSnapshot.Clear();
            _associationEndpoints.Clear();
            _utilityNetworkLayer = null;
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
            // Pro can raise duplicate notifications while one edit operation builds a row.
            // Keep one entry for each row/event type, but retain Create followed by Change
            // because that is meaningful when a new asset is immediately populated.
            var tableName = row.GetTable().GetName();
            var rowKey = $"{tableName}|{row.GetObjectID()}";
            var captureKey = $"{args.Guid:N}|{rowKey}|{type}";
            if (!_capturedRows.Add(captureKey)) return;
            if (type == ChangeOperationType.AddFeature && !_packageFeatureIds.ContainsKey(rowKey))
                _packageFeatureIds[rowKey] = $"package:{Guid.NewGuid():N}";
            if (type != ChangeOperationType.DeleteFeature)
                _associationEndpoints[rowKey] = new AssociationEndpoint(tableName, row.GetObjectID());
            var attributes = Attributes(row);
            _recorder.Record(new ChangeOperation
            {
                Type = type,
                LayerName = tableName,
                SourceObjectId = row.GetObjectID(),
                SourceGlobalId = FieldValue(row, "GLOBALID"),
                PackageFeatureId = _packageFeatureIds.GetValueOrDefault(rowKey),
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

    private Task OnEditCompleted(EditCompletedEventArgs args)
    {
        if (_recorder.ActivePackage is null) return Task.CompletedTask;
        return QueuedTask.Run(() => CaptureAssociationSnapshot(recordChanges: true));
    }

    // Associations are stored by the Utility Network, not as normal endpoint-row edits.
    // Query the authoritative associations for every edited network element. This includes
    // containment and nonspatial connectivity that may not be rendered as map graphics.
    private void CaptureAssociationSnapshot(bool recordChanges)
    {
        if (_utilityNetworkLayer is null || _associationEndpoints.Count == 0) return;
        using var utilityNetwork = _utilityNetworkLayer.GetUtilityNetwork();
        using var definition = utilityNetwork.GetDefinition();
        var current = new Dictionary<string, AssociationReference>(StringComparer.Ordinal);
        foreach (var endpoint in _associationEndpoints.Values)
        {
            NetworkSource networkSource;
            try { networkSource = definition.GetNetworkSource(endpoint.TableName); }
            catch { continue; } // Non-network rows do not participate in UN associations.
            using var table = utilityNetwork.GetTable(networkSource);
            using var cursor = table.Search(new QueryFilter { ObjectIDs = [endpoint.ObjectId] }, false);
            if (!cursor.MoveNext()) continue;
            using var row = cursor.Current;
            var element = utilityNetwork.CreateElement(row);
            foreach (var association in utilityNetwork.GetAssociations(element))
            {
                var reference = ToAssociationReference(utilityNetwork, association);
                var key = AssociationKey(association, reference);
                current[key] = reference;
                if (recordChanges && !_associationSnapshot.ContainsKey(key))
                    _recorder.Record(new ChangeOperation { Type = ChangeOperationType.AddAssociation, Association = reference });
            }
        }
        if (recordChanges)
        {
            foreach (var removed in _associationSnapshot.Where(pair => !current.ContainsKey(pair.Key)))
                _recorder.Record(new ChangeOperation { Type = ChangeOperationType.DeleteAssociation, Association = removed.Value });
        }
        _associationSnapshot.Clear();
        foreach (var pair in current) _associationSnapshot[pair.Key] = pair.Value;
    }

    private AssociationReference ToAssociationReference(UtilityNetwork utilityNetwork, Association association) => new()
    {
        SourceAssociationGlobalId = association.GlobalID.ToString(),
        AssociationType = association.Type.ToString(),
        From = ToFeatureReference(utilityNetwork, association.FromElement),
        To = ToFeatureReference(utilityNetwork, association.ToElement),
        FromTerminalId = association.FromElement.Terminal?.ID,
        ToTerminalId = association.ToElement.Terminal?.ID,
        IsContentVisible = association.Type == AssociationType.Containment ? association.IsContainmentVisible : null,
        PercentAlong = association.PercentAlong
    };

    private FeatureReference ToFeatureReference(UtilityNetwork utilityNetwork, Element element)
    {
        using var table = utilityNetwork.GetTable(element.NetworkSource);
        using var cursor = table.Search(new QueryFilter { ObjectIDs = [element.ObjectID] }, false);
        if (!cursor.MoveNext()) throw new InvalidOperationException($"Could not read association endpoint {element.NetworkSource.Name}/{element.ObjectID}.");
        using var row = cursor.Current;
        var rowKey = $"{table.GetName()}|{element.ObjectID}";
        return new FeatureReference
        {
            LayerName = table.GetName(),
            SourceGlobalId = element.GlobalID.ToString(),
            FacilityId = FieldValue(row, "FACILITYID"),
            PackageFeatureId = _packageFeatureIds.GetValueOrDefault(rowKey),
            AssetGroup = IntFieldValue(row, "ASSETGROUP"),
            AssetType = IntFieldValue(row, "ASSETTYPE")
        };
    }

    private static string AssociationKey(Association association, AssociationReference reference) =>
        string.IsNullOrWhiteSpace(reference.SourceAssociationGlobalId)
            ? $"{reference.AssociationType}|{reference.From.SourceGlobalId}|{reference.To.SourceGlobalId}|{reference.FromTerminalId}|{reference.ToTerminalId}|{reference.PercentAlong}"
            : reference.SourceAssociationGlobalId;

    private static string? FieldValue(Row row, string fieldName)
    {
        var field = row.GetTable().GetDefinition().GetFields()
            .FirstOrDefault(candidate => string.Equals(candidate.Name, fieldName, StringComparison.OrdinalIgnoreCase));
        if (field is null) return null;
        var value = row[field.Name];
        return value is null || value is DBNull ? null : value.ToString();
    }

    private static int? IntFieldValue(Row row, string fieldName)
    {
        var text = FieldValue(row, fieldName);
        return int.TryParse(text, out var value) ? value : null;
    }

    private sealed record AssociationEndpoint(string TableName, long ObjectId);

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
