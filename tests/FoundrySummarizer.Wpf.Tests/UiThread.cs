using System.Windows;
using System.Windows.Threading;

namespace FoundrySummarizer.Wpf.Tests;

/// <summary>
/// One WPF UI thread for all view tests, with the app's own <see cref="App"/> resources (styles, brushes) loaded,
/// so views are built exactly as in the running app. WPF allows one <see cref="Application"/> per process and
/// requires an STA thread, which test runner threads are not.
/// </summary>
internal static class UiThread
{
    private static readonly Lazy<Dispatcher> Instance = new(Start);

    /// <summary>Runs <paramref name="action"/> on the UI thread and waits for it.</summary>
    public static T Invoke<T>(Func<T> action) => Instance.Value.Invoke(action);

    /// <summary>Runs <paramref name="action"/> on the UI thread and waits for it.</summary>
    public static void Invoke(Action action) => Instance.Value.Invoke(action);

    /// <summary>Waits until the UI thread has finished layout, data binding and other queued work.</summary>
    public static void Flush() => Instance.Value.Invoke(() => { }, DispatcherPriority.ContextIdle);

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        Exception? failure = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                // Not Run(): that would call OnStartup and build the real app. Only the resources are wanted.
                var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
                dispatcher = Dispatcher.CurrentDispatcher;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                ready.Set();
            }

            if (failure is null) Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "WPF test UI thread"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher ?? throw new InvalidOperationException("The WPF test UI thread could not start.", failure);
    }
}
