using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpinnyDisc.Core.Engine;

namespace SpinnyDisc.Core.Reporting;

/// <summary>Renders a self-contained HTML report: verdict, stats, SMART delta,
/// event log, and an SVG scatter of fault locations across the LBA space.</summary>
public static class HtmlReport
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static void Write(TestReport report, string htmlPath, string? eventsJsonlPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(htmlPath))!);
        File.WriteAllText(htmlPath, Render(report, eventsJsonlPath), Encoding.UTF8);
    }

    /// <summary>Machine-readable summary JSON next to the HTML.</summary>
    public static string ToJson(TestReport r)
    {
        var (p50, p95, p99, max) = r.Stats.Percentiles();
        return JsonSerializer.Serialize(new
        {
            verdict = r.Verdict,
            verdictReason = r.VerdictReason,
            startedAt = r.StartedAt,
            finishedAt = r.FinishedAt,
            durationSec = (r.FinishedAt - r.StartedAt).TotalSeconds,
            disk = new
            {
                r.Disk.Number, r.Disk.Model, r.Disk.Serial, r.Disk.Firmware,
                bus = r.Disk.BusType.ToString(),
                r.Disk.SizeBytes, r.Disk.BytesPerSector, r.Disk.UsbVidPid,
                ancestors = r.Disk.AncestorIds,
            },
            plan = r.Plan,
            stats = new
            {
                r.Stats.OpsCompleted, r.Stats.BytesRead, r.Stats.BytesWritten,
                r.Stats.Hangs, r.Stats.Stalls, r.Stats.IoErrors, r.Stats.Resets,
                r.Stats.Mismatches, r.Stats.RetriedOk, r.Stats.SkippedBytes,
                latencyMs = new { p50, p95, p99, max },
            },
            badRegions = r.BadMap.Regions,
            smartBefore = r.SmartBefore?.Fields,
            smartAfter = r.SmartAfter?.Fields,
        }, Opts);
    }

    private static string Render(TestReport r, string? eventsJsonlPath)
    {
        var s = r.Stats;
        var (p50, p95, p99, max) = s.Percentiles();
        var dur = r.FinishedAt - r.StartedAt;
        string verdictClass = r.Verdict switch
        {
            Verdict.Pass => "pass", Verdict.Degraded => "degraded",
            Verdict.Failed => "fail", _ => "inconclusive",
        };

        var sb = new StringBuilder(32 * 1024);
        sb.Append($$"""
<!doctype html><html><head><meta charset="utf-8"><title>SpinnyDisc — {{Esc(r.Disk.Model)}}</title>
<style>
body{font:14px/1.45 'Segoe UI',system-ui,sans-serif;margin:2em;color:#1b1b1b;background:#fafafa}
h1{font-size:1.4em} h2{font-size:1.05em;margin-top:1.6em;border-bottom:1px solid #ddd;padding-bottom:.2em}
.verdict{display:inline-block;padding:.35em .9em;border-radius:6px;color:#fff;font-weight:700;font-size:1.25em}
.pass{background:#2e7d32}.degraded{background:#f9a825}.fail{background:#c62828}.inconclusive{background:#616161}
table{border-collapse:collapse;margin:.5em 0}td,th{padding:.3em .8em;border:1px solid #ddd;text-align:left;vertical-align:top}
th{background:#f0f0f0} code{background:#eee;padding:.1em .35em;border-radius:3px}
.evts{max-height:26em;overflow:auto;font-size:.85em;font-family:Consolas,monospace}
.hang{color:#c62828;font-weight:600}.stall{color:#e65100}.reset{color:#6a1b9a}.mismatch{color:#b71c1c;font-weight:700}
.small{color:#666;font-size:.85em}
</style></head><body>
<h1>SpinnyDisc report</h1>
<p class="small">Run {{r.StartedAt:yyyy-MM-dd HH:mm:ss}} → {{r.FinishedAt:HH:mm:ss}} UTC ({{dur.TotalMinutes:0.#}} min)</p>
<p><span class="verdict {{verdictClass}}">{{r.Verdict}}</span> &nbsp; {{Esc(r.VerdictReason)}}</p>

<h2>Drive</h2>
<table>
<tr><th>Model</th><td>{{Esc(r.Disk.Model)}}</td><th>Serial</th><td>{{Esc(r.Disk.Serial)}}</td></tr>
<tr><th>Firmware</th><td>{{Esc(r.Disk.Firmware)}}</td><th>Bus</th><td>{{r.Disk.BusType}}{{UsbBridge(r)}}</td></tr>
<tr><th>Size</th><td>{{FmtSize(r.Disk.SizeBytes)}} ({{r.Disk.TotalSectors:N0}} × {{r.Disk.BytesPerSector}} B)</td><th>Path</th><td><code>{{Esc(r.Disk.Path)}}</code></td></tr>
</table>
""");

        if (r.Disk.AncestorIds.Count > 0)
            sb.Append($"<p class=\"small\">PnP ancestry: {Esc(string.Join(" ← ", r.Disk.AncestorIds))}</p>\n");

        sb.Append($$"""
<h2>Outcome</h2>
<table>
<tr><th>Ops completed</th><td>{{s.OpsCompleted:N0}}</td><th>Bytes read</th><td>{{FmtSize(s.BytesRead)}}</td></tr>
<tr><th>Hangs</th><td>{{s.Hangs}}</td><th>Stalls (&gt;{{r.Plan.StallThresholdMs}}ms)</th><td>{{s.Stalls}}</td></tr>
<tr><th>I/O errors</th><td>{{s.IoErrors}}</td><th>Resets</th><td>{{s.Resets}}</td></tr>
<tr><th>Verify mismatches</th><td>{{s.Mismatches}}</td><th>Skipped (bad areas)</th><td>{{FmtSize(s.SkippedBytes)}}</td></tr>
<tr><th>Bytes written</th><td>{{FmtSize(s.BytesWritten)}}</td><th>Post-reset retries OK</th><td>{{s.RetriedOk}}</td></tr>
</table>
<p class="small">Latency p50={{p50:0.#}}ms p95={{p95:0.#}}ms p99={{p99:0.#}}ms max={{max:0.#}}ms</p>
""");

        // ---- fault map ----
        var regions = r.BadMap.Regions;
        if (regions.Count > 0)
        {
            sb.Append("<h2>Fault map</h2>\n");
            sb.Append("<table><tr><th>Kind</th><th>Start LBA</th><th>End LBA</th><th>Byte range</th><th>Events</th></tr>\n");
            foreach (var b in regions.OrderBy(b => b.StartByte))
                sb.Append($"<tr><td>{b.Kind}</td><td>{b.StartByte / r.Disk.BytesPerSector:N0}</td><td>{b.EndByte / r.Disk.BytesPerSector:N0}</td><td>{FmtSize(b.StartByte)} – {FmtSize(b.EndByte)}</td><td>{b.EventCount}</td></tr>\n");
            sb.Append("</table>\n");
            sb.Append(ScatterSvg(r));
            sb.Append("<p class=\"small\">Clustered faults → failing NAND region. Scattered faults → controller/firmware. Faults only after idle → sleep-state firmware bug.</p>\n");
        }

        // ---- SMART ----
        if (r.SmartBefore is not null || r.SmartAfter is not null)
        {
            sb.Append("<h2>SMART</h2>\n");
            if (r.SmartBefore?.Available == true || r.SmartAfter?.Available == true)
            {
                var keys = (r.SmartBefore?.Fields.Keys ?? Enumerable.Empty<string>())
                    .Union(r.SmartAfter?.Fields.Keys ?? Enumerable.Empty<string>());
                sb.Append("<table><tr><th>Field</th><th>Before</th><th>After</th><th>Δ</th></tr>\n");
                foreach (var k in keys.OrderBy(k => k))
                {
                    string? b = r.SmartBefore?.Fields.GetValueOrDefault(k);
                    string? a = r.SmartAfter?.Fields.GetValueOrDefault(k);
                    string delta = Delta(b, a);
                    sb.Append($"<tr><td>{Esc(k)}</td><td>{Esc(b ?? "—")}</td><td>{Esc(a ?? "—")}</td><td>{Esc(delta)}</td></tr>\n");
                }
                sb.Append("</table>\n");
            }
            else
                sb.Append("<p>SMART unavailable on this transport" + Esc(r.SmartBefore?.Failure ?? r.SmartAfter?.Failure ?? "") + " — common behind USB bridges.</p>\n");
        }

        // ---- events ----
        sb.Append("<h2>Event log</h2>\n");
        if (eventsJsonlPath is not null)
            sb.Append($"<p class=\"small\">Full JSONL: <code>{Esc(eventsJsonlPath)}</code></p>\n");
        sb.Append("<div class=\"evts\"><table><tr><th>Time</th><th>Event</th><th>Phase</th><th>Offset</th><th>Latency</th><th>Detail</th></tr>\n");
        foreach (var e in r.Events)
        {
            string cls = e.Kind switch
            {
                EventKind.Hang => "hang", EventKind.Stall => "stall",
                EventKind.ResetAttempt or EventKind.ResetOk or EventKind.ResetFailed => "reset",
                EventKind.VerifyMismatch => "mismatch", _ => "",
            };
            string off = e.Offset >= 0 ? $"0x{e.Offset:X} (LBA {e.Offset / r.Disk.BytesPerSector:N0})" : "";
            string lat = e.LatencyMs > 0 ? $"{e.LatencyMs:0}ms" : "";
            string det = e.Detail + (e.Win32Error != 0 ? $" [win32 {e.Win32Error}]" : "");
            sb.Append($"<tr class=\"{cls}\"><td>{e.Ts:HH:mm:ss.f}</td><td>{e.Kind}</td><td>{Esc(e.Phase)}</td><td>{off}</td><td>{lat}</td><td>{Esc(det)}</td></tr>\n");
        }
        sb.Append("</table></div>\n<p class=\"small\">Generated by SpinnyDisc — in-house SSD reliability triage.</p></body></html>");
        return sb.ToString();
    }

    private static string ScatterSvg(TestReport r)
    {
        const int W = 900, H = 140, pad = 12;
        var sb = new StringBuilder();
        sb.Append($"<svg viewBox=\"0 0 {W} {H}\" width=\"{W}\" style=\"background:#fff;border:1px solid #ddd\">\n");
        double X(long b) => pad + (W - 2 * pad) * (double)b / Math.Max(1, r.Disk.SizeBytes);
        // baseline track
        sb.Append($"<line x1=\"{pad}\" y1=\"{H / 2}\" x2=\"{W - pad}\" y2=\"{H / 2}\" stroke=\"#ccc\"/>\n");
        foreach (var e in r.Events.Where(e => e.Offset >= 0 &&
            e.Kind is EventKind.Hang or EventKind.Stall or EventKind.IoError or EventKind.VerifyMismatch))
        {
            double x = X(e.Offset);
            (string color, double y) = e.Kind switch
            {
                EventKind.Hang => ("#c62828", H * .3),
                EventKind.VerifyMismatch => ("#b71c1c", H * .45),
                EventKind.Stall => ("#f9a825", H * .6),
                _ => ("#fb8c00", H * .75),
            };
            sb.Append($"<line x1=\"{x:0.#}\" y1=\"{y - 8}\" x2=\"{x:0.#}\" y2=\"{y + 8}\" stroke=\"{color}\" stroke-width=\"2\"><title>{e.Kind} @LBA {e.Offset / r.Disk.BytesPerSector:N0} ({e.Detail})</title></line>\n");
        }
        foreach (var b in r.BadMap.Regions)
        {
            double x1 = X(b.StartByte), x2 = Math.Max(X(b.EndByte), x1 + 2);
            sb.Append($"<rect x=\"{x1:0.#}\" y=\"{H * .2}\" width=\"{x2 - x1:0.#}\" height=\"{H * .6}\" fill=\"#c62828\" fill-opacity=\"0.08\"/>\n");
        }
        sb.Append("<text x=\"12\" y=\"14\" font-size=\"10\" fill=\"#999\">0</text>");
        sb.Append($"<text x=\"{W - 70}\" y=\"14\" font-size=\"10\" fill=\"#999\">{FmtSize(r.Disk.SizeBytes)}</text>");
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    private static string UsbBridge(TestReport r) =>
        r.Disk.UsbVidPid is { } v ? $" (USB bridge {Esc(v)})" : "";

    private static string Delta(string? before, string? after)
    {
        if (before is null || after is null || before == after) return "";
        // extract trailing raw= number when present
        long? Raw(string s) { var i = s.LastIndexOf("raw="); return i >= 0 && long.TryParse(s[(i + 4)..].Split(' ')[0], out var v) ? v : null; }
        var (b, a) = (Raw(before), Raw(after));
        if (b is not null && a is not null && b != a) return $"{a - b:+#;-#;0}";
        if (long.TryParse(before, out var bi) && long.TryParse(after, out var ai) && bi != ai) return $"{ai - bi:+#;-#;0}";
        return "changed";
    }

    private static string FmtSize(long b) => Devices.DiskDescriptor.FormatSize(b);
    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
