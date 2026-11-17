using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal sealed class MainPanel : Form
{
    private static readonly Color Surface = Color.FromArgb(19, 19, 19);
    private static readonly Color Muted = Color.FromArgb(185, 185, 185);
    private readonly FlowLayoutPanel navigation = new GlassPanel();
    private readonly Panel pages = new Panel();
    private Button selectedTab;
    private Control selectedPage;
    private readonly Label status = new Label();
    private readonly ProgressBar progress = new ProgressBar();
    private readonly TextBox activity = Editor(true);
    private readonly TextBox list = Editor(false);
    private readonly ComboBox zap = Choice();
    private readonly ComboBox proxy = Choice();
    private readonly ComboBox lists = Choice("list-general-user.txt", "list-exclude-user.txt", "ipset-all.txt", "ipset-exclude-user.txt");
    private readonly ComboBox game = Choice("Выключен", "TCP и UDP", "Только TCP", "Только UDP");
    private readonly ComboBox ipset = Choice("По списку", "Выключен", "Все IP");
    private readonly string[] gameModes = { "disabled", "all", "tcp", "udp" };
    private readonly string[] ipsetModes = { "loaded", "none", "any" };
    private readonly CheckBox autostart = new CheckBox();
    private readonly Dictionary<string, Label> cards = new Dictionary<string, Label>();
    private readonly List<Action> refresh = new List<Action>();
    private readonly Dictionary<string, string> winners = new Dictionary<string, string>();
    private Dictionary<string, object> state, loadedList, update;
    private bool busy, explicitExit, updatingAutoStart, autoStartEnabled;
    private readonly bool fromTray;
    private readonly Func<bool> mayRun;
    internal bool IsWorking { get { return busy; } }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    internal MainPanel(Icon icon, bool fromTray, Func<bool> mayRun)
    {
        this.fromTray = fromTray;
        this.mayRun = mayRun;
        Text = "zapretgui";
        Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1080, 700);
        MinimumSize = new Size(770, 510);
        BackColor = Color.Black;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10F);
        var frame = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(15), ColumnCount = 1, RowCount = 4 };
        frame.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        frame.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        frame.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        frame.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));
        Controls.Add(frame);
        var brand = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        if (icon != null)
        {
            var image = new PictureBox { Image = icon.ToBitmap(), Width = 40, Height = 40, SizeMode = PictureBoxSizeMode.Zoom };
            image.Disposed += delegate { image.Image.Dispose(); };
            brand.Controls.Add(image);
        }
        brand.Controls.Add(new Label { Text = "zapretgui", AutoSize = true, Font = new Font("Segoe UI", 24, FontStyle.Bold) });
        frame.Controls.Add(brand, 0, 0);
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        navigation.Dock = DockStyle.Fill;
        navigation.FlowDirection = FlowDirection.TopDown;
        navigation.WrapContents = false;
        navigation.BackColor = Surface;
        navigation.Padding = new Padding(5);
        Round(navigation, 19);
        pages.Dock = DockStyle.Fill;
        pages.BackColor = Color.Black;
        body.Controls.Add(navigation, 0, 0);
        body.Controls.Add(pages, 1, 0);
        frame.Controls.Add(body, 0, 1);
        status.Dock = DockStyle.Fill;
        status.ForeColor = Muted;
        frame.Controls.Add(status, 0, 2);
        progress.Dock = DockStyle.Fill;
        progress.Visible = false;
        frame.Controls.Add(progress, 0, 3);

        var overview = Page("Статус", "\ue9d9");
        overview.Controls.Add(Heading("Работоспособность", 23));
        Add(overview, "Обновить статус", "Status");
        StatusGrid(overview, new[] { "YouTube", "Discord", "Telegram" });
        overview.Controls.Add(Heading("Утилиты", 13));
        StatusGrid(overview, new[] { "Zapret", "TG" });

        var setup = Page("Установка и тесты", "\ue896");
        setup.Controls.Add(Heading("Установка и тесты", 23));
        var packages = Row(Section(setup, "Установка и обновления"));
        Add(packages, "Скачать обе утилиты", "DownloadAll");
        Add(packages, "Проверить обновления", "Update");
        ToolControls(setup, "zapret", "Zapret", zap);
        ToolControls(setup, "TG WS Proxy", "TG", proxy);
        var ping = Row(Section(setup, "Пинг сервисов"));
        foreach (var name in new[] { "YouTube", "Discord", "Telegram" }) Add(ping, "Пинг " + name, "Ping", name);
        var journal = Section(setup, "Журнал");
        journal.Controls.Add(activity);
        FitWidth(journal, activity, journal.Padding.Horizontal + 8);

        var filters = Page("Настройка обхода", "\ue72e");
        filters.Controls.Add(Heading("Настройка обхода", 23));
        var modes = Row(Section(filters, "Фильтры zapret"));
        modes.Controls.Add(new Label { Text = "Игровой фильтр", AutoSize = true });
        modes.Controls.Add(game);
        modes.Controls.Add(new Label { Text = "IPSet", AutoSize = true });
        modes.Controls.Add(ipset);
        Add(modes, "Применить", "Filters", "Zapret",
            delegate { return gameModes[game.SelectedIndex] + "|" + ipsetModes[ipset.SelectedIndex]; }, delegate { return Installed("Zapret"); });
        var listSection = Section(filters, "Списки обхода и исключений");
        var listActions = Row(listSection);
        listActions.Controls.Add(lists);
        Add(listActions, "Открыть", "ReadList", "", delegate { return Selected(lists); }, delegate { return Installed("Zapret"); });
        Add(listActions, "Сохранить", "SaveList", "", ListRequest,
            delegate { return Installed("Zapret") && loadedList != null && Value(loadedList, "Name") == Selected(lists); });
        Add(listActions, "Папка", "ListsFolder", "Zapret", null, delegate { return Installed("Zapret"); });
        Add(listActions, "Обновить IPSet", "UpdateIPSet", "Zapret", null, delegate { return Installed("Zapret"); });
        listSection.Controls.Add(list);
        FitWidth(listSection, list, listSection.Padding.Horizontal + 8);
        filters.Resize += delegate { list.Height = Math.Max(125, filters.ClientSize.Height - 270); };
        lists.SelectedIndexChanged += delegate { RefreshButtons(); };

        var settings = Page("Настройки", "\ue713");
        settings.Controls.Add(Heading("Настройки", 23));
        var launchSettings = Section(settings, "Запуск и трей");
        autostart.Text = "Автозапуск zapretgui";
        autostart.AutoSize = true;
        launchSettings.Controls.Add(autostart);
        autostart.CheckedChanged += async delegate
        {
            if (updatingAutoStart) return;
            await Run("AutoStart", "", autostart.Checked ? "on" : "off");
            SetAutoStartCheck(autoStartEnabled);
        };
        Setting(launchSettings, "Запускать в фоне", "StartInTray", false);
        Setting(launchSettings, "Убирать окно в трей", "MinimizeToTray", true);
        var updates = Section(settings, "Обновление приложения");
        updates.Controls.Add(new Label { AutoSize = true, ForeColor = Muted, Text = "Установлена версия " + PanelHost.Version });
        Add(updates, "Обновить zapretgui", "AppUpdate", "", delegate { return PanelHost.Version; });
        Shown += async delegate
        {
            await Run(this.fromTray ? "Status" : "Startup", "", "");
            if (!IsDisposed) await Run("AutoStart", "", "");
        };
        Resize += delegate
        {
            if (WindowState == FormWindowState.Minimized && PanelHost.ReadSetting("MinimizeToTray", true)) Hide();
        };
        FormClosing += OnClosing;
        RefreshButtons();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int enabled = 1;
        if (DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(Handle, 19, ref enabled, sizeof(int));
    }

    private FlowLayoutPanel Page(string title, string glyph)
    {
        var content = new DarkScrollPage { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, Padding = new Padding(14), BackColor = Color.Black, Visible = false };
        var button = new DarkButton { Text = title, Width = 168, Height = 42, FlatStyle = FlatStyle.Flat,
            Glyph = glyph, BackColor = Surface, ForeColor = Color.White,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(9, 0, 0, 0) };
        button.FlatAppearance.BorderSize = 0;
        Round(button, 15);
        button.Click += delegate { SelectPage(button, content); };
        navigation.Controls.Add(button);
        pages.Controls.Add(content);
        if (selectedPage == null) SelectPage(button, content);
        return content;
    }

    private static FlowLayoutPanel Row(Control parent)
    {
        var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
        parent.Controls.Add(row);
        parent.Resize += delegate { row.MaximumSize = new Size(Math.Max(220, parent.ClientSize.Width - parent.Padding.Horizontal - 12), 0); };
        return row;
    }

    private static Label Heading(string title, int size)
    {
        return new Label { AutoSize = true, Text = title, Font = new Font("Segoe UI", size, FontStyle.Bold),
            Margin = new Padding(3, 2, 3, 9) };
    }

    private static void FitWidth(Control parent, Control child, int spacing)
    {
        Action resize = delegate { child.Width = Math.Max(220, parent.ClientSize.Width - spacing); };
        parent.Resize += delegate { resize(); };
        resize();
    }

    private static FlowLayoutPanel Section(FlowLayoutPanel page, string title)
    {
        var section = new GlassPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly,
            BackColor = Surface, Padding = new Padding(15), Margin = new Padding(3, 4, 3, 10),
            FlowDirection = FlowDirection.TopDown, WrapContents = false };
        page.Controls.Add(section);
        section.Controls.Add(Heading(title, 13));
        page.Resize += delegate
        {
            int width = Math.Max(220, page.ClientSize.Width - page.Padding.Horizontal - 24);
            section.MinimumSize = new Size(width, 0);
            section.MaximumSize = new Size(width, 0);
        };
        Round(section, 23);
        return section;
    }

    private void StatusGrid(FlowLayoutPanel page, string[] names)
    {
        var grid = new TableLayoutPanel { ColumnCount = names.Length, RowCount = 1, Height = 108,
            Margin = new Padding(0, 5, 0, 12) };
        page.Controls.Add(grid);
        FitWidth(page, grid, page.Padding.Horizontal + 24);
        for (int i = 0; i < names.Length; i++)
        {
            string name = names[i];
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / names.Length));
            var card = new GlassPanel { Dock = DockStyle.Fill, BackColor = Surface, Padding = new Padding(12),
                FlowDirection = FlowDirection.TopDown, WrapContents = false };
            var label = new Label { AutoSize = true, ForeColor = Muted, Text = "Ожидаю проверку",
                Margin = new Padding(3, 0, 3, 0), MaximumSize = new Size(160, 0) };
            card.Controls.Add(Heading(name == "TG" ? "TG WS Proxy" : name == "Zapret" ? "zapret" : name, 13));
            card.Controls.Add(label);
            card.Resize += delegate { label.MaximumSize = new Size(Math.Max(70, card.ClientSize.Width - 26), 0); };
            grid.Controls.Add(card, i, 0);
            cards.Add(name, label);
            Round(card, 20);
        }
    }

    private static void Round(Control control, int radius)
    {
        Action apply = delegate
        {
            int width = control.ClientSize.Width, height = control.ClientSize.Height;
            int diameter = Math.Min(radius * 2, Math.Min(width, height));
            if (diameter < 2) return;
            using (var path = new GraphicsPath())
            {
                path.AddArc(0, 0, diameter, diameter, 180, 90);
                path.AddArc(width - diameter, 0, diameter, diameter, 270, 90);
                path.AddArc(width - diameter, height - diameter, diameter, diameter, 0, 90);
                path.AddArc(0, height - diameter, diameter, diameter, 90, 90);
                path.CloseFigure();
                Region previous = control.Region;
                control.Region = new Region(path);
                if (previous != null) previous.Dispose();
            }
        };
        control.SizeChanged += delegate { apply(); };
        apply();
    }

    private void SelectPage(Button button, Control page)
    {
        if (selectedPage != null) selectedPage.Hide();
        if (selectedTab != null) selectedTab.BackColor = Surface;
        selectedPage = page;
        selectedTab = button;
        button.BackColor = Color.FromArgb(53, 53, 53);
        page.BringToFront();
        page.Show();
    }

    private void ToolControls(Control parent, string title, string target, ComboBox choices)
    {
        var row = Row(Section((FlowLayoutPanel)parent, title));
        row.Controls.Add(choices);
        Add(row, "Скачать / обновить", "Download", target);
        Add(row, "Папка", "Folder", target, null, delegate { return Installed(target); });
        Add(row, "Применить", "Apply", target, delegate { return Selected(choices); },
            delegate { return Installed(target) && choices.SelectedIndex >= 0; });
        Add(row, "Проверить конфиги", "Test", target, null, delegate { return Installed(target); });
        Add(row, "Автовыбор и запуск", "Auto", target, null, delegate { return Installed(target); });
        Add(row, "Перезапустить", "Restart", target, null, delegate { return Installed(target) && Value(state, target) != ""; });
        Add(row, "Остановить", "Stop", target, null, delegate { return Flag(state, target == "TG" ? "TGRunning" : "ZapRunning"); });
        if (target == "TG") Add(row, "Подключить Telegram", "Connect", target, null, delegate { return Flag(state, "TGRunning"); });
    }

    private void Add(Control parent, string title, string action, string target = "", Func<string> choice = null, Func<bool> available = null)
    {
        var button = new DarkButton { Text = title, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Surface,
            ForeColor = Color.White, Padding = new Padding(6, 2, 6, 2), MinimumSize = new Size(0, 34) };
        button.FlatAppearance.BorderColor = Color.DimGray;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = Color.FromArgb(42, 42, 42);
        button.Primary = action == "Status" || action == "SaveList" || action == "AppUpdate" || action == "DownloadAll";
        Round(button, 16);
        parent.Controls.Add(button);
        refresh.Add(delegate { button.Enabled = !busy && (available == null || available()); });
        button.Click += async delegate
        {
            if (action == "AppUpdate" && Dirty()) { status.Text = "Сначала сохрани правки в редакторе списков."; return; }
            if (action == "ReadList" && !ConfirmDiscard()) return;
            await Run(action, target, choice == null ? "" : choice());
        };
    }

    private void Setting(Control parent, string title, string key, bool fallback)
    {
        var box = new CheckBox { Text = title, AutoSize = true, Checked = PanelHost.ReadSetting(key, fallback) };
        parent.Controls.Add(box);
        box.Click += delegate
        {
            try { PanelHost.SaveSetting(key, box.Checked); }
            catch (Exception error) { box.Checked = !box.Checked; status.Text = error.Message; }
        };
    }

    private void SetAutoStartCheck(bool value)
    {
        updatingAutoStart = true;
        autostart.Checked = value;
        updatingAutoStart = false;
    }

    private void RefreshButtons()
    {
        foreach (var callback in refresh) callback();
        autostart.Enabled = !busy;
        list.ReadOnly = busy || loadedList == null;
        zap.Enabled = proxy.Enabled = lists.Enabled = game.Enabled = ipset.Enabled = !busy;
    }

    private bool Installed(string target) { return Flag(state, target == "TG" ? "TGInstalled" : "ZapInstalled"); }
    private static string Selected(ComboBox box) { return Convert.ToString(box.SelectedItem); }
    private static ComboBox Choice(params string[] choices)
    {
        var box = new DarkChoice { DropDownStyle = ComboBoxStyle.DropDownList, Width = 215, BackColor = Surface, ForeColor = Color.White };
        box.Items.AddRange(choices);
        if (choices.Length > 0) box.SelectedIndex = 0;
        return box;
    }

    private sealed class DarkChoice : ComboBox
    {
        private const int PaintMessage = 0x000F;

        internal DarkChoice()
        {
            FlatStyle = FlatStyle.Flat;
            DrawMode = DrawMode.OwnerDrawFixed;
            ItemHeight = 23;
        }

        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if (message.Msg != PaintMessage || !IsHandleCreated) return;
            using (var graphics = Graphics.FromHwnd(Handle))
            using (var background = new SolidBrush(Surface))
            using (var border = new Pen(Color.FromArgb(68, 68, 68)))
            using (var arrow = new Pen(Enabled ? Color.White : Muted, 1.4F))
            {
                int x = ClientSize.Width - 15, y = ClientSize.Height / 2;
                graphics.FillRectangle(background, ClientRectangle);
                graphics.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
                graphics.DrawLines(arrow, new[] { new Point(x - 4, y - 2), new Point(x, y + 2), new Point(x + 4, y - 2) });
                TextRenderer.DrawText(graphics, Text, Font, new Rectangle(8, 0, Math.Max(0, x - 12), ClientSize.Height),
                    Enabled ? Color.White : Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                if (Focused) ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(ClientRectangle, -3, -3), Muted, Surface);
            }
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            using (var brush = new SolidBrush((e.State & DrawItemState.Selected) != 0
                ? Color.FromArgb(54, 54, 54) : Surface)) e.Graphics.FillRectangle(brush, e.Bounds);
            if (e.Index >= 0)
                TextRenderer.DrawText(e.Graphics, Convert.ToString(Items[e.Index]), Font, e.Bounds,
                    Enabled ? Color.White : Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            e.DrawFocusRectangle();
        }
    }

    private sealed class DarkButton : Button
    {
        private bool hover;
        internal bool Primary { get; set; }
        internal string Glyph { get; set; }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (ClientSize.Width == 0 || ClientSize.Height == 0) return;
            Color fill = Primary && Enabled ? Color.FromArgb(247, 247, 247) : BackColor;
            Color bottom = Primary && Enabled ? Color.FromArgb(220, 220, 220) :
                Enabled && hover ? Color.FromArgb(66, 66, 66) : Color.FromArgb(25, 25, 25);
            Color ink = !Enabled ? Color.FromArgb(172, 172, 172) : Primary ? Color.FromArgb(18, 18, 18) : Color.White;
            using (var gradient = new LinearGradientBrush(ClientRectangle, fill, bottom, 90F))
                e.Graphics.FillRectangle(gradient, ClientRectangle);
            var bounds = new Rectangle(Padding.Left, Padding.Top, Width - Padding.Horizontal, Height - Padding.Vertical);
            if (!String.IsNullOrEmpty(Glyph))
            {
                using (var glyphFont = new Font("Segoe MDL2 Assets", 10F))
                    TextRenderer.DrawText(e.Graphics, Glyph, glyphFont, new Rectangle(bounds.X, 0, 19, bounds.Height), ink,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                bounds.X += 23;
                bounds.Width -= 23;
            }
            var align = TextAlign == ContentAlignment.MiddleLeft ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter;
            TextRenderer.DrawText(e.Graphics, Text, Font, bounds, ink,
                align | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4), Muted, fill);
        }
    }

    private sealed class GlassPanel : FlowLayoutPanel
    {
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Rectangle bounds = ClientRectangle;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            using (var brush = new LinearGradientBrush(bounds, Color.FromArgb(30, 30, 30), Color.FromArgb(11, 11, 11), 42F))
                e.Graphics.FillRectangle(brush, bounds);
        }
    }
    private static TextBox Editor(bool readOnly)
    {
        return new DarkTextBox { Width = 670, Height = readOnly ? 165 : 340, Multiline = true,
            ReadOnly = readOnly, ScrollBars = readOnly ? ScrollBars.None : ScrollBars.Both,
            WordWrap = readOnly, MaxLength = 0, BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(10, 10, 10), ForeColor = Color.White };
    }

    private string ListRequest()
    {
        return Serializer().Serialize(new { Name = Value(loadedList, "Name"), Token = Value(loadedList, "Token"), Text = list.Text });
    }
    private bool Dirty() { return loadedList != null && Normalize(list.Text) != Normalize(Value(loadedList, "Text")); }
    private static string Normalize(string text) { return text.Replace("\r\n", "\n").Replace("\r", "\n"); }
    private bool ConfirmDiscard()
    {
        return !Dirty() || MessageBox.Show(this, "Отбросить несохранённые правки списка?", "Списки обхода",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    internal void RequestExit()
    {
        if (busy || !mayRun())
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            status.Text = "Дождись окончания операции.";
            return;
        }
        explicitExit = true;
        try { Close(); }
        finally { explicitExit = false; }
    }

    private void OnClosing(object sender, FormClosingEventArgs e)
    {
        if (e.CloseReason != CloseReason.UserClosing) return;
        if (!explicitExit && PanelHost.ReadSetting("MinimizeToTray", true)) { e.Cancel = true; Hide(); }
        else if (busy || !mayRun())
        {
            status.Text = "Дождись окончания операции.";
            e.Cancel = true;
        }
        else if (!ConfirmDiscard()) e.Cancel = true;
    }

    private async Task Run(string action, string target, string choice)
    {
        if (busy || IsDisposed) return;
        if (!mayRun()) { status.Text = "Дождись завершения операции TG WS Proxy."; return; }
        busy = true;
        winners.Clear();
        status.Text = "Выполняю операцию…";
        progress.Visible = true;
        progress.Style = ProgressBarStyle.Marquee;
        RefreshButtons();
        var reporter = new Progress<Dictionary<string, object>>(result =>
        {
            try { if (!IsDisposed) Accept(result); }
            catch (Exception error) { Log(error.Message); }
        });
        try { await Task.Run(() => new NativeEngine(reporter).Run(action, target, choice)); }
        catch (Exception error) { Log(error.Message); }
        finally
        {
            busy = false;
            progress.Visible = false;
            if (status.Text == "Выполняю операцию…") status.Text = "";
            RefreshButtons();
        }
        if (update != null)
        {
            try { InstallUpdate(update); }
            catch (Exception error) { Log(error.Message); }
            finally { update = null; }
        }
    }

    internal async void RefreshProcessState()
    {
        await Run("Init", "", "");
    }

    private void Accept(Dictionary<string, object> result)
    {
        switch (Value(result, "Type"))
        {
            case "State":
                state = result;
                Utility("Zapret", "Zap");
                Utility("TG", "TG");
                Fill(zap, "Strategies", "Zapret", "general.bat");
                Fill(proxy, "Profiles", "TG", "Standard");
                game.SelectedIndex = Math.Max(0, Array.IndexOf(gameModes, Value(state, "GameFilter")));
                ipset.SelectedIndex = Math.Max(0, Array.IndexOf(ipsetModes, Value(state, "IPSet")));
                break;
            case "Log": case "Error": Log(Value(result, "Message")); break;
            case "Progress":
                progress.Style = ProgressBarStyle.Continuous;
                progress.Maximum = Math.Max(1, Number(result, "Total"));
                progress.Value = Math.Min(progress.Maximum, Math.Max(0, Number(result, "Current")));
                status.Text = Value(result, "Message");
                break;
            case "Winner": winners[Value(result, "Name")] = Value(result, "Choice"); break;
            case "List":
                loadedList = result;
                list.Text = Value(result, "Text");
                status.Text = Value(result, "Name");
                break;
            case "Latency":
                var name = Value(result, "Name");
                var latency = name + " - HTTPS " + Metric(result, "HTTPS");
                if (Flag(result, "Full")) latency += " · ICMP " + Metric(result, "ICMP");
                if (name == "Telegram") latency += " · MTProto " + (Flag(result, "ProxyRunning") ? Metric(result, "MTProto") : "прокси выключен");
                cards[name].Text = latency.Substring(name.Length + 3);
                if (Flag(result, "Full")) Log(latency);
                break;
            case "Folder":
                using (Process.Start("explorer.exe", "\"" + Value(result, "Path") + "\"")) { }
                break;
            case "Connect":
                using (Process.Start(Value(result, "Uri"))) { }
                break;
            case "AutoStart":
                autoStartEnabled = Flag(result, "Enabled");
                SetAutoStartCheck(autoStartEnabled);
                status.Text = autoStartEnabled ? "Автозапуск включён." : "Автозапуск выключен.";
                break;
            case "AppUpdate": update = result; break;
        }
    }

    private void InstallUpdate(Dictionary<string, object> result)
    {
        NativeSetup.QueueUpdate(Value(result, "Path"), Value(result, "Hash"));
        RequestExit();
    }

    private void Utility(string key, string prefix)
    {
        var message = Flag(state, "External" + prefix) ? "Запущен вне zapretgui" :
            Flag(state, prefix + "Running") ? "Запущен" : Installed(key) ? "Остановлен" : "Не установлен";
        string version = Value(state, key + "Version");
        string profile = Value(state, key);
        cards[key].Text = message +
            (version.Length == 0 ? "" : "  " + version) +
            (profile.Length == 0 ? "" : "  " + profile);
    }

    private void Fill(ComboBox box, string entries, string key, string fallback)
    {
        string previous = Selected(box);
        string winner;
        winners.TryGetValue(key, out winner);
        object raw;
        box.Items.Clear();
        if (state.TryGetValue(entries, out raw))
        {
            var items = raw as System.Collections.IEnumerable;
            if (items != null)
                foreach (object item in items) if (item != null) box.Items.Add(item);
        }
        foreach (string candidate in new[] { winner, previous, Value(state, key), fallback })
        {
            if (!string.IsNullOrEmpty(candidate) && box.Items.Contains(candidate)) { box.SelectedItem = candidate; return; }
        }
        if (box.Items.Count != 0) box.SelectedIndex = 0;
    }

    private void Log(string message)
    {
        status.Text = message;
        if (activity.TextLength > 26000) activity.Text = activity.Text.Substring(activity.TextLength - 16000);
        activity.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
    }
    private static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }; }
    private static string Value(Dictionary<string, object> row, string key)
    {
        object value;
        return row != null && row.TryGetValue(key, out value) ? Convert.ToString(value) : "";
    }
    private static bool Flag(Dictionary<string, object> row, string key) { return Value(row, key) == "True"; }
    private static int Number(Dictionary<string, object> row, string key)
    {
        int result;
        return int.TryParse(Value(row, key), out result) ? result : -1;
    }
    private static string Metric(Dictionary<string, object> row, string key)
    {
        int value = Number(row, key);
        return value < 0 ? "нет ответа" : value + " мс";
    }
}
