using System;
using System.Diagnostics;
using System.IO;
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

    internal static void SetAutoStart(bool enabled, string sid)
    {
        var user = new SecurityIdentifier(sid);
        dynamic scheduler = ScheduledTasks();
        dynamic folder = scheduler.GetFolder("\\");
        string name = TaskPrefix + user.Value;
        if (!enabled)
        {
            DeleteTask(folder, name + "-system");
            DeleteTask(folder, name);
            return;
        }

        dynamic previous = FindTask(folder, name);
        dynamic previousDefinition = previous == null ? null : previous.Definition;
        RegisterTray(scheduler, folder, name, user.Value, true);
        try
        {
            RegisterZap(scheduler, folder, name, user.Value, true);
        }
        catch
        {
            if (previousDefinition == null)
                DeleteTask(folder, name);
            else
                folder.RegisterTaskDefinition(name, previousDefinition, CreateOrUpdate, previousDefinition.Principal.UserId, null, InteractiveToken, null);
            throw;
        }
    }

    private static void DeleteTask(dynamic folder, string name)
    {
        if (FindTask(folder, name) != null)
            folder.DeleteTask(name, 0);
    }

    private static dynamic LogonTask(dynamic scheduler, string user, bool enabled)
    {
        dynamic task = scheduler.NewTask(0);
        task.Settings.Enabled = enabled;
        task.Settings.DisallowStartIfOnBatteries = false;
        task.Settings.StopIfGoingOnBatteries = false;
        task.Settings.ExecutionTimeLimit = "PT0S";
        dynamic trigger = task.Triggers.Create(9);
        trigger.UserId = user;
        return task;
    }

    private static void RegisterTray(dynamic scheduler, dynamic folder, string name, string user, bool enabled)
    {
        dynamic task = LogonTask(scheduler, user, enabled);
        task.Principal.UserId = user;
        task.Principal.LogonType = InteractiveToken;
        task.Principal.RunLevel = 0;
        dynamic action = task.Actions.Create(0);
        action.Path = TrayPath;
        action.WorkingDirectory = Paths.Desktop;
        folder.RegisterTaskDefinition(name, task, CreateOrUpdate, user, null, InteractiveToken, null);
    }

    private static void RegisterZap(dynamic scheduler, dynamic folder, string name, string user, bool enabled)
    {
        const string system = "S-1-5-18";
        const int serviceAccount = 5;
        dynamic task = LogonTask(scheduler, user, enabled);
        task.Principal.UserId = system;
        task.Principal.LogonType = serviceAccount;
        task.Settings.ExecutionTimeLimit = "PT2M";
        dynamic action = task.Actions.Create(0);
        action.Path = Path.Combine(Paths.Desktop, Launcher);
        action.Arguments = "--scheduled-zap";
        action.WorkingDirectory = Paths.Desktop;
        const int dontAddPrincipalAce = 16;
        const string systemTaskSecurity = "D:P(A;;FA;;;SY)(A;;FA;;;BA)";
        folder.RegisterTaskDefinition(name + "-system", task, CreateOrUpdate | dontAddPrincipalAce,
            system, null, serviceAccount, systemTaskSecurity);
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
        foreach (dynamic task in folder.GetTasks(1))
        {
            string name = task.Name;
            if (!name.StartsWith(TaskPrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            dynamic definition = task.Definition;
            if ((int)definition.Actions.Count != 1 || (int)definition.Actions.Item(1).Type != 0)
                continue;
            dynamic action = definition.Actions.Item(1);
            if ((!Same((string)action.Path, TrayPath) || (int)definition.Principal.RunLevel == 0) && !IsLegacyTask(action, legacy))
                continue;
            SecurityIdentifier user;
            try { user = new SecurityIdentifier(name.Substring(TaskPrefix.Length)); }
            catch (ArgumentException) { continue; }
            if (!Same((string)definition.Principal.UserId, user.Value))
            {
                var account = new NTAccount((string)definition.Principal.UserId);
                if (!user.Equals(account.Translate(typeof(SecurityIdentifier)))) continue;
            }
            bool enabled = task.Enabled;
            RegisterZap(service, folder, name, user.Value, enabled);
            RegisterTray(service, folder, name, user.Value, enabled);
        }

        Processes.Stop(Path.Combine(Paths.LegacyTg, "TgWsProxy_windows.exe"));
        Processes.Stop(Path.Combine(legacy, @"Zapret\bin\winws.exe"));
        Processes.Stop(Path.Combine(legacy, @"TG\TgWsProxy_windows.exe"));
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
                if ((int)action.Type != 0) continue;
                bool starter = name.EndsWith("-system", StringComparison.OrdinalIgnoreCase) &&
                    Same((string)action.Path, Path.Combine(Paths.Desktop, Launcher)) && (string)action.Arguments == "--scheduled-zap";
                if (!(starter || Same((string)action.Path, TrayPath) || IsLegacyTask(action, legacy))) continue;
                folder.DeleteTask(name, 0);
                break;
            }
        }

        if (ZapService.Ours(ZapService.Image))
            ZapService.Remove();
        Processes.Stop(Paths.ZapExe);
        Processes.Stop(Paths.TgExe);
        Processes.Stop(Path.Combine(Paths.LegacyTg, "TgWsProxy_windows.exe"));
        if (Maintenance.CanRestoreHosts())
            Maintenance.RestoreHosts();
        CleanUpdateRunners();
    }

    internal static void CleanUpdateRunners()
    {
        foreach (string path in Directory.GetFiles(Paths.Desktop, "update-*.exe"))
        {
            try
            {
                if (!Same(path, Paths.UpdateWorker)) File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void InstallUpdate()
    {
        var update = Downloads.App();
        if (update == null) return;
        try
        {
            if (!Same(update.Hash, Disk.Hash(update.Path)))
                throw new InvalidDataException("Скачанный установщик изменился.");
            using (var installer = Processes.Launch(update.Path, "/S", Paths.Desktop))
            {
                installer.WaitForExit();
                if (installer.ExitCode != 0)
                    throw new IOException("Ошибка установки, код " + installer.ExitCode + ".");
            }
        }
        finally { File.Delete(update.Path); }
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
            case "app-update":
                InstallUpdate();
                break;
            default:
                throw new ArgumentException("Неизвестная команда установщика.");
        }

        return 0;
    }
}
