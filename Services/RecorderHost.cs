namespace NetworkChangePlaybackAddin.Services;

internal static class RecorderHost
{
    internal static PackageRecorder Recorder { get; } = new();
    internal static ProEditCapture Capture { get; } = new(Recorder);
    internal static RecordingIndicator Indicator { get; } = new();
}
