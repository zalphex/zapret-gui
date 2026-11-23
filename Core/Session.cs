using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

internal sealed class Session
{
    private readonly SynchronizationContext ui;
    private CancellationTokenSource cancellation;
    internal bool Busy { get; private set; }

    internal string Activity = "";
    internal bool CanCancel
    {
        get
        {
            return Busy && cancellation != null;
        }
    }

    internal readonly Queue<string> Journal = new Queue<string>();
    internal readonly Dictionary<string, Reading> Latency = new Dictionary<string, Reading>();
    internal readonly Dictionary<Component, string> Winners = new Dictionary<Component, string>();
    internal event Action Changed, Finished;
    internal int Page;
    internal bool? AutoStart;
    internal Session(SynchronizationContext ui)
    {
        this.ui = ui;
    }

    private void Notify()
    {
        var ev = Changed;
        if (ev != null)
            ev();
    }

    private void Post(Action a)
    {
        ui.Post(delegate
        {
            a();
        }, null);
    }

    internal void Log(string text)
    {
        Post(() => Record(text));
    }

    internal void Record(string text)
    {
        while (Journal.Count >= 240)
            Journal.Dequeue();
        Journal.Enqueue(DateTime.Now.ToString("HH:mm:ss") + "   " + text);
        Notify();
    }

    internal void Cancel()
    {
        if (cancellation != null)
            cancellation.Cancel();
    }

    internal async Task<bool> Run(string title, Action<CancellationToken> job, bool cancellable = false)
    {
        if (Busy)
            return false;
        Busy = true;
        Activity = title;
        cancellation = cancellable ? new CancellationTokenSource() : null;
        var token = cancellation == null ? CancellationToken.None : cancellation.Token;
        Notify();
        try
        {
            await Task.Run(() => job(token));
            return true;
        }
        catch (OperationCanceledException)
        {
            Record("Проверка отменена. Исходное состояние восстановлено.");
            return false;
        }
        catch (Exception ex)
        {
            Record(title + " · " + ex.GetBaseException().Message);
            LastError = ex.GetBaseException().Message;
            return false;
        }
        finally
        {
            if (cancellation != null)
                cancellation.Dispose();
            cancellation = null;
            Busy = false;
            Activity = "";
            Notify();
            var ev = Finished;
            if (ev != null)
                ev();
        }
    }

    internal string LastError = "";
    internal async Task RunSimple(string title, Action job)
    {
        LastError = "";
        if (await Run(title, t => job()))
            Record(title + " · выполнено");
    }

    internal void Measure(int index)
    {
        var r = Checks.Measure(index);
        Post(() =>
        {
            Latency[r.Name] = r;
            Record(r.Name + " · " + r.Details.Replace("\n", " · "));
        });
    }

    internal void MeasureAll()
    {
        Parallel.For(0, Checks.Services.Length, Measure);
    }

    internal async Task Test(Component c, bool apply, Func<string, bool> confirm = null)
    {
        string winner = null;
        LastError = "";
        if (await Run("Проверка конфигов " + Components.Title(c), token =>
        {
            winner = Checks.Test(c, apply, token, Log, (i, n, s) => Post(() =>
            {
                Activity = Components.Title(c) + " · " + i + " / " + n + " · " + s;
                Notify();
            }), c == Component.Zapret && apply ? new Func<string, bool>(name =>
            {
                bool accepted = false;
                ui.Send(_ => { if (!token.IsCancellationRequested && confirm != null) accepted = confirm(name); }, null);
                return accepted && !token.IsCancellationRequested;
            }) : null);
        }, true))
        {
            if (!string.IsNullOrEmpty(winner))
                Winners[c] = winner;
            var ev = Finished;
            if (ev != null)
                ev();
        }
    }

    internal async Task Start(bool visible)
    {
        await Run("Запуск компонентов", token =>
        {
            foreach (var c in Components.All)
            {
                if (!Components.Installed(c) || Processes.Running(Paths.Binary(c)))
                    continue;
                try
                {
                    Components.Start(c, Components.Selected(c));
                    Log(Components.Title(c) + " запущен.");
                }
                catch (Exception ex)
                {
                    Log(Components.Title(c) + " · " + ex.GetBaseException().Message);
                }
            }

            try
            {
                AutoStart = NativeSetup.AutoStartEnabled();
            }
            catch (Exception ex)
            {
                Log("Автозапуск · " + ex.GetBaseException().Message);
            }

            if (visible)
                MeasureAll();
            if (Maintenance.CheckUpdates)
                try
                {
                    string v = Downloads.Latest(Component.Zapret).Version;
                    if (Disk.State("ZapretVersion") != v)
                        Log("Доступно обновление zapret " + v);
                }
                catch (Exception ex)
                {
                    Log("Проверка обновлений zapret · " + ex.GetBaseException().Message);
                }
        });
    }
}
