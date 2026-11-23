using System;
using System.IO;
using System.Drawing;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using F = System.Windows.Forms;
using W = System.Windows;

[assembly: AssemblyTitle("zapretgui")]
[assembly: AssemblyProduct("zapretgui")]
[assembly: AssemblyCompany("zalphex")]
[assembly: AssemblyVersion("2.1.5.0")]
[assembly: AssemblyFileVersion("2.1.5.0")]
internal static class Program
{
    internal const string MutexId = @"Local\FlowPanel.Flowseal.Manager", OpenId = @"Local\zapretgui.OpenStartupTray";
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            bool setup = args.Length >= 2 && args[0] == "--setup";
            bool scheduled = Path.GetFileNameWithoutExtension(F.Application.ExecutablePath).Equals("zapretgui-tray", StringComparison.OrdinalIgnoreCase);
            // An updater from 2.1.2 reopens the old Program Files launcher once.
            string installed = InstallFolder.Previous;
            if (!setup && installed != null && !Processes.Same(installed, Paths.Root))
            {
                using (Process.Start(Path.Combine(installed, scheduled ? "zapretgui-tray.exe" : "zapretgui.exe"))) { }
                return 0;
            }
            using (var user = WindowsIdentity.GetCurrent())
            {
                if (!new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator))
                {
                    if (setup)
                        return 1;
                    string trusted = Path.Combine(Paths.Desktop, scheduled ? "zapretgui-tray.exe" : "zapretgui.exe");
                    using (Process.Start(new ProcessStartInfo(trusted) { UseShellExecute = true, Verb = "runas" }))
                    {
                    }

                    return 0;
                }
            }

            if (setup)
                return NativeSetup.Run(args);
            using (var mutex = new Mutex(false, MutexId))
            {
                bool first;
                try
                {
                    first = mutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    first = true;
                }

                if (!first)
                {
                    if (!scheduled)
                        try
                        {
                            using (var e = EventWaitHandle.OpenExisting(OpenId))
                                e.Set();
                        }
                        catch (WaitHandleCannotBeOpenedException)
                        {
                        }

                    return 0;
                }

                try
                {
                    NativeSetup.CleanUpdateRunners();
                    ZapretTaskbarIdentity.SetProcess();
                    F.Application.EnableVisualStyles();
                    F.Application.SetCompatibleTextRenderingDefault(false);
                    using (var context = new TrayHost(scheduled && Disk.Option("StartInTray", true)))
                        F.Application.Run(context);
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            var win = ex as System.ComponentModel.Win32Exception;
            if (win != null && win.NativeErrorCode == 1223)
                return 0;
            F.MessageBox.Show(ex.GetBaseException().Message, "zapretgui", F.MessageBoxButtons.OK, F.MessageBoxIcon.Warning);
            return 1;
        }
    }
}

