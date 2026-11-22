using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.IO;

internal sealed class Reading
{
    internal string Name, Details;
    internal int Best;
    internal string Value
    {
        get
        {
            return Best < 0 ? "Нет ответа" : Best + " мс";
        }
    }
}

internal sealed class Trial
{
    internal string Name;
    internal int First, Second;
    internal double Mean;
    internal static Trial Winner(IEnumerable<Trial> rows)
    {
        return rows.Where(r => r.First + r.Second > 0).OrderByDescending(r => (r.First > 0 ? 1 : 0) + (r.Second > 0 ? 1 : 0)).ThenByDescending(r => r.First + r.Second).ThenBy(r => r.Mean).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }
}

internal static class Checks
{
    internal static readonly string[] Services =
    {
        "YouTube",
        "Discord",
        "Telegram"
    };
    internal static readonly string[] Urls =
    {
        "https://www.youtube.com/",
        "https://discord.com/api/v10/gateway",
        "https://web.telegram.org/"
    };
    private static readonly string[] Hosts =
    {
        "www.youtube.com",
        "discord.com",
        "telegram.org"
    };
    private static string Ms(int value)
    {
        return value < 0 ? "нет ответа" : value + " мс";
    }

    internal static Reading Measure(int i)
    {
        var web = Task.Run(() => FlowProbe.Request(Urls[i]));
        var icmp = Task.Run(() => FlowProbe.Icmp(Hosts[i]));
        bool proxy = i == 2 && Processes.Running(Paths.TgExe);
        int mt = -1;
        if (proxy)
        {
            mt = ProxySettings.Probe(2);
            if (mt < 0)
                mt = ProxySettings.Probe(4);
        }

        var result = web.Result;
        string full = "HTTPS " + new Uri(Urls[i]).Host + " · " + result.Describe();
        if (i == 2 && !result.Success)
        {
            result = FlowProbe.Request("https://telegram.org/");
            full += "\nHTTPS telegram.org · " + result.Describe();
        }

        int ping = icmp.Result;
        full += "\nICMP · " + Ms(ping);
        if (i == 2)
            full += proxy ? "\nMTProto · " + Ms(mt) : "\nMTProto · прокси выключен";
        return new Reading
        {
            Name = Services[i],
            Details = full,
            Best = mt >= 0 ? mt : result.Success ? result.Time : ping
        };
    }

    internal static string Test(Component c, bool apply, CancellationToken token, Action<string> log, Action<int, int, string> progress, Func<string, bool> confirm = null)
    {
        Components.Guard(c);
        bool zap = c == Component.Zapret;
        string[] names = zap ? Strategies.Names() : ProxySettings.Profiles;
        if (names.Length == 0)
            throw new InvalidOperationException("Конфиги отсутствуют.");
        if (!zap)
            ProxySettings.Ensure();
        byte[] saved = zap ? null : File.ReadAllBytes(Paths.Config);
        bool running = Processes.Running(Paths.Binary(c));
        string original = Components.Selected(c), service = zap ? ZapService.Image : null;
        var results = new List<Trial>();
        try
        {
            for (int index = 0; index < names.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                string name = names[index];
                progress(index, names.Length, name);
                Components.Stop(c);
                var row = new Trial
                {
                    Name = name
                };
                var times = new List<int>();
                try
                {
                    if (!zap)
                        ProxySettings.Profile(name);
                    Components.Start(c, name);
                    for (int round = 0; round < 2; round++)
                    {
                        token.ThrowIfCancellationRequested();
                        var first = Task.Run(() => zap ? FlowProbe.Https(Urls[0]) : ProxySettings.Probe(2));
                        int second = zap ? FlowProbe.Https(Urls[1]) : ProxySettings.Probe(4);
                        if (first.Result >= 0)
                        {
                            row.First++;
                            times.Add(first.Result);
                        }

                        if (second >= 0)
                        {
                            row.Second++;
                            times.Add(second);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    log(name + " · " + e.GetBaseException().Message);
                }

                row.Mean = times.Count == 0 ? double.PositiveInfinity : times.Average();
                results.Add(row);
                log(name + " · " + (zap ? "YouTube " : "DC2 ") + row.First + "/2 · " + (zap ? "Discord " : "DC4 ") + row.Second + "/2" + (times.Count == 0 ? "" : " · " + Math.Round(row.Mean) + " мс"));
                progress(index + 1, names.Length, name);
            }
        }
        finally
        {
            Components.Stop(c);
            if (!zap)
                Disk.Restore(Paths.Config, saved);
            if (service != null)
                ZapService.SetImage(service);
            if (running)
                Components.Start(c, original);
        }

        token.ThrowIfCancellationRequested();
        var winner = Trial.Winner(results);
        if (winner == null)
            throw new IOException("Ни один конфиг не получил ответа. Исходное состояние восстановлено.");
        if (apply && !Components.Apply(c, winner.Name, confirm))
        {
            log("Конфиг не сохранён. Исходное состояние восстановлено.");
            return null;
        }
        log((apply ? "Применён лучший конфиг: " : "Лучший конфиг: ") + winner.Name);
        return winner.Name;
    }
}
