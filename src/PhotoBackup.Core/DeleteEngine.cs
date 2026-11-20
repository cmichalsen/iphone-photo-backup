namespace PhotoBackup.Core;

public sealed record DeleteProgress(int Total, int Done, string CurrentFile);

public sealed record DeleteSummary(int Deleted, int Skipped, int Failed, long BytesFreed, IReadOnlyList<string> Problems);

/// <summary>Deletes phone files, but only ones with a verified copy on the destination.</summary>
public sealed class DeleteEngine(StateStore store, string destRoot, Action<string> log)
{
    /// <summary>Phone files that are safe to delete right now. Deletes nothing.</summary>
    public List<PhoneFile> Preview(IPhoneSource phone, CancellationToken ct = default) =>
        phone.ListFiles()
            .Where(f => VerifiedCopy.Exists(store, destRoot, phone.DeviceId, f, rehash: false, ct))
            .ToList();

    public DeleteSummary Delete(IPhoneSource phone, IReadOnlyList<PhoneFile> files, bool rehashBeforeDelete,
        IProgress<DeleteProgress>? progress = null, CancellationToken ct = default)
    {
        log($"Delete started: {files.Count} files requested on {phone.DeviceName} (rehash={rehashBeforeDelete})");
        int deleted = 0, skipped = 0, failed = 0, done = 0;
        long freed = 0;
        var problems = new List<string>();

        foreach (var f in files)
        {
            if (ct.IsCancellationRequested) break;
            progress?.Report(new(files.Count, done, f.Name));
            try
            {
                // Re-check immediately before every delete; never trust an earlier preview.
                if (!VerifiedCopy.Exists(store, destRoot, phone.DeviceId, f, rehashBeforeDelete, ct))
                {
                    skipped++;
                    problems.Add($"Skipped {f.Path}: no verified copy on the destination.");
                    log($"SKIPPED delete {f.Path}: no verified copy");
                }
                else
                {
                    var rec = store.Get(phone.DeviceId, f)!;
                    phone.Delete(f);
                    store.Set(phone.DeviceId, f, FileStatus.DeletedFromPhone, rec.Sha256, rec.NasPath);
                    deleted++;
                    freed += f.Size;
                    log($"DELETED from phone {f.Path} (copy at {rec.NasPath})");
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                failed++;
                problems.Add($"Could not delete {f.Path}: {ex.Message}");
                log($"FAILED delete {f.Path}: {ex.Message}");
            }
            done++;
        }

        try { store.BackupTo(Path.Combine(destRoot, DestinationLayout.MetaFolder, "state.jsonl")); }
        catch (Exception ex) { log($"Could not copy state database to destination: {ex.Message}"); }

        var summary = new DeleteSummary(deleted, skipped, failed, freed, problems);
        log($"Delete finished: deleted={deleted} skipped={skipped} failed={failed} bytesFreed={freed}");
        progress?.Report(new(files.Count, done, ""));
        return summary;
    }
}

