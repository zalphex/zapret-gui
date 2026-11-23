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

        var lines = current.Replace("\r", "").Split('\n').ToList();
        int a = lines.IndexOf(Begin), b = lines.IndexOf(End);
        if (a >= 0 || b >= 0)
        {
            if (a < 0 || b <= a || a != lines.LastIndexOf(Begin) || b != lines.LastIndexOf(End))
                throw new IOException("Проверь границы блока zapretgui в hosts.");
            lines.RemoveRange(a, b - a + 1);
        }

        while (lines.Count > 0 && lines.Last() == "")
            lines.RemoveAt(lines.Count - 1);
        lines.Add("");
        lines.Add(Begin);
        lines.AddRange(rows);
        lines.Add(End);
        lines.Add("");
        return string.Join("\r\n", lines);
    }

    internal static void Hosts()
    {
        string path = Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
        var source = Downloads.Text(Downloads.ServiceFiles + "hosts", 262144);
        var before = Disk.Snapshot(path);
        string text = before == null ? "" : Disk.Utf8.GetString(before);
        if (before != null && !Disk.Utf8.GetBytes(text).SequenceEqual(before))
            throw new IOException("Файл hosts использует другую кодировку. Изменения не записаны.");
        string merged = MergeHosts(text, source);
        if (merged == text)
            return;
        if (Disk.Token(Disk.Snapshot(path)) != Disk.Token(before))
            throw new IOException("Файл hosts был изменён.");
        if (before != null)
            Disk.Write(path + ".zapretgui-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".bak", before);
        Disk.Write(path, Disk.Utf8.GetBytes(merged));
    }
}
