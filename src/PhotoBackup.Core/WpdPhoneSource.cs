using System.Runtime.InteropServices;

namespace PhotoBackup.Core;

/// <summary>
/// iPhone over USB, via Windows Portable Devices ("Apple iPhone > Internal Storage" in Explorer).
/// Create and use from a background (non-UI) thread.
/// </summary>
public sealed class WpdPhoneSource : IPhoneSource
{
    private readonly IPortableDevice _device;
    private readonly IPortableDeviceContent _content;
    private readonly IPortableDeviceProperties _properties;
    private readonly IPortableDeviceKeyCollection _keys;

    private WpdPhoneSource(IPortableDevice device, string name)
    {
        _device = device;
        DeviceName = name;
        device.Content(out _content);
        _content.Properties(out _properties);

        _keys = Wpd.Create<IPortableDeviceKeyCollection>(Wpd.ClsidKeyCollection);
        foreach (var k in new[] { Wpd.ObjectName, Wpd.ObjectOriginalFileName, Wpd.ObjectContentType, Wpd.ObjectSize,
                     Wpd.ObjectDateCreated, Wpd.ObjectDateModified, Wpd.ObjectDateAuthored })
        {
            var key = k;
            _keys.Add(ref key);
        }

        _properties.GetValues(Wpd.RootObjectId, null, out var deviceValues);
        DeviceId = Wpd.Get(deviceValues, Wpd.DeviceSerialNumber) as string is { Length: > 0 } serial ? serial : name;
    }

    public string DeviceId { get; }
    public string DeviceName { get; }

    /// <summary>Opens the first connected iPhone, or returns null with a human-readable reason.</summary>
    public static WpdPhoneSource? TryOpen(out string problem)
    {
        problem = "No iPhone found. Plug it in with a USB cable and unlock it.";
        var manager = Wpd.Create<IPortableDeviceManager>(Wpd.ClsidManager);
        manager.RefreshDeviceList();

        uint count = 0;
        manager.GetDevices(null, ref count);
        if (count == 0) { problem += " (Windows reports no portable devices.)"; return null; }
        var idPtrs = new IntPtr[count];
        manager.GetDevices(idPtrs, ref count);

        foreach (var ptr in idPtrs)
        {
            var pnpId = Marshal.PtrToStringUni(ptr)!;
            Marshal.FreeCoTaskMem(ptr);

            var name = ReadText(manager.GetDeviceFriendlyName, pnpId);
            var label = $"{name} {ReadText(manager.GetDeviceManufacturer, pnpId)} {ReadText(manager.GetDeviceDescription, pnpId)}";
            if (!label.Contains("Apple", StringComparison.OrdinalIgnoreCase) &&
                !label.Contains("iPhone", StringComparison.OrdinalIgnoreCase))
            {
                problem += $" (Saw another device: \"{label.Trim()}\" {pnpId})";
                continue;
            }

            try
            {
                var clientInfo = Wpd.Create<IPortableDeviceValues>(Wpd.ClsidValues);
                clientInfo.SetStringValue(ref Wpd.ClientName, "PhotoBackup");
                clientInfo.SetUnsignedIntegerValue(ref Wpd.ClientMajorVersion, 1);
                var device = Wpd.Create<IPortableDevice>(Wpd.ClsidDeviceFtm);
                device.Open(pnpId, clientInfo);

                var phone = new WpdPhoneSource(device, name.Length > 0 ? name : "iPhone");
                if (!phone.Children(Wpd.RootObjectId).Any())
                {
                    phone.Dispose();
                    problem = $"Found \"{name}\" but it shows no storage. Unlock the phone and tap \"Trust This Computer\" (or \"Allow\"), then try again.";
                    continue;
                }
                return phone;
            }
            catch (Exception ex)
            {
                problem = $"Found \"{name}\" but could not open it: {ex.Message}";
            }
        }
        return null;
    }

    public IEnumerable<PhoneFile> ListFiles() => Walk(Wpd.RootObjectId, "");

