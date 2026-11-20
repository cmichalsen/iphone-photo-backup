using System.Text;
using PhotoBackup.Core;

namespace PhotoBackup.Tests;

internal sealed class FakePhone : IPhoneSource
{
    public readonly Dictionary<string, (byte[] Data, DateTime? Modified, long? ReportedSize)> Files = new();
    public readonly List<string> Deleted = new();
    public Func<PhoneFile, Stream, bool>? CopyOverride; // return true if handled
    public string DeviceId => "FAKE-SERIAL";
    public string DeviceName => "Fake iPhone";

    public void Add(string path, string content, DateTime? modified = null, long? reportedSize = null) =>
        Files[path] = (Encoding.UTF8.GetBytes(content), modified ?? new DateTime(2026, 11, 5, 10, 0, 0), reportedSize);

    public IEnumerable<PhoneFile> ListFiles() => Files
        .OrderBy(kv => kv.Key, StringComparer.Ordinal)
        .Select(kv => new PhoneFile(kv.Key, Path.GetFileName(kv.Key), kv.Value.ReportedSize ?? kv.Value.Data.Length, kv.Value.Modified))
        .ToList();

    public void CopyTo(PhoneFile file, Stream destination)
    {
        if (CopyOverride?.Invoke(file, destination) == true) return;
        destination.Write(Files[file.Path].Data);
    }

    public void Delete(PhoneFile file)
    {
        if (!Files.Remove(file.Path)) throw new IOException("not on phone");
        Deleted.Add(file.Path);
    }

    public void Dispose() { }
}

