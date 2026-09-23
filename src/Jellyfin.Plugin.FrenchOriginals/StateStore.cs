using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.FrenchOriginals;

public sealed class StateStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string DirectoryPath => directory;
    public Dictionary<Guid, Completion> LoadCompleted()
    {
        var path = Path.Combine(directory, "completed.json");
        if (!File.Exists(path)) return [];
        return JsonSerializer.Deserialize<Dictionary<Guid, Completion>>(File.ReadAllText(path))
            ?? throw new InvalidDataException("The completion file is invalid; restore or rename it before rerunning.");
    }
    public void SaveCompleted(Dictionary<Guid, Completion> completed) => AtomicWrite("completed.json", completed);
    public void SaveReport(RunReport report) => AtomicWrite("last-run.json", report);
    public string ReadReport() => File.Exists(Path.Combine(directory, "last-run.json"))
        ? File.ReadAllText(Path.Combine(directory, "last-run.json")) : "{\"Status\":\"Not run yet\"}";
    private void AtomicWrite(string name, object value)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        var temp = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, value.GetType(), Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public Journal OpenJournal(int retentionDays)
    {
        var path = Path.Combine(directory, "journals");
        Directory.CreateDirectory(path);
        foreach (var file in Directory.EnumerateFiles(path, "run-*.jsonl"))
        {
            if (File.GetLastWriteTimeUtc(file) >= DateTime.UtcNow.AddDays(-retentionDays)) continue;
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return new Journal(Path.Combine(path, $"run-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.jsonl"));
    }
}

public sealed record Completion(string? Fingerprint, DateTime LastAttemptUtc);

public sealed class Journal : IDisposable
{
    private readonly FileStream stream;
    public Journal(string path) => stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    public void Write(string state, object? data = null)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { TimestampUtc = DateTime.UtcNow, State = state, Data = data }) + "\n");
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
    public void Dispose() => stream.Dispose();
}
