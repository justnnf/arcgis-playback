using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using ArcGIS.Core.Data;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Core.Data.UtilityNetwork;
using ArcGIS.Desktop.Editing;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using NetworkChangePlaybackAddin.Models;

namespace NetworkChangePlaybackAddin.Services;

internal sealed class PlaybackService
{
    private ChangePackage? _package;
    private readonly Dictionary<string, TargetRow> _createdRows = new(StringComparer.Ordinal);
    private PlaybackResult? _result;
    private int _nextOperationIndex;

    internal async Task<PlaybackResult> PlayAsync(ChangePackage package)
    {
        return await QueuedTask.Run(() =>
        {
            _package = package;
            _createdRows.Clear();
            _result = new PlaybackResult();
            _nextOperationIndex = 0;
            return RunUntilPause();
        });
    }

    internal async Task<PlaybackResult> ContinueAsync(PlaybackContinuation continuation)
    {
        return await QueuedTask.Run(() =>
        {
            if (_package is null || _result is null || _result.PausedOperation is null)
                throw new InvalidOperationException("There is no paused playback to continue.");
            if (continuation == PlaybackContinuation.Skip)
            {
                _result.Skipped.Add(_result.PausedIssue!);
                _nextOperationIndex++;
            }
            else if (continuation == PlaybackContinuation.Stop)
            {
                _result.Stopped = true;
                return _result;
            }
            _result.PausedOperation = null;
            _result.PausedIssue = null;
            return RunUntilPause();
        });
    }

    private PlaybackResult RunUntilPause()
    {
        var package = _package ?? throw new InvalidOperationException("No playback package is active.");
        var result = _result ?? throw new InvalidOperationException("No playback result is active.");
        var map = MapView.Active?.Map ?? throw new InvalidOperationException("Activate the production map before playback.");

        var operations = package.Operations.OrderBy(operation => operation.Sequence).ToList();
        while (_nextOperationIndex < operations.Count)
        {
            var operation = operations[_nextOperationIndex];
            if (operation.Type is ChangeOperationType.AddAssociation or ChangeOperationType.DeleteAssociation)
            {
                try
                {
                    ApplyAssociation(package, operation, map, _createdRows);
                    result.Queued++;
                }
                catch (Exception ex)
                {
                    Pause(result, operation, $"#{operation.Sequence}: association could not be applied: {ex.Message}");
                    return result;
                }
                _nextOperationIndex++;
                continue;
            }
            var targetMember = ResolveTargetMember(map, operation, out var targetIssue);
            if (targetMember is null)
            {
                Pause(result, operation, $"#{operation.Sequence}: {targetIssue}");
                return result;
            }

            try
            {
                if (operation.Type == ChangeOperationType.AddFeature)
                {
                    var attributes = EditableAttributes(targetMember, operation.After);
                    var addEdit = NewEdit(package, operation);
                    var token = addEdit.Create(targetMember, attributes);
                    Execute(addEdit);
                    if (!string.IsNullOrWhiteSpace(operation.PackageFeatureId) && token.ObjectID is long objectId)
                        _createdRows[operation.PackageFeatureId] = new TargetRow(targetMember, objectId);
                    result.Queued++;
                    _nextOperationIndex++;
                    continue;
                }

                var target = !string.IsNullOrWhiteSpace(operation.PackageFeatureId) && _createdRows.TryGetValue(operation.PackageFeatureId, out var created)
                    ? created
                    : FindExistingTarget(targetMember, operation);
                if (target is null)
                {
                    Pause(result, operation, string.IsNullOrWhiteSpace(operation.FacilityId)
                        ? $"#{operation.Sequence}: {operation.Type} needs FacilityID or a package-created feature reference."
                        : $"#{operation.Sequence}: no {operation.LayerName} with FacilityID '{operation.FacilityId}' was found.");
                    return result;
                }
                var applyEdit = NewEdit(package, operation);
                if (operation.Type == ChangeOperationType.DeleteFeature)
                    applyEdit.Delete(target.Member, target.ObjectId);
                else
                    applyEdit.Modify(target.Member, target.ObjectId, EditableAttributes(target.Member, operation.After));
                Execute(applyEdit);
                result.Queued++;
                _nextOperationIndex++;
            }
            catch (Exception ex)
            {
                Pause(result, operation, $"#{operation.Sequence}: {ex.Message}");
                return result;
            }
        }

        result.Completed = true;
        return result;
    }

