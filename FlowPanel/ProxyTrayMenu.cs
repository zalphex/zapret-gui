using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal sealed class ProxyTrayMenu
{
    private static readonly Color Background = Color.FromArgb(24, 24, 24);
    private readonly string executable;
    private readonly string configuration;
    private readonly string log;
    private readonly Func<bool> available;
    private readonly Action changed;
    internal bool Busy { get; private set; }

    internal ProxyTrayMenu(string executable, Func<bool> available, Action changed)
    {
        this.executable = executable;
        this.available = available;
        this.changed = changed;
        string data = Path.Combine(Path.GetDirectoryName(executable), "TgWsProxy_data");
        configuration = Path.Combine(data, "config.json");
        log = Path.Combine(data, "proxy.log");
    }

    internal ToolStripMenuItem Create()
    {
        var menu = new ToolStripMenuItem("TG WS Proxy");
        var open = new ToolStripMenuItem("Открыть в Telegram");
        open.Click += delegate { Safe(delegate { using (Process.Start(Link())) { } }); };
        menu.DropDownItems.Add(open);
        menu.DropDownItems.Add("Скопировать ссылку", null, delegate { Safe(delegate { Clipboard.SetText(Link()); }); });
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add("Перезапустить прокси", null, async delegate { await Change(true); });
        menu.DropDownItems.Add("Настройки…", null, delegate { Safe(EditSettings); });
        menu.DropDownItems.Add("Открыть логи", null, delegate
        {
            Safe(delegate
            {
                if (!File.Exists(log)) throw new FileNotFoundException("Прокси ещё не создал журнал.");
                using (Process.Start("notepad.exe", "\"" + log + "\"")) { }
            });
        });
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add("Остановить прокси", null, async delegate { await Change(false); });
        menu.DropDownOpening += delegate
        {
            bool configured = File.Exists(configuration);
            foreach (ToolStripItem item in menu.DropDownItems) item.Enabled = configured && available() && !Busy;
            open.Text = "Открыть в Telegram";
            try { if (configured) open.Text += " (127.0.0.1:" + ReadPort(ReadConfig()) + ")"; }
            catch (InvalidOperationException) { }
            catch (IOException) { }
            catch (ArgumentException) { }
        };
        return menu;
    }

    private void Safe(Action operation)
    {
        if (!available() || Busy) return;
        try { operation(); }
        catch (Exception error) { MessageBox.Show(error.Message, "TG WS Proxy", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private Dictionary<string, object> ReadConfig()
    {
        return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(configuration));
    }

    private static int ReadPort(Dictionary<string, object> config)
    {
        object port, host;
        int number;
        if (config == null || !config.TryGetValue("host", out host) || !object.Equals(host, "127.0.0.1") ||
            !config.TryGetValue("port", out port) || !int.TryParse(Convert.ToString(port), out number) || number < 1 || number > 65535)
            throw new InvalidOperationException("В настройках прокси нужен адрес 127.0.0.1 и порт от 1 до 65535.");
        return number;
    }

    private static string Secret(Dictionary<string, object> config)
    {
        object value;
        if (!config.TryGetValue("secret", out value) || !Regex.IsMatch(Convert.ToString(value), @"\A[0-9a-fA-F]{32}\z"))
            throw new InvalidOperationException("Secret должен состоять из 32 шестнадцатеричных символов.");
        return Convert.ToString(value);
    }

    private string Link()
    {
        var config = ReadConfig();
        return "tg://proxy?server=127.0.0.1&port=" + ReadPort(config) + "&secret=dd" + Secret(config);
    }

    private void EditSettings()
    {
        using (var dialog = new Form { Text = "Настройки TG WS Proxy", BackColor = Background, ForeColor = Color.White,
            Width = 660, Height = 510, StartPosition = FormStartPosition.CenterScreen })
        using (var editor = new DarkTextBox { BorderStyle = BorderStyle.None, Multiline = true, Dock = DockStyle.Fill, BackColor = Color.Black,
            ForeColor = Color.White, ScrollBars = ScrollBars.Vertical, Text = File.ReadAllText(configuration) })
        using (var save = new Button { Text = "Сохранить", Dock = DockStyle.Bottom, Height = 36, FlatStyle = FlatStyle.Flat })
        {
            save.Click += delegate
            {
                string temporary = configuration + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    var config = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(editor.Text);
                    ReadPort(config);
                    Secret(config);
                    File.WriteAllText(temporary, editor.Text, new UTF8Encoding(false));
                    File.Replace(temporary, configuration, null);
                    dialog.DialogResult = DialogResult.OK;
                }
                catch (Exception error) { MessageBox.Show(dialog, error.Message, "TG WS Proxy"); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            };
            dialog.Controls.Add(editor);
            dialog.Controls.Add(save);
            dialog.ShowDialog();
        }
    }

    private async Task Change(bool restart)
    {
        if (!available() || Busy) return;
        Busy = true;
        try
        {
            await Task.Run(delegate
            {
                int port = 0;
                if (restart)
                {
                    if (!File.Exists(executable)) throw new FileNotFoundException("TG WS Proxy не установлен.");
                    var config = ReadConfig();
                    port = ReadPort(config);
                    Secret(config);
                }
                StopManaged();
                if (!restart) return;
                if (Listening(port)) throw new InvalidOperationException("Локальный порт уже занят.");
                using (Process.Start(new ProcessStartInfo(executable, "--portable")
                { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable) })) { }
                for (int attempt = 0; attempt < 72; attempt++)
                {
                    Thread.Sleep(250);
                    if (Listening(port)) return;
                }
                StopManaged();
                throw new InvalidOperationException("Прокси не начал слушать локальный порт.");
            });
        }
        catch (Exception error) { MessageBox.Show(error.Message, "TG WS Proxy", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { Busy = false; changed(); }
    }

    private void StopManaged()
    {
        var targets = new List<Process>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            try
            {
                if (String.Equals(process.MainModule.FileName, executable, StringComparison.OrdinalIgnoreCase))
                { targets.Add(process); continue; }
            }
            catch (System.ComponentModel.Win32Exception) { }
            catch (InvalidOperationException) { }
            process.Dispose();
        }
        try
        {
            foreach (var process in targets)
            {
                try { process.Kill(); }
                catch (InvalidOperationException) { }
            }
            foreach (var process in targets)
                if (!process.WaitForExit(5000)) throw new InvalidOperationException("Не удалось остановить прокси.");
        }
        finally { foreach (var process in targets) process.Dispose(); }
    }

    private static bool Listening(int port)
    {
        foreach (var address in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            if (address.Port == port) return true;
        return false;
    }
}
