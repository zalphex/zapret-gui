using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Diagnostics;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

internal static class ProxySettings
{
    internal static readonly string[] Profiles =
    {
        "Standard",
        "CF без HTTP/2",
        "Без CF"
    };
    internal static Dictionary<string, object> Read()
    {
        if (!File.Exists(Paths.Config))
            throw new IOException("Конфиг TG WS Proxy ещё не создан.");
        var config = Disk.ReadJson(Paths.Config);
        Validate(config);
        return config;
    }

    internal static void Validate(Dictionary<string, object> c)
    {
        int port;
        if (Disk.Get(c, "host") != "127.0.0.1" || !int.TryParse(Disk.Get(c, "port"), out port) || port < 1 || port > 65535 || !Regex.IsMatch(Disk.Get(c, "secret"), @"\A[0-9a-fA-F]{32}\z"))
            throw new InvalidDataException("Нужны host 127.0.0.1, порт 1-65535 и secret из 32 hex-символов.");
    }

    internal static void Ensure()
    {
        if (!File.Exists(Paths.TgExe))
            throw new IOException("Сначала установи TG WS Proxy.");
        if (!File.Exists(Paths.Config))
        {
            byte[] key = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(key);
            Disk.Json(Paths.Config, new Dictionary<string, object> { { "host", "127.0.0.1" }, { "port", 1443 }, { "secret", Disk.Hex(key) }, { "cfproxy", true }, { "h2", true }, { "language", "ru" }, { "appearance", "dark" }, { "autostart", false }, { "check_updates", false } });
        }

        Read();
        foreach (var f in new[]
        {
            ".first_run_done_mtproto",
            ".ipv6_warned"
        }

        )
        {
            string path = Path.Combine(Paths.TgData, f);
            if (!File.Exists(path))
                Disk.Write(path, new byte[0]);
        }
    }

    internal static string Link()
    {
        var c = Read();
        return "tg://proxy?server=127.0.0.1&port=" + Disk.Get(c, "port") + "&secret=dd" + Disk.Get(c, "secret");
    }

    internal static int Probe(short dc)
    {
        try
        {
            var c = Read();
            return FlowProbe.Telegram(Convert.ToInt32(c["port"]), Disk.Get(c, "secret"), dc);
        }
        catch (Exception)
        {
            return -1;
        }
    }

    internal static void Profile(string name)
    {
        int i = Array.IndexOf(Profiles, name);
        if (i < 0)
            throw new InvalidDataException("Выбери профиль прокси.");
        var cfg = Read();
        cfg["cfproxy"] = i != 2;
        cfg["h2"] = i == 0;
        Disk.Json(Paths.Config, cfg);
    }
}

internal static class Components
{
    internal static readonly Component[] All =
    {
        Component.Zapret,
        Component.TG
    };
    internal static string Title(Component c)
    {
        return c == Component.Zapret ? "zapret" : "TG WS Proxy";
    }

    internal static bool Installed(Component c)
    {
        return File.Exists(Paths.Binary(c));
    }

    internal static string Selected(Component c)
    {
        return Disk.State(c.ToString(), c == Component.Zapret ? "general.bat" : "Standard");
    }

    internal static void Guard(Component c)
    {
        if (!Installed(c))
            throw new IOException("Сначала установи " + Title(c) + ".");
        if (c == Component.Zapret)
            ZapService.Guard();
        if (Processes.Other(Paths.Binary(c)))
            throw new IOException("Обнаружена отдельно запущенная копия " + Title(c) + ".");
    }

    internal static void Stop(Component c)
    {
        Guard(c);
        if (c == Component.Zapret)
            ZapService.Stop();
        Processes.Stop(Paths.Binary(c));
    }

    internal static void Start(Component c, string config)
    {
        Guard(c);
        if (c == Component.TG)
        {
            ProxySettings.Ensure();
            int port = Convert.ToInt32(ProxySettings.Read()["port"]);
            if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(ep => ep.Port == port))
                throw new IOException("Порт " + port + " уже занят.");
            using (Processes.Launch(Paths.TgExe, "--portable", Paths.Tg))
            {
            }

            var wait = Stopwatch.StartNew();
            do
            {
                Thread.Sleep(200);
                if (Processes.OwnListener(port))
                    return;
            }
            while (wait.Elapsed.TotalSeconds < 18);
            Processes.Stop(Paths.TgExe);
            throw new TimeoutException("TG WS Proxy не открыл локальный порт.");
        }

        AddressLists.Ensure();
        string args = Strategies.Arguments(config);
        if (ZapService.Ours(ZapService.Image))
        {
            ZapService.SetImage("\"" + Paths.ZapExe + "\" " + args);
            ZapService.Start();
        }
        else
            using (var process = Processes.Launch(Paths.ZapExe, args, Paths.Bin))
            {
                if (process.WaitForExit(550))
                    throw new IOException("winws завершился при запуске, код " + process.ExitCode + ".");
            }
    }

    internal static void Apply(Component c, string next)
    {
        Apply(c, next, null);
    }

    internal static bool Apply(Component c, string next, Func<string, bool> confirm)
    {
        Guard(c);
        if (c == Component.Zapret)
            Strategies.Arguments(next);
        else
            ProxySettings.Ensure();
        bool running = Processes.Running(Paths.Binary(c));
        string previous = Selected(c), service = c == Component.Zapret ? ZapService.Image : null;
        var backup = c == Component.TG ? Disk.Snapshot(Paths.Config) : null;
        Stop(c);
        bool committed = false;
        try
        {
            if (c == Component.TG)
                ProxySettings.Profile(next);
            Start(c, next);
            if (confirm != null)
            {
                if (!confirm(next))
                    return false;
                if (!Processes.Running(Paths.Binary(c)))
                    throw new IOException("Компонент завершился во время пробного запуска.");
            }
            Disk.Remember(c.ToString(), next);
            committed = true;
            return true;
        }
        finally
        {
            if (!committed)
            {
                Stop(c);
                if (c == Component.TG)
                    Disk.Restore(Paths.Config, backup);
                if (service != null)
                    ZapService.SetImage(service);
                if (running)
                    Start(c, previous);
            }
        }
    }

    internal static void Change(Component c, string[] files, Action write)
    {
        Guard(c);
        var backup = files.Distinct().ToDictionary(p => p, Disk.Snapshot);
        bool restart = Processes.Running(Paths.Binary(c));
        if (restart)
            Stop(c);
        try
        {
            write();
            if (restart)
                Start(c, Selected(c));
        }
        catch
        {
            if (restart)
                Stop(c);
            foreach (var item in backup)
                Disk.Restore(item.Key, item.Value);
            if (restart)
                Start(c, Selected(c));
            throw;
        }
    }
}
