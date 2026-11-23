using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Diagnostics;
using System.Collections.Generic;
using System.Text.RegularExpressions;

internal sealed class ReleaseAsset
{
    internal string Version, Url, Hash, Name;
    internal long Size;
}

internal sealed class UpdateFile
{
    internal string Path, Hash, Version;
}

internal static class Downloads
{
    internal const string ServiceFiles = "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/.service/";
    private const string Published = "https://raw.githubusercontent.com/zalphex/zapret-gui/main/release/";
    private static void Transfer(string url, Stream output, long max)
    {
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.UserAgent = "zapretgui/" + Paths.Version;
        req.Timeout = 25000;
        req.ReadWriteTimeout = 20000;
        using (var reply = (HttpWebResponse)req.GetResponse())
        using (var input = reply.GetResponseStream())
        {
            if (reply.ResponseUri.Scheme != "https" || reply.StatusCode != HttpStatusCode.OK || reply.ContentLength > max)
                throw new IOException("Некорректный ответ сервера.");
            int n;
            var buf = new byte[65536];
            var timer = Stopwatch.StartNew();
            while ((n = input.Read(buf, 0, buf.Length)) > 0)
            {
                if (timer.Elapsed.TotalSeconds > 150 || output.Length + n > max)
                    throw new IOException("Загрузка превысила ограничение времени или размера.");
                output.Write(buf, 0, n);
            }
        }
    }

    internal static string Text(string url, long limit)
    {
        using (var m = new MemoryStream())
        {
            Transfer(url, m, limit);
            return Disk.Utf8.GetString(m.ToArray()).TrimStart('\ufeff');
        }
    }

    private static void FileFrom(string url, string path, long limit)
    {
        using (var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            Transfer(url, f, limit);
    }

    internal static ReleaseAsset Latest(Component c)
    {
        string repo = c == Component.Zapret ? "zapret-discord-youtube" : "tg-ws-proxy";
        string json = Text("https://api.github.com/repos/Flowseal/" + repo + "/releases/latest?per_page=1", 4 * 1024 * 1024);
        return ParseRelease(c, json);
    }

    internal static ReleaseAsset ParseRelease(Component c, string json)
    {
        string repo = c == Component.Zapret ? "zapret-discord-youtube" : "tg-ws-proxy";
        var release = Disk.Parse(json);
        var assets = (System.Collections.IEnumerable)release["assets"];
        var matches = assets.Cast<Dictionary<string, object>>().Where(a => c == Component.Zapret ? Regex.IsMatch(Disk.Get(a, "name"), @"\Azapret-discord-youtube-[a-zA-Z0-9_.-]+\.zip\z") : Disk.Get(a, "name") == Path.GetFileName(Paths.TgExe)).ToArray();
        if (matches.Length != 1)
            throw new IOException("В релизе не найден файл Windows x64 для " + Components.Title(c) + ".");
        var file = matches[0];
        var uri = new Uri(Disk.Get(file, "browser_download_url"));
        long size = Convert.ToInt64(file["size"]);
        string hash = Disk.Get(file, "digest");
        if (uri.Scheme != "https" || uri.Host != "github.com" || !uri.AbsolutePath.StartsWith("/Flowseal/" + repo + "/releases/download/", StringComparison.OrdinalIgnoreCase) || size <= 0 || size > 250L * 1024 * 1024 || (hash != "" && !Regex.IsMatch(hash, @"\Asha256:[a-fA-F0-9]{64}\z")))
            throw new IOException("Некорректные данные релиза.");
        return new ReleaseAsset
        {
            Version = Disk.Get(release, "tag_name"),
            Name = Disk.Get(file, "name"),
            Url = uri.AbsoluteUri,
            Hash = hash,
            Size = size
        };
    }

    internal static void Extract(string zip, string root)
    {
        string prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        long total = 0;
        using (var packed = File.OpenRead(zip))
        using (var archive = new ZipArchive(packed, ZipArchiveMode.Read))
        {
            foreach (var e in archive.Entries)
            {
                string name = e.FullName.Replace('\\', '/');
                if (name.StartsWith("/") || name.Contains(":") || name.Split('/').Any(s => s == ".." || s == "." || s.EndsWith(" ") || s.EndsWith(".")))
                    throw new IOException("Недопустимый путь в архиве.");
                string path = Path.GetFullPath(Path.Combine(root, name));
                total += e.Length;
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || total > 512L * 1024 * 1024)
                    throw new IOException("Недопустимый архив.");
                if (name.EndsWith("/"))
                    Directory.CreateDirectory(path);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (var src = e.Open())
                    using (var dst = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                        src.CopyTo(dst);
                }
            }
        }
    }

    private const string StrategyIndex = ".zapretgui-strategies.json";
    private static Dictionary<string, object> StrategyHashes(string dir)
    {
        return Directory.GetFiles(dir, "*.bat").Where(p => !Path.GetFileName(p).StartsWith("service", StringComparison.OrdinalIgnoreCase)).ToDictionary(Path.GetFileName, p => (object)Disk.Hash(p), StringComparer.OrdinalIgnoreCase);
    }

