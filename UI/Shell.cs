using System;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Threading;
using WPath = System.Windows.Shapes.Path;

internal sealed partial class Shell
{
    internal readonly Window Window;
    private readonly Session session;
    private readonly TrayHost host;
    private readonly string[] names =
    {
        "Статус",
        "Установка и тесты",
        "Настройка обхода",
        "Настройки"
    };
    private readonly string[] icons =
    {
        "M1,11 L5,11 8,3 12,17 15,9 20,9",
        "M10,2 L10,13 M5,8 L10,13 15,8 M3,14 L3,18 17,18 17,14",
        "M2,5 L7,5 M12,5 L19,5 M2,14 L12,14 M16,14 L19,14 M7,2 L11,2 11,8 7,8 Z M12,11 L16,11 16,17 12,17 Z",
        "M10,2 L13,3 14,5 17,6 18,10 17,13 15,14 14,17 10,18 7,17 6,15 3,14 2,10 3,7 5,6 6,3 Z M13,10 A3,3 0 1 1 7,10 A3,3 0 1 1 13,10"
    };
    private readonly StackPanel[] pages = new StackPanel[4];
    private readonly Button[] nav = new Button[4];
    private readonly TextBox[] logs = new TextBox[2];
    private readonly List<KeyValuePair<Control, Func<bool>>> commands = new List<KeyValuePair<Control, Func<bool>>>();
    private readonly Dictionary<Component, ComboBox> choices = new Dictionary<Component, ComboBox>();
    private readonly Dictionary<Component, TextBlock> componentState = new Dictionary<Component, TextBlock>();
    private readonly Dictionary<Component, TextBlock> componentDetail = new Dictionary<Component, TextBlock>();
    private readonly Dictionary<string, TextBlock> readings = new Dictionary<string, TextBlock>(), details = new Dictionary<string, TextBlock>();
    private readonly Brush muted = BrushOf("#B3B3B3");
    private bool sync, ending, closingToTray;
    private ListEdit list;
    private ComboBox listChoice, gameMode, ipMode, fake, fileType, conflict;
    private TextBox editor, tcp, udp;
    private TextBlock listHint, fileState, serviceState, proxyDetail;
    private CheckBox auto, background, tray, checkZap;
    private bool filtersDirty;
    private bool listDirty;
    private T Find<T>(string id)
        where T : FrameworkElement
    {
        return (T)Window.FindName(id);
    }

