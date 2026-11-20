using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace PhotoBackup.Core;

// Minimal hand-written COM declarations for Windows Portable Devices (PortableDeviceApi.dll, part of Windows).
// Method order must match the native vtables exactly; trailing methods we never call are omitted.

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey(string fmtid, uint pid)
{
    public Guid Fmtid = new(fmtid);
    public uint Pid = pid;
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort Vt;
    [FieldOffset(8)] public IntPtr Ptr;
    [FieldOffset(8)] public long Int64;
    [FieldOffset(8)] public double Double;
    [FieldOffset(8)] public int Int32;
}

internal static class Wpd
{
    public static readonly Guid ClsidManager = new("0af10cec-2ecd-4b92-9581-34f6ae0637f3");
    public static readonly Guid ClsidDeviceFtm = new("f7c0039a-4762-488a-b4b3-760ef9a1ba9b");
    public static readonly Guid ClsidValues = new("0c15d503-d017-47ce-9016-7b3f978721cc");
    public static readonly Guid ClsidKeyCollection = new("de2d022d-2480-43be-97f0-d1fa2cf98f4f");
    public static readonly Guid ClsidPropVariantCollection = new("08a99e2f-6d6d-4b80-af5a-baf2bcbe4cb9");

    private const string ObjectProps = "EF6B490D-5CD8-437A-AFFC-DA8B60EE4A3C";
    public static PropertyKey ObjectName = new(ObjectProps, 4);
    public static PropertyKey ObjectContentType = new(ObjectProps, 7);
    public static PropertyKey ObjectSize = new(ObjectProps, 11);
    public static PropertyKey ObjectOriginalFileName = new(ObjectProps, 12);
    public static PropertyKey ObjectDateCreated = new(ObjectProps, 18);
    public static PropertyKey ObjectDateModified = new(ObjectProps, 19);
    public static PropertyKey ObjectDateAuthored = new(ObjectProps, 20);
    public static PropertyKey ResourceDefault = new("E81E79BE-34F0-41BF-B53F-F1A06AE87842", 0);
    public static PropertyKey DeviceSerialNumber = new("26D4979A-E643-4626-9E2B-736DC0C92FDC", 9);
    public static PropertyKey ClientName = new("204D9F0C-2292-4080-9F42-40664E70F859", 2);
    public static PropertyKey ClientMajorVersion = new("204D9F0C-2292-4080-9F42-40664E70F859", 3);

    public static readonly Guid ContentTypeFolder = new("27E2E392-A111-48E0-AB0C-E17705A05F85");
    public static readonly Guid ContentTypeFunctionalObject = new("99ED0160-17FF-4C44-9D98-1D7A6F941921");

    public const string RootObjectId = "DEVICE";
    public const ushort VT_ERROR = 10, VT_DATE = 7, VT_UI4 = 19, VT_UI8 = 21, VT_LPWSTR = 31, VT_CLSID = 72;

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PropVariant pv);

    public static T Create<T>(Guid clsid) => (T)Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, throwOnError: true)!)!;

    /// <summary>Reads one property as string / Guid / DateTime / ulong, or null if absent.</summary>
    public static object? Get(IPortableDeviceValues values, PropertyKey key)
    {
        var pv = new PropVariant();
        if (values.GetValue(ref key, ref pv) != 0) return null;
        try
        {
            return pv.Vt switch
            {
                VT_LPWSTR => Marshal.PtrToStringUni(pv.Ptr),
                VT_CLSID => Marshal.PtrToStructure<Guid>(pv.Ptr),
                VT_DATE => DateTime.FromOADate(pv.Double),
                VT_UI8 => (ulong)pv.Int64,
                VT_UI4 => (ulong)(uint)pv.Int32,
                _ => null,
            };
        }
        catch (ArgumentException) { return null; } // out-of-range date
        finally { PropVariantClear(ref pv); }
    }
}

