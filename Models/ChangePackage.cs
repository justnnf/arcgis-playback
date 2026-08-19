using System.Text.Json.Nodes;

namespace NetworkChangePlaybackAddin.Models;

public sealed class ChangePackage
{
    public const string Format = "fortisalberta.utility-network-change-package";
    public const int CurrentFormatVersion = 2;

    public string PackageFormat { get; init; } = Format;
    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public Guid PackageId { get; init; } = Guid.NewGuid();
    public PackageMetadata Metadata { get; init; } = new();
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSavedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<ChangeOperation> Operations { get; init; } = [];
}

public sealed class PackageMetadata
{
    public string Name { get; init; } = string.Empty;
    public string SourceEnvironment { get; init; } = "Pre-production";
    public string SourceBranchVersion { get; init; } = "SDE.DEFAULT";
    // ArcFM Session Manager session used to correlate this package with its edit session.
    public string SessionName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string RecordedBy { get; init; } = Environment.UserName;
}

public sealed record ChangeOperation
{
    public Guid OperationId { get; init; } = Guid.NewGuid();
    public int Sequence { get; init; }
    public DateTimeOffset RecordedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public ChangeOperationType Type { get; init; }
    public string? LayerName { get; init; }
    public string? SourceGlobalId { get; init; }
    // Stable only inside this package. It links later edits/associations to assets created
    // during the same recording after production assigns different GlobalIDs.
    public string? PackageFeatureId { get; init; }
    public string? FacilityId { get; init; }
    public long? SourceObjectId { get; init; }
    public JsonObject? Before { get; init; }
    public JsonObject? After { get; init; }
    public AssociationReference? Association { get; init; }
}

public enum ChangeOperationType { AddFeature, UpdateFeature, DeleteFeature, AddAssociation, DeleteAssociation }

public sealed class AssociationReference
{
    public string? SourceAssociationGlobalId { get; init; }
    public string AssociationType { get; init; } = string.Empty;
    public FeatureReference From { get; init; } = new();
    public FeatureReference To { get; init; } = new();
    public long? FromTerminalId { get; init; }
    public long? ToTerminalId { get; init; }
    public bool? IsContentVisible { get; init; }
    public double? PercentAlong { get; init; }
}

public sealed class FeatureReference
{
    public string? LayerName { get; init; }
    public string? SourceGlobalId { get; init; }
    public string? FacilityId { get; init; }
    public string? PackageFeatureId { get; init; }
    public int? AssetGroup { get; init; }
    public int? AssetType { get; init; }
}
