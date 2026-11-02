using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using GUI;
using GUI.Devices;

internal static class WindowLifetimeChecks
{
    public static async Task RunAsync()
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var owner = new Window { Width = 100, Height = 100, Left = -10000,
            Top = -10000, ShowInTaskbar = false };
        owner.Show();
        try
        {
            foreach (var startWorker in new[] { false, true })
            {
                var window = new TrackingWindow { Owner = owner, ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
                var worker = (PythonWorker)typeof(TrackingWindow)
                    .GetField("_worker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                if (startWorker) await worker.CallAsync("map_list", new { });
                var closed = false;
                Exception? failure = null;
                DispatcherUnhandledExceptionEventHandler handler = (_, e) =>
                {
                    failure = e.Exception;
                    e.Handled = true;
                    window.Hide();
                };
                window.Closed += (_, _) => closed = true;
                window.Dispatcher.UnhandledException += handler;
                try
                {
                    _ = window.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        window.Close();
                        // A second close request before deferred finalization must be harmless.
                        if (!closed) window.Close();
                    }), DispatcherPriority.ApplicationIdle);
                    window.ShowDialog();
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    if (failure is not null) throw new InvalidOperationException("Tracking close crashed the dispatcher.", failure);
                    if (!closed || !owner.IsVisible || !owner.IsEnabled)
                        throw new InvalidOperationException("Closing tracking must leave its owner available.");
                }
                finally { window.Dispatcher.UnhandledException -= handler; }
            }
        }
        finally { owner.Close(); }
        Console.WriteLine("PASS: tracking closes before/after worker startup, repeated close, modal owner remains available.");
    }
}
