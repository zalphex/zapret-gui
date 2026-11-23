using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

internal static class DesktopLaunch
{
    private const uint QueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenAssignPrimary = 0x0001;
    private const int TokenPrimary = 1;
    private const int SecurityImpersonation = 2;
    private const int TokenElevation = 20;

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeFileHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, IntPtr attributes,
        int impersonation, int type, out SafeAccessTokenHandle duplicate);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind,
        out uint value, int length, out int returned);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessWithTokenW(SafeAccessTokenHandle token, uint logonFlags,
        string executable, IntPtr command, uint flags, IntPtr environment, string directory,
        ref StartupInfo startup, out ProcessInformation process);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal int Size;
        internal string Reserved, Desktop, Title;
        internal int X, Y, Width, Height, Columns, Rows, Fill, Flags;
        internal short Show, ReservedLength;
        internal IntPtr ReservedPointer, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal IntPtr Process, Thread;
        internal uint ProcessId, ThreadId;
    }

    internal static void Open(bool tray)
    {
        const string hint = "Открой zapretgui через меню «Пуск» обычным щелчком.";
        var shell = GetShellWindow();
        if (shell == IntPtr.Zero)
            throw new IOException("Не найден рабочий стол Windows. " + hint);
        uint pid;
        if (GetWindowThreadProcessId(shell, out pid) == 0)
            throw LaunchError(hint);

        using (var owner = OpenProcess(QueryLimitedInformation, false, pid))
        {
            if (owner.IsInvalid)
                throw LaunchError(hint);
            SafeAccessTokenHandle shellToken;
            if (!OpenProcessToken(owner, TokenQuery | TokenDuplicate, out shellToken))
                throw LaunchError(hint);
            using (shellToken)
            {
                uint elevated;
                int returned;
                if (!GetTokenInformation(shellToken, TokenElevation, out elevated, sizeof(uint), out returned))
                    throw LaunchError(hint);
                using (var desktopUser = new WindowsIdentity(shellToken.DangerousGetHandle()))
                {
                    if (elevated != 0 || new WindowsPrincipal(desktopUser).IsInRole(WindowsBuiltInRole.Administrator))
                        throw new IOException("Рабочий стол Windows запущен с правами администратора. " + hint);
                }

                SafeAccessTokenHandle primary;
                if (!DuplicateTokenEx(shellToken, TokenAssignPrimary | TokenDuplicate | TokenQuery, IntPtr.Zero,
                    SecurityImpersonation, TokenPrimary, out primary))
                    throw LaunchError(hint);
                using (primary)
                {
                    string file = Path.Combine(Paths.Desktop, tray ? "zapretgui-tray.exe" : "zapretgui.exe");
                    var startup = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfo)) };
                    ProcessInformation process;
                    if (!CreateProcessWithTokenW(primary, 0, file, IntPtr.Zero, 0, IntPtr.Zero, Paths.Desktop,
                        ref startup, out process))
                        throw LaunchError(hint);
                    CloseHandle(process.Thread);
                    CloseHandle(process.Process);
                }
            }
        }
    }

    private static IOException LaunchError(string hint)
    {
        var error = new Win32Exception(Marshal.GetLastWin32Error());
        return new IOException("Не удалось открыть панель в сеансе пользователя. " + hint + " Код Windows: " + error.NativeErrorCode, error);
    }
}
