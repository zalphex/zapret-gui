using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

internal sealed partial class NativeEngine
{
    internal static readonly string Root = Path.GetDirectoryName(PanelHost.Desktop);
    private static readonly string Zap = Path.Combine(Root, "Zapret");
    internal static readonly string ZapExe = Path.Combine(Zap, @"bin\winws.exe");
    internal static readonly string TgExe = Path.Combine(Root, @"TG\TgWsProxy_windows.exe");
    private static readonly string TgData = Path.Combine(Path.GetDirectoryName(TgExe), "TgWsProxy_data");
    private static readonly string TgConfig = Path.Combine(TgData, "config.json");
    private static readonly string StateFile = Path.Combine(Root, "state.json");
    private static readonly string GameFlag = Path.Combine(Zap, @"utils\game_filter.enabled");
    private static readonly string IPList = Path.Combine(Zap, @"lists\ipset-all.txt");
    private static readonly string IPBackup = IPList + ".backup";
    private const string Placeholder = "203.0.113.113/32";
    private static readonly string[] Profiles = { "Standard", "CF без HTTP/2", "Без CF" };
    private static readonly string[] TgNames = { "TgWsProxy_windows", "TgWsProxy_windows_arm64", "TgWsProxy_windows_7_64bit", "TgWsProxy_windows_7_32bit" };
    private static readonly string[] ServiceNames = { "YouTube", "Discord", "Telegram" };
    private static readonly string[] Hosts = { "www.youtube.com", "discord.com", "telegram.org" };
    private static readonly string[] Web = { "https://www.youtube.com/", "https://discord.com/api/v10/gateway", "https://web.telegram.org/" };
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    private readonly IProgress<Dictionary<string, object>> report;
    private readonly Dictionary<string, object> state;

    internal NativeEngine(IProgress<Dictionary<string, object>> report)
    {
        this.report = report;
        state = File.Exists(StateFile) ? ReadJson(StateFile) : new Dictionary<string, object>();
    }

    internal static JavaScriptSerializer Serializer()
    {
        return new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    }

