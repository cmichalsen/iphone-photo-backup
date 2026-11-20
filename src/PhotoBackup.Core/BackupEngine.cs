namespace PhotoBackup.Core;

public sealed record BackupProgress(int Total, int Done, int Copied, int AlreadyBackedUp, int Failed,
    string CurrentFile, long CurrentBytes, long CurrentSize);

public sealed record BackupSummary(int Total, int Copied, int AlreadyBackedUp, int Failed, string? StoppedEarly);

/// <summary>Copies new phone files to the destination and verifies each one by hash before recording it.</summary>
public sealed class BackupEngine(StateStore store, string destRoot, Action<string> log)
{
    public const int MaxConsecutiveFailures = 5;

    public BackupSummary Run(IPhoneSource phone, IProgress<BackupProgress>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destRoot);
        CleanPartials();

        // Reading the list is slow on a big library; report Total = 0 with Done = files found so far.
        var files = new List<PhoneFile>();
        foreach (var f in phone.ListFiles())
        {
            if (ct.IsCancellationRequested) return new BackupSummary(0, 0, 0, 0, "Cancelled");
            files.Add(f);
            if (files.Count % 25 == 0) progress?.Report(new(0, files.Count, 0, 0, 0, "", 0, 0));
        }
        log($"Backup started: {files.Count} files on {phone.DeviceName}, destination {destRoot}");
        int done = 0, copied = 0, already = 0, failed = 0, consecutive = 0;
        string? stopped = null;

        foreach (var f in files)
        {
            if (ct.IsCancellationRequested) { stopped = "Cancelled"; break; }
            void Report(long bytes) => progress?.Report(new(files.Count, done, copied, already, failed, f.Name, bytes, f.Size));
            Report(0);
            try
            {
                if (VerifiedCopy.Exists(store, destRoot, phone.DeviceId, f, rehash: false, ct))
                {
                    already++;
                }
                else
                {
                    var (rel, hash, wasNew) = BackUpOne(phone, f, Report, ct);
                    store.Set(phone.DeviceId, f, FileStatus.Verified, hash, rel);
                    if (wasNew) copied++; else already++;
                    log($"VERIFIED {f.Path} -> {rel} sha256={hash}{(wasNew ? "" : " (identical file was already there)")}");
                }
                consecutive = 0;
            }
            catch (OperationCanceledException)
            {
                stopped = "Cancelled";
                break;
            }
            catch (Exception ex)
            {
                failed++;
                consecutive++;
                store.Set(phone.DeviceId, f, FileStatus.Failed, null, null, ex.Message);
                log($"FAILED {f.Path}: {ex.Message}");
                if (consecutive >= MaxConsecutiveFailures)
                {
                    stopped = $"Stopped after {consecutive} failures in a row (phone unplugged/locked, or NAS unreachable?). Last error: {ex.Message}";
                    break;
                }
            }
            done++;
        }

        try { store.BackupTo(Path.Combine(destRoot, DestinationLayout.MetaFolder, "state.jsonl")); }
        catch (Exception ex) { log($"Could not copy state database to destination: {ex.Message}"); }

        var summary = new BackupSummary(files.Count, copied, already, failed, stopped);
        log($"Backup finished: {summary}");
        progress?.Report(new(files.Count, done, copied, already, failed, "", 0, 0));
        return summary;
    }

    /// <returns>Relative destination path, hash, and whether a new file was written.</returns>
    private (string Rel, string Hash, bool WasNew) BackUpOne(IPhoneSource phone, PhoneFile f, Action<long> report, CancellationToken ct)
    {
        var relFolder = DestinationLayout.RelativeFolderFor(f.Modified);
        var folder = Path.Combine(destRoot, relFolder);
        Directory.CreateDirectory(folder);
        var partial = Path.Combine(folder, f.Name + DestinationLayout.PartialSuffix);

        try
        {
            string phoneHash;
            long written;
            using (var fs = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var hs = new HashingWriteStream(fs, ct, report))
            {
                phone.CopyTo(f, hs);
                fs.Flush(flushToDisk: true);
                phoneHash = hs.HashHex;
                written = hs.BytesWritten;
            }

            if (written == 0)
                throw new IOException("The phone sent 0 bytes.");
            if (f.Size > 0 && written != f.Size)
                throw new IOException($"The phone reported {f.Size} bytes but sent {written}. " +
                    "Check Settings > Photos > Transfer to Mac or PC is set to Keep Originals.");

            for (var attempt = 1; ; attempt++)
            {
                var name = DestinationLayout.CandidateName(f.Name, attempt);
                var final = Path.Combine(folder, name);
                var rel = Path.Combine(relFolder, name);

                if (!File.Exists(final))
                {
                    File.Move(partial, final);
                    // Read back what actually landed on the destination and compare with what the phone sent.
                    var nasHash = Hashing.HashFile(final, ct);
                    if (nasHash != phoneHash)
                    {
                        File.Delete(final);
                        throw new IOException($"Verification failed: destination copy hash {nasHash} != phone hash {phoneHash}.");
                    }
                    return (rel, phoneHash, true);
                }

                if (new FileInfo(final).Length == written && Hashing.HashFile(final, ct) == phoneHash)
                    return (rel, phoneHash, false); // identical content already there
            }
        }
        finally
        {
            try { File.Delete(partial); } catch { /* cleaned up on next run */ }
        }
    }

    private void CleanPartials()
    {
        try
        {
            foreach (var p in Directory.EnumerateFiles(destRoot, "*" + DestinationLayout.PartialSuffix, SearchOption.AllDirectories))
            {
                File.Delete(p);
                log($"Removed leftover partial file {p}");
            }
        }
        catch (Exception ex) { log($"Could not clean partial files: {ex.Message}"); }
    }
}

public static class VerifiedCopy
{
    /// <summary>
    /// True only if the state store says this exact phone file was verified AND the destination file
    /// is still there with the right size (and, if <paramref name="rehash"/>, the right content).
    /// </summary>
    public static bool Exists(StateStore store, string destRoot, string deviceId, PhoneFile f, bool rehash, CancellationToken ct = default)
    {
        var rec = store.Get(deviceId, f);
        if (rec is null || rec.Status == FileStatus.Failed || rec.NasPath is null || rec.Sha256 is null) return false;
        var path = Path.Combine(destRoot, rec.NasPath);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0) return false;
        if (f.Size > 0 && info.Length != f.Size) return false;
        return !rehash || Hashing.HashFile(path, ct) == rec.Sha256;
    }
}

