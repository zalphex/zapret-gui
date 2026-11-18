using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

internal sealed class ListEdit
{
    internal string Name, Text, Token, File;
    internal bool Inactive;
}

internal static class AddressLists
{
    internal static readonly string[] Names =
    {
        "list-general-user.txt",
        "list-exclude-user.txt",
        "ipset-all.txt",
        "ipset-exclude-user.txt"
    };
    internal static readonly string[] Hints =
    {
        "Домены для обхода. Поддомены учитываются автоматически.",
        "Исключения доменов, в том числе внутри подсетей IPSet.",
        "IP-адреса и подсети для обхода.",
        "Исключения IP-адресов и подсетей."
    };
    internal const string EmptyIp = "203.0.113.113/32";
    internal const int Limit = 8 * 1024 * 1024;
    internal static readonly string Active = Path.Combine(Paths.Lists, Names[2]), Backup = Active + ".backup";
    internal static string Mode(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            return "any";
        return File.ReadLines(path).Any(s => s.Trim() == EmptyIp) ? "none" : "loaded";
    }

    internal static void Ensure()
    {
        foreach (var name in Names.Where(n => n.EndsWith("-user.txt", StringComparison.Ordinal)))
        {
            var file = Path.Combine(Paths.Lists, name);
            if (!File.Exists(file))
                Disk.Write(file, Disk.Utf8.GetBytes((name.StartsWith("ipset") ? EmptyIp : "domain.example.abc") + "\r\n"));
        }
    }

    internal static ListEdit Read(string name)
    {
        if (!Names.Contains(name))
            throw new IOException("Неизвестный список.");
        if (!Directory.Exists(Paths.Lists))
            throw new IOException("Сначала установи zapret.");
        bool inactive = name == Names[2] && Mode(Active) != "loaded";
        string path = inactive ? Backup : Path.Combine(Paths.Lists, name);
        if (File.Exists(path) && new FileInfo(path).Length > Limit)
            throw new IOException("Список превышает 8 МБ.");
        byte[] bytes = Disk.Snapshot(path);
        return new ListEdit
        {
            Name = name,
            File = path,
            Inactive = inactive,
            Token = (inactive ? "B" : "A") + Disk.Token(bytes),
            Text = bytes == null ? "" : Disk.Utf8.GetString(bytes).TrimStart('\ufeff')
        };
    }

    internal static bool IsAddress(string value)
    {
        var pair = value.Split('/');
        IPAddress ip;
        int prefix;
        if (pair.Length > 2 || !Regex.IsMatch(pair[0], @"\A[0-9a-fA-F.:]+\z") || !IPAddress.TryParse(pair[0], out ip))
            return false;
        bool ipv4 = ip.AddressFamily == AddressFamily.InterNetwork;
        if (ipv4 && ip.ToString() != pair[0])
            return false;
        return pair.Length == 1 || (Regex.IsMatch(pair[1], @"\A[0-9]{1,3}\z") && int.TryParse(pair[1], out prefix) && prefix <= (ipv4 ? 32 : 128));
    }

    internal static byte[] Encode(string name, string text)
    {
        if (text.IndexOf('\0') >= 0)
            throw new InvalidDataException("Список содержит нулевой символ.");
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd(new[] { '\n' }).Split('\n');
        bool ip = name.StartsWith("ipset-");
        int entries = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string value = lines[i].Split('#')[0].Trim();
            if (value.Length == 0)
                continue;
            if (ip && !IsAddress(value) || !ip && Uri.CheckHostName(value) != UriHostNameType.Dns)
                throw new InvalidDataException("Проверь строку " + (i + 1) + ": " + value);
            if (name == Names[2] && value == EmptyIp)
                throw new InvalidDataException("Для отключения используй режим IPSet.");
            entries++;
        }

        if (entries == 0 && name == Names[2])
            throw new InvalidDataException("Нужен хотя бы один IP или подсеть.");
        string normalized = string.Join("\r\n", lines);
        if (entries == 0)
            normalized += "\r\n" + (ip ? EmptyIp : "domain.example.abc");
        byte[] data = Disk.Utf8.GetBytes(normalized.TrimStart(new[] { '\r', '\n' }) + "\r\n");
        if (data.Length > Limit)
            throw new InvalidDataException("Список превышает 8 МБ.");
        return data;
    }

    internal static void Save(ListEdit edit, string text)
    {
        var current = Read(edit.Name);
        if (current.Token != edit.Token)
            throw new IOException("Файл или режим изменился. Открой список заново.");
        var data = Encode(edit.Name, text);
        Action write = delegate
        {
            if (Read(edit.Name).Token != edit.Token)
                throw new IOException("Файл изменился на диске.");
            Disk.Write(current.File, data);
        };
        if (current.Inactive)
            write();
        else
            Components.Change(Component.Zapret, new[] { current.File }, write);
    }

    internal static void Filters(GamePorts ports, string target)
    {
        byte[] game = ports.Encode();
        if (!new[]
        {
            "none",
            "any",
            "loaded"
        }.Contains(target))
            throw new InvalidDataException("Неизвестный режим IPSet.");
        string old = Mode(Active);
        if (old != target && target == "loaded" && Mode(Backup) != "loaded")
            throw new IOException("Сначала обнови IPSet или заполни список адресов.");
        Components.Change(Component.Zapret, new[] { GamePorts.FileName, Active, Backup }, delegate
        {
            Disk.Write(GamePorts.FileName, game);
            if (old == target)
                return;
            if (old == "loaded")
                Disk.Write(Backup, File.ReadAllBytes(Active));
            Disk.Write(Active, target == "loaded" ? File.ReadAllBytes(Backup) : target == "none" ? Disk.Utf8.GetBytes(EmptyIp + "\r\n") : new byte[0]);
        });
    }

    internal static void Update()
    {
        var previous = Read(Names[2]);
        var text = Downloads.Text(Downloads.ServiceFiles + "ipset-service.txt", Limit);
        var data = Encode(Names[2], text);
        if (previous.Token != Read(previous.Name).Token)
            throw new IOException("IPSet изменился во время загрузки.");
        if (File.Exists(previous.File) && File.ReadAllBytes(previous.File).SequenceEqual(data))
            return;
        if (File.Exists(previous.File))
            File.Copy(previous.File, Active + ".before-update." + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".bak");
        Save(previous, text);
    }
}
