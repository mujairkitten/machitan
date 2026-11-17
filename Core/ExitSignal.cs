using System;
using System.Threading;
using Microsoft.UI.Dispatching;

namespace Machitan.Core;

/// <summary>
/// Lets a second launch ("Machitan.exe --exit") ask the running shell to close cleanly.
/// </summary>
internal sealed class ExitSignal
{
    internal const string Argument = "--exit";

    private const string EventName = "Local\\Machitan_ExitRequest";

    private readonly EventWaitHandle _event;

    /// <summary>Starts listening. <paramref name="onExit"/> runs on the UI thread.</summary>
    internal ExitSignal(DispatcherQueue queue, Action onExit)
    {
        _event = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);

        var thread = new Thread(() =>
        {
            try
            {
                _event.WaitOne();
                queue.TryEnqueue(() => onExit());
            }
            catch (Exception ex)
            {
                Log.Warn("Exit signal listener stopped.", ex);
            }
        })
        {
            IsBackground = true,
            Name = "Machitan exit signal",
        };
        thread.Start();
    }

    /// <summary>Signals the running shell. Returns false when no shell is listening.</summary>
    internal static bool Send()
    {
        if (!EventWaitHandle.TryOpenExisting(EventName, out EventWaitHandle? existing))
        {
            return false;
        }

        using (existing)
        {
            existing.Set();
        }

        return true;
    }
}
