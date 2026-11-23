using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Security.Principal;
using System.Text.RegularExpressions;

internal static class NativeSetup
{
    private const string TrayExecutable = "zapretgui-tray.exe";
    private const string Launcher = "zapretgui.exe";
    private const string TaskPrefix = "zapret-GUI-";
    private const int InteractiveToken = 3;
    private const int CreateOrUpdate = 6;
    private static string TrayPath
    {
        get
        {
            return Path.Combine(Paths.Desktop, TrayExecutable);
        }
    }

    private static dynamic ScheduledTasks()
    {
        var scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
        dynamic service = scheduler;
        service.Connect();
        return service;
    }

    private static string TaskName()
    {
        using (var identity = WindowsIdentity.GetCurrent())
            return TaskPrefix + identity.User.Value;
    }

    private static dynamic FindTask(dynamic folder, string name)
    {
        try
        {
            return folder.GetTask(name);
        }
        catch (Exception error)
        {
            // The COM interop layer can translate this HRESULT to FileNotFoundException.
            if (error.HResult == unchecked((int)0x80070002))
                return null;
            throw;
        }
    }

    internal static bool AutoStartEnabled()
    {
        dynamic service = ScheduledTasks();
        dynamic folder = service.GetFolder("\\");
        dynamic task = FindTask(folder, TaskName());
        return task != null && task.Enabled;
    }

    internal static void SetAutoStart(bool enabled)
    {
        dynamic service = ScheduledTasks();
        dynamic folder = service.GetFolder("\\");
        string name = TaskName();
        if (!enabled)
        {
            if (FindTask(folder, name) != null)
                folder.DeleteTask(name, 0);
            return;
        }

        if (!File.Exists(TrayPath))
            throw new FileNotFoundException("Установи zapretgui через установщик.");
        string user;
        using (var identity = WindowsIdentity.GetCurrent())
            user = identity.Name;
        dynamic task = service.NewTask(0);
        task.Principal.UserId = user;
        task.Principal.LogonType = InteractiveToken;
        task.Principal.RunLevel = 1;
        task.Settings.DisallowStartIfOnBatteries = false;
        task.Settings.StopIfGoingOnBatteries = false;
        task.Settings.ExecutionTimeLimit = "PT0S";
        dynamic trigger = task.Triggers.Create(9);
        trigger.UserId = user;
        dynamic action = task.Actions.Create(0);
        action.Path = TrayPath;
        action.WorkingDirectory = Paths.Desktop;
        folder.RegisterTaskDefinition(name, task, CreateOrUpdate, user, null, InteractiveToken, null);
    }

    private static bool IsLegacyTask(dynamic action, string legacy)
    {
        string path = (string)action.Path;
        if (Same(path, Path.Combine(legacy, @"Desktop\zapret GUI.exe")) || Same(path, Path.Combine(Paths.Desktop, Launcher)))
            return true;
        string powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        string arguments = "-NoLogo -NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + Path.Combine(Paths.Desktop, "FlowPanel.ps1") + "\" -WindowsStartup";
        return Same(path, powershell) && Same((string)action.Arguments, arguments);
    }

    private static bool Same(string first, string second)
    {
        return String.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    }