    private IEnumerable<PhoneFile> Walk(string parentId, string parentPath)
    {
        foreach (var id in Children(parentId))
        {
            _properties.GetValues(id, _keys, out var v);
            var name = Wpd.Get(v, Wpd.ObjectOriginalFileName) as string ?? Wpd.Get(v, Wpd.ObjectName) as string ?? id;
            var path = parentPath + "\\" + name;
            var type = Wpd.Get(v, Wpd.ObjectContentType) as Guid?;

            if (type == Wpd.ContentTypeFolder || type == Wpd.ContentTypeFunctionalObject)
            {
                foreach (var f in Walk(id, path)) yield return f;
            }
            else
            {
                var size = (long)(Wpd.Get(v, Wpd.ObjectSize) as ulong? ?? 0);
                var date = Wpd.Get(v, Wpd.ObjectDateModified) as DateTime?
                           ?? Wpd.Get(v, Wpd.ObjectDateCreated) as DateTime?
                           ?? Wpd.Get(v, Wpd.ObjectDateAuthored) as DateTime?;
                yield return new PhoneFile(path, name, size, date) { Handle = id };
            }
            Marshal.ReleaseComObject(v);
        }
    }

    private List<string> Children(string parentId)
    {
        _content.EnumObjects(0, parentId, null, out var e);
        var ids = new List<string>();
        var batch = new IntPtr[64];
        while (true)
        {
            uint fetched = 0;
            var hr = e.Next((uint)batch.Length, batch, ref fetched);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            for (var i = 0; i < fetched; i++)
            {
                ids.Add(Marshal.PtrToStringUni(batch[i])!);
                Marshal.FreeCoTaskMem(batch[i]);
            }
            if (fetched == 0 || hr != 0) break;
        }
        Marshal.ReleaseComObject(e);
        return ids;
    }

    public void CopyTo(PhoneFile file, Stream destination)
    {
        _content.Transfer(out var resources);
        uint optimal = 0;
        resources.GetStream(Handle(file), ref Wpd.ResourceDefault, 0 /* STGM_READ */, ref optimal, out var stream);
        try
        {
            var buffer = new byte[optimal is > 0 and <= 16 << 20 ? optimal : 256 * 1024];
            var readPtr = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                while (true)
                {
                    stream.Read(buffer, buffer.Length, readPtr);
                    var read = Marshal.ReadInt32(readPtr);
                    if (read <= 0) break;
                    destination.Write(buffer, 0, read);
                }
            }
            finally { Marshal.FreeHGlobal(readPtr); }
        }
        finally
        {
            Marshal.ReleaseComObject(stream);
            Marshal.ReleaseComObject(resources);
        }
    }

    public void Delete(PhoneFile file)
    {
        var ids = Wpd.Create<IPortableDevicePropVariantCollection>(Wpd.ClsidPropVariantCollection);
        var pv = new PropVariant { Vt = Wpd.VT_LPWSTR, Ptr = Marshal.StringToCoTaskMemUni(Handle(file)) };
        try { ids.Add(ref pv); }
        finally { Wpd.PropVariantClear(ref pv); }

        IPortableDevicePropVariantCollection? results = null;
        var hr = _content.Delete(0 /* no recursion */, ids, ref results);
        if (hr == 0) return;

        var detail = hr;
        if (results is not null)
        {
            var r = new PropVariant();
            results.GetAt(0, ref r);
            if (r.Vt == Wpd.VT_ERROR) detail = r.Int32;
        }
        throw new IOException($"The phone refused to delete this file (error 0x{detail:X8}).");
    }

    private static string Handle(PhoneFile f) =>
        f.Handle ?? throw new InvalidOperationException("This file entry did not come from the connected phone.");

    private delegate int TextGetter(string id, ushort[]? buffer, ref uint length);

    private static string ReadText(TextGetter getter, string pnpId)
    {
        uint len = 0;
        if (getter(pnpId, null, ref len) != 0 || len == 0) return "";
        var buf = new ushort[len];
        if (getter(pnpId, buf, ref len) != 0) return "";
        return new string(Array.ConvertAll(buf, c => (char)c)).TrimEnd('\0');
    }

    public void Dispose()
    {
        try { _device.Close(); } catch { /* already gone */ }
        Marshal.ReleaseComObject(_device);
    }
}
