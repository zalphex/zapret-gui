using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Collections.Generic;
using Microsoft.Win32;

internal static class Maintenance
{
    internal static readonly string UpdateFlag = Path.Combine(Paths.Utils, "check_updates.enabled");
    internal static bool CheckUpdates
    {
        get { return Disk.Option("CheckZapretUpdates", File.Exists(UpdateFlag)); }
    }

    internal static string[] Samples()
    {
        return Directory.Exists(Paths.Bin) ? Directory.GetFiles(Paths.Bin, "*.bin").Select(Path.GetFileName).Where(s => !s.StartsWith("ACTIVE_", StringComparison.OrdinalIgnoreCase)).OrderBy(s => s).ToArray() : new string[0];
    }

    internal static void ReplaceSample(string name, bool discord)
    {
        if (!Samples().Contains(name))
            throw new IOException("Выбери файл из установленного пакета.");
        string path = Path.Combine(Paths.Bin, discord ? "ACTIVE_DISCORD_UDP.bin" : "ACTIVE_GAME_UDP.bin");
        Components.Change(Component.Zapret, new[] { path }, () => Disk.Write(path, File.ReadAllBytes(Path.Combine(Paths.Bin, name))));
    }

    internal static void InstallService(string strategy)
    {
        Components.Guard(Component.Zapret);
        AddressLists.Ensure();
        string args = Strategies.Arguments(strategy);
        bool running = Processes.Running(Paths.ZapExe);
        string original = Components.Selected(Component.Zapret), previous = ZapService.Image;
        Components.Stop(Component.Zapret);
        try
        {
            ZapService.SetImage("\"" + Paths.ZapExe + "\" " + args);
            ZapService.Start();
            Disk.Remember("Zapret", strategy);
        }
        catch
        {
            if (ZapService.Ours(ZapService.Image))
            {
                ZapService.Stop();
                if (previous == null)
                    ZapService.Remove();
                else
                    ZapService.SetImage(previous);
            }

            if (running)
                Components.Start(Component.Zapret, original);
            throw;
        }
    }

    internal static void Diagnose(Action<string> log)
    {
        ServiceTools.Inspect(log);
        foreach (var name in new[]
        {
            "zapret",
            "WinDivert",
            "BFE",
            "Dnscache"
        }

        )
            log(name + " · " + ZapService.Status(name));
        log("WinDivert64.sys · " + (File.Exists(Path.Combine(Paths.Bin, "WinDivert64.sys")) ? "найден" : "отсутствует"));
        foreach (var c in Components.All)
        {
            log(Components.Title(c) + " · " + (Processes.Running(Paths.Binary(c)) ? "работает" : Components.Installed(c) ? "остановлен" : "не установлен"));
            if (Processes.Other(Paths.Binary(c)))
                log("Запущена отдельная копия " + Components.Title(c));
        }

        var proxy = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Internet Settings", "ProxyEnable", 0);
        log("Системный прокси · " + (Convert.ToInt32(proxy) != 0 ? "включён" : "выключен"));
        var adapters = NetworkInterface.GetAllNetworkInterfaces().Where(x => x.OperationalStatus == OperationalStatus.Up).ToArray();
        log("Сетевые интерфейсы · " + string.Join(", ", adapters.Select(a => a.Name)));
        log("DNS · " + string.Join(", ", adapters.SelectMany(a => a.GetIPProperties().DnsAddresses).Select(a => a.ToString()).Distinct()));
        foreach (var name in new[]
        {
            "AdguardSvc",
            "KillerNetworkService",
            "SmartByteNetworkService",
            "TracSrvWrapper",
            "GoodbyeDPI"
        }

        )
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Length > 0)
                    log("Возможный конфликт · " + name);
            }
            finally
            {
                foreach (var p in processes)
                    p.Dispose();
            }
        }

        if (File.Exists(Paths.Config))
        {
            var cfg = ProxySettings.Read();
            log("Порт прокси " + cfg["port"] + " · " + (Processes.OwnListener(Convert.ToInt32(cfg["port"])) ? "слушает" : "не занят прокси"));
        }
    }

    private const string Begin = "# zapretgui: Flowseal hosts begin", End = "# zapretgui: Flowseal hosts end";
    internal static string MergeHosts(string current, string downloaded)
    {
        var rows = downloaded.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")).ToArray();
        if (rows.Length == 0)
            throw new IOException("Список hosts пуст.");
        foreach (string row in rows)
        {
            var fields = row.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            IPAddress ip;
            if (fields.Length != 2 || !IPAddress.TryParse(fields[0], out ip) || Uri.CheckHostName(fields[1]) != UriHostNameType.Dns)
                throw new IOException("Некорректная запись hosts.");
        }

        string withoutBlock = RemoveHostsBlock(current);
        string separator = withoutBlock.Length == 0 || withoutBlock.EndsWith("\n") ? "" : "\r\n";
        return withoutBlock + separator + Begin + "\r\n" + string.Join("\r\n", rows) + "\r\n" + End + "\r\n";
    }

    internal static string RemoveHostsBlock(string current)
    {
        var begin = System.Text.RegularExpressions.Regex.Matches(current, "^" + Begin + @"\r?$",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        var end = System.Text.RegularExpressions.Regex.Matches(current, "^" + End + @"\r?$",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        if (begin.Count == 0 && end.Count == 0) return current;
        if (begin.Count != 1 || end.Count != 1 || begin[0].Index >= end[0].Index)
            throw new IOException("Проверь границы блока zapretgui в hosts.");
        int after = end[0].Index + end[0].Length;
        if (after < current.Length && current[after] == '\n') after++;
        return current.Remove(begin[0].Index, after - begin[0].Index);
    }

    private static string HostsPath
    {
        get { return Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts"); }
    }

    internal static string HostsSource()
    {
        return Downloads.Text(Downloads.ServiceFiles + "hosts", 262144);
    }

    private static string DecodeHosts(byte[] content)
    {
        if (content == null) return "";
        string text = Disk.Utf8.GetString(content);
        if (!Disk.Utf8.GetBytes(text).SequenceEqual(content))
            throw new IOException("Файл hosts использует другую кодировку. Изменения не записаны.");
        return text;
    }

    internal static bool CanRestoreHosts()
    {
        var before = Disk.Snapshot(HostsPath);
        return before != null && Disk.Utf8.GetString(before).Contains(Begin);
    }

    internal static void RestoreHosts()
    {
        var before = Disk.Snapshot(HostsPath);
        string oldText = DecodeHosts(before);
        SaveHosts(before, oldText, RemoveHostsBlock(oldText));
    }

    internal static void Hosts(string expected)
    {
        string source = HostsSource();
        if (Disk.Token(Disk.Utf8.GetBytes(source)) != expected)
            throw new IOException("Список Flowseal изменился. Открой предпросмотр снова.");
        var before = Disk.Snapshot(HostsPath);
        string oldText = DecodeHosts(before);
        SaveHosts(before, oldText, MergeHosts(oldText, source));
    }

    private static void SaveHosts(byte[] before, string oldText, string next)
    {
        if (next == oldText) return;
        if (before != null)
            Disk.Write(HostsPath + ".zapretgui-" + Guid.NewGuid().ToString("N") + ".bak", before);
        if (Disk.Token(Disk.Snapshot(HostsPath)) != Disk.Token(before))
            throw new IOException("Файл hosts был изменён.");
        Disk.Write(HostsPath, Disk.Utf8.GetBytes(next));
    }
}
