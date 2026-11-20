// Spike: proves list / copy / delete work against the real iPhone before relying on the app.
//   list                      show a summary and the 15 newest files
//   copy <phone path> <dir>   copy one file to a local folder and print its hash
//   delete <phone path>       delete ONE file from the phone (asks you to type YES)
using PhotoBackup.Core;

using var phone = WpdPhoneSource.TryOpen(out var problem);
if (phone is null) { Console.WriteLine(problem); return 1; }
Console.WriteLine($"Connected: {phone.DeviceName} (id {phone.DeviceId})");

var files = args.FirstOrDefault() is "list" or null ? phone.ListFiles().ToList() : [];
PhoneFile? Find(string path) => phone.ListFiles().FirstOrDefault(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

switch (args.FirstOrDefault())
{
    case "list" or null:
        Console.WriteLine($"{files.Count} files, {files.Sum(f => f.Size) / 1e9:F2} GB");
        foreach (var g in files.GroupBy(f => Path.GetExtension(f.Name).ToUpperInvariant()).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {g.Key,-6} {g.Count(),6}");
        Console.WriteLine("Newest 15:");
        foreach (var f in files.OrderByDescending(f => f.Modified).Take(15))
            Console.WriteLine($"  {f.Modified:yyyy-MM-dd HH:mm}  {f.Size,12:N0}  {f.Path}");
        return 0;

    case "copy" when args.Length == 3 && Find(args[1]) is { } f:
        Directory.CreateDirectory(args[2]);
        var dest = Path.Combine(args[2], f.Name);
        using (var fs = File.Create(dest))
        using (var hs = new HashingWriteStream(fs, CancellationToken.None))
        {
            phone.CopyTo(f, hs);
            Console.WriteLine($"Copied {hs.BytesWritten:N0} bytes (phone reported {f.Size:N0}) to {dest}\nsha256 {hs.HashHex}");
        }
        return 0;

    case "delete" when args.Length == 2 && Find(args[1]) is { } f:
        Console.Write($"Delete {f.Path} ({f.Size:N0} bytes) from the phone? Type YES: ");
        if (Console.ReadLine()?.Trim('﻿', ' ', '\r') != "YES") { Console.WriteLine("Not deleted."); return 1; }
        phone.Delete(f);
        Console.WriteLine("Deleted. Check the Photos app (and Recently Deleted) on the phone.");
        return 0;

    default:
        Console.WriteLine("Usage: list | copy <phone path> <dir> | delete <phone path>   (path must match a file from 'list')");
        return 1;
}
