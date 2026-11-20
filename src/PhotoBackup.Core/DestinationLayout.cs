namespace PhotoBackup.Core;

public static class DestinationLayout
{
    public const string PartialSuffix = ".partial";
    public const string MetaFolder = ".photobackup";

    /// <summary>Folder relative to the destination root, e.g. "2026\2026-11".</summary>
    public static string RelativeFolderFor(DateTime? modified)
    {
        if (modified is not { Year: > 1990 } d) return "unknown-date";
        return Path.Combine(d.ToString("yyyy"), d.ToString("yyyy-MM"));
    }

    /// <summary>attempt 1 = "IMG_1.HEIC", attempt 2 = "IMG_1_2.HEIC", ...</summary>
    public static string CandidateName(string name, int attempt)
    {
        if (attempt <= 1) return name;
        return $"{Path.GetFileNameWithoutExtension(name)}_{attempt}{Path.GetExtension(name)}";
    }
}
