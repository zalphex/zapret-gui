using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Collections.Generic;

internal sealed partial class Shell
{
    private static readonly string[] IpModes =
    {
        "loaded",
        "none",
        "any"
    };
    private static readonly string[] IpLabels =
    {
        "По списку",
        "Выключен",
        "Все IP"
    };
    private void Status(StackPanel page)
    {
        var components = Card(page, "Компоненты");
        foreach (var c in Components.All)
        {
            var row = new Grid
            {
                Margin = new Thickness(0, 6, 0, 7)
            };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel();
            Row(left, Label(Components.Title(c), 16, true));
            componentDetail[c] = Label("", 12, false, true);
            Row(left, componentDetail[c]);
            Row(row, left);
            componentState[c] = Label("", 13, true);
            Grid.SetColumn(componentState[c], 1);
            Row(row, componentState[c]);
            Row(components, row);
        }

        var heading = new DockPanel
        {
            Margin = new Thickness(1, 3, 0, 10)
        };
        var refresh = new Button
        {
            Content = "Обновить статус",
            Margin = new Thickness(4, 0, 0, 0)
        };
        refresh.Click += async delegate
        {
            session.LastError = "";
            await session.Run("Проверка связи", t => session.MeasureAll());
        };
        Enable(refresh);
        DockPanel.SetDock(refresh, Dock.Right);
        Row(heading, refresh);
        Row(heading, Label("Связь с сервисами", 18, true));
        Row(page, heading);
        var grid = new UniformGrid
        {
            Columns = 3
        };
        Row(page, grid);
        foreach (var name in Checks.Services)
        {
            var stack = Card(grid);
            var border = (Border)stack.Parent;
            border.Margin = new Thickness(0, 0, 5, 12);
            border.Padding = new Thickness(13, 13, 10, 12);
            Row(stack, Label(name, 14, true));
            var value = Label("Нет замера", 21, true);
            value.Margin = new Thickness(0, 8, 0, 6);
            Row(stack, value);
            readings[name] = value;
            details[name] = Label("", 11.5, false, true);
            Row(stack, details[name]);
        }

        LogBox(page, 0, "Журнал");
    }

