using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

public static class ZapretTaskbarIdentity
{
    private const string AppId = "Zapret.GUI";
    private static readonly Guid AppModel = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private static readonly Guid ShellLink = new Guid("00021401-0000-0000-C000-000000000046");

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid Format;
        public uint Id;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropertyValue
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Text;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropertyValue value);
        void SetValue(ref PropertyKey key, ref PropertyValue value);
        void Commit();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetPropertyStoreForWindow(IntPtr window, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    public static void SetProcess()
    {
        Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(AppId));
    }

    public static void SetWindow(IntPtr window)
    {
        Guid iid = typeof(IPropertyStore).GUID;
        IPropertyStore store;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(window, ref iid, out store));
        try { WriteId(store); }
        finally { Marshal.ReleaseComObject(store); }
    }

    public static void SetShortcut(string path)
    {
        object link = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLink));
        try
        {
            var file = (IPersistFile)link;
            file.Load(path, 2); // STGM_READWRITE
            var store = (IPropertyStore)link;
            WriteId(store);
            store.Commit();
            file.Save(path, true);
        }
        finally { Marshal.ReleaseComObject(link); }
    }

    private static void WriteId(IPropertyStore store)
    {
        var key = new PropertyKey { Format = AppModel, Id = 5 };
        var value = new PropertyValue { Type = 31, Text = Marshal.StringToCoTaskMemUni(AppId) };
        try { store.SetValue(ref key, ref value); }
        finally { Marshal.FreeCoTaskMem(value.Text); }
    }
}
