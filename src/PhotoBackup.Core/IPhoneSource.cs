namespace PhotoBackup.Core;

/// <summary>A file on the phone. Path + Size + Modified together identify it across sessions.</summary>
public sealed record PhoneFile(string Path, string Name, long Size, DateTime? Modified)
{
    /// <summary>Device object id, only valid for the current connection.</summary>
    public string? Handle { get; init; }
}

public interface IPhoneSource : IDisposable
{
    string DeviceId { get; }
    string DeviceName { get; }
    IEnumerable<PhoneFile> ListFiles();

    /// <summary>Streams the file's bytes from the phone into <paramref name="destination"/>.</summary>
    void CopyTo(PhoneFile file, Stream destination);

    void Delete(PhoneFile file);
}
