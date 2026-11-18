using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Collections.Generic;

internal sealed class GamePorts
{
    internal string Mode = "disabled", Tcp = "1024-65535", Udp = "1024-65535";
    internal static readonly string[] Modes =
    {
        "disabled",
        "tcp",
        "udp",
        "all"
    };
    internal string ActiveTcp
    {
        get
        {
            return Mode == "tcp" || Mode == "all" ? Tcp : "12";
        }
    }

    internal string ActiveUdp
    {
        get
        {
            return Mode == "udp" || Mode == "all" ? Udp : "12";
        }
    }

    internal static readonly string FileName = Path.Combine(Paths.Utils, "game_filter.enabled");
    internal static void Check(string range)
    {
        foreach (string item in range.Split(','))
        {
            var pair = item.Split('-');
            int first, last;
            if (pair.Length > 2 || !Regex.IsMatch(item, @"^\d{1,5}(-\d{1,5})?$") || !int.TryParse(pair[0], out first) || !int.TryParse(pair.Last(), out last) || first < 1 || last > 65535 || first > last)
                throw new InvalidDataException("Недопустимый диапазон портов: " + item);
        }
    }

    internal static GamePorts Load()
    {
        var p = new GamePorts();
        if (File.Exists(FileName))
            foreach (var line in File.ReadLines(FileName))
            {
                string[] kv = line.Trim().ToLowerInvariant().Split('=');
                string val = kv.Last();
                if ((kv.Length == 1 || kv[0] == "mode") && Modes.Contains(val))
                    p.Mode = val;
                if (kv.Length != 2 || (kv[0] != "tcp" && kv[0] != "udp"))
                    continue;
                Check(val);
                if (kv[0] == "tcp")
                    p.Tcp = val;
                else
                    p.Udp = val;
            }

        return p;
    }

    internal byte[] Encode()
    {
        Check(Tcp);
        Check(Udp);
        if (!Modes.Contains(Mode))
            throw new InvalidDataException("Неизвестный игровой фильтр.");
        return Disk.Utf8.GetBytes("mode=" + Mode + "\r\ntcp=" + Tcp + "\r\nudp=" + Udp + "\r\n");
    }
}

internal static class Strategies
{
    private static readonly Regex Start = new Regex(@"^\s*@?start\s+(?:""[^""]*""\s+)?(?:/(?:min|max|b)\s+)*(?:""(?:%BIN%|%~dp0bin\\)winws\.exe""|(?:%BIN%)?winws\.exe)\s+(?<options>--.*)$", RegexOptions.IgnoreCase);
    internal static string[] Names()
    {
        if (!Directory.Exists(Paths.Zap))
            return new string[0];
        return Directory.GetFiles(Paths.Zap, "*.bat").Select(Path.GetFileName).Where(name => !name.StartsWith("service", StringComparison.OrdinalIgnoreCase)).OrderBy(name => name.Equals("general.bat", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static string Arguments(string name)
    {
        if (!Names().Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new IOException("Стратегия отсутствует: " + name);
        return Parse(File.ReadAllText(Path.Combine(Paths.Zap, name)), Paths.Zap, GamePorts.Load());
    }

    internal static string Parse(string script, string directory, GamePorts ports)
    {
        string joined = Regex.Replace(script, @"\^[ \t]*\r?\n[ \t]*", " ");
        var commands = joined.Replace("\r", "").Split('\n').Select(line => Start.Match(line)).Where(m => m.Success).ToArray();
        if (commands.Length != 1)
            throw new InvalidDataException("Нужна одна команда запуска winws.exe в формате стратегии Flowseal.");
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {
                "BIN",
                Path.Combine(directory, "bin") + Path.DirectorySeparatorChar
            },
            {
                "LISTS",
                Path.Combine(directory, "lists") + Path.DirectorySeparatorChar
            },
            {
                "GameFilterTCP",
                ports.ActiveTcp
            },
            {
                "GameFilterUDP",
                ports.ActiveUdp
            },
            {
                "GameFilter",
                ports.Mode == "udp" ? ports.ActiveUdp : ports.ActiveTcp
            }
        };
        // cmd consumes a caret before an unquoted literal (for example ^!).
        string template = commands[0].Groups["options"].Value;
        var decoded = new System.Text.StringBuilder();
        bool quoted = false;
        for (int at = 0; at < template.Length; at++)
        {
            char ch = template[at];
            if (ch == '"')
                quoted = !quoted;
            else if (!quoted && ch == '^' && at + 1 < template.Length)
                ch = template[++at];
            decoded.Append(ch);
        }

        if (quoted)
            throw new InvalidDataException("В стратегии не закрыты кавычки.");
        string args = Regex.Replace(decoded.ToString(), @"%([^%]+)%", m =>
        {
            string value;
            if (!vars.TryGetValue(m.Groups[1].Value, out value))
                throw new InvalidDataException("Неизвестная переменная стратегии: " + m.Value);
            return value;
        });
        if (args.Length > 30000)
            throw new InvalidDataException("Слишком длинная команда стратегии.");
        return args.Trim();
    }
}
