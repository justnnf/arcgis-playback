using System.Text.Json;
using System.IO;
using NetworkChangePlaybackAddin.Models;

namespace NetworkChangePlaybackAddin.Services;

public sealed class PackageRecorder
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object _gate = new();

    public ChangePackage? ActivePackage { get; private set; }
    public string? ActiveFilePath { get; private set; }
    public event Action<ChangeOperation>? OperationRecorded;

    public void Start(PackageMetadata metadata, string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        lock (_gate)
        {
            if (ActivePackage is not null) throw new InvalidOperationException("A recording is already active.");
            ActivePackage = new ChangePackage { Metadata = metadata };
            ActiveFilePath = filePath;
            SaveUnsafe(); // Persist metadata immediately so a started recording is never invisible.
        }
    }

    public void Record(ChangeOperation operation)
    {
        ChangeOperation savedOperation;
        lock (_gate)
        {
            var package = ActivePackage ?? throw new InvalidOperationException("No recording is active.");
            savedOperation = operation with { Sequence = package.Operations.Count + 1 };
            package.Operations.Add(savedOperation);
            SaveUnsafe();
        }
        OperationRecorded?.Invoke(savedOperation);
    }

    public string StopAndSave()
    {
        lock (_gate)
        {
            if (ActivePackage is null || ActiveFilePath is null) throw new InvalidOperationException("No recording is active.");
            SaveUnsafe();
            var path = ActiveFilePath;
            ActivePackage = null;
            ActiveFilePath = null;
            return path;
        }
    }

    public static ChangePackage Read(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<ChangePackage>(stream, JsonOptions)
            ?? throw new InvalidDataException("The selected file is not a change package.");
    }

    private void SaveUnsafe()
    {
        var package = ActivePackage!;
        package.LastSavedAtUtc = DateTimeOffset.UtcNow;
        var folder = Path.GetDirectoryName(ActiveFilePath!);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        var temporaryPath = ActiveFilePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(package, JsonOptions));
        File.Move(temporaryPath, ActiveFilePath!, overwrite: true);
    }
}