    private void Installation(StackPanel page)
    {
        var packages = Card(page, "Утилиты Flowseal");
        var buttons = Actions(packages);
        Job(buttons, "Скачать обе утилиты", () =>
        {
            var errors = new List<string>();
            foreach (var c in Components.All)
                try
                {
                    Downloads.Install(c, session.Log);
                }
                catch (Exception e)
                {
                    errors.Add(Components.Title(c) + " · " + e.Message);
                }

            if (errors.Count > 0)
                throw new IOException(string.Join("\n", errors));
        }, true);
        Job(buttons, "Проверить обновления", () =>
        {
            var errors = new List<string>();
            foreach (var c in Components.All)
                try
                {
                    string v = Downloads.Latest(c).Version, local = Disk.State(c + "Version", "нет");
                    session.Log(Components.Title(c) + " · установлено: " + local + " · доступно: " + v);
                }
                catch (Exception e)
                {
                    errors.Add(Components.Title(c) + " · " + e.Message);
                }

            if (errors.Count > 0)
                throw new IOException(string.Join("\n", errors));
        });
        foreach (var tool in Components.All)
        {
            Component c = tool;
            var card = Card(page, Components.Title(c));
            var row = Actions(card);
            choices[c] = Pick(row, 210);
            var box = choices[c];
            Func<bool> installed = () => Components.Installed(c);
            Enable(box, installed);
            box.SelectionChanged += delegate
            {
                if (!sync)
                    session.Winners.Remove(c);
            };
            Button(row, "Применить", () =>
            {
                var name = (string)box.SelectedItem;
                return session.RunSimple("Применение конфига", () => Components.Apply(c, name));
            }, true, () => installed() && box.SelectedItem != null);
            Click(row, "Папка", () => OpenFolder(c == Component.TG ? Paths.TgData : Paths.Zap), false, installed);
            Job(row, "Скачать", () => Downloads.Install(c, session.Log));
            var tests = Actions(card);
            Button(tests, "Проверить конфиги", () => session.Test(c, false), false, installed);
            Button(tests, "Автовыбор и запуск", () => session.Test(c, true, ConfirmStrategy), false, installed);
            Job(tests, "Перезапустить", () =>
            {
                Components.Stop(c);
                Components.Start(c, Components.Selected(c));
            }, false, installed);
            Job(tests, "Остановить", () => Components.Stop(c), false, installed);
            if (c == Component.TG)
                Click(tests, "Подключить Telegram", () => TrayHost.Launch(ProxySettings.Link()), false, () => File.Exists(Paths.Config));
        }

        var pings = Actions(Card(page, "Пинг сервисов"));
        for (int i = 0; i < Checks.Services.Length; i++)
        {
            int index = i;
            Button(pings, "Пинг " + Checks.Services[i], () =>
            {
                session.LastError = "";
                return session.Run("Проверка " + Checks.Services[index], t => session.Measure(index));
            });
        }

        LogBox(page, 1, "Журнал проверки");
        var maintenance = Card(page, "Обслуживание zapret");
        serviceState = Label("", 13, false, true);
        Row(maintenance, serviceState);
        var tools = Actions(maintenance);
        Job(tools, "Диагностика", () => Maintenance.Diagnose(session.Log));
        Button(tools, "Установить службу", async () =>
        {
            var chosen = (string)choices[Component.Zapret].SelectedItem;
            if (Confirm("Установить службу Windows для стратегии " + chosen + " и запускать её до входа в систему?"))
                await session.RunSimple("Установка службы", () => Maintenance.InstallService(chosen));
        }, false, () => Components.Installed(Component.Zapret) && choices[Component.Zapret].SelectedItem != null);
        Button(tools, "Удалить службу", async () =>
        {
            if (Confirm("Остановить и удалить службу zapret этой установки?"))
                await session.RunSimple("Удаление службы", ZapService.Remove);
        }, false, () => ZapService.Ours(ZapService.Image));
        Button(tools, "Обновить hosts", async () =>
        {
            string source = null;
            session.LastError = "";
            if (!await session.Run("Загрузка списка hosts", t => source = Maintenance.HostsSource()))
                return;
            if (Review("Обновление hosts", "Проверь блок Flowseal перед записью. Панель сохранит резервную копию.", "Применить", "Отмена", source))
                await session.RunSimple("Обновление hosts", () => Maintenance.Hosts(Disk.Token(Disk.Utf8.GetBytes(source))));
        });
        Button(tools, "Восстановить hosts", async () =>
        {
            if (Confirm("Убрать из hosts блок Flowseal, добавленный zapretgui? Остальные записи будут сохранены."))
                await session.RunSimple("Восстановление hosts", Maintenance.RestoreHosts);
        }, false, Maintenance.CanRestoreHosts);
        Button(tools, "Включить TCP timestamps", async () =>
        {
            if (Confirm("Включить TCP timestamps в сетевых настройках Windows?"))
                await session.RunSimple("TCP timestamps", ServiceTools.TcpTimestamps);
        });
        Button(tools, "Очистить кэш Discord", async () =>
        {
            if (Confirm("Очистить Cache, Code Cache и GPUCache установленных версий Discord? Сначала закрой Discord."))
                await session.RunSimple("Очистка кэша Discord", () => ServiceTools.ClearDiscordCache(session.Log));
        });
        var recovery = Actions(maintenance);
        conflict = Pick(recovery, 170);
        Enable(conflict);
        Button(recovery, "Остановить выбранную службу", async () =>
        {
            string service = (string)conflict.SelectedItem;
            if (service != null && Confirm("Временно остановить службу " + service + "? Её можно снова запустить из панели."))
                await session.RunSimple("Остановка службы " + service, () => ServiceTools.SetConflictRunning(service, false));
        }, false, () => conflict.SelectedItem != null);
        Button(recovery, "Запустить выбранную службу", () =>
        {
            string service = (string)conflict.SelectedItem;
            return session.RunSimple("Запуск службы " + service, () => ServiceTools.SetConflictRunning(service, true));
        }, false, () => conflict.SelectedItem != null);
        Row(maintenance, Label("WinDivert отображается в диагностике. Его могут использовать другие приложения.", 12, false, true));
    }

