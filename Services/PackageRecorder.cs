using System.Text.Json;
using System.IO;
using NetworkChangePlaybackAddin.Models;

namespace NetworkChangePlaybackAddin.Services;

public sealed class PackageRecorder
{
    private static readonly TimeSpan AutoSaveInterval = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private Timer? _autoSaveTimer;
    private bool _dirty;

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
            _dirty = true;
            _autoSaveTimer = new Timer(_ => _ = FlushAsync(), null, AutoSaveInterval, AutoSaveInterval);
        }
        SaveNow(); // Persist metadata once, outside the Pro edit event pipeline.
    }

    public void Record(ChangeOperation operation)
    {
        ChangeOperation savedOperation;
        lock (_gate)
        {
            var package = ActivePackage ?? throw new InvalidOperationException("No recording is active.");
            savedOperation = operation with { Sequence = package.Operations.Count + 1 };
            package.Operations.Add(savedOperation);
            _dirty = true;
        }
        OperationRecorded?.Invoke(savedOperation);
    }

    public string StopAndSave()
    {
        Timer? timer;
        string path;
        string contents;
        lock (_gate)
        {
            if (ActivePackage is null || ActiveFilePath is null) throw new InvalidOperationException("No recording is active.");
            timer = _autoSaveTimer;
            _autoSaveTimer = null;
            path = ActiveFilePath;
            contents = SerializeUnsafe();
            ActivePackage = null;
            ActiveFilePath = null;
            _dirty = false;
        }
        timer?.Dispose();
        _writeGate.Wait();
        try { Write(path, contents); }
        finally { _writeGate.Release(); }
        return path;
    }

    public static ChangePackage Read(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<ChangePackage>(stream, JsonOptions)
            ?? throw new InvalidDataException("The selected file is not a change package.");
    }

    private void SaveNow()
    {
        string? path;
        string? contents;
        lock (_gate)
        {
            if (ActivePackage is null || ActiveFilePath is null) return;
            path = ActiveFilePath;
            contents = SerializeUnsafe();
            _dirty = false;
        }
        _writeGate.Wait();
        try { Write(path, contents); }
        finally { _writeGate.Release(); }
    }

    private async Task FlushAsync()
    {
        string? path;
        string? contents;
        lock (_gate)
        {
            if (!_dirty || ActivePackage is null || ActiveFilePath is null) return;
            path = ActiveFilePath;
            contents = SerializeUnsafe();
            _dirty = false;
        }
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try { Write(path, contents); }
        catch
        {
            lock (_gate) { if (ActivePackage is not null && ActiveFilePath == path) _dirty = true; }
        }
        finally { _writeGate.Release(); }
    }

    private string SerializeUnsafe()
    {
        var package = ActivePackage!;
        package.LastSavedAtUtc = DateTimeOffset.UtcNow;
        return JsonSerializer.Serialize(package, JsonOptions);
    }

    private static void Write(string path, string contents)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, contents);
        File.Move(temporaryPath, path, overwrite: true);
    }
}
