using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

internal static class Paths
{
    internal const string Version = "2.1.1";
#if ISOLATED_TESTS
    internal static readonly string Root = Path.Combine(Path.GetTempPath(), "zapretgui-tests-" + Guid.NewGuid().ToString("N"));
#else
    internal static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "zapretgui");
#endif
    internal static readonly string Desktop = Path.Combine(Root, "Desktop");
    internal static readonly string Zap = Path.Combine(Root, "Zapret");
    internal static readonly string Bin = Path.Combine(Zap, "bin");
    internal static readonly string Lists = Path.Combine(Zap, "lists");
    internal static readonly string Utils = Path.Combine(Zap, "utils");
    internal static readonly string ZapExe = Path.Combine(Bin, "winws.exe");
#if ISOLATED_TESTS
    internal static readonly string UserRoot = Root;
#else
    internal static readonly string UserRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "zapretgui");
#endif
    internal static readonly string LegacyTg = Path.Combine(Root, "TG");
    internal static readonly string Tg = Path.Combine(UserRoot, "TG");
    internal static readonly string UpdateWorker = Path.Combine(Desktop, "update-worker-" + Version + ".exe");
    internal static readonly string TgExe = Path.Combine(Tg, "TgWsProxy_windows.exe");
    internal static readonly string TgData = Path.Combine(Tg, "TgWsProxy_data");
    internal static readonly string Config = Path.Combine(TgData, "config.json");
    internal static string Binary(Component c)
    {
        return c == Component.Zapret ? ZapExe : TgExe;
    }
}

internal enum Component
{
    Zapret,
    TG
}

internal static class Disk
{
    internal static readonly Encoding Utf8 = new UTF8Encoding(false);
    internal static string NewFile(string directory)
    {
        return Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
    }

    internal static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temp = NewFile(Path.GetDirectoryName(path));
        try
        {
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(path))
                File.Replace(temp, path, null);
            else
                File.Move(temp, path);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    internal static byte[] Snapshot(string path)
    {
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    internal static void Restore(string path, byte[] bytes)
    {
        if (bytes == null)
            File.Delete(path);
        else
            Write(path, bytes);
    }

    internal static string Hash(string path)
    {
        using (var file = File.OpenRead(path))
        using (var hash = SHA256.Create())
            return Hex(hash.ComputeHash(file));
    }

    internal static string Hex(byte[] data)
    {
        return BitConverter.ToString(data).Replace("-", "").ToLowerInvariant();
    }

    internal static string Token(byte[] bytes)
    {
        if (bytes == null)
            return "missing";
        using (var hash = SHA256.Create())
            return Hex(hash.ComputeHash(bytes));
    }

    internal static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in new DirectoryInfo(source).GetFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("В папке обнаружена ссылка: " + entry.Name);
            string target = Path.Combine(destination, entry.Name);
            if (entry is DirectoryInfo)
                CopyTree(entry.FullName, target);
            else
                File.Copy(entry.FullName, target, true);
        }
    }

    internal static Dictionary<string, object> Parse(string json)
    {
        var obj = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
        if (obj == null)
            throw new InvalidDataException("Пустой JSON.");
        return obj;
    }

    internal static Dictionary<string, object> ReadJson(string path)
    {
        return File.Exists(path) ? Parse(File.ReadAllText(path)) : new Dictionary<string, object>();
    }

    internal static string Get(Dictionary<string, object> values, string key, string fallback = "")
    {
        object value;
        return values.TryGetValue(key, out value) && value != null && Convert.ToString(value).Length > 0 ? Convert.ToString(value) : fallback;
    }

    internal static void Json(string path, Dictionary<string, object> obj)
    {
        Write(path, Utf8.GetBytes(new JavaScriptSerializer().Serialize(obj)));
    }

    internal static string State(string key, string fallback = "")
    {
        if (key.StartsWith("TG", StringComparison.Ordinal))
        {
            string value = Get(ReadJson(Path.Combine(Paths.UserRoot, "user-state.json")), key);
            if (value.Length != 0)
                return value;
        }
        return Get(ReadJson(Path.Combine(Paths.Root, "state.json")), key, fallback);
    }

    internal static void Remember(string key, string value)
    {
        string file = key.StartsWith("TG", StringComparison.Ordinal)
            ? Path.Combine(Paths.UserRoot, "user-state.json")
            : Path.Combine(Paths.Root, "state.json");
        var obj = ReadJson(file);
        obj[key] = value;
        Json(file, obj);
    }

    internal static bool Option(string key, bool fallback)
    {
        return Convert.ToInt32(Registry.GetValue(@"HKEY_CURRENT_USER\Software\zapretgui", key, fallback ? 1 : 0) ?? (fallback ? 1 : 0)) != 0;
    }

    internal static void OptionSet(string key, bool value)
    {
        Registry.SetValue(@"HKEY_CURRENT_USER\Software\zapretgui", key, value ? 1 : 0, RegistryValueKind.DWord);
    }
}
