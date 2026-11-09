using PCL.Core.App.Lunar;
using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace PCL;

public static partial class ModLunar
{
    private static readonly LunarSessionCoordinator GameSessions = new();
    private static long _processSession;
    private static Process? _stoppingProcess;

    // Always queue notifications: never invoke UI subscribers or message boxes while holding the session gate.
    private static void QueueSessionNotification(long session, Action callback, bool completed = false)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        dispatcher.InvokeAsync(() =>
        {
            if (completed ? GameSessions.IsLatest(session) : GameSessions.IsCurrent(session)) callback();
        }, DispatcherPriority.Normal);
    }

    private static void LogSession(long session, string message, bool completed = false)
    {
        if (completed ? !GameSessions.IsLatest(session) : !GameSessions.IsCurrent(session)) return;
        ModBase.Log("[Lunar] " + message);
        QueueSessionNotification(session, () => LogReceived?.Invoke(message), completed);
    }

    private static void CaptureSessionOutput(long session, string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (!GameSessions.RunIfCurrent(session, () =>
        {
            Interlocked.Exchange(ref _lastOutputTicks, Environment.TickCount64);
            lock (_recentOutputLock)
            {
                _recentOutput.Enqueue(line);
                while (_recentOutput.Count > RecentOutputLimit) _recentOutput.Dequeue();
            }
        })) return;
        QueueSessionNotification(session, () => LogReceived?.Invoke(line));
    }
}
