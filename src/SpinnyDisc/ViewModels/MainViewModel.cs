using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpinnyDisc.Core.Devices;
using SpinnyDisc.Core.Engine;
using SpinnyDisc.Core.Reporting;

namespace SpinnyDisc.ViewModels;

public partial class MainViewModel : ViewModelBase, ITestSink
{
    private const int MaxLogLines = 4000;
    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer _elapsedTimer;
    private DateTime _runStartedUtc;

    public ObservableCollection<DiskDescriptor> Disks { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDiskDetails))]
    [NotifyPropertyChangedFor(nameof(SelectedDiskIsSystem))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial DiskDescriptor? SelectedDisk { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool AllowWrites { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool AllowSystemDisk { get; set; }

    // decimal because NumericUpDown.Value is decimal? — int bindings render blank.
    [ObservableProperty] public partial decimal HangTimeoutMs { get; set; } = 30_000m;
    [ObservableProperty] public partial decimal StallThresholdMs { get; set; } = 5_000m;
    [ObservableProperty] public partial decimal MaxResets { get; set; } = 10m;
    [ObservableProperty] public partial decimal RandomReadOps { get; set; } = 4096m;

    public static string[] Intensities { get; } = ["gentle", "normal", "heavy"];
    [ObservableProperty] public partial string Intensity { get; set; } = "normal";

    // Preset-scaled knobs not exposed as their own inputs.
    private int _sweepStride = 1;
    private int _idleRounds = 3;
    private int _idleSeconds = 30;
    private int _idleBurstOps = 64;

    partial void OnIntensityChanged(string value)
    {
        switch (value)
        {
            case "gentle": // quick triage — ~25% surface, light provocation
                _sweepStride = 4; _idleRounds = 1; _idleSeconds = 15; _idleBurstOps = 32;
                RandomReadOps = 1024;
                break;
            case "heavy":  // strain it — full surface, sustained pressure
                _sweepStride = 1; _idleRounds = 5; _idleSeconds = 60; _idleBurstOps = 256;
                RandomReadOps = 32768;
                break;
            default:
                _sweepStride = 1; _idleRounds = 3; _idleSeconds = 30; _idleBurstOps = 64;
                RandomReadOps = 4096;
                break;
        }
    }

    [ObservableProperty] public partial string OutputDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SpinnyDiscReports");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotRunning))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty] public partial double ProgressFraction { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "select a drive";
    [ObservableProperty] public partial string VerdictText { get; set; } = "";
    [ObservableProperty] public partial string VerdictCss { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport))]
    public partial string? ReportPath { get; set; }
    [ObservableProperty] public partial bool AutoScrollLog { get; set; } = true;
    [ObservableProperty] public partial string ElapsedText { get; set; } = "";

    public bool IsNotRunning => !IsRunning;
    public bool HasReport => ReportPath is not null;
    public bool SelectedDiskIsSystem => SelectedDisk?.IsSystemDisk == true;

    public bool CanStart => SelectedDisk is not null && !IsRunning
        && (!AllowWrites || !SelectedDiskIsSystem || AllowSystemDisk);

    public string SelectedDiskDetails => SelectedDisk is { } d
        ? $"serial {d.Serial} · fw {d.Firmware} · {d.BytesPerSector}B sectors"
          + (d.UsbVidPid is { } v ? $" · USB bridge {v}" : "")
          + (d.MountedVolumes.Count > 0 ? $" · volumes: {string.Join(" ", d.MountedVolumes)}" : "")
        : "";

    public MainViewModel()
    {
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) =>
        {
            var t = DateTime.UtcNow - _runStartedUtc;
            ElapsedText = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
        };
        Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        var prev = SelectedDisk?.IdentityKey;
        Disks.Clear();
        foreach (var d in DiskEnumerator.Enumerate()) Disks.Add(d);
        SelectedDisk = Disks.FirstOrDefault(d => d.IdentityKey == prev)
            ?? Disks.FirstOrDefault(d => !d.IsSystemDisk)
            ?? Disks.FirstOrDefault();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var disk = SelectedDisk!;
        IsRunning = true;
        VerdictText = ""; VerdictCss = ""; ReportPath = null;
        ProgressFraction = 0;
        LogLines.Clear();
        _cts = new CancellationTokenSource();
        _runStartedUtc = DateTime.UtcNow;
        ElapsedText = "00:00";
        _elapsedTimer.Start();
        Log($"testing {disk.DisplayName}");

        Directory.CreateDirectory(OutputDir);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string jsonl = Path.Combine(OutputDir, $"disk{disk.Number}-{stamp}.jsonl");
        string html = Path.Combine(OutputDir, $"disk{disk.Number}-{stamp}.html");

        var plan = new TestPlan
        {
            AllowWrites = AllowWrites,
            AllowSystemDisk = AllowSystemDisk,
            HangTimeoutMs = (int)HangTimeoutMs,
            StallThresholdMs = (int)StallThresholdMs,
            MaxResets = (int)MaxResets,
            RandomReadOps = (int)RandomReadOps,
            SweepStride = _sweepStride,
            IdleRounds = _idleRounds,
            IdleSeconds = _idleSeconds,
            IdleBurstOps = _idleBurstOps,
        };

        try
        {
            using var log = new JsonlEventLog(jsonl);
            var sink = new TeeSink(this, log);
            var report = await new TestRunner(sink).RunAsync(disk, plan, _cts.Token);

            HtmlReport.Write(report, html, jsonl);
            File.WriteAllText(Path.Combine(OutputDir, $"disk{disk.Number}-{stamp}.json"), HtmlReport.ToJson(report));

            ReportPath = html;
            VerdictText = $"{report.Verdict} — {report.VerdictReason}";
            VerdictCss = report.Verdict switch
            {
                Verdict.Pass => "pass",
                Verdict.Degraded => "degraded",
                Verdict.Failed => "fail",
                _ => "inconclusive",
            };
            StatusText = "done";
            Log($"VERDICT: {VerdictText}");
        }
        catch (Exception ex)
        {
            Log($"engine fault: {ex.Message}");
            StatusText = "error";
            VerdictText = "engine fault";
            VerdictCss = "inconclusive";
        }
        finally
        {
            _elapsedTimer.Stop();
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void OpenReport()
    {
        if (ReportPath is { } p && File.Exists(p))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenOutputDir()
    {
        Directory.CreateDirectory(OutputDir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(OutputDir) { UseShellExecute = true });
    }

    // ------------------------------------------------------------ ITestSink

    public void Emit(TestEvent e)
    {
        string off = e.Offset >= 0 ? $"  @0x{e.Offset:X} LBA {e.Offset / Math.Max(1u, SelectedDisk?.BytesPerSector ?? 512)}" : "";
        string lat = e.LatencyMs > 0 ? $"  {e.LatencyMs:0}ms" : "";
        string line = $"[{e.Ts:HH:mm:ss}] {e.Kind,-14} {e.Phase,-12}{off}{lat} {e.Detail}";

        if (e.Kind is EventKind.PhaseStart or EventKind.Stall or EventKind.Hang
            or EventKind.IoError or EventKind.VerifyMismatch or EventKind.ResetAttempt
            or EventKind.ResetOk or EventKind.ResetFailed or EventKind.Aborted
            or EventKind.TestStart or EventKind.TestEnd or EventKind.Note)
            Post(() => Log(line));
    }

    public void Progress(PhaseProgress p) => Post(() =>
    {
        ProgressFraction = p.Fraction;
        StatusText = $"{p.Phase} — {p.Fraction * 100:0.#}% {p.Status}";
    });

    private void Log(string line)
    {
        LogLines.Add(line);
        while (LogLines.Count > MaxLogLines) LogLines.RemoveAt(0);
    }

    private static void Post(Action a)
    {
        if (Dispatcher.UIThread.CheckAccess()) a();
        else Dispatcher.UIThread.Post(a);
    }
}

/// <summary>Fan-out sink: GUI live view + JSONL file.</summary>
public sealed class TeeSink(ITestSink a, ITestSink b) : ITestSink
{
    public void Emit(TestEvent e) { a.Emit(e); b.Emit(e); }
    public void Progress(PhaseProgress p) { a.Progress(p); b.Progress(p); }
}