public sealed class EngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoBackupTests", Guid.NewGuid().ToString("N"));
    private readonly string _dest;
    private readonly StateStore _store;
    private readonly FakePhone _phone = new();
    private readonly List<string> _log = new();

    public EngineTests()
    {
        _dest = Path.Combine(_root, "nas");
        _store = new StateStore(Path.Combine(_root, "state.jsonl"));
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private BackupEngine Backup => new(_store, _dest, _log.Add);
    private DeleteEngine Deleter => new(_store, _dest, _log.Add);
    private string Nas(params string[] parts) => Path.Combine([_dest, .. parts]);

    [Fact]
    public void Backup_copies_into_year_month_folders_and_records_verified()
    {
        _phone.Add(@"\DCIM\100APPLE\IMG_0001.HEIC", "photo-one");
        _phone.Add(@"\DCIM\100APPLE\IMG_0002.MOV", "video-two", new DateTime(2025, 1, 31));

        var s = Backup.Run(_phone);

        Assert.Equal((2, 2, 0, 0, null), (s.Total, s.Copied, s.AlreadyBackedUp, s.Failed, s.StoppedEarly));
        Assert.Equal("photo-one", File.ReadAllText(Nas("2026", "2026-11", "IMG_0001.HEIC")));
        Assert.Equal("video-two", File.ReadAllText(Nas("2025", "2025-01", "IMG_0002.MOV")));
        Assert.Equal(FileStatus.Verified, _store.Get(_phone.DeviceId, _phone.ListFiles().First())!.Status);
        Assert.True(File.Exists(Nas(".photobackup", "state.jsonl")));
    }

    [Fact]
    public void Second_run_copies_nothing()
    {
        _phone.Add(@"\DCIM\IMG_1.JPG", "a");
        Backup.Run(_phone);
        _phone.CopyOverride = (_, _) => throw new InvalidOperationException("should not copy again");

        var s = Backup.Run(_phone);

        Assert.Equal((0, 1, 0), (s.Copied, s.AlreadyBackedUp, s.Failed));
    }

    [Fact]
    public void State_survives_reopening_the_store()
    {
        _phone.Add(@"\DCIM\IMG_1.JPG", "a");
        Backup.Run(_phone);
        _store.Dispose();

        using var reopened = new StateStore(Path.Combine(_root, "state.jsonl"));
        Assert.Equal(FileStatus.Verified, reopened.Get(_phone.DeviceId, _phone.ListFiles().Single())!.Status);
    }

    [Fact]
    public void Interrupted_copy_fails_leaves_no_file_and_is_retried_next_run()
    {
        _phone.Add(@"\DCIM\IMG_1.JPG", "full-content");
        _phone.CopyOverride = (_, dest) => { dest.Write("ful"u8); throw new IOException("USB unplugged"); };

        var first = Backup.Run(_phone);
        Assert.Equal(1, first.Failed);
        Assert.Empty(Directory.EnumerateFiles(_dest, "IMG_1*", SearchOption.AllDirectories));
        Assert.Empty(Deleter.Preview(_phone));

        _phone.CopyOverride = null;
        var second = Backup.Run(_phone);
        Assert.Equal((1, 0), (second.Copied, second.Failed));
        Assert.Equal("full-content", File.ReadAllText(Nas("2026", "2026-11", "IMG_1.JPG")));
    }

    [Fact]
    public void Short_transfer_is_a_failure()
    {
        _phone.Add(@"\DCIM\IMG_1.JPG", "abc", reportedSize: 10);

        var s = Backup.Run(_phone);

        Assert.Equal(1, s.Failed);
        Assert.Empty(Directory.EnumerateFiles(_dest, "IMG_1*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Stops_after_repeated_failures()
    {
        for (var i = 0; i < 20; i++) _phone.Add($@"\DCIM\IMG_{i:00}.JPG", "x");
        _phone.CopyOverride = (_, _) => throw new IOException("device gone");

        var s = Backup.Run(_phone);

        Assert.Equal(BackupEngine.MaxConsecutiveFailures, s.Failed);
        Assert.NotNull(s.StoppedEarly);
    }

    [Fact]
    public void Leftover_partial_files_are_removed()
    {
        Directory.CreateDirectory(Nas("2026", "2026-11"));
        File.WriteAllText(Nas("2026", "2026-11", "IMG_9.JPG.partial"), "junk");

        Backup.Run(_phone);

        Assert.False(File.Exists(Nas("2026", "2026-11", "IMG_9.JPG.partial")));
    }

    [Fact]
    public void Same_name_different_content_gets_a_suffix_and_keeps_the_existing_file()
    {
        Directory.CreateDirectory(Nas("2026", "2026-11"));
        File.WriteAllText(Nas("2026", "2026-11", "IMG_1.JPG"), "someone else's photo");
        _phone.Add(@"\DCIM\IMG_1.JPG", "my photo");

        Backup.Run(_phone);

        Assert.Equal("someone else's photo", File.ReadAllText(Nas("2026", "2026-11", "IMG_1.JPG")));
        Assert.Equal("my photo", File.ReadAllText(Nas("2026", "2026-11", "IMG_1_2.JPG")));
    }

    [Fact]
    public void Same_name_same_content_is_not_duplicated()
    {
        Directory.CreateDirectory(Nas("2026", "2026-11"));
        File.WriteAllText(Nas("2026", "2026-11", "IMG_1.JPG"), "my photo");
        _phone.Add(@"\DCIM\IMG_1.JPG", "my photo");

        var s = Backup.Run(_phone);

        Assert.Equal((0, 1), (s.Copied, s.AlreadyBackedUp));
        Assert.Single(Directory.EnumerateFiles(Nas("2026", "2026-11")));
        Assert.Single(Deleter.Preview(_phone));
    }

    [Fact]
    public void Cancel_stops_early_without_failures()
    {
        for (var i = 0; i < 5; i++) _phone.Add($@"\DCIM\IMG_{i}.JPG", "x" + i);
        using var cts = new CancellationTokenSource();
        var copies = 0;
        _phone.CopyOverride = (_, _) => { if (++copies == 3) cts.Cancel(); return false; };

        var s = Backup.Run(_phone, ct: cts.Token);

        Assert.Equal("Cancelled", s.StoppedEarly);
        Assert.Equal(0, s.Failed);
        Assert.True(s.Copied < 5);
        Assert.Empty(Directory.EnumerateFiles(_dest, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public void Delete_removes_only_verified_files()
    {
        _phone.Add(@"\DCIM\IMG_1.JPG", "backed up");
        Backup.Run(_phone);
        _phone.Add(@"\DCIM\IMG_2.JPG", "taken after the backup");

        var preview = Deleter.Preview(_phone);
        Assert.Equal([@"\DCIM\IMG_1.JPG"], preview.Select(f => f.Path));

        // Even if asked to delete everything, the unverified file must survive.
        var s = Deleter.Delete(_phone, _phone.ListFiles().ToList(), rehashBeforeDelete: true);

        Assert.Equal((1, 1, 0), (s.Deleted, s.Skipped, s.Failed));
        Assert.Equal([@"\DCIM\IMG_1.JPG"], _phone.Deleted);
        Assert.True(_phone.Files.ContainsKey(@"\DCIM\IMG_2.JPG"));
    }

    [Fact]
    public void Delete_is_blocked_when_the_nas_copy_is_missing_and_backup_recopies_it()
    {
        _phone.Add(@"\DCIM\IMG_1.JPG", "precious");
        Backup.Run(_phone);
        var files = _phone.ListFiles().ToList();
        File.Delete(Nas("2026", "2026-11", "IMG_1.JPG"));

        Assert.Empty(Deleter.Preview(_phone));
        var s = Deleter.Delete(_phone, files, rehashBeforeDelete: false);
        Assert.Equal((0, 1), (s.Deleted, s.Skipped));
        Assert.Empty(_phone.Deleted);

        Assert.Equal(1, Backup.Run(_phone).Copied);
        Assert.Equal("precious", File.ReadAllText(Nas("2026", "2026-11", "IMG_1.JPG")));
    }

    [Fact]
    public void Delete_with_rehash_is_blocked_when_the_nas_copy_was_altered()
    {
        _phone.Add(@"\DCIM\IMG_1.JPG", "precious");
        Backup.Run(_phone);
        File.WriteAllText(Nas("2026", "2026-11", "IMG_1.JPG"), "PRECIOUS"); // same length, different bytes

        var s = Deleter.Delete(_phone, _phone.ListFiles().ToList(), rehashBeforeDelete: true);

        Assert.Equal((0, 1), (s.Deleted, s.Skipped));
        Assert.Empty(_phone.Deleted);
    }

    [Fact]
    public void A_changed_phone_file_with_the_same_path_is_treated_as_new()
    {
        _phone.Add(@"\DCIM\IMG_1.JPG", "old");
        Backup.Run(_phone);
        _phone.Add(@"\DCIM\IMG_1.JPG", "new and longer");

        Assert.Empty(Deleter.Preview(_phone));
        Backup.Run(_phone);
        Assert.Equal("new and longer", File.ReadAllText(Nas("2026", "2026-11", "IMG_1_2.JPG")));
    }

    [Theory]
    [InlineData(1, "IMG_1.HEIC")]
    [InlineData(2, "IMG_1_2.HEIC")]
    [InlineData(3, "IMG_1_3.HEIC")]
    public void Candidate_names(int attempt, string expected) =>
        Assert.Equal(expected, DestinationLayout.CandidateName("IMG_1.HEIC", attempt));

    [Fact]
    public void Missing_date_goes_to_unknown_folder() =>
        Assert.Equal("unknown-date", DestinationLayout.RelativeFolderFor(null));
}
