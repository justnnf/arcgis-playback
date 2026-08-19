using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using ArcGIS.Core.Data;
using ArcGIS.Core.Data.Exceptions;
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
    internal event Action<PlaybackProgress>? ProgressChanged;

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

    internal async Task ZoomToRecordedExtentAsync(ChangePackage package)
    {
        if (string.IsNullOrWhiteSpace(package.Metadata.RecordedMapExtentJson)) return;
        try
        {
            var mapView = await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => MapView.Active);
            if (mapView is null) return;
            var extent = await QueuedTask.Run(() => EnvelopeBuilderEx.FromJson(package.Metadata.RecordedMapExtentJson));
            var zoomTask = await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => mapView.ZoomToAsync(extent));
            await zoomTask;
        }
        catch
        {
            // A target map may use an incompatible spatial reference. Playback remains valid.
        }
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
            Report(operation, "Applying");
            if (IsSystemManagedOutput(operation.LayerName))
            {
                result.AlreadySatisfied++;
                Report(operation, "Excluded", "System-managed Utility Network output is never replayed.");
                _nextOperationIndex++;
                continue;
            }
            // Version-capture 0.5.18 could classify a generic endpoint
            // connectivity association as midspan when the service supplied a
            // default PercentAlong of zero. Suppress that legacy false-positive.
            if (operation.Association?.AssociationType == "JunctionEdgeObjectConnectivityMidspan" && operation.Association.PercentAlong == 0)
            {
                result.AlreadySatisfied++;
                Report(operation, "Excluded", "A zero-percent midspan association is not replayed.");
                _nextOperationIndex++;
                continue;
            }
            if (operation.Type is ChangeOperationType.AddAssociation or ChangeOperationType.DeleteAssociation)
            {
                try
                {
                    if (ApplyAssociation(package, operation, map, _createdRows)) { result.Queued++; Report(operation, "Applied"); }
                    else { result.AlreadySatisfied++; Report(operation, "Already satisfied"); }
                }
                catch (Exception ex)
                {
                    Pause(result, operation, $"#{operation.Sequence}: association could not be applied: {ex.Message}");
                    Report(operation, "Paused", result.PausedIssue);
                    return result;
                }
                _nextOperationIndex++;
                continue;
            }
            var targetMember = ResolveTargetMember(map, operation, out var targetIssue);
            if (targetMember is null)
            {
                Pause(result, operation, $"#{operation.Sequence}: {targetIssue}");
                Report(operation, "Paused", result.PausedIssue);
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
                    if (token.ObjectID is long objectId)
                    {
                        var createdTarget = new TargetRow(targetMember, objectId);
                        ApplyFacilityId(package, operation, createdTarget);
                        if (!string.IsNullOrWhiteSpace(operation.PackageFeatureId))
                            _createdRows[operation.PackageFeatureId] = createdTarget;
                    }
                    result.Queued++;
                    Report(operation, "Applied");
                    _nextOperationIndex++;
                    continue;
                }

                var target = !string.IsNullOrWhiteSpace(operation.PackageFeatureId) && _createdRows.TryGetValue(operation.PackageFeatureId, out var created)
                    ? created
                    : FindExistingTarget(map, targetMember, operation);
                if (target is null)
                {
                    Pause(result, operation, string.IsNullOrWhiteSpace(operation.FacilityId)
                        ? $"#{operation.Sequence}: {operation.Type} needs FacilityID or a package-created feature reference."
                        : $"#{operation.Sequence}: no {operation.LayerName} with FacilityID '{operation.FacilityId}' was found.");
                    Report(operation, "Paused", result.PausedIssue);
                    return result;
                }
                var applyEdit = NewEdit(package, operation);
                if (operation.Type == ChangeOperationType.DeleteFeature)
                    applyEdit.Delete(target.Member, target.ObjectId);
                else
                    applyEdit.Modify(target.Member, target.ObjectId, EditableAttributes(target.Member, operation.After));
                Execute(applyEdit);
                if (operation.Type == ChangeOperationType.UpdateFeature)
                    ApplyFacilityId(package, operation, target);
                result.Queued++;
                Report(operation, "Applied");
                _nextOperationIndex++;
            }
            catch (Exception ex)
            {
                Pause(result, operation, $"#{operation.Sequence}: {ex.Message}");
                Report(operation, "Paused", result.PausedIssue);
                return result;
            }
        }

        result.Completed = true;
        ProgressChanged?.Invoke(new PlaybackProgress(null, "Completed", $"{result.Queued} applied; {result.AlreadySatisfied} already satisfied; {result.Skipped.Count} skipped."));
        return result;
    }

    private void Report(ChangeOperation operation, string state, string? detail = null) =>
        ProgressChanged?.Invoke(new PlaybackProgress(operation, state, detail));

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

    // Some feature services apply defaults or attribute rules after Create and can
    // silently replace a managed FacilityID supplied with that create. Write the
    // captured identity in a separate edit after the row exists so version-delta
    // packages preserve the same FacilityIDs as live-recorded packages.
    private static void ApplyFacilityId(ChangePackage package, ChangeOperation operation, TargetRow target)
    {
        if (string.IsNullOrWhiteSpace(operation.FacilityId)) return;
        using var table = GetTable(target.Member);
        var field = table.GetDefinition().GetFields().FirstOrDefault(candidate =>
            string.Equals(candidate.Name, "FACILITYID", StringComparison.OrdinalIgnoreCase));
        if (field is null) return;
        var facilityEdit = NewEdit(package, operation);
        facilityEdit.Modify(target.Member, target.ObjectId, new Dictionary<string, object>
        {
            [field.Name] = operation.FacilityId
        });
        Execute(facilityEdit);
    }

    private static TargetRow? FindExistingTarget(Map map, MapMember targetMember, ChangeOperation operation)
    {
        if (!string.IsNullOrWhiteSpace(operation.FacilityId) && FindObjectIdByFacilityId(targetMember, operation.FacilityId) is long objectId)
            return new TargetRow(targetMember, objectId);

        var reference = FeatureReferenceFromOperation(targetMember, operation);
        if (FindObjectIdByLocation(targetMember, reference) is long spatialObjectId)
            return new TargetRow(targetMember, spatialObjectId);

        var anchors = operation.AssociationAnchorFacilityIds ?? [];
        TargetRow? anchored = null;
        foreach (var facilityId in anchors)
        {
            foreach (var member in map.GetLayersAsFlattenedList().OfType<FeatureLayer>().Cast<MapMember>()
                .Concat(map.GetStandaloneTablesAsFlattenedList().Cast<MapMember>()))
            {
                long? anchorObjectId;
                try { anchorObjectId = FindObjectIdByFacilityId(member, facilityId); }
                catch (InvalidOperationException) { continue; }
                if (anchorObjectId is null) continue;
                if (FindAssociationAnchoredTarget(map, targetMember, reference, new TargetRow(member, anchorObjectId.Value)) is not long candidateObjectId) continue;
                if (anchored is not null && anchored.ObjectId != candidateObjectId) return null;
                anchored = new TargetRow(targetMember, candidateObjectId);
            }
        }
        return anchored;
    }

    private static FeatureReference FeatureReferenceFromOperation(MapMember targetMember, ChangeOperation operation)
    {
        string? locationJson = null;
        if (targetMember is FeatureLayer)
        {
            using var table = GetTable(targetMember);
            using var definition = table.GetDefinition();
            var geometryField = definition.GetFields().FirstOrDefault(field => field.FieldType == FieldType.Geometry);
            var attributes = operation.After ?? operation.Before;
            if (geometryField is not null && attributes?.TryGetPropertyValue(geometryField.Name, out var geometry) == true && geometry is not null)
                locationJson = geometry.ToJsonString();
        }
        return new FeatureReference
        {
            LayerName = operation.LayerName,
            FacilityId = operation.FacilityId,
            AssetGroup = int.TryParse(AttributeValue(operation, "ASSETGROUP"), out var group) ? group : null,
            AssetType = int.TryParse(AttributeValue(operation, "ASSETTYPE"), out var type) ? type : null,
            LocationJson = locationJson
        };
    }

    private sealed record TargetRow(MapMember Member, long ObjectId);

    // Returns false when the target already has the requested final state. This makes
    // packages safe when a template or a prior playback operation has already created it.
    private static bool ApplyAssociation(ChangePackage package, ChangeOperation operation, Map map, IReadOnlyDictionary<string, TargetRow> createdRows)
    {
        var association = operation.Association ?? throw new InvalidOperationException("The package operation has no association payload.");
        if (!Enum.TryParse<AssociationType>(association.AssociationType, true, out var type))
            throw new InvalidOperationException($"Unsupported association type '{association.AssociationType}'.");
        var from = ResolveAssociationEndpoint(map, association.From, createdRows);
        var to = ResolveAssociationEndpoint(map, association.To, createdRows);
        from ??= ResolveAssociationEndpoint(map, association.From, createdRows, to);
        to ??= ResolveAssociationEndpoint(map, association.To, createdRows, from);
        if (from is null) throw new InvalidOperationException("The from endpoint could not be resolved by package ID, FacilityID, association anchor, or a unique spatial match.");
        if (to is null) throw new InvalidOperationException("The to endpoint could not be resolved by package ID, FacilityID, association anchor, or a unique spatial match.");
        var description = AssociationDescription(type, new RowHandle(from.Member, from.ObjectId), new RowHandle(to.Member, to.ObjectId), association);
        var exists = AssociationExists(map, type, from, to, association);
        if (operation.Type == ChangeOperationType.AddAssociation && exists) return false;
        if (operation.Type == ChangeOperationType.DeleteAssociation && !exists) return false;
        var edit = NewEdit(package, operation);
        if (operation.Type == ChangeOperationType.AddAssociation) edit.Create(description);
        else edit.Delete(description);
        Execute(edit);
        return true;
    }

    private static TargetRow? ResolveAssociationEndpoint(Map map, FeatureReference reference, IReadOnlyDictionary<string, TargetRow> createdRows, TargetRow? related = null)
    {
        if (!string.IsNullOrWhiteSpace(reference.PackageFeatureId) && createdRows.TryGetValue(reference.PackageFeatureId, out var created)) return created;
        var attributes = new JsonObject();
        if (reference.AssetGroup is int assetGroup) attributes["ASSETGROUP"] = assetGroup;
        if (reference.AssetType is int assetType) attributes["ASSETTYPE"] = assetType;
        var operation = new ChangeOperation { LayerName = reference.LayerName, FacilityId = reference.FacilityId, After = attributes };
        var member = ResolveTargetMember(map, operation, out _);
        if (member is null) return null;
        if (!string.IsNullOrWhiteSpace(reference.FacilityId) && FindObjectIdByFacilityId(member, reference.FacilityId) is long objectId)
            return new TargetRow(member, objectId);
        if (related is not null && FindAssociationAnchoredTarget(map, member, reference, related) is long anchoredObjectId)
            return new TargetRow(member, anchoredObjectId);
        return FindObjectIdByLocation(member, reference) is long spatialObjectId ? new TargetRow(member, spatialObjectId) : null;
    }

    private static long? FindObjectIdByLocation(MapMember member, FeatureReference reference)
    {
        if (member is not FeatureLayer layer || string.IsNullOrWhiteSpace(reference.LocationJson)) return null;
        Geometry geometry;
        try { geometry = GeometryFromJson(reference.LocationJson, layer.ShapeType); }
        catch { return null; }
        using var table = layer.GetTable();
        using var definition = table.GetDefinition();
        var fields = definition.GetFields().ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);
        using var cursor = table.Search(new SpatialQueryFilter { FilterGeometry = geometry, SpatialRelationship = SpatialRelationship.Intersects }, false);
        long? match = null;
        while (cursor.MoveNext())
        {
            using var row = cursor.Current;
            if (!MatchesAssetSubtype(row, fields, reference)) continue;
            if (match is not null) return null;
            match = row.GetObjectID();
        }
        return match;
    }

    private static long? FindAssociationAnchoredTarget(Map map, MapMember member, FeatureReference reference, TargetRow related)
    {
        var networkLayer = map.GetLayersAsFlattenedList().OfType<UtilityNetworkLayer>().FirstOrDefault();
        if (networkLayer is null) return null;
        using var utilityNetwork = networkLayer.GetUtilityNetwork();
        var relatedElement = GetElement(utilityNetwork, related);
        if (relatedElement is null) return null;
        long? match = null;
        foreach (var association in utilityNetwork.GetAssociations(relatedElement))
        {
            var candidate = association.FromElement.GlobalID == relatedElement.GlobalID ? association.ToElement : association.FromElement;
            if (!IsSourceTable(map, member, candidate.NetworkSource.Name)) continue;
            if (reference.AssetGroup is int group && candidate.AssetGroup.Code != group) continue;
            if (reference.AssetType is int type && candidate.AssetType.Code != type) continue;
            if (match is not null) return null;
            match = candidate.ObjectID;
        }
        return match;
    }

    private static bool AssociationExists(Map map, AssociationType type, TargetRow from, TargetRow to, AssociationReference reference)
    {
        var networkLayer = map.GetLayersAsFlattenedList().OfType<UtilityNetworkLayer>().FirstOrDefault();
        if (networkLayer is null) return false;
        using var utilityNetwork = networkLayer.GetUtilityNetwork();
        var fromElement = GetElement(utilityNetwork, from);
        var toElement = GetElement(utilityNetwork, to);
        if (fromElement is null || toElement is null) return false;
        return utilityNetwork.GetAssociations(fromElement).Any(existing =>
            existing.Type == type && SameAssociationEndpoints(existing, fromElement, toElement, reference));
    }

    private static bool SameAssociationEndpoints(Association existing, Element from, Element to, AssociationReference reference)
    {
        var direct = existing.FromElement.GlobalID == from.GlobalID && existing.ToElement.GlobalID == to.GlobalID;
        var reversed = existing.FromElement.GlobalID == to.GlobalID && existing.ToElement.GlobalID == from.GlobalID;
        if (!direct && !reversed) return false;
        var expectedFromTerminal = direct ? reference.FromTerminalId : reference.ToTerminalId;
        var expectedToTerminal = direct ? reference.ToTerminalId : reference.FromTerminalId;
        if (existing.FromElement.Terminal?.ID != expectedFromTerminal || existing.ToElement.Terminal?.ID != expectedToTerminal) return false;
        if (existing.Type == AssociationType.Containment && existing.IsContainmentVisible != (reference.IsContentVisible ?? false)) return false;
        return existing.Type != AssociationType.JunctionEdgeObjectConnectivityMidspan || Math.Abs(existing.PercentAlong - (reference.PercentAlong ?? 0)) < .000001;
    }

    private static Element? GetElement(UtilityNetwork utilityNetwork, TargetRow target)
    {
        using var table = GetTable(target.Member);
        using var cursor = table.Search(new QueryFilter { ObjectIDs = [target.ObjectId] }, false);
        if (!cursor.MoveNext()) return null;
        using var row = cursor.Current;
        return utilityNetwork.CreateElement(row);
    }

    private static bool MatchesAssetSubtype(Row row, IReadOnlyDictionary<string, Field> fields, FeatureReference reference)
    {
        if (reference.AssetGroup is int group && (!fields.TryGetValue("ASSETGROUP", out var groupField) || Convert.ToInt32(row[groupField.Name], CultureInfo.InvariantCulture) != group)) return false;
        if (reference.AssetType is int type && (!fields.TryGetValue("ASSETTYPE", out var typeField) || Convert.ToInt32(row[typeField.Name], CultureInfo.InvariantCulture) != type)) return false;
        return true;
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

    private static bool IsSourceTable(Map map, MapMember mapMember, string? sourceName)
    {
        if (string.IsNullOrWhiteSpace(sourceName)) return false;
        if (string.Equals(mapMember.Name, sourceName, StringComparison.OrdinalIgnoreCase)) return true;
        using var table = GetTable(mapMember);
        var tableName = table.GetDefinition().GetName();
        if (string.Equals(tableName, sourceName, StringComparison.OrdinalIgnoreCase)) return true;
        return UtilityNetworkSourceTableNames(map, sourceName).Contains(tableName);
    }

    // Version-difference packages use the canonical Utility Network source name
    // (for example, ElectricDevice). A map's subtype children can expose that same
    // source as a service-prefixed physical table (for example, L0ElectricDevice).
    private static HashSet<string> UtilityNetworkSourceTableNames(Map map, string sourceName)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var networkLayer = map.GetLayersAsFlattenedList().OfType<UtilityNetworkLayer>().FirstOrDefault();
        if (networkLayer is null) return names;
        try
        {
            using var utilityNetwork = networkLayer.GetUtilityNetwork();
            using var definition = utilityNetwork.GetDefinition();
            var source = definition.GetNetworkSources()
                .FirstOrDefault(candidate => string.Equals(candidate.Name, sourceName, StringComparison.OrdinalIgnoreCase));
            if (source is null) return names;
            using var sourceTable = utilityNetwork.GetTable(source);
            names.Add(sourceTable.GetName());
            names.Add(sourceTable.GetDefinition().GetName());
        }
        catch (GeodatabaseException)
        {
            // The ordinary map-member identity checks above remain available.
        }
        return names;
    }

    // A subtype layer is a filtered view of one underlying feature class.  Selecting the
    // first matching view can create a feature in the wrong AssetGroup/AssetType, so use
    // the recorded subtype values to select the one view that permits the operation.
    private static MapMember? ResolveTargetMember(Map map, ChangeOperation operation, out string issue)
    {
        var candidates = map.GetLayersAsFlattenedList().OfType<FeatureLayer>().Cast<MapMember>()
            .Concat(map.GetStandaloneTablesAsFlattenedList().Cast<MapMember>())
            .Where(member => IsSourceTable(map, member, operation.LayerName)).ToList();
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
            if (!fields.TryGetValue(pair.Key, out var field)) continue;
            // FacilityID is the identity used by playback to locate source assets.
            // Copy it explicitly even where the layer metadata reports it as a
            // managed field; if the target truly forbids it, the edit will surface a
            // clear error instead of silently dropping the value.
            if (!field.IsEditable && !string.Equals(field.Name, "FACILITYID", StringComparison.OrdinalIgnoreCase)) continue;
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

    private static bool IsSystemManagedOutput(string? layerName) => !string.IsNullOrWhiteSpace(layerName) &&
        string.Concat(layerName.Where(char.IsLetterOrDigit)).EndsWith("ELECTRICSUBNETLINE", StringComparison.OrdinalIgnoreCase);

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
    internal int AlreadySatisfied { get; set; }
    internal List<string> Skipped { get; } = [];
    internal bool Completed { get; set; }
    internal bool Stopped { get; set; }
    internal ChangeOperation? PausedOperation { get; set; }
    internal string? PausedIssue { get; set; }
}

internal sealed record PlaybackProgress(ChangeOperation? Operation, string State, string? Detail);

internal enum PlaybackContinuation { Retry, Skip, Stop }
