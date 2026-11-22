using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static class AdminOperations
{
    private const string PipePrefix = "zapretgui-admin-";
    private const int FrameLimit = 32 * 1024 * 1024;

    internal static bool Elevated
    {
        get
        {
            using (var identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint id);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint id);

    private static void Write(BinaryWriter writer, string value)
    {
        var bytes = Disk.Utf8.GetBytes(value ?? "");
        if (bytes.Length > FrameLimit) throw new IOException("Слишком большой запрос.");
        writer.Write(bytes.Length);
        writer.Write(bytes);
        writer.Flush();
    }

    private static string Read(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > FrameLimit) throw new IOException("Некорректный размер запроса.");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return Disk.Utf8.GetString(bytes);
    }

    internal static string Invoke(string action, params string[] arguments)
    {
        return Invoke(action, arguments, CancellationToken.None, null, null, null);
    }

    internal static string Invoke(string action, string[] arguments, CancellationToken cancellation, Action<string> log,
        Action<int, int, string> progress, Func<string, bool> confirm)
    {
        string id = Guid.NewGuid().ToString("N");
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(true, false);
        using (var identity = WindowsIdentity.GetCurrent())
            acl.AddAccessRule(new PipeAccessRule(identity.User, PipeAccessRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        using (var pipe = new NamedPipeServerStream(PipePrefix + id, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 8192, 8192, acl))
        using (var self = Process.GetCurrentProcess())
        {
            Process worker;
            try
            {
                worker = Process.Start(new ProcessStartInfo(Path.Combine(Paths.Desktop, "zapretgui.exe"),
                    "--admin " + id + " " + self.Id) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Paths.Desktop });
            }
            catch (Win32Exception error)
            {
                if (error.NativeErrorCode == 1223) throw new IOException("Запрос прав администратора отменён.");
                throw;
            }

            using (worker)
            {
                var wait = pipe.BeginWaitForConnection(null, null);
                using (var ready = wait.AsyncWaitHandle)
                {
                    var deadline = Stopwatch.StartNew();
                    while (!ready.WaitOne(200))
                        if (worker.HasExited || deadline.Elapsed.TotalSeconds > 15)
                            throw new IOException("Не удалось связаться с системным помощником.");
                    pipe.EndWaitForConnection(wait);
                }
                uint peer;
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out peer) || peer != worker.Id)
                    throw new IOException("Неверный процесс системного помощника.");

                using (var reader = new BinaryReader(pipe, Encoding.UTF8, true))
                using (var writer = new BinaryWriter(pipe, Encoding.UTF8, true))
                {
                    Write(writer, action);
                    writer.Write(arguments.Length);
                    foreach (var argument in arguments) Write(writer, argument);
                    var writeLock = new object();
                    using (cancellation.Register(() =>
                    {
                        lock (writeLock)
                        {
                            try { Write(writer, "cancel"); } catch (IOException) { } catch (ObjectDisposedException) { }
                        }
                    }))
                    {
                        for (;;)
                        {
                            switch (Read(reader))
                            {
                                case "log":
                                    var message = Read(reader);
                                    if (log != null) log(message);
                                    break;
                                case "progress":
                                    int done = reader.ReadInt32(), total = reader.ReadInt32();
                                    var name = Read(reader);
                                    if (progress != null) progress(done, total, name);
                                    break;
                                case "confirm":
                                    var candidate = Read(reader);
                                    bool accepted = confirm != null && !cancellation.IsCancellationRequested && confirm(candidate);
                                    lock (writeLock) Write(writer, accepted ? "yes" : "no");
                                    break;
                                case "done": return Read(reader);
                                case "cancel": throw new OperationCanceledException();
                                case "error": throw new IOException(Read(reader));
                                default: throw new IOException("Неизвестный ответ системного помощника.");
                            }
                        }
                    }
                }
            }
        }
    }

    internal static int Serve(string id, int callerPid)
    {
        Guid parsed;
        if (!Elevated || !Guid.TryParseExact(id, "N", out parsed)) return 1;
        using (var pipe = new NamedPipeClientStream(".", PipePrefix + id, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification))
        {
            pipe.Connect(10000);
            uint peer;
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out peer) || peer != callerPid) return 1;
            using (var reader = new BinaryReader(pipe, Encoding.UTF8, true))
            using (var writer = new BinaryWriter(pipe, Encoding.UTF8, true))
            using (var cancel = new CancellationTokenSource())
            {
                try
                {
                    string operation = Read(reader);
                    int count = reader.ReadInt32();
                    if (count < 0 || count > 4) throw new IOException("Некорректный запрос.");
                    var args = new string[count];
                    for (int i = 0; i < count; i++) args[i] = Read(reader);
                    var accepted = new TaskCompletionSource<bool>();
                    Task.Run(() =>
                    {
                        try
                        {
                            for (;;)
                            {
                                string answer = Read(reader);
                                if (answer == "cancel") break;
                                if (answer == "yes" || answer == "no") accepted.TrySetResult(answer == "yes");
                            }
                        }
                        catch (IOException) { }
                        catch (ObjectDisposedException) { }
                        try { cancel.Cancel(); } catch (ObjectDisposedException) { }
                    });
                    string result = InSystemLock(() => Run(operation, args, cancel.Token,
                        line => { Write(writer, "log"); Write(writer, line); },
                        (done, total, name) =>
                        {
                            Write(writer, "progress");
                            writer.Write(done);
                            writer.Write(total);
                            Write(writer, name);
                        },
                        name =>
                        {
                            Write(writer, "confirm"); Write(writer, name);
                            return accepted.Task.Wait(180000, cancel.Token) && accepted.Task.Result;
                        }, callerPid));
                    Write(writer, "done"); Write(writer, result);
                    return 0;
                }
                catch (OperationCanceledException) { Write(writer, "cancel"); return 0; }
                catch (Exception error) { Write(writer, "error"); Write(writer, error.GetBaseException().Message); return 1; }
            }
        }
    }

    internal static T InSystemLock<T>(Func<T> action)
    {
        var access = new MutexSecurity();
        foreach (var type in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            access.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(type, null), MutexRights.FullControl, AccessControlType.Allow));
        bool created;
        using (var gate = new Mutex(false, @"Global\zapretgui.SystemOperations", out created, access))
        {
            bool acquired;
            try { acquired = gate.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Другая системная операция zapretgui ещё выполняется.");
            try { return action(); }
            finally { gate.ReleaseMutex(); }
        }
    }

    private static string Run(string operation, string[] args, CancellationToken token, Action<string> log,
        Action<int, int, string> progress, Func<string, bool> confirm, int callerPid)
    {
        token.ThrowIfCancellationRequested();
        switch (operation)
        {
            case "start": if (args.Length == 0) StartZap(); else goto default; break;
            case "restart": Components.Stop(Component.Zapret); StartZap(); break;
            case "stop": Components.Stop(Component.Zapret); break;
            case "apply": Components.Apply(Component.Zapret, args[0]); break;
            case "install": Downloads.Install(Component.Zapret, log); break;
            case "test": return Checks.Test(Component.Zapret, args[0] == "preview", token, log, progress, confirm);
            case "filters": AddressLists.Filters(new GamePorts { Mode = args[0], Tcp = args[1], Udp = args[2] }, args[3]); break;
            case "ipset": AddressLists.Update(); break;
            case "list": AddressLists.Save(new ListEdit { Name = args[0], Token = args[1] }, args[2]); break;
            case "sample": Maintenance.ReplaceSample(args[0], args[1] == "discord"); break;
            case "service-install": Maintenance.InstallService(args[0]); break;
            case "service-remove": ZapService.Remove(); break;
            case "service-stop": ServiceTools.SetConflictRunning(args[0], false); break;
            case "service-start": ServiceTools.SetConflictRunning(args[0], true); break;
            case "hosts": Maintenance.Hosts(args[0]); break;
            case "hosts-restore": Maintenance.RestoreHosts(); break;
            case "timestamps": ServiceTools.TcpTimestamps(); break;
            case "autostart": NativeSetup.SetAutoStart(args[0] == "on", Processes.UserSid(callerPid)); break;
            default: throw new IOException("Неизвестная системная операция.");
        }
        return "";
    }

    internal static void StartZap()
    {
        if (Components.Installed(Component.Zapret) && !Processes.Running(Paths.ZapExe))
            Components.Start(Component.Zapret, Components.Selected(Component.Zapret));
    }
}