    private static void Pause(PlaybackResult result, ChangeOperation operation, string issue)
    {
        result.PausedOperation = operation;
        result.PausedIssue = issue;
    }

    private static EditOperation NewEdit(ChangePackage package, ChangeOperation operation) => new()
    {
        Name = $"Playback {operation.Sequence}: {package.Metadata.Name}",
        SelectModifiedFeatures = true
    };

    private static void Execute(EditOperation edit)
    {
        if (!edit.Execute()) throw new InvalidOperationException(edit.ErrorMessage ?? "ArcGIS Pro rejected the playback edit operation.");
    }

    private static TargetRow? FindExistingTarget(MapMember targetMember, ChangeOperation operation) =>
        string.IsNullOrWhiteSpace(operation.FacilityId) ? null : FindObjectIdByFacilityId(targetMember, operation.FacilityId) is long objectId
            ? new TargetRow(targetMember, objectId) : null;

    private sealed record TargetRow(MapMember Member, long ObjectId);

    private static void ApplyAssociation(ChangePackage package, ChangeOperation operation, Map map, IReadOnlyDictionary<string, TargetRow> createdRows)
    {
        var association = operation.Association ?? throw new InvalidOperationException("The package operation has no association payload.");
        if (!Enum.TryParse<AssociationType>(association.AssociationType, true, out var type))
            throw new InvalidOperationException($"Unsupported association type '{association.AssociationType}'.");
        var from = ResolveAssociationEndpoint(map, association.From, createdRows)
            ?? throw new InvalidOperationException("The from endpoint could not be resolved by package ID or FacilityID.");
        var to = ResolveAssociationEndpoint(map, association.To, createdRows)
            ?? throw new InvalidOperationException("The to endpoint could not be resolved by package ID or FacilityID.");
        var description = AssociationDescription(type, new RowHandle(from.Member, from.ObjectId), new RowHandle(to.Member, to.ObjectId), association);
        var edit = NewEdit(package, operation);
        if (operation.Type == ChangeOperationType.AddAssociation) edit.Create(description);
        else edit.Delete(description);
        Execute(edit);
    }

    private static TargetRow? ResolveAssociationEndpoint(Map map, FeatureReference reference, IReadOnlyDictionary<string, TargetRow> createdRows)
    {
        if (!string.IsNullOrWhiteSpace(reference.PackageFeatureId) && createdRows.TryGetValue(reference.PackageFeatureId, out var created)) return created;
        var attributes = new JsonObject();
        if (reference.AssetGroup is int assetGroup) attributes["ASSETGROUP"] = assetGroup;
        if (reference.AssetType is int assetType) attributes["ASSETTYPE"] = assetType;
        var operation = new ChangeOperation { LayerName = reference.LayerName, FacilityId = reference.FacilityId, After = attributes };
        var member = ResolveTargetMember(map, operation, out _);
        return member is null || string.IsNullOrWhiteSpace(reference.FacilityId) ? null : FindObjectIdByFacilityId(member, reference.FacilityId) is long objectId
            ? new TargetRow(member, objectId) : null;
    }

    private static AssociationDescription AssociationDescription(AssociationType type, RowHandle from, RowHandle to, AssociationReference association)
    {
        if (type == AssociationType.Containment) return new AssociationDescription(type, from, to, association.IsContentVisible ?? false);
        if (type == AssociationType.JunctionEdgeObjectConnectivityMidspan)
            return new AssociationDescription(type, from, to, association.PercentAlong ?? throw new InvalidOperationException("Midspan association is missing PercentAlong."));
        if (association.FromTerminalId is long fromTerminal && association.ToTerminalId is long toTerminal)
            return new AssociationDescription(type, from, fromTerminal, to, toTerminal);
        if (association.FromTerminalId is long terminal1) return new AssociationDescription(type, from, terminal1, to);
        if (association.ToTerminalId is long terminal2) return new AssociationDescription(type, from, to, terminal2);
        return new AssociationDescription(type, from, to);
    }