    private void Bypass(StackPanel page)
    {
        var filters = Card(page, "Игровой фильтр и IPSet");
        var grouping = new WrapPanel();
        Row(filters, grouping);
        gameMode = FieldChoice(grouping, "Игровой фильтр", 180, new[] { "Выключен", "Только TCP", "Только UDP", "TCP + UDP" });
        ipMode = FieldChoice(grouping, "IPSet", 170, IpLabels);
        var portRow = new WrapPanel();
        Row(filters, portRow);
        tcp = Port(portRow, "Порты TCP");
        udp = Port(portRow, "Порты UDP");
        var filterBtns = Actions(filters);
        Button(filterBtns, "Применить фильтры", async () =>
        {
            var g = new GamePorts
            {
                Mode = GamePorts.Modes[gameMode.SelectedIndex],
                Tcp = tcp.Text.Trim(),
                Udp = udp.Text.Trim()
            };
            string mode = IpModes[ipMode.SelectedIndex];
            session.LastError = "";
            if (Dirty && !Confirm("Сменить режим и перечитать список? Несохранённые правки будут потеряны."))
                return;
            if (await session.Run("Применение фильтров", t => AddressLists.Filters(g, mode)))
            {
                filtersDirty = false;
                session.Record("Фильтры применены.");
                Refresh();
                if (list != null)
                    await ReadList(list.Name);
            }
        }, true, () => Components.Installed(Component.Zapret));
        gameMode.SelectionChanged += delegate
        {
            if (!sync)
                filtersDirty = true;
        };
        ipMode.SelectionChanged += delegate
        {
            if (!sync)
                filtersDirty = true;
        };
        tcp.TextChanged += delegate
        {
            if (!sync)
                filtersDirty = true;
        };
        udp.TextChanged += delegate
        {
            if (!sync)
                filtersDirty = true;
        };
        var lists = Card(page, "Списки обхода и исключений");
        var row = Actions(lists);
        listChoice = Pick(row, 210, AddressLists.Names);
        Button(row, "Открыть", async () =>
        {
            if (Dirty && !Confirm("Перечитать файл и отбросить несохранённые правки?"))
                return;
            await ReadList((string)listChoice.SelectedItem);
        }, false, () => Components.Installed(Component.Zapret));
        Click(row, "Папка", () => OpenFolder(Paths.Lists), false, () => Directory.Exists(Paths.Lists));
        Button(row, "Обновить IPSet", async () =>
        {
            if (Dirty && list.Name == AddressLists.Names[2] && !Confirm("Обновить IPSet и отбросить несохранённые правки этого списка?"))
                return;
            session.LastError = "";
            if (await session.Run("Обновление IPSet", t => AddressLists.Update()))
            {
                session.Record("IPSet обновлён.");
                if (list != null && list.Name == AddressLists.Names[2])
                    await ReadList(list.Name);
            }
        }, false, () => Components.Installed(Component.Zapret));
        listHint = Label(AddressLists.Hints[0], 13, false, true);
        listHint.Margin = new Thickness(0, 1, 0, 8);
        Row(lists, listHint);
        editor = new TextBox
        {
            Height = 190,
            AcceptsReturn = true,
            AcceptsTab = false,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsReadOnly = true
        };
        Row(lists, editor);
        var saveRow = Actions(lists);
        var saveButton = Button(saveRow, "Сохранить список", async () =>
        {
            var edit = list;
            string next = editor.Text;
            session.LastError = "";
            if (await session.Run("Сохранение списка", t => AddressLists.Save(edit, next)))
            {
                await ReadList(edit.Name);
                session.Record("Список сохранён.");
            }
        }, true, () => Dirty);
        fileState = Label("Выбери файл и нажми «Открыть».", 12, false, true);
        Row(saveRow, fileState);
        editor.TextChanged += delegate
        {
            if (!sync)
            {
                listDirty = list != null;
                SetFileState();
                saveButton.IsEnabled = !session.Busy && Dirty;
            }
        };
        listChoice.SelectionChanged += delegate
        {
            if (sync || listChoice.SelectedIndex < 0)
                return;
            var name = (string)listChoice.SelectedItem;
            if (Dirty && !Confirm("Сменить файл и отбросить несохранённые правки?"))
            {
                sync = true;
                listChoice.SelectedItem = list.Name;
                sync = false;
                return;
            }

            listDirty = false;
            list = null;
            editor.IsReadOnly = true;
            editor.Text = "";
            listHint.Text = AddressLists.Hints[listChoice.SelectedIndex];
            SetFileState();
        };
        Enable(listChoice);
        Enable(editor, () => list != null);
        Enable(gameMode);
        Enable(ipMode);
        Enable(tcp);
        Enable(udp);
        var packets = Card(page, "Шаблоны UDP");
        var samples = Actions(packets);
        fileType = Pick(samples, 135, new[] { "Discord UDP", "Игровой UDP" });
        fake = Pick(samples, 225);
        Button(samples, "Применить шаблон", () =>
        {
            bool discord = fileType.SelectedIndex == 0;
            string name = (string)fake.SelectedItem;
            return session.RunSimple("Замена шаблона UDP", () => Maintenance.ReplaceSample(name, discord));
        }, false, () => fake.SelectedItem != null);
    }