internal sealed class TrayHost : F.ApplicationContext
{
    private readonly F.Control dispatcher = new F.Control();
    private readonly F.NotifyIcon tray = new F.NotifyIcon();
    private readonly F.ContextMenuStrip menu = new F.ContextMenuStrip();
    private readonly F.Timer housekeeping = new F.Timer();
    private readonly EventWaitHandle open = new EventWaitHandle(false, EventResetMode.AutoReset, Program.OpenId);
    private readonly RegisteredWaitHandle signal;
    private readonly Icon icon;
    private readonly Session session;
    private Shell shell;
    private bool ending, openedDuringStartup, startupFinished;
    internal TrayHost(bool hidden)
    {
        var handle = dispatcher.Handle;
        var context = new F.WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(context);
        session = new Session(context);
        icon = Icon.ExtractAssociatedIcon(F.Application.ExecutablePath);
        tray.Icon = icon;
        tray.Text = "zapretgui";
        tray.ContextMenuStrip = menu;
        menu.ShowImageMargin = false;
        var palette = new GrayMenu();
        menu.Renderer = new F.ToolStripProfessionalRenderer(palette);
        menu.ForeColor = Color.White;
        menu.BackColor = palette.ToolStripDropDownBackground;
        menu.Items.Add("Открыть zapretgui", null, (s, e) => OpenWindow());
        var tg = new F.ToolStripMenuItem("TG WS Proxy");
        tg.ForeColor = Color.White;
        tg.DropDown.ForeColor = Color.White;
        tg.DropDown.BackColor = menu.BackColor;
        menu.Items.Add(tg);
        Add(tg, "Открыть в Telegram", () => Launch(ProxySettings.Link()));
        Add(tg, "Скопировать ссылку", () => F.Clipboard.SetText(ProxySettings.Link()));
        tg.DropDownItems.Add(new F.ToolStripSeparator());
        Add(tg, "Перезапустить прокси", async () => await session.RunSimple("Перезапуск TG WS Proxy", () =>
        {
            Components.Stop(Component.TG);
            Components.Start(Component.TG, Components.Selected(Component.TG));
        }));
        Add(tg, "Настройки прокси", () =>
        {
            OpenWindow();
            shell.ProxyEditor();
        });
        Add(tg, "Открыть журнал", () =>
        {
            var file = Path.Combine(Paths.TgData, "proxy.log");
            if (!File.Exists(file))
                throw new IOException("Прокси ещё не создал журнал.");
            using (Processes.Launch(Path.Combine(Environment.SystemDirectory, "notepad.exe"), ZapService.Quote(file), Paths.TgData))
            {
            }
        });
        Add(tg, "Остановить прокси", async () => await session.RunSimple("Остановка TG WS Proxy", () => Components.Stop(Component.TG)));
        menu.Opening += delegate
        {
            tg.Enabled = !session.Busy && File.Exists(Paths.TgExe);
        };
        menu.Items.Add(new F.ToolStripSeparator());
        menu.Items.Add("Выход", null, (s, e) => Quit());
        tray.DoubleClick += delegate
        {
            OpenWindow();
        };
        tray.Visible = true;
        signal = ThreadPool.RegisterWaitForSingleObject(open, (s, timeout) =>
        {
            if (!ending)
                try
                {
                    dispatcher.BeginInvoke((Action)OpenWindow);
                }
                catch (InvalidOperationException)
                {
                }
        }, null, Timeout.Infinite, false);
        housekeeping.Interval = 3500;
        housekeeping.Tick += delegate
        {
            if (File.Exists(Paths.TgExe))
                ZapretProxyTray.HideManagedProxy(Paths.TgExe);
        };
        housekeeping.Start();
        dispatcher.BeginInvoke((Action)(async delegate
        {
            if (!hidden)
                OpenWindow();
            await session.Start(!hidden);
            startupFinished = true;
            if (hidden && openedDuringStartup && session.Latency.Count == 0 && !ending)
                await session.Run("Проверка связи", t => session.MeasureAll());
        }));
    }

    private void Add(F.ToolStripMenuItem parent, string caption, Action run)
    {
        var item = parent.DropDownItems.Add(caption, null, (s, e) =>
        {
            try
            {
                run();
            }
            catch (Exception ex)
            {
                F.MessageBox.Show(ex.Message, "zapretgui");
            }
        });
        item.ForeColor = Color.White;
    }

    internal static void Launch(string uri)
    {
        using (Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }))
        {
        }
    }

    internal void OpenWindow()
    {
        if (ending)
            return;
        bool create = shell == null;
        if (create)
        {
            shell = new Shell(session, this);
            System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(shell.Window);
            shell.Window.Closed += delegate
            {
                shell = null;
            };
        }

        shell.Window.Show();
        if (shell.Window.WindowState == W.WindowState.Minimized)
            shell.Window.WindowState = W.WindowState.Normal;
        shell.Window.Activate();
        if (!startupFinished)
            openedDuringStartup = true;
        else if (!session.Busy && session.Latency.Count == 0)
            FirstReading();
    }

    private async void FirstReading()
    {
        await session.Run("Проверка связи", t => session.MeasureAll());
    }

    internal void Quit()
    {
        if (session.Busy)
        {
            F.MessageBox.Show("Дождись завершения операции или отмени проверку конфигов.", "zapretgui");
            return;
        }

        if (shell != null && !shell.CanDiscard())
            return;
        ending = true;
        if (shell != null)
            shell.CloseForExit();
        ExitThread();
    }

    internal void FinishUpdate(UpdateFile update)
    {
        if (shell != null && !shell.CanDiscard())
        {
            File.Delete(update.Path);
            return;
        }

        NativeSetup.QueueUpdate(update.Path, update.Hash);
        ending = true;
        if (shell != null)
            shell.CloseForExit();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ending = true;
            signal.Unregister(null);
            open.Dispose();
            housekeeping.Dispose();
            tray.Visible = false;
            tray.Dispose();
            menu.Dispose();
            icon.Dispose();
            dispatcher.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed class GrayMenu : F.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground
        {
            get
            {
                return Color.FromArgb(27, 27, 27);
            }
        }

        public override Color MenuItemSelected
        {
            get
            {
                return Color.FromArgb(59, 59, 59);
            }
        }

        public override Color MenuItemBorder
        {
            get
            {
                return Color.DimGray;
            }
        }

        public override Color MenuBorder
        {
            get
            {
                return Color.FromArgb(65, 65, 65);
            }
        }
    }
}