    private static long? FindObjectIdByFacilityId(MapMember mapMember, string facilityId)
    {
        using var table = GetTable(mapMember);
        var facilityField = table.GetDefinition().GetFields().FirstOrDefault(field =>
            string.Equals(field.Name, "facilityid", StringComparison.OrdinalIgnoreCase));
        if (facilityField is null) throw new InvalidOperationException($"Target '{mapMember.Name}' has no FacilityID field.");
        var escaped = facilityId.Replace("'", "''");
        using var cursor = table.Search(new QueryFilter { WhereClause = $"{facilityField.Name} = '{escaped}'" }, false);
        return cursor.MoveNext() ? cursor.Current.GetObjectID() : null;
    }

    private static bool IsSourceTable(MapMember mapMember, string? sourceName)
    {
        if (string.IsNullOrWhiteSpace(sourceName)) return false;
        if (string.Equals(mapMember.Name, sourceName, StringComparison.OrdinalIgnoreCase)) return true;
        using var table = GetTable(mapMember);
        return string.Equals(table.GetDefinition().GetName(), sourceName, StringComparison.OrdinalIgnoreCase);
    }

    // A subtype layer is a filtered view of one underlying feature class.  Selecting the
    // first matching view can create a feature in the wrong AssetGroup/AssetType, so use
    // the recorded subtype values to select the one view that permits the operation.
    private static MapMember? ResolveTargetMember(Map map, ChangeOperation operation, out string issue)
    {
        var candidates = map.GetLayersAsFlattenedList().OfType<FeatureLayer>().Cast<MapMember>()
            .Concat(map.GetStandaloneTablesAsFlattenedList().Cast<MapMember>())
            .Where(member => IsSourceTable(member, operation.LayerName)).ToList();
        if (candidates.Count == 0)
        {
            issue = $"feature class or object table '{operation.LayerName}' is not represented in the active map.";
            return null;
        }

        var assetGroup = AttributeValue(operation, "ASSETGROUP");
        var assetType = AttributeValue(operation, "ASSETTYPE");
        var subtypeViews = candidates.Where(member => MatchesSubtypeView(member, assetGroup, assetType)).ToList();
        if (subtypeViews.Count == 1)
        {
            issue = string.Empty;
            return subtypeViews[0];
        }

        // An unfiltered base feature layer is safe when there is exactly one. It is also
        // useful for maps that mix a base layer with subtype layers.
        var baseViews = candidates.Where(member => string.IsNullOrWhiteSpace(DefinitionQuery(member))).ToList();
        if (baseViews.Count == 1)
        {
            issue = string.Empty;
            return baseViews[0];
        }
        if (candidates.Count == 1 && !IsNativeSubtypeMember(candidates[0]))
        {
            issue = string.Empty;
            return candidates[0];
        }

        var subtype = $"ASSETGROUP={assetGroup ?? "(missing)"}, ASSETTYPE={assetType ?? "(missing)"}";
        issue = subtypeViews.Count > 1
            ? $"more than one target subtype layer for '{operation.LayerName}' ({subtype}) matched; playback was not applied."
            : $"no target subtype layer for '{operation.LayerName}' ({subtype}) matched. Add the matching subtype layer or an unfiltered base layer to the active map.";
        return null;
    }

    private static string? AttributeValue(ChangeOperation operation, string fieldName)
    {
        var attributes = operation.After ?? operation.Before;
        return attributes is not null && attributes.TryGetPropertyValue(fieldName, out var value) && value is not null
            ? value.ToString()
            : null;
    }