[ComImport, Guid("a1567595-4c2f-4574-a6fa-ecef917b9a40"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceManager
{
    void GetDevices([In, Out, MarshalAs(UnmanagedType.LPArray)] IntPtr[]? pnpDeviceIds, ref uint count);
    void RefreshDeviceList();
    [PreserveSig] int GetDeviceFriendlyName([MarshalAs(UnmanagedType.LPWStr)] string id, [In, Out, MarshalAs(UnmanagedType.LPArray)] ushort[]? name, ref uint length);
    [PreserveSig] int GetDeviceDescription([MarshalAs(UnmanagedType.LPWStr)] string id, [In, Out, MarshalAs(UnmanagedType.LPArray)] ushort[]? text, ref uint length);
    [PreserveSig] int GetDeviceManufacturer([MarshalAs(UnmanagedType.LPWStr)] string id, [In, Out, MarshalAs(UnmanagedType.LPArray)] ushort[]? text, ref uint length);
}

[ComImport, Guid("625e2df8-6392-4cf0-9ad1-3cfa5f17775c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDevice
{
    void Open([MarshalAs(UnmanagedType.LPWStr)] string pnpDeviceId, IPortableDeviceValues clientInfo);
    void SendCommand(uint flags, IPortableDeviceValues parameters, out IPortableDeviceValues results);
    void Content(out IPortableDeviceContent content);
    void Capabilities([MarshalAs(UnmanagedType.IUnknown)] out object capabilities);
    void Cancel();
    void Close();
}

[ComImport, Guid("6a96ed84-7c73-4480-9938-bf5af477d426"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceContent
{
    void EnumObjects(uint flags, [MarshalAs(UnmanagedType.LPWStr)] string parentObjectId, IPortableDeviceValues? filter, out IEnumPortableDeviceObjectIDs result);
    void Properties(out IPortableDeviceProperties properties);
    void Transfer(out IPortableDeviceResources resources);
    void CreateObjectWithPropertiesOnly(IPortableDeviceValues values, [MarshalAs(UnmanagedType.LPWStr)] out string objectId);
    void CreateObjectWithPropertiesAndData(IPortableDeviceValues values, out IStream data, ref uint optimalWriteBufferSize, IntPtr cookie);
    [PreserveSig] int Delete(uint options, IPortableDevicePropVariantCollection objectIds, ref IPortableDevicePropVariantCollection? results);
}

[ComImport, Guid("10ece955-cf41-4728-bfa0-41eedf1bbf19"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumPortableDeviceObjectIDs
{
    [PreserveSig] int Next(uint count, [In, Out, MarshalAs(UnmanagedType.LPArray)] IntPtr[] objectIds, ref uint fetched);
}

[ComImport, Guid("7f6d695c-03df-4439-a809-59266beee3a6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceProperties
{
    void GetSupportedProperties([MarshalAs(UnmanagedType.LPWStr)] string objectId, out IPortableDeviceKeyCollection keys);
    void GetPropertyAttributes([MarshalAs(UnmanagedType.LPWStr)] string objectId, ref PropertyKey key, out IPortableDeviceValues attributes);
    void GetValues([MarshalAs(UnmanagedType.LPWStr)] string objectId, IPortableDeviceKeyCollection? keys, out IPortableDeviceValues values);
}

[ComImport, Guid("fd8878ac-d841-4d17-891c-e6829cdb6934"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceResources
{
    void GetSupportedResources([MarshalAs(UnmanagedType.LPWStr)] string objectId, out IPortableDeviceKeyCollection keys);
    void GetResourceAttributes([MarshalAs(UnmanagedType.LPWStr)] string objectId, ref PropertyKey key, out IPortableDeviceValues attributes);
    void GetStream([MarshalAs(UnmanagedType.LPWStr)] string objectId, ref PropertyKey key, uint mode, ref uint optimalBufferSize, out IStream stream);
}

[ComImport, Guid("6848f6f2-3155-4f86-b6f5-263eeeab3143"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceValues
{
    void GetCount(ref uint count);
    void GetAt(uint index, ref PropertyKey key, ref PropVariant value);
    void SetValue(ref PropertyKey key, ref PropVariant value);
    [PreserveSig] int GetValue(ref PropertyKey key, ref PropVariant value);
    void SetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int GetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] out string? value);
    void SetUnsignedIntegerValue(ref PropertyKey key, uint value);
}

[ComImport, Guid("dada2357-e0ad-492e-98db-dd61c53ba353"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceKeyCollection
{
    void GetCount(ref uint count);
    void GetAt(uint index, ref PropertyKey key);
    void Add(ref PropertyKey key);
}

[ComImport, Guid("89b2e422-4f1b-4316-bcef-a44afea83eb3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDevicePropVariantCollection
{
    void GetCount(ref uint count);
    void GetAt(uint index, ref PropVariant value);
    void Add(ref PropVariant value);
}

