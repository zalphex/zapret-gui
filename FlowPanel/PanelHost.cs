using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class PanelHost
{
    internal const string Version = "1.3.18";
    private const string MutexName = @"Local\FlowPanel.Flowseal.Manager";
    private const string OpenEvent = @"Local\zapretgui.OpenStartupTray";
    internal static readonly string Desktop = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"zapretgui\Desktop");
    internal static bool ReadSetting(string key, bool fallback)
    {
        using (var settings = Registry.CurrentUser.OpenSubKey(@"Software\zapretgui"))
        {
            if (settings == null || settings.GetValue(key) == null) return fallback;
            return object.Equals(settings.GetValue(key), 1);
        }
    }

    internal static void SaveSetting(string key, bool value)
    {
        using (var settings = Registry.CurrentUser.CreateSubKey(@"Software\zapretgui"))
            settings.SetValue(key, value ? 1 : 0, RegistryValueKind.DWord);
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [STAThread]
    private static int Main(string[] args)
    {
        bool setup = args.Length >= 2 && args[0] == "--setup";
        bool scheduled = Path.GetFileNameWithoutExtension(Application.ExecutablePath)
            .Equals("zapretgui-tray", StringComparison.OrdinalIgnoreCase);
        try
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                {
                    if (setup) return 1;
                    string trusted = Path.Combine(Desktop, Path.GetFileName(Application.ExecutablePath));
                    using (Process.Start(new ProcessStartInfo(trusted) { Verb = "runas", UseShellExecute = true })) { }
                    return 0;
                }
            }
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            if (setup) return NativeSetup.Run(args);
            using (var mutex = new Mutex(false, MutexName))
            {
                bool owner;
                try { owner = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { owner = true; }
                if (!owner)
                {
                    if (!scheduled)
                    {
                        try
                        {
                            using (var signal = EventWaitHandle.OpenExisting(OpenEvent)) signal.Set();
                        }
                        catch (WaitHandleCannotBeOpenedException) { }
                    }
                    return 0;
                }
                try
                {
                    NativeSetup.CleanUpdateRunners();
                    SetProcessDPIAware();
                    ZapretTaskbarIdentity.SetProcess();
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (var context = new PanelContext(scheduled && ReadSetting("StartInTray", false)))
                        Application.Run(context);
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            if (error.NativeErrorCode == 1223) return 0;
            if (!setup || args[1] == "update") MessageBox.Show(error.Message, "zapretgui");
            return 1;
        }
        catch (Exception error)
        {
            if (!setup || args[1] == "update") MessageBox.Show(error.Message, "zapretgui");
            return 1;
        }
        return 0;
    }

    private sealed class PanelContext : ApplicationContext
    {
        private readonly EventWaitHandle signal = new EventWaitHandle(false, EventResetMode.AutoReset, OpenEvent);
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly Icon icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        private readonly string proxy = Path.Combine(Path.GetDirectoryName(Desktop), @"TG\TgWsProxy_windows.exe");
        private readonly ProxyTrayMenu proxyMenu;
        private MainPanel panel;
        private Task startup;
        private bool openRequested;
        private bool exitRequested;
        private bool startedInTray;
        private DateTime nextHide;

        internal PanelContext(bool background)
        {
            tray.Icon = icon ?? SystemIcons.Application;
            tray.Text = "zapretgui";
            tray.ContextMenuStrip = menu;
            menu.Items.Add("Открыть zapretgui", null, delegate { RequestOpen(); });
            proxyMenu = new ProxyTrayMenu(proxy, delegate { return startup == null && (panel == null || !panel.IsWorking); },
                delegate { if (panel != null) panel.RefreshProcessState(); });
            menu.Items.Add(proxyMenu.Create());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Выход", null, delegate
            {
                if (proxyMenu.Busy) return;
                if (panel != null) panel.RequestExit();
                else if (startup == null) ExitThread();
                else exitRequested = true;
            });
            tray.DoubleClick += delegate { RequestOpen(); };
            timer.Interval = 250;
            timer.Tick += OnTick;
            if (background)
            {
                startedInTray = true;
                tray.Visible = true;
                startup = Task.Run(() => new NativeEngine(null).Run("StartTools", "", ""));
            }
            else RequestOpen();
            timer.Start();
        }

        private void RequestOpen()
        {
            if (startup != null) { openRequested = true; return; }
            if (panel == null)
            {
                panel = new MainPanel(icon, startedInTray, delegate { return !proxyMenu.Busy; });
                panel.FormClosed += delegate { ExitThread(); };
                panel.VisibleChanged += delegate { tray.Visible = !panel.Visible; };
            }
            panel.Show();
            if (panel.WindowState == FormWindowState.Minimized) panel.WindowState = FormWindowState.Normal;
            panel.Activate();
            tray.Visible = false;
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (DateTime.UtcNow >= nextHide)
            {
                ZapretProxyTray.HideManagedProxy(proxy);
                nextHide = DateTime.UtcNow.AddSeconds(2);
            }
            if (startup != null && startup.IsCompleted)
            {
                if (startup.IsFaulted)
                    tray.ShowBalloonTip(3500, "Ошибка автозапуска", startup.Exception.GetBaseException().Message, ToolTipIcon.Warning);
                startup = null;
                if (exitRequested) { ExitThread(); return; }
                if (openRequested) RequestOpen();
            }
            if (signal.WaitOne(0)) RequestOpen();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                tray.Visible = false;
                tray.Dispose();
                menu.Dispose();
                signal.Dispose();
                if (panel != null) panel.Dispose();
                if (icon != null) icon.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