    private static bool MatchesSubtypeView(MapMember mapMember, string? assetGroup, string? assetType)
    {
        // ArcGIS Pro subtype group children do not expose their subtype as a SQL
        // definition query. Their SubtypeValue is the AssetGroup code in a UN map.
        if (mapMember is FeatureLayer featureLayer && featureLayer.IsSubtypeLayer)
            return assetGroup is not null && string.Equals(featureLayer.SubtypeValue.ToString(), assetGroup, StringComparison.OrdinalIgnoreCase);
        if (mapMember is StandaloneTable standaloneTable && standaloneTable.IsSubtypeTable)
            return assetGroup is not null && string.Equals(standaloneTable.SubtypeValue.ToString(), assetGroup, StringComparison.OrdinalIgnoreCase);

        var query = DefinitionQuery(mapMember);
        var constrainsGroup = HasFieldConstraint(query, "ASSETGROUP");
        var constrainsType = HasFieldConstraint(query, "ASSETTYPE");
        if (!constrainsGroup && !constrainsType) return false;
        return (!constrainsGroup || (assetGroup is not null && QueryAllowsValue(query, "ASSETGROUP", assetGroup)))
            && (!constrainsType || (assetType is not null && QueryAllowsValue(query, "ASSETTYPE", assetType)));
    }

    private static string DefinitionQuery(MapMember mapMember) => mapMember switch
    {
        FeatureLayer featureLayer when !featureLayer.IsSubtypeLayer => featureLayer.DefinitionQuery,
        StandaloneTable standaloneTable when !standaloneTable.IsSubtypeTable => standaloneTable.DefinitionQuery,
        _ => string.Empty
    };

    private static bool IsNativeSubtypeMember(MapMember mapMember) => mapMember is FeatureLayer featureLayer && featureLayer.IsSubtypeLayer
        || mapMember is StandaloneTable standaloneTable && standaloneTable.IsSubtypeTable;

    private static Table GetTable(MapMember mapMember) => mapMember switch
    {
        FeatureLayer featureLayer => featureLayer.GetTable(),
        StandaloneTable standaloneTable => standaloneTable.GetTable(),
        _ => throw new InvalidOperationException($"Map member '{mapMember.Name}' does not provide a table.")
    };

    private static bool HasFieldConstraint(string query, string fieldName) =>
        !string.IsNullOrWhiteSpace(query) && Regex.IsMatch(query, $@"(?i)\b{Regex.Escape(fieldName)}\b\s*(=|IN\b)");

    private static bool QueryAllowsValue(string query, string fieldName, string value)
    {
        var field = Regex.Escape(fieldName);
        var escapedValue = Regex.Escape(value);
        if (Regex.IsMatch(query, $@"(?i)\b{field}\b\s*=\s*'?{escapedValue}'?\b")) return true;
        foreach (Match match in Regex.Matches(query, $@"(?i)\b{field}\b\s+IN\s*\(([^)]*)\)"))
        {
            if (Regex.IsMatch(match.Groups[1].Value, $@"(?<![\w.])'?{escapedValue}'?(?![\w.])", RegexOptions.CultureInvariant)) return true;
        }
        return false;
    }

    private static Dictionary<string, object> EditableAttributes(MapMember mapMember, JsonObject? attributes)
    {
        if (attributes is null) throw new InvalidOperationException("The package operation has no feature attributes.");
        using var table = GetTable(mapMember);
        var fields = table.GetDefinition().GetFields().ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in attributes)
        {
            if (!fields.TryGetValue(pair.Key, out var field) || !field.IsEditable) continue;
            if (string.Equals(field.Name, "GLOBALID", StringComparison.OrdinalIgnoreCase)) continue;
            if (field.FieldType == FieldType.Geometry)
            {
                if (pair.Value is not null && mapMember is FeatureLayer layer)
                    result[field.Name] = GeometryFromJson(pair.Value.ToJsonString(), layer.ShapeType);
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
    internal bool Completed { get; set; }
    internal bool Stopped { get; set; }
    internal ChangeOperation? PausedOperation { get; set; }
    internal string? PausedIssue { get; set; }
}

internal enum PlaybackContinuation { Retry, Skip, Stop }
