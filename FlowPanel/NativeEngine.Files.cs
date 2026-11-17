using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

internal sealed partial class NativeEngine
{
    private const int MaximumListSize = 8 * 1024 * 1024;
    private static readonly string[] EditableLists = { "list-general-user.txt", "list-exclude-user.txt", "ipset-all.txt", "ipset-exclude-user.txt" };

    private static Dictionary<string, string> ReadGame()
    {
        var settings = new Dictionary<string, string> { { "mode", "disabled" }, { "tcp", "1024-65535" }, { "udp", "1024-65535" } };
        if (!File.Exists(GameFlag)) return settings;
        foreach (string line in File.ReadLines(GameFlag))
        {
            var match = Regex.Match(line, @"\A(?:mode=)?(disabled|all|tcp|udp)\z", RegexOptions.IgnoreCase);
            if (match.Success) settings["mode"] = match.Groups[1].Value.ToLowerInvariant();
            match = Regex.Match(line, @"\A(tcp|udp)=([0-9,-]+)\z", RegexOptions.IgnoreCase);
            if (match.Success) settings[match.Groups[1].Value.ToLowerInvariant()] = match.Groups[2].Value;
        }
        return settings;
    }

    private static string ReadIPSet(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) return "any";
        return File.ReadAllText(path).Contains(Placeholder) ? "none" : "loaded";
    }

    private static byte[] Backup(string path)
    {
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private void ChangeLists(bool restart, Dictionary<string, byte[]> previous, Action change)
    {
        if (restart)
        {
            if (Text(state, "Zapret") == "") throw new InvalidOperationException("Не удалось определить текущую стратегию zapret.");
            Stop(ZapExe);
        }
        try
        {
            change();
            if (restart) Start("Zapret");
        }
        catch
        {
            if (restart) Stop(ZapExe);
            foreach (var saved in previous)
            {
                if (saved.Value == null) File.Delete(saved.Key); else Write(saved.Key, saved.Value);
            }
            if (restart) Start("Zapret");
            throw;
        }
    }

    private void SetFilters(string selection)
    {
        var match = Regex.Match(selection, @"\A(disabled|all|tcp|udp)\|(loaded|none|any)\z");
        if (!match.Success) throw new ArgumentException("Выбери оба режима фильтра.");
        RequireZap();
        Guard("Zapret");
        var game = ReadGame();
        string oldMode = ReadIPSet(IPList), gameMode = match.Groups[1].Value, ipMode = match.Groups[2].Value;
        if (game["mode"] == gameMode && oldMode == ipMode) { Log("Фильтры уже настроены."); return; }
        if (ipMode == "loaded" && oldMode != "loaded" && ReadIPSet(IPBackup) != "loaded")
            throw new InvalidOperationException(@"Резервный список IPSet отсутствует. Помести исходный список в lists\ipset-all.txt.backup.");
        var previous = new[] { GameFlag, IPList, IPBackup }.ToDictionary(path => path, Backup);
        bool running = IsRunning(ZapExe);
        ChangeLists(running, previous, delegate
        {
            if (game["mode"] != gameMode)
                Write(GameFlag, Encoding.ASCII.GetBytes("mode=" + gameMode + "\r\ntcp=" + game["tcp"] + "\r\nudp=" + game["udp"] + "\r\n"));
            if (oldMode == ipMode) return;
            if (oldMode == "loaded") Write(IPBackup, previous[IPList]);
            Write(IPList, ipMode == "loaded" ? File.ReadAllBytes(IPBackup) :
                ipMode == "none" ? Encoding.ASCII.GetBytes(Placeholder + "\r\n") : new byte[0]);
        });
        Log(running ? "Фильтры сохранены, zapret перезапущен." : "Фильтры сохранены. Они применятся при запуске zapret.");
    }

    private static int CountEntries(IEnumerable<string> lines)
    {
        int count = 0;
        foreach (string line in lines)
        {
            string value = line.Trim(new char[0]);
            if (value.Length == 0 || value.StartsWith("#", StringComparison.Ordinal)) continue;
            var match = Regex.Match(value, @"\A([0-9A-Fa-f:.]+)(?:/(\d{1,3}))?\z");
            IPAddress address;
            if (!match.Success || !IPAddress.TryParse(match.Groups[1].Value, out address))
                throw new InvalidDataException("Некорректная строка в списке IPSet: " + value);
            if (match.Groups[2].Success && Int32.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) >
                (address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128))
                throw new InvalidDataException("Некорректная подсеть в списке IPSet: " + value);
            count++;
        }
        if (count == 0) throw new InvalidDataException("Список IPSet не содержит адресов.");
        return count;
    }

    private void UpdateIPSet()
    {
        RequireZap();
        bool active = ReadIPSet(IPList) == "loaded";
        string target = active ? IPList : IPBackup;
        if (active) Guard("Zapret");
        const string source = "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/ipset-service.txt";
        string temporary = NewPath(Path.GetDirectoryName(IPList), "ipset-", ".tmp");
        try
        {
            Fetch(source, temporary, MaximumListSize, false, 30000, false);
            if (ReadIPSet(temporary) != "loaded") throw new InvalidDataException("GitHub вернул недопустимый список IPSet.");
            int entries = CountEntries(File.ReadLines(temporary));
            byte[] next = File.ReadAllBytes(temporary), previous = Backup(target);
            if (previous != null && previous.SequenceEqual(next)) { Log("IPSet уже актуален."); return; }
            if (previous != null && ReadIPSet(target) == "loaded")
                Write(Path.Combine(Path.GetDirectoryName(IPList), "ipset-all.txt.before-update." +
                    DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".bak"), previous);
            bool restart = active && IsRunning(ZapExe);
            ChangeLists(restart, new Dictionary<string, byte[]> { { target, previous } }, delegate { Write(target, next); });
            Log("IPSet обновлён (" + entries + " записей)" +
                (restart ? ", zapret перезапущен." : active ? "." : " и применится при выборе режима «По списку»."));
        }
        finally { File.Delete(temporary); }
    }

    private sealed class ListFile
    {
        internal string Name, Path, Text, Token;
        internal bool Inactive;
        internal byte[] Bytes;
    }

    private static ListFile ReadList(string name)
    {
        if (!EditableLists.Contains(name)) throw new ArgumentException("Неизвестный список.");
        RequireZap();
        bool inactive = name == "ipset-all.txt" && ReadIPSet(IPList) != "loaded";
        string path = inactive ? IPBackup : Path.Combine(Path.GetDirectoryName(IPList), name);
        if (File.Exists(path) && new FileInfo(path).Length > MaximumListSize)
            throw new InvalidDataException("Список превышает 8 МБ.");
        byte[] bytes = Backup(path);
        return new ListFile
        {
            Name = name, Path = path, Inactive = inactive, Bytes = bytes,
            Text = bytes == null ? "" : Utf8.GetString(bytes).TrimStart(new[] { '\ufeff' }),
            Token = (inactive ? "inactive:" : "active:") + (bytes == null ? "missing" : Convert.ToBase64String(Hash(new MemoryStream(bytes))))
        };
    }

    private void ReportList(ListFile list)
    {
        Send("List", "Name", list.Name, "Text", list.Text, "Token", list.Token, "Inactive", list.Inactive);
    }

    private void SaveList(string payload)
    {
        var edit = Serializer().Deserialize<Dictionary<string, object>>(payload);
        var list = ReadList(Text(edit, "Name"));
        if (list.Token != Text(edit, "Token")) throw new InvalidOperationException("Список или режим IPSet изменились. Перечитай файл.");
        string text = Text(edit, "Text").Replace("\r\n", "\n").Replace("\r", "\n");
        if (text.IndexOf('\0') >= 0) throw new InvalidDataException("Список содержит недопустимый нулевой символ.");
        bool hasEntries = Regex.IsMatch(text, @"(?m)^[ \t]*[^#\s]");
        if (list.Name == "ipset-all.txt" && (!hasEntries || text.Contains(Placeholder)))
            throw new InvalidDataException("Для IPSet укажи хотя бы один IP или подсеть.");
        if (!hasEntries) text = text.TrimEnd(new char[0]) + "\n" + (list.Name == "ipset-exclude-user.txt" ? Placeholder : "domain.example.abc");
        if (list.Name.StartsWith("ipset-", StringComparison.Ordinal)) CountEntries(text.Split('\n'));
        byte[] bytes = Utf8.GetBytes((text.TrimEnd(new[] { '\n' }) + "\n").Replace("\n", "\r\n"));
        if (bytes.Length > MaximumListSize) throw new InvalidDataException("Список превышает 8 МБ.");
        if (!list.Inactive) Guard("Zapret");
        bool restart = !list.Inactive && IsRunning(ZapExe);
        ChangeLists(restart, new Dictionary<string, byte[]> { { list.Path, list.Bytes } }, delegate { Write(list.Path, bytes); });
        ReportList(ReadList(list.Name));
        Log(restart ? "Список сохранён, zapret перезапущен." : list.Inactive ?
            "Список сохранён. Он применится в режиме IPSet «По списку»." : "Список сохранён.");
    }

    private static string NewPath(string parent, string prefix, string suffix)
    {
        return Path.Combine(parent, prefix + Guid.NewGuid().ToString("N") + suffix);
    }

    private static byte[] Hash(Stream input)
    {
        using (input) using (var sha = SHA256.Create()) return sha.ComputeHash(input);
    }

    internal static string FileHash(string path)
    {
        return BitConverter.ToString(Hash(File.OpenRead(path))).Replace("-", "");
    }

    private static HttpWebResponse Connect(string url, bool json, int timeout, bool redirect, bool raw = false)
    {
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.UserAgent = "zapretgui";
        request.Timeout = timeout;
        request.ReadWriteTimeout = timeout;
        request.AllowAutoRedirect = redirect;
        if (json || raw)
        {
            request.Headers["X-GitHub-Api-Version"] = "2022-11-28";
            request.Accept = raw ? "application/vnd.github.raw+json" : "application/vnd.github+json";
        }
        var response = (HttpWebResponse)request.GetResponse();
        if (response.StatusCode == HttpStatusCode.OK && response.ResponseUri.Scheme == Uri.UriSchemeHttps) return response;
        response.Dispose();
        throw new IOException("Сервер вернул неожиданный ответ.");
    }

    private static Dictionary<string, object> FetchJson(string url, bool raw)
    {
        using (var response = Connect(url, true, 20000, false, raw))
        using (var stream = new StreamReader(response.GetResponseStream(), Utf8))
        {
            var buffer = new char[8192];
            var text = new StringBuilder();
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (text.Length + read > 4 * 1024 * 1024) throw new IOException("Ответ GitHub слишком большой.");
                text.Append(buffer, 0, read);
            }
            var data = Serializer().Deserialize<Dictionary<string, object>>(text.ToString());
            if (data == null) throw new InvalidDataException("GitHub вернул неверный JSON.");
            return data;
        }
    }

    private static void Fetch(string url, string path, long maximum, bool raw, int timeout, bool redirect)
    {
        using (var response = Connect(url, false, timeout, redirect, raw))
        using (var source = response.GetResponseStream())
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            var clock = Stopwatch.StartNew();
            var buffer = new byte[65536];
            int count;
            while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (clock.ElapsedMilliseconds > timeout) throw new TimeoutException("Истекло время загрузки.");
                if (output.Length + count > maximum) throw new InvalidDataException("Размер файла превышает ожидаемый.");
                output.Write(buffer, 0, count);
            }
        }
    }

    private sealed class Asset
    {
        internal string Version, Name, Url, Digest;
        internal long Size;
    }

    private static Asset Release(string tool)
    {
        string repo = tool == "Zapret" ? "zapret-discord-youtube" : "tg-ws-proxy";
        var release = FetchJson("https://api.github.com/repos/Flowseal/" + repo + "/releases/latest", false);
        object raw;
        var candidates = new List<Dictionary<string, object>>();
        if (release.TryGetValue("assets", out raw) && raw is IEnumerable)
            foreach (var value in (IEnumerable)raw)
            {
                var asset = value as Dictionary<string, object>;
                if (asset == null) continue;
                string name = Text(asset, "name");
                if (tool == "Zapret" ? Regex.IsMatch(name, @"\Azapret-discord-youtube-[a-zA-Z0-9._-]+\.zip\z", RegexOptions.IgnoreCase) :
                    name == Path.GetFileName(TgExe)) candidates.Add(asset);
            }
        if (candidates.Count != 1) throw new InvalidDataException("В последнем релизе " + tool + " не найден подходящий файл.");
        var selected = candidates[0];
        string url = Text(selected, "browser_download_url");
        Uri uri;
        long size;
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" ||
            !uri.AbsolutePath.StartsWith("/Flowseal/" + repo + "/releases/download/", StringComparison.Ordinal) ||
            !Int64.TryParse(Text(selected, "size"), out size) || size <= 0 || size > 250L * 1024 * 1024)
            throw new InvalidDataException("GitHub вернул неверные данные файла.");
        return new Asset { Version = Text(release, "tag_name"), Name = Text(selected, "name"), Url = url,
            Digest = Text(selected, "digest"), Size = size };
    }

    private static void Extract(string archive, string expanded)
    {
        string prefix = Path.GetFullPath(expanded) + Path.DirectorySeparatorChar;
        using (var zip = ZipFile.OpenRead(archive))
        {
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                total += entry.Length;
                if (entry.FullName.Contains(":") || !Path.GetFullPath(Path.Combine(expanded, entry.FullName)).StartsWith(prefix,
                    StringComparison.OrdinalIgnoreCase) || total > 512L * 1024 * 1024)
                    throw new InvalidDataException("Архив содержит недопустимый путь или превышает лимит размера.");
            }
        }
        ZipFile.ExtractToDirectory(archive, expanded);
    }

    internal static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (string directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static void PrepareZap(string expanded, string staged, string live)
    {
        var layout = new[] { expanded }.Concat(Directory.GetDirectories(expanded))
            .Where(path => File.Exists(Path.Combine(path, @"bin\winws.exe")) && File.Exists(Path.Combine(path, "service.bat"))).ToArray();
        if (layout.Length != 1) throw new InvalidDataException(@"Не найдены bin\winws.exe и service.bat в архиве zapret.");
        Directory.Move(layout[0], staged);
        foreach (string subfolder in new[] { "lists", "utils" })
        {
            string old = Path.Combine(live, subfolder), newer = Path.Combine(staged, subfolder);
            if (!Directory.Exists(old) || !Directory.Exists(newer)) continue;
            if (subfolder == "utils" && !File.Exists(Path.Combine(old, "check_updates.enabled")))
                File.Delete(Path.Combine(newer, "check_updates.enabled"));
            foreach (string file in Directory.GetFiles(old))
            {
                string name = Path.GetFileName(file);
                if (Regex.IsMatch(name, @"(?:-user(?:-[a-z0-9_-]+)?\.txt\z|\Agame_filter\.enabled\z|\Aipset-all\.txt(?:\.backup|\.before-update\.[0-9-]+\.bak)?\z|\Acheck_updates\.enabled\z)", RegexOptions.IgnoreCase))
                    File.Copy(file, Path.Combine(newer, name), true);
            }
        }
    }

    private void Install(string tool)
    {
        string exe = Executable(tool);
        Asset asset = Release(tool);
        if (File.Exists(exe) && Text(state, tool + "Version") == asset.Version)
        { Log(tool + " уже обновлён до " + asset.Version + "."); return; }
        string temp = NewPath(Root, "stage-", ""), live = Path.Combine(Root, tool);
        Directory.CreateDirectory(temp);
        string previous = Path.Combine(temp, "previous"), staged = Path.Combine(temp, "ready"), download = Path.Combine(temp, asset.Name);
        bool running = false, switched = false, preserve = false;
        try
        {
            Log("Скачиваю " + tool + " " + asset.Version + " с GitHub.");
            Fetch(asset.Url, download, asset.Size, false, 120000, true);
            var digest = Regex.Match(asset.Digest, @"\Asha256:([0-9a-fA-F]{64})\z");
            if (new FileInfo(download).Length != asset.Size || digest.Success &&
                !String.Equals(FileHash(download), digest.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Размер или SHA-256 файла отличается от релиза.");
            if (tool == "Zapret")
            {
                string expanded = Path.Combine(temp, "expanded");
                Extract(download, expanded);
                PrepareZap(expanded, staged, live);
            }
            else
            {
                Directory.CreateDirectory(staged);
                File.Move(download, Path.Combine(staged, asset.Name));
            }
            bool wasRunning = IsRunning(exe);
            if (wasRunning) { Guard(tool); running = true; Stop(exe); }
            if (tool == "TG" && Directory.Exists(TgData))
                CopyDirectory(TgData, Path.Combine(staged, "TgWsProxy_data"));
            if (Directory.Exists(live)) Directory.Move(live, previous);
            Directory.Move(staged, live);
            switched = true;
            if (running) Start(tool);
            SetState(tool + "Version", asset.Version);
            Log(tool + " " + asset.Version + " установлен.");
        }
        catch
        {
            try
            {
                if (switched) { Stop(exe); Directory.Move(live, Path.Combine(temp, "failed")); }
                if (Directory.Exists(previous)) Directory.Move(previous, live);
                if (running && !IsRunning(exe)) Start(tool);
            }
            catch (Exception error)
            {
                preserve = true;
                Log("Восстановление не завершилось. Рабочая папка: " + temp + ". " + error.Message);
            }
            throw;
        }
        finally
        {
            if (!preserve)
                try { Directory.Delete(temp, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
        }
    }

    private void AppUpdate(string current)
    {
        const string source = "https://api.github.com/repos/zalphex/zapret-gui/contents/release/";
        var manifest = FetchJson(source + "update.json?ref=main", true);
        string version = Text(manifest, "version"), hash = Text(manifest, "sha256");
        Version parsed;
        if (!Regex.IsMatch(version, @"\A\d+\.\d+\.\d+\z") || !Version.TryParse(version, out parsed) ||
            !Regex.IsMatch(hash, @"\A[a-fA-F0-9]{64}\z")) throw new InvalidDataException("Файл версии GitHub содержит неверные данные.");
        if (parsed <= new Version(current)) { Log("Установлена актуальная версия zapretgui."); return; }
        string path = NewPath(PanelHost.Desktop, "update-", ".exe");
        bool ready = false;
        try
        {
            Fetch(source + "zapretgui_Setup_x64.exe?ref=main", path, 32 * 1024 * 1024, true, 120000, false);
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!String.Equals(hash, FileHash(path), StringComparison.OrdinalIgnoreCase) || info.ProductName != "zapretgui" || info.FileVersion != version)
                throw new InvalidDataException("Установщик не совпадает с данными GitHub.");
            Send("AppUpdate", "Path", path, "Hash", hash, "Version", version);
            ready = true;
        }
        finally { if (!ready) File.Delete(path); }
    }
}