    private static void Preserve(string live, string staged)
    {
        foreach (var sub in new[]
        {
            "lists",
            "utils",
            "bin"
        }

        )
        {
            string dir = Path.Combine(live, sub);
            if (!Directory.Exists(dir))
                continue;
            foreach (var path in Directory.GetFiles(dir))
            {
                string name = Path.GetFileName(path);
                if (Regex.IsMatch(name, @"(?:-user(?:-[\w-]+)?\.txt$|^ipset-all\.txt(?:\.backup|\.before-update\..+\.bak)?$|^game_filter\.enabled$|^check_updates\.enabled$|^ACTIVE_(?:GAME|DISCORD)_UDP\.bin$)", RegexOptions.IgnoreCase))
                    File.Copy(path, Path.Combine(staged, sub, name), true);
            }

            if (sub == "utils" && !File.Exists(Path.Combine(dir, "check_updates.enabled")))
                File.Delete(Path.Combine(staged, sub, "check_updates.enabled"));
        }

        var baseline = Disk.ReadJson(Path.Combine(live, StrategyIndex));
        foreach (var path in Directory.GetFiles(live, "*.bat"))
        {
            var to = Path.Combine(staged, Path.GetFileName(path));
            string name = Path.GetFileName(path);
            if (name.StartsWith("service", StringComparison.OrdinalIgnoreCase))
                continue;
            string oldDigest = Disk.Get(baseline, name);
            if (!File.Exists(to) || !Processes.Same(Disk.Hash(path), oldDigest))
                File.Copy(path, to, true);
        }
    }

    internal static void Install(Component c, Action<string> log)
    {
        var a = Latest(c);
        if (Components.Installed(c) && Disk.State(c + "Version") == a.Version)
        {
            log(Components.Title(c) + " уже обновлён.");
            return;
        }

        string live = c == Component.Zapret ? Paths.Zap : Paths.Tg;
        string stage = Path.Combine(Paths.Root, "stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        string ready = Path.Combine(stage, "ready"), old = Path.Combine(stage, "previous"), file = Path.Combine(stage, a.Name);
        bool resume = false, swapped = false, keep = false;
        try
        {
            log("Загрузка " + Components.Title(c) + " " + a.Version);
            FileFrom(a.Url, file, a.Size);
            if (new FileInfo(file).Length != a.Size || a.Hash != "" && !Processes.Same("sha256:" + Disk.Hash(file), a.Hash))
                throw new IOException("Проверка загруженного файла не пройдена.");
            if (c == Component.Zapret)
            {
                string contents = Path.Combine(stage, "expanded");
                Extract(file, contents);
                var dirs = new[]
                {
                    contents
                }.Concat(Directory.GetDirectories(contents)).Where(p => File.Exists(Path.Combine(p, "bin", "winws.exe")) && File.Exists(Path.Combine(p, "service.bat"))).ToArray();
                if (dirs.Length != 1)
                    throw new InvalidDataException("Не найдена папка zapret.");
                Directory.Move(dirs[0], ready);
                Disk.Json(Path.Combine(ready, StrategyIndex), StrategyHashes(ready));
                if (Directory.Exists(live))
                    Preserve(live, ready);
            }
            else
            {
                Directory.CreateDirectory(ready);
                File.Move(file, Path.Combine(ready, a.Name));
            }

            resume = Processes.Running(Paths.Binary(c));
            if (resume)
                Components.Stop(c);
            if (c == Component.TG && Directory.Exists(Paths.TgData))
                Disk.CopyTree(Paths.TgData, Path.Combine(ready, "TgWsProxy_data"));
            if (Directory.Exists(live))
                Directory.Move(live, old);
            Directory.Move(ready, live);
            swapped = true;
            if (c == Component.TG)
                ProxySettings.Ensure();
            else
                AddressLists.Ensure();
            if (resume)
                Components.Start(c, Components.Selected(c));
            Disk.Remember(c + "Version", a.Version);
            log(Components.Title(c) + " " + a.Version + " установлен.");
        }
        catch
        {
            try
            {
                if (swapped)
                {
                    if (Processes.Running(Paths.Binary(c)))
                        Components.Stop(c);
                    Directory.Move(live, Path.Combine(stage, "failed"));
                }

                if (Directory.Exists(old))
                    Directory.Move(old, live);
                if (resume && !Processes.Running(Paths.Binary(c)))
                    Components.Start(c, Components.Selected(c));
            }
            catch (Exception ex)
            {
                keep = true;
                log("Копия для восстановления: " + stage + ". " + ex.Message);
            }

            throw;
        }
        finally
        {
            if (!keep)
                try
                {
                    Directory.Delete(stage, true);
                }
                catch (IOException)
                {
                }
        }
    }

    internal static UpdateFile App()
    {
        var info = Disk.Parse(Text(Published + "update.json", 128 * 1024));
        string v = Disk.Get(info, "version"), hash = Disk.Get(info, "sha256");
        if (!Regex.IsMatch(v, @"\A\d+\.\d+\.\d+\z") || !Regex.IsMatch(hash, @"\A[a-fA-F0-9]{64}\z"))
            throw new InvalidDataException("Повреждён файл версии zapretgui.");
        if (new Version(v) <= new Version(Paths.Version))
            return null;
        string path = Path.Combine(Paths.Desktop, "update-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            FileFrom(Published + "zapretgui_Setup_x64.exe", path, 64 * 1024 * 1024);
            var metadata = FileVersionInfo.GetVersionInfo(path);
            if (!Processes.Same(Disk.Hash(path), hash) || metadata.ProductName != "zapretgui" || metadata.FileVersion != v)
                throw new IOException("Проверка установщика не пройдена.");
            return new UpdateFile
            {
                Path = path,
                Hash = hash,
                Version = v
            };
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }
}
