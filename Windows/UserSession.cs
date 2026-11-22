using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class UserSession
{
    internal static void MigrateProxy()
    {
        if (Directory.Exists(Paths.Tg) || !File.Exists(Path.Combine(Paths.LegacyTg, Path.GetFileName(Paths.TgExe))))
            return;
        Directory.CreateDirectory(Paths.UserRoot);
        string stage = Disk.NewFile(Paths.UserRoot);
        try
        {
            Disk.CopyTree(Paths.LegacyTg, stage);
            Directory.Move(stage, Paths.Tg);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    internal static void OpenFromExplorer(bool tray)
    {
        // Ask the desktop shell (running under the interactive user's token).
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true));
        dynamic windows = shell.Windows();
        object location = null, root = null;
        int window;
        dynamic desktop = windows.FindWindowSW(ref location, ref root, 8, out window, 1);
        if (desktop == null || window == 0)
            throw new IOException("Открой zapretgui через меню «Пуск» без запуска от администратора.");
        desktop.Document.Application.ShellExecute(Path.Combine(Paths.Desktop, tray ? "zapretgui-tray.exe" : "zapretgui.exe"),
            "--desktop-user", Paths.Desktop, "open", 1);
    }

    internal static void QueueUpdate()
    {
        Directory.CreateDirectory(Paths.UserRoot);
        CleanRunners();
        string runner = Path.Combine(Paths.UserRoot, "update-ui-" + Guid.NewGuid().ToString("N") + ".exe");
        File.Copy(Assembly.GetExecutingAssembly().Location, runner);
        using (var self = Process.GetCurrentProcess())
        using (Processes.Launch(runner, "--update-ui " + self.Id, Paths.UserRoot)) { }
    }

    internal static int Update(int pid)
    {
        if (AdminOperations.Elevated) return 1;
        try
        {
            using (var parent = Process.GetProcessById(pid))
                if (!parent.WaitForExit(30000)) throw new IOException("zapretgui не закрылся.");
        }
        catch (ArgumentException) { }

        try
        {
            using (var installer = Process.Start(new ProcessStartInfo(Paths.UpdateWorker, "--setup app-update")
            { UseShellExecute = true, Verb = "runas", WorkingDirectory = Paths.Desktop }))
            {
                installer.WaitForExit();
                if (installer.ExitCode != 0)
                    throw new IOException("Не удалось установить обновление zapretgui.");
            }
        }
        catch (Exception error)
        {
            MessageBox.Show(error.GetBaseException().Message, "zapretgui", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        using (Processes.Launch(Path.Combine(Paths.Desktop, "zapretgui.exe"), "", Paths.Desktop)) { }
        return 0;
    }

    internal static void CleanRunners()
    {
        if (!Directory.Exists(Paths.UserRoot)) return;
        foreach (var file in Directory.GetFiles(Paths.UserRoot, "update-ui-*.exe"))
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
