using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Net;
using Microsoft.Win32;
using System.ComponentModel;

internal static class Processes
{
    internal static bool Same(string a, string b)
    {
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static string Image(Process p)
    {
        try
        {
            return p.MainModule.FileName;
        }
        catch (Win32Exception)
        {
            return "";
        }
        catch (InvalidOperationException)
        {
            return "";
        }
    }

    private static bool Visit(string path, bool external, Action<Process> act)
    {
        var candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path));
        bool found = false;
        try
        {
            foreach (var p in candidates)
            {
                if (Same(Image(p), path) == external)
                    continue;
                found = true;
                if (act == null)
                    break;
                act(p);
            }
        }
        finally
        {
            foreach (var p in candidates)
                p.Dispose();
        }

        return found;
    }

    internal static bool Running(string path)
    {
        return Visit(path, false, null);
    }

    internal static bool Other(string path)
    {
        return Visit(path, true, null);
    }

    internal static void Stop(string path)
    {
        Visit(path, false, p =>
        {
            try
            {
                if (!p.HasExited)
                    p.Kill();
            }
            catch (InvalidOperationException)
            {
                return;
            }

            if (!p.WaitForExit(6000))
                throw new IOException("Процесс не завершился: " + p.ProcessName);
        });
    }

    internal static Process Launch(string executable, string args, string cwd)
    {
        return Process.Start(new ProcessStartInfo(executable, args) { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true });
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int length, bool order, int family, int kind, uint reserved);
    internal static bool OwnListener(int port)
    {
        int bytes = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref bytes, false, 2, 3, 0);
        if (bytes < 4)
            return false;
        var block = Marshal.AllocHGlobal(bytes);
        try
        {
            if (GetExtendedTcpTable(block, ref bytes, false, 2, 3, 0) != 0)
                return false;
            int rows = Marshal.ReadInt32(block);
            for (int i = 0; i < rows; i++)
            {
                int row = 4 + i * 24;
                if (Marshal.ReadInt32(block, row + 4) != 0x0100007f || (ushort)IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(block, row + 8)) != port)
                    continue;
                try
                {
                    using (var p = Process.GetProcessById(Marshal.ReadInt32(block, row + 20)))
                        return Same(Image(p), Paths.TgExe);
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }

            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }
}

internal static class ZapService
{
    internal static string Image
    {
        get
        {
            return (string)Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\zapret", "ImagePath", null);
        }
    }

    internal static bool Ours(string image)
    {
        return image != null && image.StartsWith("\"" + Paths.ZapExe + "\" ", StringComparison.OrdinalIgnoreCase);
    }

    internal static void Guard()
    {
        var image = Image;
        if (image != null && !Ours(image))
            throw new IOException("Обнаружена отдельная служба zapret. Управляй ей из её установки.");
    }

    internal static void Stop()
    {
        StopOwned(Paths.ZapExe);
    }

    internal static void StopOwned(string exe)
    {
        string image = Image;
        if (image == null || !image.StartsWith("\"" + exe + "\" ", StringComparison.OrdinalIgnoreCase))
            return;
        using (var sc = new ServiceController("zapret"))
        {
            if (sc.Status == ServiceControllerStatus.Stopped)
                return;
            if (sc.Status != ServiceControllerStatus.StopPending)
                sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(12));
        }
    }

    internal static string Status(string name)
    {
        using (var s = new ServiceController(name))
        {
            try
            {
                return s.Status.ToString();
            }
            catch (InvalidOperationException e)
            {
                var native = e.InnerException as Win32Exception;
                if (native != null && native.NativeErrorCode == 1060)
                    return "не установлена";
                throw;
            }
        }
    }

    // Quote a single Windows argv value. ImagePath itself contains quoted paths.
    internal static string Quote(string s)
    {
        var b = new System.Text.StringBuilder("\"");
        int slashes = 0;
        foreach (char c in s)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }

            if (c == '"')
            {
                b.Append('\\', slashes * 2 + 1);
                b.Append('"');
            }
            else
            {
                b.Append('\\', slashes);
                b.Append(c);
            }

            slashes = 0;
        }

        return b.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static void Sc(string args)
    {
        using (var p = Processes.Launch(Path.Combine(Environment.SystemDirectory, "sc.exe"), args, Environment.SystemDirectory))
        {
            if (!p.WaitForExit(15000))
                throw new System.TimeoutException("Истекло время ответа диспетчера служб.");
            if (p.ExitCode != 0)
                throw new Win32Exception(p.ExitCode);
        }
    }

    internal static void Relocate(string oldExe, string image, string oldRoot)
    {
        if (Image != image || !image.StartsWith(Quote(oldExe) + " ", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Путь службы zapret изменился во время переноса.");
        string updated = System.Text.RegularExpressions.Regex.Replace(image,
            System.Text.RegularExpressions.Regex.Escape(oldRoot + Path.DirectorySeparatorChar),
            match => Paths.Root + Path.DirectorySeparatorChar, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Sc("config zapret binPath= " + Quote(updated));
    }

    internal static void SetImage(string image)
    {
        Guard();
        Sc((Image == null ? "create zapret start= auto" : "config zapret") + " binPath= " + Quote(image));
    }

    internal static void Start()
    {
        using (var s = new ServiceController("zapret"))
        {
            s.Start();
            s.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(12));
        }
    }

    internal static void Remove()
    {
        Guard();
        if (Image == null)
            return;
        Stop();
        Sc("delete zapret");
    }
}
