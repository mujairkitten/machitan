using System;
using System.Threading;

namespace Machitan.Core;

/// <summary>Only one shell instance may run per session.</summary>
internal static class SingleInstance
{
    internal const string MutexName = "Local\\Machitan_SingleInstance";

    private static Mutex? _mutex;
    private static bool _owned;

    /// <summary>Returns false when another instance already holds the mutex.</summary>
    internal static bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        _owned = createdNew;
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
        }

        return createdNew;
    }

    /// <summary>Must run on the thread that acquired the mutex.</summary>
    internal static void Release()
    {
        try
        {
            if (_mutex is not null && _owned)
            {
                _mutex.ReleaseMutex();
            }
        }
        catch (ApplicationException)
        {
            // Not owned by this thread; the handle is closed below either way.
        }

        _mutex?.Dispose();
        _mutex = null;
        _owned = false;
    }

    /// <summary>True when some shell instance currently holds the mutex.</summary>
    internal static bool IsHeldBySomeone()
    {
        if (Mutex.TryOpenExisting(MutexName, out Mutex? existing))
        {
            existing.Dispose();
            return true;
        }

        return false;
    }
}