    private static void Migrate()
    {
        string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FlowPanel");
        string marker = Path.Combine(Paths.Root, ".flowpanel-migrated");
        if (Directory.Exists(legacy) && !File.Exists(marker))
        {
            foreach (string name in new[]
            {
                "Zapret",
                "TG",
                "state.json"
            }

            )
            {
                string old = Path.Combine(legacy, name), target = Path.Combine(Paths.Root, name);
                if (File.Exists(target) || Directory.Exists(target))
                    continue;
                string temporary = target + "." + Guid.NewGuid().ToString("N") + ".migration";
                try
                {
                    if (Directory.Exists(old))
                    {
                        Disk.CopyTree(old, temporary);
                        Directory.Move(temporary, target);
                    }
                    else if (File.Exists(old))
                    {
                        File.Copy(old, temporary);
                        File.Move(temporary, target);
                    }
                }
                finally
                {
                    if (Directory.Exists(temporary))
                        Directory.Delete(temporary, true);
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
            }

            File.WriteAllText(marker, "");
        }

        dynamic service = ScheduledTasks();
        dynamic folder = service.GetFolder("\\");
        RemoveSplitStartup(folder);
        foreach (dynamic task in folder.GetTasks(1))
        {
            string name = task.Name;
            if (!name.StartsWith(TaskPrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            dynamic definition = task.Definition;
            if ((int)definition.Actions.Count != 1 || (int)definition.Actions.Item(1).Type != 0)
                continue;
            dynamic action = definition.Actions.Item(1);
            bool splitTray = Same((string)action.Path, TrayPath) && (int)definition.Principal.RunLevel == 0;
            if (!splitTray && !IsLegacyTask(action, legacy))
                continue;
            definition.Principal.RunLevel = 1;
            bool enabled = task.Enabled;
            action.Path = TrayPath;
            action.Arguments = "";
            action.WorkingDirectory = Paths.Desktop;
            definition.Settings.Enabled = enabled;
            definition.Settings.ExecutionTimeLimit = "PT0S";
            folder.RegisterTaskDefinition(name, definition, CreateOrUpdate, definition.Principal.UserId, null, InteractiveToken, null);
        }

        Processes.Stop(Path.Combine(legacy, @"Zapret\bin\winws.exe"));
        Processes.Stop(Path.Combine(legacy, @"TG\TgWsProxy_windows.exe"));
    }

    private static void RemoveSplitStartup(dynamic folder)
    {
        var names = new List<string>();
        foreach (dynamic task in folder.GetTasks(1))
        {
            string name = task.Name;
            if (!name.StartsWith(TaskPrefix, StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith("-system", StringComparison.OrdinalIgnoreCase)) continue;
            dynamic actions = task.Definition.Actions;
            if ((int)actions.Count != 1) continue;
            dynamic action = actions.Item(1);
            if ((int)action.Type == 0 && Same((string)action.Path, Path.Combine(Paths.Desktop, Launcher)) &&
                (string)action.Arguments == "--scheduled-zap") names.Add(name);
        }
        foreach (string name in names) folder.DeleteTask(name, 0);
    }

    internal static void RestoreProxySettings()
    {
        string userRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "zapretgui");
        string oldExe = Path.Combine(userRoot, "TG", Path.GetFileName(Paths.TgExe));
        Processes.Stop(oldExe);
        string oldConfig = Path.Combine(Path.GetDirectoryName(oldExe), "TgWsProxy_data", "config.json");
        string marker = Path.Combine(Paths.Root, ".proxy-settings-restored");
        if (File.Exists(marker) || !File.Exists(oldConfig)) return;
        if (new FileInfo(oldConfig).Length > 128 * 1024) throw new InvalidDataException("Конфиг прокси слишком большой.");
        var config = Disk.ReadJson(oldConfig);
        ProxySettings.Validate(config);
        var existing = Disk.ReadJson(Paths.Config);
        existing["host"] = "127.0.0.1";
        existing["port"] = int.Parse(Disk.Get(config, "port"));
        existing["secret"] = Disk.Get(config, "secret");
        foreach (string key in new[] { "cfproxy", "h2" })
        {
            object value;
            if (config.TryGetValue(key, out value) && value is bool) existing[key] = value;
        }
        Disk.Json(Paths.Config, existing);
        Disk.Write(marker, new byte[0]);
    }

    private static void Uninstall()
    {
        dynamic service = ScheduledTasks();
        dynamic folder = service.GetFolder("\\");
        string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FlowPanel");
        foreach (dynamic task in folder.GetTasks(1))
        {
            string name = task.Name;
            if (!name.StartsWith(TaskPrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (dynamic action in task.Definition.Actions)
            {
                if (action.Type != 0 || !(Same((string)action.Path, TrayPath) || IsLegacyTask(action, legacy)))
                    continue;
                folder.DeleteTask(name, 0);
                break;
            }
        }

        if (ZapService.Ours(ZapService.Image))
            ZapService.Remove();
        if (Maintenance.CanRestoreHosts())
            Maintenance.RestoreHosts();
        Processes.Stop(Paths.ZapExe);
        Processes.Stop(Paths.TgExe);
        CleanUpdateRunners();
    }

    internal static void CleanUpdateRunners()
    {
        foreach (string path in System.Linq.Enumerable.Concat(
            Directory.GetFiles(Paths.Desktop, "update-run-*.exe"),
            Directory.GetFiles(Paths.Desktop, "update-worker-*.exe")))
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    internal static void QueueUpdate(string path, string hash)
    {
        CheckUpdatePath(path, hash);
        string runner = Path.Combine(Paths.Desktop, "update-run-" + Guid.NewGuid().ToString("N") + ".exe");
        File.Copy(Path.Combine(Paths.Desktop, Launcher), runner);
        try
        {
            using (var current = Process.GetCurrentProcess())
            using (Process.Start(new ProcessStartInfo(runner, "--setup update " + current.Id + " \"" + path + "\" " + hash) { UseShellExecute = false }))
            {
            }
        }
        catch
        {
            File.Delete(runner);
            throw;
        }
    }

    private static void CheckUpdatePath(string path, string hash)
    {
        if (!Same(Path.GetDirectoryName(Path.GetFullPath(path)), Paths.Desktop) || !Regex.IsMatch(Path.GetFileName(path), @"\Aupdate-[0-9a-f]{32}\.exe\z") || !Regex.IsMatch(hash, @"\A[0-9a-fA-F]{64}\z"))
            throw new InvalidOperationException("Некорректный путь к обновлению.");
    }

    private static void InstallUpdate(int pid, string path, string hash)
    {
        CheckUpdatePath(path, hash);
        try
        {
            Process parent;
            try
            {
                parent = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                parent = null;
            }

            using (parent)
                if (parent != null && !parent.WaitForExit(30000))
                    throw new TimeoutException("Приложение не закрылось.");
            if (!Same(hash, Disk.Hash(path)))
                throw new InvalidDataException("Скачанный установщик изменился.");
            using (var install = Process.Start(new ProcessStartInfo(path, "/S") { UseShellExecute = false }))
            {
                install.WaitForExit();
                if (install.ExitCode != 0)
                    throw new InvalidOperationException("Ошибка установки, код " + install.ExitCode + ".");
            }

            using (Process.Start(Path.Combine(Paths.Desktop, Launcher)))
            {
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    internal static int Run(string[] args)
    {
        switch (args[1])
        {
            case "migrate":
                Migrate();
                break;
            case "shortcut":
                ZapretTaskbarIdentity.SetShortcut(args[2]);
                break;
            case "uninstall":
                Uninstall();
                break;
            case "update":
                InstallUpdate(Int32.Parse(args[2]), args[3], args[4]);
                break;
            default:
                throw new ArgumentException("Неизвестная команда установщика.");
        }

        return 0;
    }
}
