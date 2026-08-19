using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using ArcGIS.Desktop.Framework;
using NetworkChangePlaybackAddin.Models;
using NetworkChangePlaybackAddin.Services;

namespace NetworkChangePlaybackAddin.Views;

public partial class PlaybackWindow : Window
{
    private static PlaybackWindow? _instance;
    private readonly PlaybackService _playback = new();
    private ChangePackage? _openedPackage;

    public ObservableCollection<ChangeOperation> RecordedOperations { get; } = [];
    public ObservableCollection<ChangeOperation> PlaybackOperations { get; } = [];

    public PlaybackWindow()
    {
        InitializeComponent();
        RecordingGrid.ItemsSource = RecordedOperations;
        PlaybackGrid.ItemsSource = PlaybackOperations;
        ApplyProTheme();
        FrameworkApplication.Current.Activated += OnApplicationActivated;
        Closed += (_, _) =>
        {
            FrameworkApplication.Current.Activated -= OnApplicationActivated;
            RecorderHost.Recorder.OperationRecorded -= OnOperationRecorded;
            _instance = null;
        };
        RecorderHost.Recorder.OperationRecorded += OnOperationRecorded;
    }

    internal static void ShowPlayback()
    {
        if (_instance is { IsVisible: true }) { _instance.Activate(); return; }
        _instance = new PlaybackWindow();
        _instance.Show();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PackageNameBox.Text))
        {
            StatusText.Text = "Enter a package name before recording.";
            return;
        }
        var dialog = new SaveFileDialog
        {
            Filter = "ArcGIS playback package (*.unplayback.json)|*.unplayback.json",
            DefaultExt = ".unplayback.json",
            FileName = PackageNameBox.Text.Trim() + ".unplayback.json"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await RecorderHost.Capture.StartAsync();
            RecorderHost.Recorder.Start(new PackageMetadata
            {
                Name = PackageNameBox.Text.Trim(),
                SourceBranchVersion = string.IsNullOrWhiteSpace(SourceVersionBox.Text) ? "SDE.DEFAULT" : SourceVersionBox.Text.Trim(),
                SessionName = NullWhenBlank(WorkOrderBox.Text) ?? string.Empty,
                Description = NullWhenBlank(DescriptionBox.Text)
            }, dialog.FileName);
            RecordedOperations.Clear();
            RecordingSummaryText.Text = $"Recording to {dialog.FileName}";
            StatusText.Text = "Recording active. Feature adds, updates, and deletes will appear here.";
            StartButton.IsEnabled = false;
            SaveButton.IsEnabled = true;
        }
        catch (Exception ex) { StatusText.Text = $"Could not start recording: {ex.Message}"; }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RecorderHost.Capture.StopAsync();
            var path = RecorderHost.Recorder.StopAndSave();
            RecordingSummaryText.Text = $"Saved {RecordedOperations.Count} operation(s) to {path}";
            StatusText.Text = "Recording saved.";
            StartButton.IsEnabled = true;
            SaveButton.IsEnabled = false;
        }
        catch (Exception ex) { StatusText.Text = $"Could not save recording: {ex.Message}"; }
    }

    private void OpenPackage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "ArcGIS playback package (*.unplayback.json)|*.unplayback.json|JSON files (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _openedPackage = PackageRecorder.Read(dialog.FileName);
            PlaybackOperations.Clear();
            foreach (var operation in _openedPackage.Operations.OrderBy(item => item.Sequence)) PlaybackOperations.Add(operation);
            PlaybackSummaryText.Text = $"{_openedPackage.Metadata.Name} — {PlaybackOperations.Count} captured operation(s)";
            PlayButton.IsEnabled = true;
            StatusText.Text = "Package loaded. Play applies the consolidated feature operations to the active map.";
        }
        catch (Exception ex) { StatusText.Text = $"Could not read package: {ex.Message}"; }
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_openedPackage is null) return;
        if (MessageBox.Show(this, "Apply this package to the active map? This creates, updates, and deletes features in its current edit version.", "ArcGIS Playback", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try
        {
            var result = await _playback.PlayAsync(_openedPackage);
            StatusText.Text = result.Skipped.Count == 0
                ? $"Playback completed: {result.Queued} operation(s) applied."
                : $"Playback applied {result.Queued} operation(s); {result.Skipped.Count} skipped. {result.Skipped[0]}";
        }
        catch (Exception ex) { StatusText.Text = $"Playback failed: {ex.Message}"; }
    }

    private void OnOperationRecorded(ChangeOperation operation) => Dispatcher.BeginInvoke(() =>
    {
        RecordedOperations.Add(operation);
        RecordingSummaryText.Text = $"Recording {RecordedOperations.Count} operation(s).";
    });

    private void OnApplicationActivated(object? sender, EventArgs e) => ApplyProTheme();
    private void ApplyProTheme()
    {
        var dark = FrameworkApplication.ApplicationTheme is ApplicationTheme.Dark or ApplicationTheme.HighContrast;
        SetBrush("AppBackgroundBrush", dark ? "#1F2328" : "#F5F7F9");
        SetBrush("SurfaceBrush", dark ? "#2B333B" : "#FFFFFF");
        SetBrush("SurfaceAltBrush", dark ? "#252C33" : "#F8FAFB");
        SetBrush("BorderBrush", dark ? "#58636D" : "#D7DEE5");
        SetBrush("TextBrush", dark ? "#F4F7F9" : "#17212B");
        SetBrush("MutedTextBrush", dark ? "#BEC8D0" : "#61707C");
        SetBrush("HoverBrush", dark ? "#35424C" : "#EDF4F8");
    }
    private void SetBrush(string key, string hex) => Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    private static string? NullWhenBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
