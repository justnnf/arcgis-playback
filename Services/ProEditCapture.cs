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
    private static readonly TimeSpan AssociationIdleDelay = TimeSpan.FromSeconds(2);
    private readonly PackageRecorder _recorder;
    private readonly List<(SubscriptionToken Create, SubscriptionToken Change, SubscriptionToken Delete)> _subscriptions = [];
    private readonly HashSet<string> _capturedRows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _packageFeatureIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AssociationReference> _associationSnapshot = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AssociationEndpoint> _associationEndpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _networkSourceNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Table> _subscribedTables = [];
    private readonly object _associationScanGate = new();
    private SubscriptionToken? _editCompletedSubscription;
    private UtilityNetworkLayer? _utilityNetworkLayer;
    private CancellationTokenSource? _associationScanDelay;
    private bool _associationScanRunning;

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
            CacheNetworkSourceNames();
            _subscribedTables.AddRange(map.GetLayersAsFlattenedList().OfType<FeatureLayer>().Select(layer => layer.GetTable()));
            _subscribedTables.AddRange(map.GetStandaloneTablesAsFlattenedList().Select(table => table.GetTable()));

            foreach (var table in _subscribedTables)
            {
                _subscriptions.Add((
                    RowCreatedEvent.Subscribe(OnRowCreated, table, true),
                    RowChangedEvent.Subscribe(OnRowChanged, table, true),
                    RowDeletedEvent.Subscribe(OnRowDeleted, table, true)));
            }
            _editCompletedSubscription = EditCompletedEvent.Subscribe(OnEditCompleted, true);
        });
    }

    internal async Task StopAsync()
    {
        CancelAssociationScan();
        await QueuedTask.Run(() =>
        {
            // Perform one authoritative scan only after editing has stopped. Doing this
            // after every row edit can contend with placement tools and destabilize Pro.
            if (_recorder.ActivePackage is not null) CaptureAssociationSnapshot(recordChanges: true);
            foreach (var subscription in _subscriptions)
            {
                RowCreatedEvent.Unsubscribe(subscription.Create);
                RowChangedEvent.Unsubscribe(subscription.Change);
                RowDeletedEvent.Unsubscribe(subscription.Delete);
            }
            _subscriptions.Clear();
            foreach (var table in _subscribedTables) table.Dispose();
            _subscribedTables.Clear();
            if (_editCompletedSubscription is not null) EditCompletedEvent.Unsubscribe(_editCompletedSubscription);
            _editCompletedSubscription = null;
            _associationSnapshot.Clear();
            _associationEndpoints.Clear();
            _networkSourceNames.Clear();
            _utilityNetworkLayer = null;
        });
    }

    private void OnRowCreated(RowChangedEventArgs args) => Record(args, ChangeOperationType.AddFeature);
    private void OnRowChanged(RowChangedEventArgs args) => Record(args, ChangeOperationType.UpdateFeature);
    private void OnRowDeleted(RowChangedEventArgs args) => Record(args, ChangeOperationType.DeleteFeature);

    private void Record(RowChangedEventArgs args, ChangeOperationType type)
    {
        if (_recorder.ActivePackage is null) return;
        CancelAssociationScan();
        try
        {
            var row = args.Row;
            // Pro can raise duplicate notifications while one edit operation builds a row.
            // Keep one entry for each row/event type, but retain Create followed by Change
            // because that is meaningful when a new asset is immediately populated.
            using var table = row.GetTable();
            using var definition = table.GetDefinition();
            var fields = definition.GetFields();
            var tableName = CanonicalTableName(table.GetName());
            var rowKey = $"{tableName}|{row.GetObjectID()}";
            var captureKey = $"{args.Guid:N}|{rowKey}|{type}";
            if (!_capturedRows.Add(captureKey)) return;
            if (type == ChangeOperationType.AddFeature && !_packageFeatureIds.ContainsKey(rowKey))
                _packageFeatureIds[rowKey] = $"package:{Guid.NewGuid():N}";
            if (type != ChangeOperationType.DeleteFeature)
                _associationEndpoints[rowKey] = new AssociationEndpoint(tableName, row.GetObjectID());
            var attributes = Attributes(row, fields);
            _recorder.Record(new ChangeOperation
            {
                Type = type,
                LayerName = tableName,
                SourceObjectId = row.GetObjectID(),
                SourceGlobalId = FieldValue(row, fields, "GLOBALID"),
                PackageFeatureId = _packageFeatureIds.GetValueOrDefault(rowKey),
                FacilityId = FieldValue(row, fields, "FACILITYID"),
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
        ScheduleAssociationScan();
        return Task.CompletedTask;
    }

    private void ScheduleAssociationScan()
    {
        CancellationTokenSource delay;
        lock (_associationScanGate)
        {
            _associationScanDelay?.Cancel();
            _associationScanDelay?.Dispose();
            delay = _associationScanDelay = new CancellationTokenSource();
        }
        _ = ScanWhenEditingIsIdleAsync(delay.Token);
    }

    private async Task ScanWhenEditingIsIdleAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(AssociationIdleDelay, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested || _recorder.ActivePackage is null) return;
            await QueuedTask.Run(() =>
            {
                lock (_associationScanGate)
                {
                    if (cancellationToken.IsCancellationRequested || _associationScanRunning) return;
                    _associationScanRunning = true;
                }
                try { CaptureAssociationSnapshot(recordChanges: true); }
                catch { /* Association capture must never interrupt editing. */ }
                finally { lock (_associationScanGate) _associationScanRunning = false; }
            });
        }
        catch (OperationCanceledException) { }
        catch { /* Never surface a background capture error through the Pro event pump. */ }
    }

    private void CancelAssociationScan()
    {
        lock (_associationScanGate)
        {
            _associationScanDelay?.Cancel();
            _associationScanDelay?.Dispose();
            _associationScanDelay = null;
        }
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
        using var definition = table.GetDefinition();
        var fields = definition.GetFields();
        using var cursor = table.Search(new QueryFilter { ObjectIDs = [element.ObjectID] }, false);
        if (!cursor.MoveNext()) throw new InvalidOperationException($"Could not read association endpoint {element.NetworkSource.Name}/{element.ObjectID}.");
        using var row = cursor.Current;
        var tableName = CanonicalTableName(table.GetName());
        var rowKey = $"{tableName}|{element.ObjectID}";
        return new FeatureReference
        {
            LayerName = tableName,
            SourceGlobalId = element.GlobalID.ToString(),
            FacilityId = FieldValue(row, fields, "FACILITYID"),
            PackageFeatureId = _packageFeatureIds.GetValueOrDefault(rowKey),
            AssetGroup = IntFieldValue(row, fields, "ASSETGROUP"),
            AssetType = IntFieldValue(row, fields, "ASSETTYPE")
        };
    }

    // Feature-service maps can prefix a physical table with a portal/service label
    // (for example, "L2"). Packages must instead carry the UN source name, which is
    // stable across maps and is what playback uses to resolve subtype layers/tables.
    private void CacheNetworkSourceNames()
    {
        _networkSourceNames.Clear();
        if (_utilityNetworkLayer is null) return;
        try
        {
            using var utilityNetwork = _utilityNetworkLayer.GetUtilityNetwork();
            using var definition = utilityNetwork.GetDefinition();
            foreach (var source in definition.GetNetworkSources())
            {
                using var table = utilityNetwork.GetTable(source);
                _networkSourceNames[table.GetName()] = source.Name;
            }
        }
        catch
        {
            // Ordinary non-UN tables use their table name as before.
        }
    }

    private string CanonicalTableName(string tableName) =>
        _networkSourceNames.TryGetValue(tableName, out var sourceName) ? sourceName : tableName;

    private static string AssociationKey(Association association, AssociationReference reference) =>
        string.IsNullOrWhiteSpace(reference.SourceAssociationGlobalId)
            ? $"{reference.AssociationType}|{reference.From.SourceGlobalId}|{reference.To.SourceGlobalId}|{reference.FromTerminalId}|{reference.ToTerminalId}|{reference.PercentAlong}"
            : reference.SourceAssociationGlobalId;

    private static string? FieldValue(Row row, IReadOnlyList<Field> fields, string fieldName)
    {
        var field = fields
            .FirstOrDefault(candidate => string.Equals(candidate.Name, fieldName, StringComparison.OrdinalIgnoreCase));
        if (field is null) return null;
        var value = row[field.Name];
        return value is null || value is DBNull ? null : value.ToString();
    }

    private static int? IntFieldValue(Row row, IReadOnlyList<Field> fields, string fieldName)
    {
        var text = FieldValue(row, fields, fieldName);
        return int.TryParse(text, out var value) ? value : null;
    }

    private sealed record AssociationEndpoint(string TableName, long ObjectId);

    private static JsonObject Attributes(Row row, IReadOnlyList<Field> fields)
    {
        var values = new JsonObject();
        foreach (var field in fields)
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
                var shape = feature.GetShape();
                values[field.Name] = JsonNode.Parse(shape.ToJson());
                continue;
            }

            values[field.Name] = JsonValue.Create(value.ToString());
        }
        return values;
    }
}
