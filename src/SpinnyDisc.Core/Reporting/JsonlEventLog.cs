using System.Text.Json;
using SpinnyDisc.Core.Engine;

namespace SpinnyDisc.Core.Reporting;

/// <summary>Streams every test event to a .jsonl file — evidence survives a crashed run.</summary>
public sealed class JsonlEventLog : ITestSink, IDisposable
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly StreamWriter _w;
    private readonly object _gate = new();

    public JsonlEventLog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _w = new StreamWriter(File.Open(path, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    }

    public void Emit(TestEvent e)
    {
        lock (_gate)
            _w.WriteLine(JsonSerializer.Serialize(e, Opts));
    }

    public void Progress(PhaseProgress p) { } // progress isn't evidence

    public void Dispose() => _w.Dispose();
}
