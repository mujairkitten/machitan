using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Machitan.Core;

/// <summary>
/// Logs every exception at the moment it is thrown (a "first-chance" exception), before
/// anything catches it. Some crashes end the process without passing through any handler;
/// this leaves the exception that started them in the diagnostics log. Capped per run so a
/// noisy loop cannot flood the log.
/// </summary>
internal static class ExceptionTrail
{
    private const int MaxEntries = 150;
    private const int MaxStackLines = 18;

    [ThreadStatic]
    private static bool _inside;

    private static int _count;

    internal static void Start()
    {
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
    }

    private static void OnFirstChance(object? sender, FirstChanceExceptionEventArgs e)
    {
        // Logging can itself throw (and be caught inside Log); never recurse.
        if (_inside)
        {
            return;
        }

        int number = Interlocked.Increment(ref _count);
        if (number > MaxEntries)
        {
            return;
        }

        _inside = true;
        try
        {
            string[] stack = Environment.StackTrace.Split(Environment.NewLine);
            int take = Math.Min(stack.Length, MaxStackLines + 3);
            string trail = string.Join(Environment.NewLine, stack, 3, Math.Max(0, take - 3));

            Log.Warn(
                "Exception thrown (#" + number + (number == MaxEntries ? ", last one logged this run" : string.Empty) + "): "
                + e.Exception.GetType().FullName + ": " + e.Exception.Message
                + Environment.NewLine + trail);
        }
        catch (Exception)
        {
            // Never let the trail itself cause trouble.
        }
        finally
        {
            _inside = false;
        }
    }
}