    internal static Dictionary<string, object> ReadJson(string path)
    {
        var data = Serializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Utf8));
        if (data == null) throw new InvalidDataException("Некорректный JSON: " + Path.GetFileName(path));
        return data;
    }

    private static string Text(Dictionary<string, object> data, string key)
    {
        object result;
        return data.TryGetValue(key, out result) ? Convert.ToString(result, CultureInfo.InvariantCulture) : "";
    }

    private static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { File.Delete(temporary); }
    }

    private static void WriteJson(string path, Dictionary<string, object> data)
    {
        Write(path, Utf8.GetBytes(Serializer().Serialize(data)));
    }

    private void SetState(string key, string value)
    {
        var next = new Dictionary<string, object>(state);
        next[key] = value;
        WriteJson(StateFile, next);
        state[key] = value;
    }

    private void Send(string type, params object[] fields)
    {
        if (report == null) return;
        var message = new Dictionary<string, object> { { "Type", type } };
        for (int i = 0; i < fields.Length; i += 2) message.Add((string)fields[i], fields[i + 1]);
        report.Report(message);
    }

    private void Log(string message) { Send("Log", "Message", message); }
    private void Error(Exception error) { Send("Error", "Message", error.Message); }

    internal void Run(string action, string target, string choice)
    {
        Directory.CreateDirectory(Root);
        try
        {
            switch (action)
            {
                case "AutoStart":
                    if (choice != "") NativeSetup.SetAutoStart(choice == "on");
                    Send("AutoStart", "Enabled", NativeSetup.AutoStartEnabled());
                    return;
                case "StartTools": StartInstalledTools(); return;
                case "Startup":
                    StartInstalledTools();
                    Status();
                    if (Text(state, "ZapretVersion") != "" && File.Exists(Path.Combine(Zap, @"utils\check_updates.enabled")))
                    {
                        try
                        {
                            string latest = Release("Zapret").Version;
                            if (latest != Text(state, "ZapretVersion")) Log("zapret: доступна версия " + latest + ".");
                        }
                        catch (Exception error) { Log("Проверка обновления zapret: " + error.Message); }
                    }
                    break;
                case "Status": Status(); break;
                case "Ping": Latency(target, true); break;
                case "Download": Install(target); break;
                case "DownloadAll":
                    foreach (string tool in new[] { "Zapret", "TG" }) try { Install(tool); } catch (Exception error) { Error(error); }
                    break;
                case "Update":
                    foreach (string tool in new[] { "Zapret", "TG" })
                        try
                        {
                            string latest = Release(tool).Version;
                            string local = Text(state, tool + "Version");
                            Log(tool + ": " + (latest == local ? "обновлений нет." : "на GitHub " + latest + ", здесь " + (local == "" ? "не установлен" : local) + "."));
                        }
                        catch (Exception error) { Error(error); }
                    break;
                case "Filters": SetFilters(choice); break;
                case "UpdateIPSet": UpdateIPSet(); break;
                case "ReadList": ReportList(ReadList(choice)); break;
                case "SaveList": SaveList(choice); break;
                case "ListsFolder": RequireZap(); Send("Folder", "Path", Path.GetDirectoryName(IPList)); break;
                case "Folder":
                    if (target == "Zapret") RequireZap(); else EnsureTgConfig();
                    Send("Folder", "Path", target == "Zapret" ? Zap : TgData);
                    break;
                case "Apply": Apply(target, choice); break;
                case "Test": case "Auto": Test(target, action == "Auto"); break;
                case "Restart": Restart(target); break;
                case "Stop": Stop(Executable(target)); Log(target + " остановлен."); break;
                case "Connect":
                    if (!IsRunning(TgExe)) throw new InvalidOperationException("Сначала запусти TG WS Proxy.");
                    var cfg = GetProxyConfig();
                    Send("Connect", "Uri", "tg://proxy?server=127.0.0.1&port=" + Text(cfg, "port") + "&secret=dd" + Text(cfg, "secret"));
                    break;
                case "AppUpdate": AppUpdate(choice); break;
                case "Init": break;
                default: throw new ArgumentException("Неизвестная операция.");
            }
        }
        catch (Exception error) { Error(error); }
        finally { if (action != "AutoStart" && action != "StartTools") ReportState(); }
    }

    private void ReportState()
    {
        Send("State", "Zapret", Text(state, "Zapret"), "TG", Text(state, "TG"),
            "ZapretVersion", Text(state, "ZapretVersion"), "TGVersion", Text(state, "TGVersion"),
            "ZapInstalled", File.Exists(ZapExe), "TGInstalled", File.Exists(TgExe),
            "ZapRunning", IsRunning(ZapExe), "TGRunning", IsRunning(TgExe),
            "ExternalZap", HasExternal("Zapret"), "ExternalTG", HasExternal("TG"),
            "GameFilter", ReadGame()["mode"], "IPSet", ReadIPSet(IPList), "Strategies", Strategies(), "Profiles", Profiles);
    }

    private static string Executable(string name)
    {
        if (name == "Zapret") return ZapExe;
        if (name == "TG") return TgExe;
        throw new ArgumentException("Неизвестная утилита.");
    }

    private static string ProcessPath(Process process)
    {
        try { return process.MainModule.FileName; }
        catch (System.ComponentModel.Win32Exception) { return ""; }
        catch (InvalidOperationException) { return ""; }
    }

    internal static bool IsRunning(string executable)
    {
        return SelectProcesses(new[] { Path.GetFileNameWithoutExtension(executable) },
            path => String.Equals(path, executable, StringComparison.OrdinalIgnoreCase), null);
    }

    private static bool SelectProcesses(string[] names, Func<string, bool> select, Action<Process> action)
    {
        bool found = false;
        foreach (string name in names)
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                foreach (var process in processes)
                {
                    if (!select(ProcessPath(process))) continue;
                    found = true;
                    if (action == null) return true;
                    action(process);
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        return found;
    }

    internal static void Stop(string executable)
    {
        SelectProcesses(new[] { Path.GetFileNameWithoutExtension(executable) },
            path => String.Equals(path, executable, StringComparison.OrdinalIgnoreCase), process =>
            {
                try { process.Kill(); }
                catch (InvalidOperationException) { return; }
                catch (System.ComponentModel.Win32Exception) { if (process.HasExited) return; throw; }
                if (!process.WaitForExit(4500)) throw new InvalidOperationException("Процесс не остановился: " + process.Id);
            });
    }

    private static bool HasExternal(string name)
    {
        return SelectProcesses(name == "TG" ? TgNames : new[] { "winws" },
            path => !String.Equals(path, Executable(name), StringComparison.OrdinalIgnoreCase), null);
    }

    private static void Guard(string name)
    {
        if (name == "Zapret")
        {
            ServiceControllerStatus service;
            using (var zapret = new ServiceController("zapret"))
            {
                try { service = zapret.Status; }
                catch (InvalidOperationException error)
                {
                    var native = error.InnerException as System.ComponentModel.Win32Exception;
                    if (native == null || native.NativeErrorCode != 1060) throw;
                    service = ServiceControllerStatus.Stopped;
                }
            }
            if (service != ServiceControllerStatus.Stopped) throw new InvalidOperationException("Сначала останови службу zapret в services.msc.");
        }
        if (HasExternal(name)) throw new InvalidOperationException("Сначала закрой отдельно запущенный " + name + ".");
    }

    private static void RequireZap()
    {
        if (!File.Exists(ZapExe)) throw new InvalidOperationException("Сначала скачай zapret.");
    }

    private static readonly Regex StartLine = new Regex(@"^[ \t]*@?start\b(?=[^\r\n]*winws\.exe)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex KnownStart = new Regex(@"^(?<indent>[ \t]*)@?start[ \t]+(?:""[^""\r\n]*""[ \t]+)?(?:/(?:min|max|b)[ \t]+)*(?<exe>""[^""\r\n]*winws\.exe""|[^ \t""\r\n]*winws\.exe)(?=[ \t\r\n]|$)", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static string[] Strategies()
    {
        if (!File.Exists(ZapExe)) return new string[0];
        return Directory.GetFiles(Zap, "*.bat").Where(path =>
        {
            string name = Path.GetFileName(path);
            return Regex.IsMatch(name, @"\A[a-zA-Z0-9() ._+-]+\.bat\z", RegexOptions.IgnoreCase) &&
                !name.StartsWith("service", StringComparison.OrdinalIgnoreCase) && StartLine.IsMatch(File.ReadAllText(path));
        }).Select(Path.GetFileName).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void StartZap(string name)
    {
        if (!Strategies().Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Стратегия отсутствует в установленном релизе.");
        string script = Path.Combine(Zap, name);
        string temporary = Path.Combine(Zap, "gui-" + Guid.NewGuid().ToString("N") + ".cmd");
        var encoding = Encoding.GetEncoding(28591);
        string body = encoding.GetString(File.ReadAllBytes(script));
        int lines = StartLine.Matches(body).Count;
        if (lines == 0 || KnownStart.Matches(body).Count != lines)
            throw new InvalidDataException("В стратегии не распознана строка запуска winws.exe.");
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            "/d /s /c \"\"" + temporary + "\"\"") { WorkingDirectory = Zap, UseShellExecute = false, CreateNoWindow = true };
        start.EnvironmentVariables["NO_UPDATE_CHECK"] = "1";
        try
        {
            File.WriteAllBytes(temporary, encoding.GetBytes(KnownStart.Replace(body, "${indent}start \"\" /b ${exe}")));
            using (var launcher = Process.Start(start))
            {
                if (!launcher.WaitForExit(12000))
                {
                    launcher.Kill();
                    Stop(ZapExe);
                    throw new System.TimeoutException("Скрипт стратегии не завершил запуск.");
                }
            }
            Thread.Sleep(420);
            if (!IsRunning(ZapExe)) throw new InvalidOperationException("winws.exe не удержался после запуска.");
        }
        finally { File.Delete(temporary); }
    }

    private static void EnsureTgConfig()
    {
        if (!File.Exists(TgExe)) throw new InvalidOperationException("Сначала скачай TG WS Proxy.");
        if (!File.Exists(TgConfig))
        {
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            WriteJson(TgConfig, new Dictionary<string, object> { { "host", "127.0.0.1" }, { "port", 1443 },
                { "secret", BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant() }, { "appearance", "dark" },
                { "language", "ru" }, { "autostart", false }, { "check_updates", false }, { "cfproxy", true }, { "h2", true } });
        }
        foreach (string marker in new[] { ".first_run_done_mtproto", ".ipv6_warned" })
        {
            string file = Path.Combine(TgData, marker);
            if (!File.Exists(file)) using (File.Open(file, FileMode.OpenOrCreate)) { }
        }
    }

    private static Dictionary<string, object> GetProxyConfig()
    {
        var config = ReadJson(TgConfig);
        int port;
        if (Text(config, "host") != "127.0.0.1" ||
            !Int32.TryParse(Text(config, "port"), NumberStyles.None, CultureInfo.InvariantCulture, out port) ||
            port < 1 || port > 65535 || !Regex.IsMatch(Text(config, "secret"), @"\A[0-9a-fA-F]{32}\z"))
            throw new InvalidDataException("В config.json нужен адрес 127.0.0.1, порт 1-65535 и secret из 32 hex-символов.");
        return config;
    }

    private static void SetProfile(string name)
    {
        int profile = Array.IndexOf(Profiles, name);
        if (profile < 0) throw new ArgumentException("Выбери профиль TG WS Proxy.");
        var config = GetProxyConfig();
        config["cfproxy"] = profile < 2;
        config["h2"] = profile == 0;
        WriteJson(TgConfig, config);
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int length, bool order, int family, int kind, uint reserved);

    private static bool ProxyOwnsListener(int port)
    {
        int length = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref length, false, 2, 3, 0);
        if (length < 4) return false;
        var table = Marshal.AllocHGlobal(length);
        try
        {
            if (GetExtendedTcpTable(table, ref length, false, 2, 3, 0) != 0) return false;
            int count = Marshal.ReadInt32(table);
            for (int i = 0; i < count; i++)
            {
                int offset = 4 + i * 24;
                int address = Marshal.ReadInt32(table, offset + 4);
                int localPort = (ushort)IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(table, offset + 8));
                if (address != 0x0100007f || localPort != port) continue;
                try
                {
                    using (var owner = Process.GetProcessById(Marshal.ReadInt32(table, offset + 20)))
                        if (String.Equals(ProcessPath(owner), TgExe, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (ArgumentException) { }
            }
            return false;
        }
        finally { Marshal.FreeHGlobal(table); }
    }

    internal static void StartTg()
    {
        EnsureTgConfig();
        int port = Int32.Parse(Text(GetProxyConfig(), "port"), CultureInfo.InvariantCulture);
        if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(value => value.Port == port))
            throw new InvalidOperationException("Локальный порт уже занят: " + port);
        using (Process.Start(new ProcessStartInfo(TgExe, "--portable") { WorkingDirectory = Path.GetDirectoryName(TgExe),
            UseShellExecute = false, CreateNoWindow = true })) { }
        var clock = Stopwatch.StartNew();
        do
        {
            Thread.Sleep(250);
            if (ProxyOwnsListener(port)) return;
        } while (clock.Elapsed.TotalSeconds < 18);
        Stop(TgExe);
        throw new System.TimeoutException("TG WS Proxy не начал слушать локальный порт.");
    }

    private void Start(string name)
    {
        if (name == "Zapret") StartZap(Text(state, name)); else StartTg();
    }

    private void Restart(string name)
    {
        Guard(name);
        if (name == "Zapret" && Text(state, name) == "") throw new InvalidOperationException("Сначала примени стратегию zapret.");
        if (name == "TG") { EnsureTgConfig(); GetProxyConfig(); }
        Stop(Executable(name));
        Start(name);
        Log(name + " перезапущен.");
    }

    private void StartInstalledTools()
    {
        foreach (string name in new[] { "Zapret", "TG" })
        {
            string exe = Executable(name);
            if (!File.Exists(exe) || IsRunning(exe)) continue;
            try
            {
                Guard(name);
                if (name == "Zapret" && Text(state, name) == "") Apply(name, "general.bat"); else Start(name);
                Log(name + " запущен вместе с панелью.");
            }
            catch (Exception error) { Send("Error", "Message", "Автозапуск " + name + ": " + error.Message); }
        }
    }

    private void Apply(string name, string choice)
    {
        Guard(name);
        string exe = Executable(name);
        bool running = IsRunning(exe);
        var previous = name == "TG" ? BackupProxy() : null;
        if (name == "Zapret" && !Strategies().Contains(choice, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Выбери стратегию zapret.");
        if (name == "TG" && !Profiles.Contains(choice)) throw new ArgumentException("Выбери профиль TG WS Proxy.");
        Stop(exe);
        try
        {
            if (name == "Zapret") StartZap(choice);
            else { SetProfile(choice); StartTg(); }
            SetState(name, choice);
        }
        catch
        {
            Stop(exe);
            if (previous != null) Write(TgConfig, previous);
            if (running) Start(name);
            throw;
        }
        Log("Применён конфиг " + choice + ".");
    }

    private static byte[] BackupProxy()
    {
        EnsureTgConfig();
        GetProxyConfig();
        return File.ReadAllBytes(TgConfig);
    }

    private static int TgTime(short dc)
    {
        if (!IsRunning(TgExe) || !File.Exists(TgConfig)) return -1;
        var config = GetProxyConfig();
        try { return FlowProbe.Telegram(Int32.Parse(Text(config, "port"), CultureInfo.InvariantCulture), Text(config, "secret"), dc); }
        catch (Exception) { return -1; }
    }

    private void Status() { foreach (string name in ServiceNames) Latency(name, false); }

    private void Latency(string name, bool full)
    {
        int index = Array.IndexOf(ServiceNames, name);
        if (index < 0) throw new ArgumentException("Неизвестный сервис.");
        bool proxy = name == "Telegram" && IsRunning(TgExe);
        Send("Latency", "Name", name, "HTTPS", FlowProbe.Https(Web[index]), "ICMP", full ? FlowProbe.Icmp(Hosts[index]) : -1,
            "MTProto", proxy ? TgTime(2) : -1, "ProxyRunning", proxy, "Full", full);
    }

    private void Test(string name, bool apply)
    {
        Guard(name);
        bool zapret = name == "Zapret";
        var configs = zapret ? Strategies() : Profiles;
        if (configs.Length == 0) throw new InvalidOperationException("Сначала скачай zapret.");
        byte[] previous = zapret ? null : BackupProxy();
        string exe = Executable(name);
        bool running = IsRunning(exe);
        string winner = null;
        int coverage = 0, successes = 0, score = Int32.MaxValue;
        try
        {
            for (int i = 0; i < configs.Length; i++)
            {
                string config = configs[i];
                Stop(exe);
                Send("Progress", "Message", name + " · " + (i + 1) + "/" + configs.Length + " · " + config, "Current", i + 1, "Total", configs.Length);
                var first = new List<int>();
                var second = new List<int>();
                try
                {
                    if (zapret) StartZap(config); else { SetProfile(config); StartTg(); }
                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        int one = zapret ? FlowProbe.Https(Web[0]) : TgTime(2);
                        int two = zapret ? FlowProbe.Https(Web[1]) : TgTime(4);
                        if (one >= 0) first.Add(one);
                        if (two >= 0) second.Add(two);
                    }
                }
                catch (Exception error) { Log(config + ": " + error.Message); }
                int taken = (first.Count > 0 ? 1 : 0) + (second.Count > 0 ? 1 : 0);
                int count = first.Count + second.Count;
                int average = count == 0 ? Int32.MaxValue : (int)Math.Round(first.Concat(second).Average());
                if (count != 0 && (taken > coverage || taken == coverage &&
                    (count > successes || count == successes && (average < score || average == score &&
                    StringComparer.OrdinalIgnoreCase.Compare(config, winner) < 0))))
                { winner = config; coverage = taken; successes = count; score = average; }
                Log(config + ": " + (zapret ? "YouTube " : "DC2 ") + first.Count + "/2 · " + (zapret ? "Discord " : "DC4 ") +
                    second.Count + "/2" + (count == 0 ? "" : " · среднее " + average + " мс"));
            }
        }
        finally
        {
            Stop(exe);
            if (previous != null) Write(TgConfig, previous);
            if (running) Start(name);
        }
        if (winner == null) throw new InvalidOperationException("Ни один конфиг не получил ответа. Предыдущий выбор сохранён.");
        Send("Winner", "Name", name, "Choice", winner);
        if (apply) Apply(name, winner); else Log("Лучший конфиг: " + winner + ". Его можно применить из списка.");
    }
}
