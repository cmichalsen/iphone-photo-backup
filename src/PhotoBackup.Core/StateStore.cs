using System.Text.Json;

namespace PhotoBackup.Core;

public enum FileStatus { Verified, Failed, DeletedFromPhone }

/// <param name="NasPath">Path relative to the destination root.</param>
public sealed record FileRecord(FileStatus Status, string? Sha256, string? NasPath, string? Error, DateTime UpdatedUtc);

/// <summary>
/// Record of every phone file we've seen, keyed by (device, path, size, modified).
/// Stored as an append-only JSON-lines file; the last line for a key wins.
/// </summary>
public sealed class StateStore : IDisposable
{
    private sealed record Line(string D, string P, long S, long M, FileStatus St, string? H, string? N, string? E, DateTime U);

    private readonly string _path;
    private readonly Dictionary<(string, string, long, long), FileRecord> _records = new();
    private readonly Lock _gate = new();
    private StreamWriter _writer;

    public StateStore(string path)
    {
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        var lines = 0;
        if (File.Exists(_path))
        {
            foreach (var text in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                Line? l;
                try { l = JsonSerializer.Deserialize<Line>(text); }
                catch (JsonException) { continue; } // torn last line after a crash
                if (l is null) continue;
                _records[(l.D, l.P, l.S, l.M)] = new FileRecord(l.St, l.H, l.N, l.E, l.U);
                lines++;
            }
        }

        if (lines > _records.Count * 2 + 100) Compact();
        _writer = OpenWriter();
    }

    public FileRecord? Get(string deviceId, PhoneFile file)
    {
        lock (_gate) return _records.GetValueOrDefault(Key(deviceId, file));
    }

    public void Set(string deviceId, PhoneFile file, FileStatus status, string? sha256, string? nasPath, string? error = null)
    {
        lock (_gate)
        {
            var key = Key(deviceId, file);
            var rec = new FileRecord(status, sha256, nasPath, error, DateTime.UtcNow);
            _records[key] = rec;
            _writer.WriteLine(Serialize(key, rec));
            _writer.Flush();
        }
    }

    /// <summary>Writes a copy of the state file (used to keep a copy on the NAS).</summary>
    public void BackupTo(string path)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            _writer.Dispose();
            try
            {
                File.Copy(_path, path + ".tmp", overwrite: true);
                File.Move(path + ".tmp", path, overwrite: true);
            }
            finally { _writer = OpenWriter(); }
        }
    }

    private void Compact()
    {
        var tmp = _path + ".tmp";
        File.WriteAllLines(tmp, _records.Select(kv => Serialize(kv.Key, kv.Value)));
        File.Move(tmp, _path, overwrite: true);
    }

    private StreamWriter OpenWriter() => new(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read));

    private static (string, string, long, long) Key(string deviceId, PhoneFile f) => (deviceId, f.Path, f.Size, f.Modified?.Ticks ?? 0);

    private static string Serialize((string D, string P, long S, long M) k, FileRecord r) =>
        JsonSerializer.Serialize(new Line(k.D, k.P, k.S, k.M, r.Status, r.Sha256, r.NasPath, r.Error, r.UpdatedUtc));

    public void Dispose() => _writer.Dispose();
}
