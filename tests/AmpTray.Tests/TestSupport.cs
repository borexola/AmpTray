using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using Xunit;

// Theme holds static state (the WPF resources), so the suites run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace AmpTray.Tests;

/// <summary>Formats numbers the same way on every machine while the scope lasts.</summary>
internal sealed class CultureScope : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _ui = CultureInfo.CurrentUICulture;

    public CultureScope(CultureInfo? c = null)
    {
        CultureInfo.CurrentCulture = c ?? CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = c ?? CultureInfo.InvariantCulture;
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _ui;
    }
}

/// <summary>
/// Runs work on one long-lived STA thread that owns the WPF <see cref="Application"/>, so resource
/// brushes created by the theme are always touched from the thread they belong to.
/// </summary>
internal static class Sta
{
    private static readonly BlockingCollection<Action> Queue = new();

    static Sta()
    {
        var thread = new Thread(() =>
        {
            foreach (var work in Queue.GetConsumingEnumerable()) work();
        }) { IsBackground = true, Name = "Test STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public static void Run(Action work)
    {
        Exception? failure = null;
        using var done = new ManualResetEventSlim();
        Queue.Add(() =>
        {
            try
            {
                if (Application.Current == null) _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                work();
            }
            catch (Exception ex) { failure = ex; }
            finally { done.Set(); }
        });
        done.Wait();
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public static T Run<T>(Func<T> work)
    {
        T result = default!;
        Run(() => { result = work(); });
        return result;
    }
}