    private ComboBox FieldChoice(Panel row, string name, int width, string[] options)
    {
        var field = new StackPanel();
        Row(row, field);
        Row(field, Label(name, 12, false, true));
        return Pick(field, width, options);
    }

    private TextBox Port(Panel row, string title)
    {
        var col = new StackPanel
        {
            Width = 180,
            Margin = new Thickness(0, 0, 8, 0)
        };
        Row(row, col);
        Row(col, Label(title, 12, false, true));
        var b = new TextBox
        {
            MinHeight = 35,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        Row(col, b);
        return b;
    }

    private void SetFileState()
    {
        if (fileState != null)
            fileState.Text = list == null ? "Выбери файл и нажми «Открыть»." : Dirty ? "Есть несохранённые изменения" : list.Inactive ? "Редактируется список для режима «По списку»." : list.Name;
    }

    private async Task ReadList(string name)
    {
        ListEdit loaded = null;
        session.LastError = "";
        if (await session.Run("Чтение списка", t => loaded = AddressLists.Read(name)))
        {
            sync = true;
            try
            {
                listDirty = false;
                list = loaded;
                listChoice.SelectedItem = name;
                editor.IsReadOnly = false;
                editor.Text = list.Text;
                listHint.Text = AddressLists.Hints[Array.IndexOf(AddressLists.Names, name)];
            }
            finally
            {
                sync = false;
            }

            SetFileState();
            UpdateActivity();
        }
    }

    private void Settings(StackPanel page)
    {
        var card = Card(page, "Запуск и трей");
        auto = Toggle(card, "Автозапуск при входе в Windows", async value =>
        {
            await session.RunSimple("Автозапуск", () =>
            {
                NativeSetup.SetAutoStart(value);
                session.AutoStart = NativeSetup.AutoStartEnabled();
            });
            Refresh();
        });
        background = Toggle(card, "Запускать в фоне при входе", value =>
        {
            Disk.OptionSet("StartInTray", value);
            return Task.FromResult(0);
        });
        tray = Toggle(card, "Сворачивать и закрывать окно в трей", value =>
        {
            Disk.OptionSet("MinimizeToTray", value);
            return Task.FromResult(0);
        });
        var update = Card(page, "Обновления");
        Row(update, Label("zapretgui " + Paths.Version + " · Windows x64", 15, true));
        var updateNote = Label("", 13, false, true);
        var bar = Actions(update);
        Button(bar, "Обновить zapretgui", async () =>
        {
            UpdateFile file = null;
            session.LastError = "";
            updateNote.Text = "Проверка версии на GitHub…";
            if (await session.Run("Поиск обновления приложения", t => file = Downloads.App()))
            {
                if (file == null)
                {
                    updateNote.Text = "Установлена актуальная версия " + Paths.Version + ".";
                    session.Record(updateNote.Text);
                }
                else
                {
                    updateNote.Text = "Установка версии " + file.Version + "…";
                    host.FinishUpdate(file);
                }
            }
            else
                updateNote.Text = session.LastError;
        }, true);
        Row(update, updateNote);
        checkZap = Toggle(update, "Проверять обновления zapret при запуске", value =>
        {
            Disk.OptionSet("CheckZapretUpdates", value);
            return Task.FromResult(0);
        }, () => Components.Installed(Component.Zapret));
        var proxy = Card(page, "TG WS Proxy");
        proxyDetail = Label("", 13, false, true);
        Row(proxy, proxyDetail);
        var p = Actions(proxy);
        Click(p, "Настройки прокси", ProxyEditor, false, () => File.Exists(Paths.Config));
        Click(p, "Скопировать ссылку", () => Clipboard.SetText(ProxySettings.Link()), false, () => File.Exists(Paths.Config));
    }

    private CheckBox Toggle(Panel card, string title, Func<bool, Task> callback, Func<bool> allowed = null)
    {
        var c = new CheckBox
        {
            Content = title,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        Row(card, c);
        Enable(c, allowed);
        c.Click += async delegate
        {
            if (sync)
                return;
            try
            {
                await callback(c.IsChecked == true);
            }
            catch (Exception ex)
            {
                Error(ex);
                Refresh();
            }
        };
        return c;
    }

    internal void ProxyEditor()
    {
        if (session.Busy)
            return;
        try
        {
            ProxySettings.Ensure();
            var initial = File.ReadAllBytes(Paths.Config);
            var dlg = new Window
            {
                Title = "Настройки TG WS Proxy",
                Owner = Window,
                Icon = Window.Icon,
                Width = 600,
                Height = 450,
                MinWidth = 400,
                MinHeight = 260,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Brushes.Black,
                Foreground = Brushes.White,
                Resources = Window.Resources
            };
            var grid = new DockPanel
            {
                Margin = new Thickness(18)
            };
            dlg.Content = grid;
            var save = new Button
            {
                Content = "Сохранить",
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
                Style = (Style)Window.FindResource("Primary")
            };
            DockPanel.SetDock(save, Dock.Bottom);
            Row(grid, save);
            var input = new TextBox
            {
                Text = Disk.Utf8.GetString(initial),
                FontFamily = new FontFamily("Consolas"),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible
            };
            Row(grid, input);
            string json = null;
            save.Click += delegate
            {
                try
                {
                    var cfg = Disk.Parse(input.Text);
                    ProxySettings.Validate(cfg);
                    json = input.Text;
                    dlg.DialogResult = true;
                }
                catch (Exception e)
                {
                    MessageBox.Show(dlg, e.Message, "TG WS Proxy");
                }
            };
            if (dlg.ShowDialog() == true)
                SaveProxy(initial, json);
        }
        catch (Exception ex)
        {
            Error(ex);
        }
    }

    private async void SaveProxy(byte[] initial, string json)
    {
        await session.RunSimple("Сохранение настроек прокси", () =>
        {
            if (!initial.SequenceEqual(File.ReadAllBytes(Paths.Config)))
                throw new IOException("Конфиг прокси изменился. Открой настройки заново.");
            Components.Change(Component.TG, new[] { Paths.Config }, () => Disk.Write(Paths.Config, Disk.Utf8.GetBytes(json)));
        });
    }
}