    private static Brush BrushOf(string color)
    {
        var b = (Brush)new BrushConverter().ConvertFromString(color);
        b.Freeze();
        return b;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int key, ref int value, int bytes);
    internal Shell(Session state, TrayHost host)
    {
        this.session = state;
        this.host = host;
        var asm = Assembly.GetExecutingAssembly();
        using (var xaml = asm.GetManifestResourceStream("Window.xaml"))
            Window = (Window)XamlReader.Load(xaml);
        using (var stream = asm.GetManifestResourceStream("zapretgui.ico"))
        {
            var icon = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var img = icon.Frames.OrderBy(f => Math.Abs(f.PixelWidth - 40)).First();
            img.Freeze();
            Window.Icon = img;
            Find<Image>("BrandIcon").Source = img;
        }

        Window.SourceInitialized += delegate
        {
            var handle = new WindowInteropHelper(Window).Handle;
            int on = 1, black = 0x080808;
            DwmSetWindowAttribute(handle, 20, ref on, 4);
            DwmSetWindowAttribute(handle, 35, ref black, 4);
            ZapretTaskbarIdentity.SetWindow(handle);
        };
        Window.Closing += (s, e) =>
        {
            if (ending)
                return;
            if (Disk.Option("MinimizeToTray", true))
            {
                if (Dirty || filtersDirty)
                {
                    e.Cancel = true;
                    Window.Hide();
                    return;
                }

                closingToTray = true;
                return;
            }

            e.Cancel = true;
            Window.Dispatcher.BeginInvoke((Action)host.Quit);
        };
        Window.StateChanged += delegate
        {
            if (Window.WindowState == WindowState.Minimized && Disk.Option("MinimizeToTray", true))
                Window.Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (Window.IsVisible && Window.WindowState == WindowState.Minimized)
                        Window.Close();
                }));
        };
        Window.Closed += delegate
        {
            session.Changed -= UpdateActivity;
            session.Finished -= Refresh;
            if (closingToTray)
                Window.Content = null;
        };
        for (int i = 0; i < names.Length; i++)
        {
            int index = i;
            var button = new Button
            {
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 1, 0, 1),
                Padding = new Thickness(10, 9, 4, 9),
                MinHeight = 41,
                BorderBrush = Brushes.Transparent
            };
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal
            };
            row.Children.Add(new WPath { Data = Geometry.Parse(icons[i]), Stroke = Brushes.WhiteSmoke, StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = 19, Height = 19, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 10, 0) });
            row.Children.Add(new TextBlock { Text = names[i], VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
            button.Content = row;
            button.Click += delegate
            {
                Switch(index);
            };
            Find<StackPanel>("Navigation").Children.Add(button);
            nav[i] = button;
        }

        Find<Button>("CancelJob").Click += delegate
        {
            session.Cancel();
        };
        session.Changed += UpdateActivity;
        session.Finished += Refresh;
        Switch(session.Page);
    }

    internal void CloseForExit()
    {
        ending = true;
        Window.Close();
    }

    private bool Dirty
    {
        get
        {
            return list != null && listDirty;
        }
    }

    internal bool CanDiscard()
    {
        return (!Dirty && !filtersDirty) || Confirm("Закрыть панель и отбросить несохранённые изменения?");
    }

    private bool Confirm(string text)
    {
        return MessageBox.Show(Window, text, "zapretgui", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private void Error(Exception ex)
    {
        session.Record(ex.GetBaseException().Message);
        MessageBox.Show(Window, ex.GetBaseException().Message, "zapretgui", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Switch(int page)
    {
        if (pages[page] == null)
        {
            var p = new StackPanel();
            pages[page] = p;
            if (page == 0)
                Status(p);
            else if (page == 1)
                Installation(p);
            else if (page == 2)
                Bypass(p);
            else
                Settings(p);
        }

        session.Page = page;
        Find<TextBlock>("PageTitle").Text = names[page];
        Find<ContentControl>("PageContent").Content = pages[page];
        Find<ScrollViewer>("PageScroll").ScrollToTop();
        for (int i = 0; i < nav.Length; i++)
        {
            nav[i].Background = i == page ? BrushOf("#373737") : Brushes.Transparent;
            nav[i].BorderBrush = i == page ? BrushOf("#5A5A5A") : Brushes.Transparent;
        }

        Refresh();
    }

    private void Refresh()
    {
        sync = true;
        try
        {
            foreach (var c in Components.All)
            {
                bool installed = Components.Installed(c), running = Processes.Running(Paths.Binary(c));
                if (componentState.ContainsKey(c))
                {
                    componentState[c].Text = running ? "Работает" : installed ? "Остановлен" : "Не установлен";
                    componentDetail[c].Text = installed ? string.Join(" · ", new[] { Disk.State(c + "Version"), Components.Selected(c) }.Where(s => s.Length > 0)) : "Установка доступна на второй вкладке";
                }

                if (choices.ContainsKey(c))
                {
                    string preferred = null;
                    session.Winners.TryGetValue(c, out preferred);
                    Fill(choices[c], c == Component.Zapret ? Strategies.Names() : ProxySettings.Profiles, preferred ?? (string)choices[c].SelectedItem ?? Components.Selected(c));
                }
            }

            if (gameMode != null && !filtersDirty)
            {
                var g = GamePorts.Load();
                gameMode.SelectedIndex = Array.IndexOf(GamePorts.Modes, g.Mode);
                tcp.Text = g.Tcp;
                udp.Text = g.Udp;
                ipMode.SelectedIndex = Array.IndexOf(IpModes, AddressLists.Mode(AddressLists.Active));
            }

            if (fake != null)
                Fill(fake, Maintenance.Samples(), (string)fake.SelectedItem);
            if (conflict != null)
                Fill(conflict, ServiceTools.ManageableConflicts(), (string)conflict.SelectedItem);
            if (serviceState != null)
                serviceState.Text = "Служба zapret · " + ZapService.Status("zapret");
            if (auto != null)
            {
                auto.IsChecked = session.AutoStart;
                background.IsChecked = Disk.Option("StartInTray", true);
                tray.IsChecked = Disk.Option("MinimizeToTray", true);
                checkZap.IsChecked = Maintenance.CheckUpdates;
            }

            if (proxyDetail != null)
                proxyDetail.Text = File.Exists(Paths.Config) ? "Параметры локального подключения и профиль прокси." : "Конфиг появится после установки прокси.";
        }
        catch (Exception ex)
        {
            session.Record(ex.GetBaseException().Message);
        }
        finally
        {
            sync = false;
        }

        UpdateActivity();
    }

    private static void Fill(ComboBox b, string[] items, string preferred)
    {
        var existing = b.ItemsSource as string[];
        if (existing == null || !existing.SequenceEqual(items))
            b.ItemsSource = items;
        b.SelectedIndex = items.Length == 0 ? -1 : Math.Max(0, Array.IndexOf(items, preferred));
    }

    private void UpdateActivity()
    {
        foreach (var pair in commands)
            pair.Key.IsEnabled = !session.Busy && pair.Value();
        var activity = Find<Border>("ActivityBar");
        bool error = !session.Busy && session.LastError.Length > 0;
        activity.Visibility = session.Busy || error ? Visibility.Visible : Visibility.Collapsed;
        Find<TextBlock>("ActivityText").Text = error ? session.LastError : session.Activity;
        Find<Button>("CancelJob").Visibility = session.CanCancel ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in session.Latency)
            if (readings.ContainsKey(item.Key))
            {
                readings[item.Key].Text = item.Value.Value;
                details[item.Key].Text = item.Value.Details;
            }

        string text = string.Join(Environment.NewLine, session.Journal);
        foreach (var box in logs)
        {
            if (box == null || box.Text == text)
                continue;
            bool follow = box.VerticalOffset + box.ViewportHeight >= box.ExtentHeight - 3 || box.Text.Length == 0;
            box.Text = text;
            if (follow)
                box.ScrollToEnd();
        }
    }

    private void Enable(Control c, Func<bool> allowed = null)
    {
        commands.Add(new KeyValuePair<Control, Func<bool>>(c, allowed ?? (() => true)));
    }

    private static void Row(Panel parent, UIElement element)
    {
        parent.Children.Add(element);
    }

    private TextBlock Label(string text, double size = 14, bool bold = false, bool secondary = false)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = secondary ? muted : Brushes.WhiteSmoke,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private StackPanel Card(Panel parent, string title = null)
    {
        var content = new StackPanel();
        var box = new Border
        {
            Background = (Brush)Window.FindResource("Glass"),
            BorderBrush = BrushOf("#323232"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(17),
            Padding = new Thickness(18, 15, 18, 13),
            Margin = new Thickness(0, 0, 0, 12),
            Child = content
        };
        Row(parent, box);
        if (title != null)
        {
            var label = Label(title, 18, true);
            label.Margin = new Thickness(0, 0, 0, 11);
            Row(content, label);
        }

        return content;
    }

    private static WrapPanel Actions(Panel parent)
    {
        var row = new WrapPanel
        {
            Margin = new Thickness(0, 8, 0, 0)
        };
        Row(parent, row);
        return row;
    }

    private Button Button(Panel parent, string text, Func<Task> action, bool primary = false, Func<bool> allowed = null)
    {
        var b = new Button
        {
            Content = text
        };
        if (primary)
            b.Style = (Style)Window.FindResource("Primary");
        b.Click += async delegate
        {
            try
            {
                await action();
            }
            catch (Exception e)
            {
                Error(e);
            }
        };
        Row(parent, b);
        Enable(b, allowed);
        return b;
    }

    private Button Job(Panel parent, string title, Action work, bool primary = false, Func<bool> allowed = null)
    {
        return Button(parent, title, () => session.RunSimple(title, work), primary, allowed);
    }

    private Button Click(Panel parent, string title, Action work, bool primary = false, Func<bool> allowed = null)
    {
        return Button(parent, title, () =>
        {
            work();
            return Task.FromResult(0);
        }, primary, allowed);
    }

    private static ComboBox Pick(Panel row, double width, string[] items = null)
    {
        var b = new ComboBox
        {
            Width = width,
            ItemsSource = items
        };
        if (items != null && items.Length > 0)
            b.SelectedIndex = 0;
        Row(row, b);
        return b;
    }

    private void LogBox(Panel page, int index, string title)
    {
        var card = Card(page, title);
        var box = new TextBox
        {
            Height = index == 0 ? 135 : 180,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            IsReadOnly = true,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Row(card, box);
        logs[index] = box;
    }

    private static void OpenFolder(string directory)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("Папка появится после установки компонента.");
        using (Processes.Launch(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), ZapService.Quote(directory), Environment.SystemDirectory))
        {
        }
    }
}
