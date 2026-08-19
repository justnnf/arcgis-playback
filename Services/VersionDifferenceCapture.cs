using System.Text.Json.Nodes;
using System.Net.Http;
using System.IO;
using ArcGIS.Core.Data;
using ArcGIS.Core.Data.Exceptions;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Core.Portal;
using ArcGIS.Desktop.Mapping;
using NetworkChangePlaybackAddin.Models;

namespace NetworkChangePlaybackAddin.Services;

/// <summary>
/// Builds a state-delta package from a named version and its DEFAULT ancestor.
/// This intentionally emits final row state, not the user's original edit sequence.
/// </summary>
internal sealed class VersionDifferenceCapture
{
    private const int QueryBatchSize = 200;
    private static readonly AsyncLocal<string?> ServiceToken = new();
    // Supporting versioned feature classes used by this network that are not Utility
    // Network topology sources, but must move with the captured network edits.
    private static readonly HashSet<string> SupplementalCaptureSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "FIBERSPLICE", "FIBEROPTIC", "PURCHASEAREA"
    };
    // This is a service-managed output of subnetwork operations, not a user edit
    // that can be reproduced safely in a playback package.
    private static readonly HashSet<string> ExcludedUtilityNetworkSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "ELECTRICSUBNETLINE"
    };
    internal CaptureResult Capture(Map map, PackageMetadata requestedMetadata)
    {
        var previousToken = ServiceToken.Value;
        try
        {
            return CaptureCore(map, requestedMetadata);
        }
        catch (NullReferenceException ex)
        {
            throw new InvalidOperationException("ArcGIS returned an incomplete layer or version object while reading the active map. Reconnect the map's versioned service and try again; no package was saved.", ex);
        }
        finally
        {
            ServiceToken.Value = previousToken;
        }
    }

    private CaptureResult CaptureCore(Map map, PackageMetadata requestedMetadata)
    {
        var skipped = new List<string>();
        var members = UtilityNetworkMembers(map, skipped);
        if (members.Count == 0) throw new InvalidOperationException("The active map has no feature layers or standalone tables to capture.");

        using var firstTable = GetTable(members[0]);
        if (firstTable.GetDatastore() is not Geodatabase geodatabase || !geodatabase.IsVersioningSupported())
            throw new InvalidOperationException("Capture Version Changes requires an enterprise geodatabase version.");

        using var versionManager = geodatabase.GetVersionManager()
            ?? throw new InvalidOperationException("The active enterprise geodatabase does not provide a version manager.");
        using var currentVersion = versionManager.GetCurrentVersion()
            ?? throw new InvalidOperationException("The active enterprise geodatabase did not provide the current version.");
        using var defaultVersion = versionManager.GetDefaultVersion()
            ?? throw new InvalidOperationException("The active enterprise geodatabase did not provide its default version.");
        var sourceVersion = currentVersion.GetName();
        var defaultVersionName = defaultVersion.GetName();
        if (string.Equals(sourceVersion, defaultVersionName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Activate a named version before capturing changes. DEFAULT has no version delta to capture.");

        var operations = new List<ChangeOperation>();
        var capturedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (versionManager.GetVersioningType() == VersionType.Branch)
        {
            // Branch-versioned maps are normally feature-service connections. Do not
            // call Version.Connect here: client-server connections can return no direct
            // DEFAULT geodatabase even though the REST Version Management API is valid.
            CaptureBranchServiceDelta(map, members, sourceVersion, ref defaultVersionName, operations, skipped);
        }
        else
        {
            using var defaultGeodatabase = defaultVersion.Connect()
                ?? throw new InvalidOperationException("Could not open DEFAULT for the active enterprise geodatabase version.");
            foreach (var member in members)
            {
                using var sourceTable = GetTable(member);
                var datasetName = sourceTable.GetName();
                if (!capturedSources.Add(datasetName)) continue; // Subtype layers can expose the same source table.

                try
                {
                    using var defaultTable = defaultGeodatabase.OpenDataset<Table>(datasetName);
                    CaptureRows(sourceTable, defaultTable, member.Name, DifferenceType.Insert, ChangeOperationType.AddFeature, operations);
                    CaptureRows(sourceTable, defaultTable, member.Name, DifferenceType.UpdateNoChange, ChangeOperationType.UpdateFeature, operations);
                    CaptureDeletedRows(sourceTable, defaultTable, member.Name, operations);
                }
                catch (NotSupportedException ex) { skipped.Add($"{member.Name}: {ex.Message}"); }
                catch (GeodatabaseException ex) { skipped.Add($"{member.Name}: {ex.Message}"); }
            }
        }

        // A direct enterprise connection can still report client-server limitations.
        if (operations.Count == 0 && skipped.Count > 0)
        {
            skipped.Clear();
            CaptureBranchServiceDelta(map, members, sourceVersion, ref defaultVersionName, operations, skipped);
        }

        if (operations.Count == 0 && skipped.Count > 0)
            throw new InvalidOperationException("No version changes could be read. " + string.Join(" ", skipped));

        var metadata = new PackageMetadata
        {
            Name = requestedMetadata.Name,
            SourceEnvironment = requestedMetadata.SourceEnvironment,
            SourceBranchVersion = sourceVersion,
            RecordedMapExtentJson = requestedMetadata.RecordedMapExtentJson,
            SessionName = requestedMetadata.SessionName,
            Description = requestedMetadata.Description,
            RecordedBy = requestedMetadata.RecordedBy,
            Origin = PackageOrigin.VersionDifference,
            ComparedToVersion = defaultVersionName,
            CapturedAtUtc = DateTimeOffset.UtcNow
        };
        var package = new ChangePackage { Metadata = metadata };
        // Associations must be removed before their endpoint features are deleted,
        // while new endpoint features must exist before their associations are made.
        // Version differences describe final state rather than edit order, so make
        // that dependency order explicit in the generated playback package.
        package.Operations.AddRange(operations.OrderBy(PlaybackOrder)
            .Select((operation, index) => operation with { Sequence = index + 1 }));
        return new CaptureResult(package, skipped);
    }

    private static int PlaybackOrder(ChangeOperation operation) => operation.Type switch
    {
        ChangeOperationType.AddFeature or ChangeOperationType.UpdateFeature => 0,
        ChangeOperationType.DeleteAssociation => 1,
        ChangeOperationType.AddAssociation => 2,
        ChangeOperationType.DeleteFeature => 3,
        _ => 4
    };

    private static IEnumerable<MapMember> MapMembers(Map map) => map.GetLayersAsFlattenedList()
        .OfType<FeatureLayer>().Cast<MapMember>()
        .Concat(map.GetStandaloneTablesAsFlattenedList().Cast<MapMember>());

    // A version capture is intentionally limited to actual Utility Network source
    // tables. Dirty areas, error layers, traces, and unrelated operational tables
    // can be present in the same map/service but must never become playback edits.
    private static List<MapMember> UtilityNetworkMembers(Map map, List<string> skipped)
    {
        var networkLayer = map.GetLayersAsFlattenedList().OfType<UtilityNetworkLayer>().FirstOrDefault()
            ?? throw new InvalidOperationException("Capture Version Changes requires an active Utility Network layer in the map.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var utilityNetwork = networkLayer.GetUtilityNetwork())
        using (var definition = utilityNetwork.GetDefinition())
        {
            foreach (var source in definition.GetNetworkSources())
            {
                names.Add(source.Name);
                // A utility-network definition can include sources not published in
                // the active map's service. They are not capture candidates, so do
                // not fail the whole capture merely because their table is absent.
                try
                {
                    using var table = utilityNetwork.GetTable(source);
                    names.Add(table.GetName());
                }
                catch (GeodatabaseException)
                {
                    // The source name above is still useful if a matching map member
                    // is available; otherwise it remains outside the capture scope.
                }
            }
        }

        var eligible = new List<MapMember>();
        foreach (var member in MapMembers(map))
        {
            try
            {
                using var table = GetTable(member);
                if (IsExcludedUtilityNetworkSource(member.Name) || IsExcludedUtilityNetworkSource(table.GetName()))
                    skipped.Add($"{member.Name}: excluded because it is a system-managed Utility Network output.");
                else if (names.Contains(table.GetName()) || IsSupplementalCaptureSource(member.Name) || IsSupplementalCaptureSource(table.GetName()))
                    eligible.Add(member);
                else skipped.Add($"{member.Name}: excluded because it is not a Utility Network source (for example, dirty areas and error layers are never captured).");
            }
            catch (Exception ex) when (ex is InvalidOperationException or GeodatabaseException)
            {
                skipped.Add($"{member.Name}: excluded because its table is unavailable ({ex.Message}).");
            }
        }
        return eligible;
    }

    private static bool IsSupplementalCaptureSource(string name) =>
        SupplementalCaptureSources.Contains(NormalizeName(name));

    private static bool IsExcludedUtilityNetworkSource(string name) =>
        ExcludedUtilityNetworkSources.Contains(NormalizeName(name));

    private static bool SameNormalizedName(string left, string right) =>
        string.Equals(NormalizeName(left), NormalizeName(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeName(string name) => string.Concat(name.Where(char.IsLetterOrDigit));

    // Subtype group child layers frequently have a subtype display name instead of
    // the service's Utility Network source name. Resolve them through the UN table
    // identity so a differences response such as "ElectricDevice" finds its map
    // source regardless of the subtype layer names shown in the Contents pane.
    private static Dictionary<string, MapMember> UtilityNetworkSourceMembers(Map map, IReadOnlyList<MapMember> members)
    {
        var result = new Dictionary<string, MapMember>(StringComparer.OrdinalIgnoreCase);
        var networkLayer = map.GetLayersAsFlattenedList().OfType<UtilityNetworkLayer>().FirstOrDefault();
        if (networkLayer is null) return result;
        using var utilityNetwork = networkLayer.GetUtilityNetwork();
        using var definition = utilityNetwork.GetDefinition();
        foreach (var source in definition.GetNetworkSources())
        {
            try
            {
                using var sourceTable = utilityNetwork.GetTable(source);
                var member = members.FirstOrDefault(candidate =>
                {
                    try
                    {
                        using var table = GetTable(candidate);
                        return string.Equals(table.GetName(), sourceTable.GetName(), StringComparison.OrdinalIgnoreCase);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or GeodatabaseException)
                    {
                        return false;
                    }
                });
                if (member is not null) result[source.Name] = member;
            }
            catch (GeodatabaseException)
            {
                // The source is not published in the active map/service.
            }
        }
        return result;
    }

    private static Table GetTable(MapMember member) => member switch
    {
        FeatureLayer layer => layer.GetTable() ?? throw new InvalidOperationException($"{layer.Name} does not currently expose a readable table. Remove the broken layer or reconnect it before capturing."),
        StandaloneTable table => table.GetTable() ?? throw new InvalidOperationException($"{table.Name} does not currently expose a readable table. Remove the broken table or reconnect it before capturing."),
        _ => throw new InvalidOperationException($"{member.Name} does not expose a table.")
    };

    private static void CaptureRows(Table source, Table baseline, string layerName, DifferenceType differenceType,
        ChangeOperationType operationType, List<ChangeOperation> operations)
    {
        using var cursor = source.Differences(baseline, differenceType, null);
        while (cursor.MoveNext())
        {
            using var row = cursor.Current;
            var attributes = Attributes(row);
            operations.Add(new ChangeOperation
            {
                Type = operationType,
                LayerName = layerName,
                SourceObjectId = row.GetObjectID(),
                SourceGlobalId = FieldValue(row, "GLOBALID"),
                FacilityId = FieldValue(row, "FACILITYID"),
                PackageFeatureId = operationType == ChangeOperationType.AddFeature ? $"package:{Guid.NewGuid():N}" : null,
                After = attributes
            });
        }
    }

    private static void CaptureDeletedRows(Table source, Table baseline, string layerName, List<ChangeOperation> operations)
    {
        using var cursor = source.Differences(baseline, DifferenceType.DeleteNoChange, null);
        while (cursor.MoveNext())
        {
            var objectId = cursor.ObjectID;
            using var rowCursor = baseline.Search(new QueryFilter { ObjectIDs = [objectId] }, false);
            if (!rowCursor.MoveNext()) continue;
            using var row = rowCursor.Current;
            operations.Add(new ChangeOperation
            {
                Type = ChangeOperationType.DeleteFeature,
                LayerName = layerName,
                SourceObjectId = objectId,
                SourceGlobalId = FieldValue(row, "GLOBALID"),
                FacilityId = FieldValue(row, "FACILITYID"),
                Before = Attributes(row)
            });
        }
    }

    private static void CaptureBranchServiceDelta(Map map, IReadOnlyList<MapMember> members, string sourceVersion, ref string defaultVersionName,
        List<ChangeOperation> operations, List<string> skipped)
    {
        var utilityNetworkMembers = UtilityNetworkSourceMembers(map, members);
        var serviceMembers = members.Select(ServiceMember.From).ToList();
        foreach (var member in serviceMembers.Where(item => item.ServiceUrl is null))
            skipped.Add($"{member.Member.Name}: not connected to a feature-service layer and was not captured.");
        serviceMembers = serviceMembers.Where(item => item.ServiceUrl is not null).ToList();
        if (serviceMembers.Count == 0)
            throw new InvalidOperationException("The active map is not connected to a version-enabled feature service.");

        var serviceUrl = serviceMembers[0].ServiceUrl!;
        if (serviceMembers.Any(item => !string.Equals(item.ServiceUrl, serviceUrl, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Capture Version Changes currently requires all captured layers to use one feature service.");

        var client = AuthenticatedServiceClient();
        var versionServiceUrl = serviceUrl.Replace("/FeatureServer", "/VersionManagementServer", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        var versionServiceInfo = GetJson(client, $"{versionServiceUrl}?f=json");
        var authoritativeDefaultVersion = versionServiceInfo["defaultVersionName"]?.ToString();
        if (!string.IsNullOrWhiteSpace(authoritativeDefaultVersion)) defaultVersionName = authoritativeDefaultVersion;
        var versions = GetJson(client, $"{versionServiceUrl}/versions?f=json");
        var versionName = sourceVersion;
        var versionGuid = versions["versions"]?.AsArray()
            .FirstOrDefault(item => string.Equals(item?["versionName"]?.ToString(), versionName, StringComparison.OrdinalIgnoreCase))?["versionGuid"]?.ToString();
        if (string.IsNullOrWhiteSpace(versionGuid))
            throw new InvalidOperationException($"The active version '{versionName}' was not returned by the Version Management service.");
        // The versions resource commonly returns {GUID}, while Version Management
        // operation URLs require the bare GUID path segment.
        versionGuid = versionGuid.Trim().Trim('{', '}');

        var serviceInfo = GetJson(client, $"{serviceUrl.TrimEnd('/')}?f=json");
        var layerNames = ServiceLayerNames(serviceInfo);
        var response = PostJson(client, $"{versionServiceUrl}/versions/{versionGuid}/differences",
            "f=json&resultType=objectIds&async=false");
        if (response["success"]?.GetValue<bool>() == false)
            throw new InvalidOperationException(response["error"]?["message"]?.ToString() ?? "The Version Management service rejected the differences request.");

        foreach (var difference in response["differences"]?.AsArray() ?? [])
        {
            var layerId = difference?["layerId"]?.GetValue<int>();
            if (layerId is null || !layerNames.TryGetValue(layerId.Value, out var sourceName))
            {
                skipped.Add($"A service layer in the differences response could not be matched to the active map.");
                continue;
            }
            var serviceMember = serviceMembers.FirstOrDefault(item => item.LayerId == layerId)
                ?? serviceMembers.FirstOrDefault(item => string.Equals(item.DatasetName, sourceName, StringComparison.OrdinalIgnoreCase))
                ?? serviceMembers.FirstOrDefault(item => SameNormalizedName(item.Member.Name, sourceName) || SameNormalizedName(item.DatasetName, sourceName))
                ?? (utilityNetworkMembers.TryGetValue(sourceName, out var utilityNetworkMember)
                    ? serviceMembers.FirstOrDefault(item => ReferenceEquals(item.Member, utilityNetworkMember))
                    : null);
            if (serviceMember is null)
            {
                skipped.Add($"{sourceName}: this changed service layer is not present in the active map.");
                continue;
            }
            using var source = GetTable(serviceMember.Member);
            // Use the service dataset name, never a user-editable map display name.
            CaptureObjectIds(source, sourceName, difference?["inserts"]?.AsArray(), ChangeOperationType.AddFeature, operations);
            CaptureObjectIds(source, sourceName, difference?["updates"]?.AsArray(), ChangeOperationType.UpdateFeature, operations);
            CaptureDeletedObjectIdsViaService(client, serviceUrl, layerId.Value, source, sourceName, defaultVersionName,
                difference?["deletes"]?.AsArray(), operations);
        }

        CaptureBranchAssociationDelta(map, client, serviceUrl, sourceVersion, defaultVersionName, serviceInfo, layerNames, operations);
    }

    private static string? FeatureServiceUrl(MapMember member)
    {
        using var table = GetTable(member);
        using var datastore = table.GetDatastore();
        var url = (datastore.GetConnector() as ServiceConnectionProperties)?.URL.ToString();
        if (string.IsNullOrWhiteSpace(url)) return null;

        // Depending on the map's provenance, the service connector can expose
        // either the FeatureServer root or a particular FeatureServer/<layerId>.
        // The version-management and service-metadata endpoints require the root.
        var marker = url.IndexOf("/FeatureServer", StringComparison.OrdinalIgnoreCase);
        return marker < 0 ? null : url[..(marker + "/FeatureServer".Length)];
    }

    private static Dictionary<int, string> ServiceLayerNames(JsonNode serviceInfo)
    {
        EnsureNoServiceError(serviceInfo, "reading feature-service metadata");
        var layers = serviceInfo["layers"]?.AsArray() ?? [];
        var tables = serviceInfo["tables"]?.AsArray() ?? [];
        return layers.Concat(tables)
            .Where(item => item?["id"] is not null && item?["name"] is not null)
            .ToDictionary(item => item!["id"]!.GetValue<int>(), item => item!["name"]!.ToString());
    }

    private static JsonNode GetJson(EsriHttpClient client, string url)
    {
        var response = client.Get(WithToken(url)).EnsureSuccessStatusCode();
        var document = JsonNode.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())
            ?? throw new InvalidDataException("The feature service returned an empty response.");
        EnsureNoServiceError(document, "reading the Version Management service");
        return document;
    }

    private static EsriHttpClient AuthenticatedServiceClient()
    {
        // EsriHttpClient normally appends portal credentials automatically. Version
        // Management Server can sit behind a different service endpoint, however,
        // and then needs the active Portal token supplied explicitly.
        var portal = ArcGISPortalManager.Current.GetActivePortal()
            ?? throw new InvalidOperationException("Sign in to the Portal that hosts this versioned service before capturing changes.");
        var token = portal.GetToken();
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Sign in to the Portal that hosts this versioned service before capturing changes.");
        ServiceToken.Value = token;
        return new EsriHttpClient { ShowDialogs = true };
    }

    private static JsonNode PostJson(EsriHttpClient client, string url, string body)
    {
        var token = ServiceToken.Value;
        if (!string.IsNullOrWhiteSpace(token)) body += $"&token={Uri.EscapeDataString(token)}";
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
        var response = client.Post(url, content).EnsureSuccessStatusCode();
        var document = JsonNode.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())
            ?? throw new InvalidDataException("The Version Management service returned an empty response.");
        EnsureNoServiceError(document, "requesting version differences");
        return document;
    }

    private static string WithToken(string url)
    {
        var token = ServiceToken.Value;
        if (string.IsNullOrWhiteSpace(token)) return url;
        return $"{url}{(url.Contains('?') ? "&" : "?")}token={Uri.EscapeDataString(token)}";
    }

    private static void EnsureNoServiceError(JsonNode document, string stage)
    {
        var error = document["error"];
        if (error is null) return;
        var message = error["message"]?.ToString() ?? "The service did not provide an error message.";
        var details = error["details"]?.AsArray().Select(item => item?.ToString()).Where(item => !string.IsNullOrWhiteSpace(item));
        throw new InvalidOperationException($"The service failed while {stage}: {message}" + (details?.Any() == true ? $" ({string.Join("; ", details!)})" : string.Empty));
    }

    private static void CaptureObjectIds(Table source, string layerName, JsonArray? ids, ChangeOperationType type,
        List<ChangeOperation> operations)
    {
        if (ids is null || ids.Count == 0) return;
        var objectIds = ids.Select(item => item!.GetValue<long>()).ToList();
        var found = new HashSet<long>();
        foreach (var batch in objectIds.Chunk(QueryBatchSize))
        {
            using var cursor = source.Search(new QueryFilter { ObjectIDs = batch.ToList() }, false);
            while (cursor.MoveNext())
            {
                using var row = cursor.Current;
                found.Add(row.GetObjectID());
                operations.Add(new ChangeOperation
                {
                    Type = type,
                    LayerName = layerName,
                    SourceObjectId = row.GetObjectID(),
                    SourceGlobalId = FieldValue(row, "GLOBALID"),
                    FacilityId = FieldValue(row, "FACILITYID"),
                    PackageFeatureId = type == ChangeOperationType.AddFeature ? $"package:{Guid.NewGuid():N}" : null,
                    After = Attributes(row)
                });
            }
        }
        EnsureAllRowsReturned(objectIds, found, layerName, type.ToString());
    }

    private static void CaptureDeletedObjectIds(Table baseline, string layerName, JsonArray? ids, List<ChangeOperation> operations)
    {
        if (ids is null || ids.Count == 0) return;
        var objectIds = ids.Select(item => item!.GetValue<long>()).ToList();
        using var cursor = baseline.Search(new QueryFilter { ObjectIDs = objectIds }, false);
        while (cursor.MoveNext())
        {
            using var row = cursor.Current;
            operations.Add(new ChangeOperation
            {
                Type = ChangeOperationType.DeleteFeature,
                LayerName = layerName,
                SourceObjectId = row.GetObjectID(),
                SourceGlobalId = FieldValue(row, "GLOBALID"),
                FacilityId = FieldValue(row, "FACILITYID"),
                Before = Attributes(row)
            });
        }
    }

    private static void CaptureDeletedObjectIdsViaService(EsriHttpClient client, string serviceUrl, int layerId, Table source,
        string layerName, string defaultVersionName, JsonArray? ids, List<ChangeOperation> operations)
    {
        if (ids is null || ids.Count == 0) return;
        var requestedIds = ids.Select(item => item!.GetValue<long>()).ToList();
        var found = new HashSet<long>();
        foreach (var batch in requestedIds.Chunk(QueryBatchSize))
        {
            var objectIds = string.Join(",", batch);
            var result = PostJson(client, $"{serviceUrl.TrimEnd('/')}/{layerId}/query",
                $"f=json&objectIds={Uri.EscapeDataString(objectIds)}&outFields=*&returnGeometry=true&gdbVersion={Uri.EscapeDataString(defaultVersionName)}");
            foreach (var feature in result["features"]?.AsArray() ?? [])
            {
                var attributes = RestAttributes(source, feature);
                var objectId = feature?["attributes"]?[ObjectIdField(source)]?.GetValue<long>();
                if (objectId is null) continue;
                found.Add(objectId.Value);
                operations.Add(new ChangeOperation
                {
                    Type = ChangeOperationType.DeleteFeature,
                    LayerName = layerName,
                    SourceObjectId = objectId,
                    SourceGlobalId = AttributeText(attributes, "GLOBALID"),
                    FacilityId = AttributeText(attributes, "FACILITYID"),
                    Before = attributes
                });
            }
        }
        EnsureAllRowsReturned(requestedIds, found, layerName, "deleted row");
    }

    private static void EnsureAllRowsReturned(IEnumerable<long> requested, HashSet<long> found, string layerName, string operation)
    {
        var missing = requested.Where(id => !found.Contains(id)).Take(10).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"{layerName}: the service reported {operation} object ID(s) that could not be read ({string.Join(", ", missing)}). Capture was stopped to avoid an incomplete playback package.");
    }

    // The differences endpoint does not include utility-network associations. The
    // associations system table is authoritative and, unlike an endpoint query,
    // also exposes association-only edits (where neither endpoint feature changed).
    private static void CaptureBranchAssociationDelta(Map map, EsriHttpClient client, string featureServiceUrl, string sourceVersion,
        string defaultVersionName, JsonNode serviceInfo, IReadOnlyDictionary<int, string> layerNames, List<ChangeOperation> operations)
    {
        var networkSources = UtilityNetworkSources(map);
        if (networkSources.Count == 0)
            throw new InvalidOperationException("The active Utility Network did not expose source IDs needed to capture association changes.");

        var utilityNetworkLayerId = serviceInfo["utilityNetworkLayerId"]?.GetValue<int?>()
            ?? throw new InvalidOperationException("The feature service did not expose its Utility Network layer ID, so association changes cannot be captured safely.");
        var utilityNetworkInfo = GetJson(client, $"{featureServiceUrl.TrimEnd('/')}/{utilityNetworkLayerId}?f=json");
        var associationsTableId = utilityNetworkInfo["systemLayers"]?["associationsTableId"]?.GetValue<int?>()
            ?? throw new InvalidOperationException("The Utility Network did not expose an associations system table, so association changes cannot be captured safely.");

        var current = QueryAssociationTable(client, featureServiceUrl, associationsTableId, sourceVersion);
        var baseline = QueryAssociationTable(client, featureServiceUrl, associationsTableId, defaultVersionName);
        // A changed association uses the same GlobalID. Represent it as a remove
        // followed by an add so terminal, visibility, and position changes replay.
        var added = current.Where(pair => !baseline.TryGetValue(pair.Key, out var old) || old != pair.Value).Select(pair => pair.Value).ToList();
        var removed = baseline.Where(pair => !current.TryGetValue(pair.Key, out var replacement) || replacement != pair.Value).Select(pair => pair.Value).ToList();
        if (added.Count == 0 && removed.Count == 0) return;

        var currentReferences = QueryAssociationReferences(client, featureServiceUrl, sourceVersion, added, networkSources, layerNames, operations);
        var baselineReferences = QueryAssociationReferences(client, featureServiceUrl, defaultVersionName, removed, networkSources, layerNames, operations);
        foreach (var association in removed)
            operations.Add(new ChangeOperation { Type = ChangeOperationType.DeleteAssociation, Association = ToAssociationReference(association, baselineReferences) });
        foreach (var association in added)
            operations.Add(new ChangeOperation { Type = ChangeOperationType.AddAssociation, Association = ToAssociationReference(association, currentReferences) });
    }

    private static Dictionary<string, ServiceAssociation> QueryAssociationTable(EsriHttpClient client, string serviceUrl, int tableId, string version)
    {
        var result = new Dictionary<string, ServiceAssociation>(StringComparer.OrdinalIgnoreCase);
        var tableUrl = $"{serviceUrl.TrimEnd('/')}/{tableId}/query";
        var idsResponse = PostJson(client, tableUrl,
            $"f=json&where=1%3D1&returnIdsOnly=true&gdbVersion={Uri.EscapeDataString(version)}");
        var objectIds = idsResponse["objectIds"]?.AsArray()
            .Select(node => node?.GetValue<long>() ?? throw new InvalidDataException("The associations table returned an invalid object ID."))
            .ToList() ?? [];
        foreach (var batch in objectIds.Chunk(QueryBatchSize))
        {
            var response = PostJson(client, tableUrl,
                $"f=json&objectIds={string.Join(",", batch)}&outFields=*&returnGeometry=false&gdbVersion={Uri.EscapeDataString(version)}");
            foreach (var feature in response["features"]?.AsArray() ?? [])
            {
                if (feature?["attributes"] is not JsonObject attributes)
                    throw new InvalidDataException("The associations table returned a row without attributes.");
                var association = ServiceAssociation.FromAttributes(attributes);
                result[association.GlobalId] = association;
            }
        }
        return result;
    }

    private static Dictionary<string, FeatureReference> QueryAssociationReferences(EsriHttpClient client, string serviceUrl, string version,
        IReadOnlyList<ServiceAssociation> associations, IReadOnlyDictionary<int, NetworkSourceInfo> sourceById,
        IReadOnlyDictionary<int, string> layerNames, IReadOnlyList<ChangeOperation> operations)
    {
        var endpoints = associations.SelectMany(association => new[]
        {
            new NetworkElement(association.FromNetworkSourceId, association.FromGlobalId),
            new NetworkElement(association.ToNetworkSourceId, association.ToGlobalId)
        }).Distinct().ToList();
        var references = new Dictionary<string, FeatureReference>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in endpoints.GroupBy(endpoint => endpoint.NetworkSourceId))
        {
            if (!sourceById.TryGetValue(group.Key, out var source))
                throw new InvalidOperationException($"The Utility Network did not identify source {group.Key}.");
            var layer = layerNames.FirstOrDefault(pair => string.Equals(pair.Value, source.Name, StringComparison.OrdinalIgnoreCase));
            if (layer.Equals(default(KeyValuePair<int, string>)))
                throw new InvalidOperationException($"The Utility Network source '{source.Name}' is not exposed by the feature service.");
            foreach (var batch in group.Select(endpoint => endpoint.GlobalId).Distinct(StringComparer.OrdinalIgnoreCase).Chunk(QueryBatchSize))
            {
                var where = $"GLOBALID IN ({string.Join(",", batch.Select(id => $"'{id.Replace("'", "''")}'"))})";
                var response = PostJson(client, $"{serviceUrl.TrimEnd('/')}/{layer.Key}/query",
                    $"f=json&where={Uri.EscapeDataString(where)}&outFields=GLOBALID,FACILITYID,ASSETGROUP,ASSETTYPE&returnGeometry=true&gdbVersion={Uri.EscapeDataString(version)}");
                foreach (var feature in response["features"]?.AsArray() ?? [])
                {
                    var attributes = feature?["attributes"] as JsonObject;
                    var globalId = AttributeText(attributes ?? [], "GLOBALID");
                    if (string.IsNullOrWhiteSpace(globalId)) continue;
                    var packageFeatureId = operations.FirstOrDefault(operation => operation.Type == ChangeOperationType.AddFeature &&
                        string.Equals(operation.SourceGlobalId, globalId, StringComparison.OrdinalIgnoreCase))?.PackageFeatureId;
                    references[$"{group.Key}|{globalId}"] = new FeatureReference
                    {
                        LayerName = source.Name,
                        SourceGlobalId = globalId,
                        FacilityId = AttributeText(attributes!, "FACILITYID"),
                        PackageFeatureId = packageFeatureId,
                        AssetGroup = AttributeInt(attributes!, "ASSETGROUP"),
                        AssetType = AttributeInt(attributes!, "ASSETTYPE"),
                        LocationJson = feature?["geometry"]?.ToJsonString()
                    };
                }
            }
        }
        var missing = endpoints.Where(endpoint => !references.ContainsKey($"{endpoint.NetworkSourceId}|{endpoint.GlobalId}")).FirstOrDefault();
        if (missing is not null) throw new InvalidOperationException($"Could not read association endpoint {missing.NetworkSourceId}/{missing.GlobalId} from version '{version}'.");
        return references;
    }

    private static AssociationReference ToAssociationReference(ServiceAssociation association, IReadOnlyDictionary<string, FeatureReference> references) => new()
    {
        SourceAssociationGlobalId = association.GlobalId,
        AssociationType = AssociationTypeName(association.Type),
        From = WithRelatedFacility(references[$"{association.FromNetworkSourceId}|{association.FromGlobalId}"], references[$"{association.ToNetworkSourceId}|{association.ToGlobalId}"].FacilityId),
        To = WithRelatedFacility(references[$"{association.ToNetworkSourceId}|{association.ToGlobalId}"], references[$"{association.FromNetworkSourceId}|{association.FromGlobalId}"].FacilityId),
        FromTerminalId = association.FromTerminalId,
        ToTerminalId = association.ToTerminalId,
        IsContentVisible = association.IsContentVisible,
        PercentAlong = association.PercentAlong
    };

    private static FeatureReference WithRelatedFacility(FeatureReference reference, string? relatedFacilityId) => new()
    {
        LayerName = reference.LayerName,
        SourceGlobalId = reference.SourceGlobalId,
        FacilityId = reference.FacilityId,
        PackageFeatureId = reference.PackageFeatureId,
        AssetGroup = reference.AssetGroup,
        AssetType = reference.AssetType,
        LocationJson = reference.LocationJson,
        RelatedFacilityId = relatedFacilityId
    };

    private static string AssociationTypeName(string type) => type.ToLowerInvariant() switch
    {
        "attachment" => "Attachment",
        "containment" => "Containment",
        "junctionjunctionconnectivity" => "JunctionJunctionConnectivity",
        "junctionedgefromconnectivity" => "JunctionEdgeFromConnectivity",
        "junctionedgetoconnectivity" => "JunctionEdgeToConnectivity",
        "junctionmidspanconnectivity" => "JunctionEdgeObjectConnectivityMidspan",
        _ => throw new InvalidOperationException($"Unsupported Utility Network association type '{type}'.")
    };

    private static Dictionary<int, NetworkSourceInfo> UtilityNetworkSources(Map map)
    {
        var networkLayer = map.GetLayersAsFlattenedList().OfType<UtilityNetworkLayer>().FirstOrDefault();
        if (networkLayer is null) return [];
        using var utilityNetwork = networkLayer.GetUtilityNetwork();
        using var definition = utilityNetwork.GetDefinition();
        return definition.GetNetworkSources()
            .Select(source => new NetworkSourceInfo(source.ID, source.Name))
            .ToDictionary(source => source.Id);
    }

    private static int? AttributeInt(JsonObject attributes, string fieldName) =>
        int.TryParse(AttributeText(attributes, fieldName), out var value) ? value : null;

    private static JsonObject RestAttributes(Table source, JsonNode? feature)
    {
        var values = feature?["attributes"]?.DeepClone() as JsonObject ?? [];
        if (feature?["geometry"] is null || source.GetDefinition() is not FeatureClassDefinition featureClassDefinition) return values;
        values[featureClassDefinition.GetShapeField()] = feature["geometry"]!.DeepClone();
        return values;
    }

    private static string ObjectIdField(Table table) => table.GetDefinition().GetObjectIDField();

    private static string? AttributeText(JsonObject attributes, string fieldName) => attributes
        .FirstOrDefault(item => string.Equals(item.Key, fieldName, StringComparison.OrdinalIgnoreCase)).Value?.ToString();

    private static string? FieldValue(Row row, string name)
    {
        using var definition = row.GetTable().GetDefinition();
        var field = definition.GetFields().FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (field is null) return null;
        var value = row[field.Name];
        return value is null or DBNull ? null : value.ToString();
    }

    private static JsonObject Attributes(Row row)
    {
        using var definition = row.GetTable().GetDefinition();
        var values = new JsonObject();
        foreach (var field in definition.GetFields())
        {
            if (field.FieldType is FieldType.Blob or FieldType.Raster) continue;
            var value = row[field.Name];
            if (value is null or DBNull) { values[field.Name] = null; continue; }
            if (field.FieldType == FieldType.Geometry && row is Feature feature)
                values[field.Name] = JsonNode.Parse(feature.GetShape().ToJson());
            else
                values[field.Name] = JsonValue.Create(value.ToString());
        }
        return values;
    }

    private sealed record ServiceMember(MapMember Member, string? ServiceUrl, int? LayerId, string DatasetName)
    {
        internal static ServiceMember From(MapMember member)
        {
            using var table = GetTable(member);
            using var datastore = table.GetDatastore();
            var url = (datastore.GetConnector() as ServiceConnectionProperties)?.URL.ToString();
            if (string.IsNullOrWhiteSpace(url)) return new(member, null, null, table.GetName());
            var marker = url.IndexOf("/FeatureServer", StringComparison.OrdinalIgnoreCase);
            if (marker < 0) return new(member, null, null, table.GetName());
            var root = url[..(marker + "/FeatureServer".Length)];
            var tail = url[(marker + "/FeatureServer".Length)..].Trim('/');
            return new(member, root, int.TryParse(tail.Split('/').FirstOrDefault(), out var layerId) ? layerId : null, table.GetName());
        }
    }

    private sealed record NetworkSourceInfo(int Id, string Name);
    private sealed record NetworkElement(int NetworkSourceId, string GlobalId);
    private sealed record ServiceAssociation(string GlobalId, int FromNetworkSourceId, string FromGlobalId, long? FromTerminalId,
        int ToNetworkSourceId, string ToGlobalId, long? ToTerminalId, string Type, bool? IsContentVisible, double? PercentAlong)
    {
        internal static ServiceAssociation FromAttributes(JsonObject attributes) => new(
            RequiredText(attributes, "globalid"),
            RequiredInt(attributes, "fromnetworksourceid"),
            RequiredText(attributes, "fromglobalid"),
            TerminalId(attributes, "fromterminalid"),
            RequiredInt(attributes, "tonetworksourceid"),
            RequiredText(attributes, "toglobalid"),
            TerminalId(attributes, "toterminalid"),
            RequiredText(attributes, "associationtype"),
            BooleanValue(attributes, "iscontentvisible"),
            DoubleValue(attributes, "percentalong"));

        private static string RequiredText(JsonObject attributes, string fieldName) => AttributeText(attributes, fieldName)
            ?? throw new InvalidDataException($"The associations table is missing {fieldName}.");

        private static int RequiredInt(JsonObject attributes, string fieldName) => int.TryParse(AttributeText(attributes, fieldName), out var value)
            ? value
            : throw new InvalidDataException($"The associations table has an invalid {fieldName}.");

        private static long? TerminalId(JsonObject attributes, string fieldName) => long.TryParse(AttributeText(attributes, fieldName), out var id) && id > -1
            ? id
            : null;

        private static bool? BooleanValue(JsonObject attributes, string fieldName)
        {
            var value = AttributeText(attributes, fieldName);
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (bool.TryParse(value, out var boolean)) return boolean;
            return long.TryParse(value, out var integer) ? integer != 0 : null;
        }

        private static double? DoubleValue(JsonObject attributes, string fieldName) => double.TryParse(AttributeText(attributes, fieldName), out var value)
            ? value
            : null;
    }
}

internal sealed record CaptureResult(ChangePackage Package, IReadOnlyList<string> SkippedSources);
