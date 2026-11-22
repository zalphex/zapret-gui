using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

internal sealed partial class Shell
{
    private bool Review(string title, string message, string accept, string reject,
        string text = null, TimeSpan? timeout = null)
    {
        var dialog = new Window
        {
            Owner = Window, Title = title, Icon = Window.Icon, Width = 630,
            Height = text == null ? 280 : 510, MinWidth = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.Black, Foreground = Brushes.White,
            Resources = Window.Resources, ShowInTaskbar = false
        };
        var layout = new DockPanel { Margin = new Thickness(20) };
        dialog.Content = layout;
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(actions, Dock.Bottom);
        layout.Children.Add(actions);
        var yes = new Button { Content = accept, Style = (Style)Window.FindResource("Primary") };
        var no = new Button { Content = reject, IsCancel = true };
        yes.Click += delegate { dialog.DialogResult = true; };
        actions.Children.Add(yes);
        actions.Children.Add(no);
        var description = Label(message, 14);
        description.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(description, Dock.Top);
        layout.Children.Add(description);

        if (text != null)
            layout.Children.Add(new TextBox
            {
                Text = text, IsReadOnly = true, Margin = new Thickness(0, 12, 0, 0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.Wrap
            });

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        if (timeout.HasValue)
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            timer.Tick += delegate
            {
                var remaining = timeout.Value - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    dialog.DialogResult = false;
                else
                    description.Text = message + "\n\nОсталось " + (int)Math.Ceiling(remaining.TotalSeconds) + " сек.";
            };
            timer.Start();
        }
        try { return dialog.ShowDialog() == true; }
        finally { timer.Stop(); }
    }

    internal bool ConfirmStrategy(string name)
    {
        return Review("Проверка в приложениях",
            "Временно включён конфиг " + name + ". Запусти видео в YouTube и зайди в голосовой канал Discord. Сохрани конфиг, если всё работает. Через 2 минуты панель вернёт прежний конфиг.",
            "Сохранить конфиг", "Вернуть прежний", timeout: TimeSpan.FromMinutes(2));
    }
}
