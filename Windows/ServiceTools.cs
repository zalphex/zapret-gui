using System;
using System.Diagnostics;
using System.ComponentModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Security.Principal;
using Microsoft.Win32;

internal static class ServiceTools
{
    internal static readonly string[] Known =
    {
        "WinDivert",
        "WinDivert14",
        "GoodbyeDPI",
        "discordfix_zapret",
        "winws1",
        "winws2"
    };
    internal static string[] Conflicts()
    {
        return Known.Where(n => Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\" + n, "ImagePath", null) != null).ToArray();
    }

    internal static void Remove(string name)
    {
        if (!Known.Contains(name) || !Conflicts().Contains(name))
            throw new IOException("Выбери установленную службу.");
        if (name.StartsWith("WinDivert") && (Processes.Running(Paths.ZapExe) || Processes.Other(Paths.ZapExe)))
            throw new IOException("Сначала останови winws перед удалением драйвера.");
        using (var s = new ServiceController(name))
        {
            if (s.Status != ServiceControllerStatus.Stopped)
            {
                if (s.Status != ServiceControllerStatus.StopPending)
                    s.Stop();
                s.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }
        }

        using (var p = Processes.Launch(Path.Combine(Environment.SystemDirectory, "sc.exe"), "delete " + name, Environment.SystemDirectory))
        {
            if (!p.WaitForExit(15000))
                throw new System.TimeoutException();
            if (p.ExitCode != 0)
                throw new Win32Exception(p.ExitCode);
        }
    }

    internal static void TcpTimestamps()
    {
        using (var p = Processes.Launch(Path.Combine(Environment.SystemDirectory, "netsh.exe"), "interface tcp set global timestamps=enabled", Environment.SystemDirectory))
        {
            if (!p.WaitForExit(15000) || p.ExitCode != 0)
                throw new IOException("Windows не применила настройку TCP timestamps.");
        }
    }

    private static bool Doh(RegistryKey key)
    {
        if (key == null)
            return false;
        if (Convert.ToInt64(key.GetValue("DohFlags", 0)) > 0)
            return true;
        foreach (var name in key.GetSubKeyNames())
            using (var child = key.OpenSubKey(name))
                if (Doh(child))
                    return true;
        return false;
    }

    internal static void Inspect(Action<string> log)
    {
        log("Папка zapret · " + Paths.Zap);
        if (System.Text.RegularExpressions.Regex.IsMatch(Paths.Zap, @"[А-Яа-яЁё]"))
            log("В пути установки есть кириллица.");
        var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        if (!string.IsNullOrEmpty(oneDrive) && Paths.Zap.StartsWith(oneDrive + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            log("Папка находится в OneDrive.");
        using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters"))
            log("DNS over HTTPS Windows · " + (Doh(key) ? "обнаружен" : "настройка не найдена"));
        string hosts = Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
        if (File.Exists(hosts) && File.ReadLines(hosts).Select(s => s.Split('#')[0]).Any(s => s.IndexOf("youtube.com", StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf("youtu.be", StringComparison.OrdinalIgnoreCase) >= 0))
            log("В hosts есть адреса YouTube.");
        var services = ServiceController.GetServices();
        try
        {
            foreach (var s in services)
            {
                string label = s.ServiceName + " " + s.DisplayName;
                if (System.Text.RegularExpressions.Regex.IsMatch(label, @"Killer|Intel.*Connectivity.*Network|TracSrvWrapper|EPWD|SmartByte|VPN|HappService|GoodbyeDPI|discordfix_zapret|\bwinws[12]\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    log("Сетевая служба · " + s.DisplayName + " · " + s.Status);
            }
        }
        finally
        {
            foreach (var s in services)
                s.Dispose();
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int type, out IntPtr data, int size, out int written);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);
    internal static void ClearDiscordCache(Action<string> log)
    {
        // AppData belongs to the interactive user. Walk it only with the linked, non-elevated token.
        using (var admin = WindowsIdentity.GetCurrent())
        {
            IntPtr token;
            int length;
            if (!GetTokenInformation(admin.Token, 19, out token, IntPtr.Size, out length))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                using (WindowsIdentity.Impersonate(token))
                {
                    if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                        throw new IOException("Нужен обычный токен пользователя для очистки кэша.");
                    var basePath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    int removed = 0;
                    foreach (var edition in new[]
                    {
                        "discord",
                        "discordptb",
                        "discordcanary",
                        "discorddevelopment"
                    }

                    )
                    {
                        string folder = Path.Combine(basePath, edition);
                        if (!Directory.Exists(folder))
                            continue;
                        var processes = Process.GetProcessesByName(edition);
                        try
                        {
                            if (processes.Length != 0)
                                throw new IOException("Закрой " + edition + " перед очисткой кэша.");
                        }
                        finally
                        {
                            foreach (var p in processes)
                                p.Dispose();
                        }

                        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                            throw new IOException("Папка Discord является ссылкой.");
                        foreach (var cache in new[]
                        {
                            "Cache",
                            "Code Cache",
                            "GPUCache"
                        }

                        )
                        {
                            var path = Path.Combine(folder, cache);
                            if (!Directory.Exists(path))
                                continue;
                            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                                throw new IOException("Папка кэша является ссылкой.");
                            Directory.Delete(path, true);
                            removed++;
                        }
                    }

                    log("Удалено папок кэша Discord: " + removed);
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }
    }
}
