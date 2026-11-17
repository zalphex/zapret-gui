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

public static class ZapretProxyTray
{
    private const string WindowPrefix = "TgWsProxy";
    private const string WindowSuffix = "SystemTrayIcon";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint IconId;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid IconGuid;
        public IntPtr BalloonIcon;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetClassNameW(IntPtr window, System.Text.StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData icon);

    public static void HideManagedProxy(string proxyPath)
    {
        var processIds = new System.Collections.Generic.HashSet<uint>();
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("TgWsProxy_windows"))
        {
            using (process)
            {
                try
                {
                    if (String.Equals(process.MainModule.FileName, proxyPath, StringComparison.OrdinalIgnoreCase))
                        processIds.Add((uint)process.Id);
                }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
            }
        }
        if (processIds.Count == 0) return;

        var className = new System.Text.StringBuilder(256);
        EnumWindows(delegate(IntPtr window, IntPtr parameter)
        {
            uint processId;
            GetWindowThreadProcessId(window, out processId);
            if (!processIds.Contains(processId) ||
                GetClassNameW(window, className, className.Capacity) == 0)
                return true;

            string name = className.ToString();
            // The upstream pystray Win32 backend includes Python's icon ID in the class name.
            if (!name.StartsWith(WindowPrefix, StringComparison.Ordinal) ||
                !name.EndsWith(WindowSuffix, StringComparison.Ordinal))
                return true;
            int length = name.Length - WindowPrefix.Length - WindowSuffix.Length;
            ulong id;
            if (length <= 0 || !UInt64.TryParse(name.Substring(WindowPrefix.Length, length),
                System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out id))
                return true;

            var icon = new NotifyIconData
            {
                Size = (uint)Marshal.SizeOf(typeof(NotifyIconData)),
                Window = window,
                IconId = unchecked((uint)id)
            };
            if (!Shell_NotifyIconW(2, ref icon)) // NIM_DELETE
            {
                // Some pystray releases pass hID instead of the uID field in
                // NOTIFYICONDATA. ctypes then leaves the actual icon ID at zero.
                icon.IconId = 0;
                Shell_NotifyIconW(2, ref icon);
            }
            return true;
        }, IntPtr.Zero);
    }
}
