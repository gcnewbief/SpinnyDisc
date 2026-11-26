using SpinnyDisc.Core.Devices;
using SpinnyDisc.Core.Engine;
using SpinnyDisc.Core.Reporting;

// SpinnyDisc.Cli — headless runner for the test engine.
//   SpinnyDisc.Cli list
//   SpinnyDisc.Cli test <diskNumber> [--destructive] [--hang-ms N] [--stall-ms N]
//                        [--max-resets N] [--ops N] [--out DIR]

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("""
        SpinnyDisc.Cli — SSD reliability triage
          list                         enumerate physical disks
          test <diskNumber> [options]  run the test plan
            --destructive              enable write+verify (destroys data!)
            --allow-system-disk        also permit destructive tests on the boot disk
            --hang-ms N                per-I/O hang timeout (default 30000)
            --stall-ms N               stall threshold (default 5000)
            --max-resets N             reset budget (default 10)
            --ops N                    random-read op count (default 4096)
            --cap-mb N                 only touch the first N MiB (smoke tests)
            --out DIR                  report dir (default ./reports)
        """);
    return 0;
}

int GetOpt(string name, int fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;
}
string GetOptStr(string name, string fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}
bool HasFlag(string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);

if (args[0].Equals("pnp", StringComparison.OrdinalIgnoreCase))
{
    var nodes = SpinnyDisc.Core.Interop.Pnp.EnumerateDiskDevNodes();
    Console.WriteLine($"{nodes.Count} disk devnodes; lastErr={SpinnyDisc.Core.Interop.Pnp.LastError ?? "none"}");
    foreach (var kv in nodes)
    {
        Console.WriteLine($"disk {kv.Key}: devinst={kv.Value.DevInst} pdo={kv.Value.PhysicalObjectName}");
        Console.WriteLine($"    instance: {kv.Value.DeviceInstanceId}");
        Console.WriteLine($"    path: {kv.Value.DevicePath}");
        foreach (var a in kv.Value.AncestorIds) Console.WriteLine($"    ancestor: {a}");
    }
    return 0;
}

if (args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
{
    foreach (var d in DiskEnumerator.Enumerate())
    {
        Console.WriteLine($"{d.DisplayName}");
        Console.WriteLine($"    serial={d.Serial} fw={d.Firmware} sector={d.BytesPerSector}B " +
            $"seekPenalty={(d.IncursSeekPenalty?.ToString() ?? "?")} trim={(d.SupportsTrim?.ToString() ?? "?")}");
        if (d.UsbVidPid is { } v) Console.WriteLine($"    USB bridge: {v}");
        if (d.MountedVolumes.Count > 0) Console.WriteLine($"    volumes: {string.Join(' ', d.MountedVolumes)}");
        if (d.IsSystemDisk) Console.WriteLine($"    *** SYSTEM DISK — destructive tests refused ***");
        foreach (var a in d.AncestorIds) Console.WriteLine($"      ancestor: {a}");
    }
    return 0;
}

if (!args[0].Equals("test", StringComparison.OrdinalIgnoreCase) || args.Length < 2 || !int.TryParse(args[1], out int num))
{
    Console.Error.WriteLine("expected: test <diskNumber>");
    return 2;
}

var disk = DiskEnumerator.Enumerate().FirstOrDefault(d => d.Number == num);
if (disk is null) { Console.Error.WriteLine($"disk {num} not found"); return 3; }

// --cap-mb N: only touch the first N MiB of the disk (smoke tests).
int capMb = GetOpt("--cap-mb", 0);
if (capMb > 0) disk = disk with { SizeBytes = Math.Min(disk.SizeBytes, (long)capMb << 20) };

var plan = new TestPlan
{
    AllowWrites = HasFlag("--destructive"),
    AllowSystemDisk = HasFlag("--allow-system-disk"),
    HangTimeoutMs = GetOpt("--hang-ms", 30_000),
    StallThresholdMs = GetOpt("--stall-ms", 5_000),
    MaxResets = GetOpt("--max-resets", 10),
    RandomReadOps = GetOpt("--ops", 4096),
};
var outDir = GetOptStr("--out", Path.Combine(AppContext.BaseDirectory, "reports"));
Directory.CreateDirectory(outDir);
string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
string jsonl = Path.Combine(outDir, $"disk{num}-{stamp}.jsonl");
string html = Path.Combine(outDir, $"disk{num}-{stamp}.html");

Console.WriteLine($"SpinnyDisc: testing {disk.DisplayName}");
Console.WriteLine($"  writes={plan.AllowWrites} hangTimeout={plan.HangTimeoutMs}ms reports → {outDir}");

using var log = new JsonlEventLog(jsonl);
var sink = new ConsoleSink(log);
var runner = new TestRunner(sink);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var report = await runner.RunAsync(disk, plan, cts.Token);
HtmlReport.Write(report, html, jsonl);
File.WriteAllText(Path.Combine(outDir, $"disk{num}-{stamp}.json"), HtmlReport.ToJson(report));

Console.WriteLine();
Console.WriteLine($"VERDICT: {report.Verdict} — {report.VerdictReason}");
Console.WriteLine($"report: {html}");
return report.Verdict == Verdict.Pass ? 0 : 1;

sealed class ConsoleSink(ITestSink inner) : ITestSink
{
    public void Emit(TestEvent e)
    {
        inner.Emit(e);
        if (e.Kind is EventKind.PhaseStart or EventKind.PhaseEnd
            or EventKind.Hang or EventKind.Stall or EventKind.ResetAttempt
            or EventKind.ResetOk or EventKind.ResetFailed or EventKind.VerifyMismatch
            or EventKind.IoError or EventKind.Aborted or EventKind.TestStart
            or EventKind.TestEnd or EventKind.Note or EventKind.IdentifyInfo
            or EventKind.SmartSnapshot)
        {
            string off = e.Offset >= 0 ? $"  @0x{e.Offset:X}" : "";
            string lat = e.LatencyMs > 0 ? $"  {e.LatencyMs:0}ms" : "";
            Console.WriteLine($"[{e.Ts:HH:mm:ss}] {e.Kind,-14} {e.Phase,-12}{off}{lat} {e.Detail}");
            if (e.Extra is { } x && e.Kind is EventKind.IdentifyInfo or EventKind.SmartSnapshot)
                foreach (var kv in x.Take(24))
                    Console.WriteLine($"      {kv.Key} = {kv.Value}");
        }
    }

    public void Progress(PhaseProgress p)
        => Console.Write($"\r  {p.Phase}: {p.Fraction * 100,5:0.#}%  {p.Status}          ");
}
