using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.UI.WallpaperEngine;

namespace PCL;

internal static class WallpaperEngineWindows
{
    internal sealed record Engine(string Executable, string Directory, int ProcessId, long Started);
    internal sealed record Monitor(string Device, double Left, double Top, double Width, double Height);
    [StructLayout(LayoutKind.Sequential)] internal struct Rectangle { public int Left, Top, Right, Bottom; }
    private delegate bool EnumerateWindow(IntPtr window, IntPtr parameter);

    internal static Engine? FindEngine(string configuredDirectory)
    {
        int session = Process.GetCurrentProcess().SessionId;
        foreach (string name in new[] { "wallpaper64", "wallpaper32" })
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != session) continue;
                    string? file = process.MainModule?.FileName;
                    if (file != null && File.Exists(file))
                        return new(file, Path.GetDirectoryName(file)!, process.Id, process.StartTime.ToUniversalTime().Ticks);
                    if (!string.IsNullOrEmpty(configuredDirectory))
                    {
                        file = Path.Combine(configuredDirectory, name + ".exe");
                        if (File.Exists(file)) return new(file, configuredDirectory, process.Id, process.StartTime.ToUniversalTime().Ticks);
                    }
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
            }
        }
        return null;
    }

    internal static Monitor[] Monitors() => System.Windows.Forms.Screen.AllScreens
        .Select(s => new Monitor(s.DeviceName, s.Bounds.Left, s.Bounds.Top, s.Bounds.Width, s.Bounds.Height))
        .ToArray();

    internal static async Task<string> ControlAsync(Engine engine, CancellationToken token, params string[] arguments)
    {
        if (!EngineIsAlive(engine)) throw new OperationCanceledException("Wallpaper Engine has exited.");
        var info = new ProcessStartInfo(engine.Executable)
        {
            WorkingDirectory = engine.Directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (arguments is ["-control", "applyProperties", "-location", var name, "-properties", var properties])
            info.Arguments = WallpaperEngineCommands.PropertiesArguments(name, properties);
        else
            foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Could not start Wallpaper Engine controller.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            // This is only the short-lived controller we started, not the WE renderer.
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            throw;
        }
        string result = await output.ConfigureAwait(false);
        string message = await error.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new IOException($"Wallpaper Engine {arguments.ElementAtOrDefault(1)} exited with {process.ExitCode}: {message}");
        return result;
    }

    internal static IntPtr FindOwnedWindow(Engine engine, string name)
    {
        IntPtr window = FindWindow(null, name);
        if (window == IntPtr.Zero) return window;
        GetWindowThreadProcessId(window, out uint process);
        return process == engine.ProcessId ? window : IntPtr.Zero;
    }

    internal static void ConfigureOwnedWindow(IntPtr window, int width, int height, bool hideTaskbar)
    {
        // SetWindowPos is DPI-virtualized for callers without a PMv2 manifest.
        // Keep render dimensions and screen bounds in physical pixels on every thread.
        IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
        // Only our uniquely named pop-out is passed here, never WE's desktop render HWND.
        long style = GetWindowLongPtr(window, -16).ToInt64();
        long nextStyle = (style & ~0x00C40000L) | 0x80000000L;
        if (style != nextStyle) SetWindowLongPtr(window, -16, new IntPtr(nextStyle));
        if (hideTaskbar)
        {
            long extended = GetWindowLongPtr(window, -20).ToInt64();
            long next = (extended & ~0x00040000L) | 0x08000080L;
            if (next != extended) SetWindowLongPtr(window, -20, new IntPtr(next));
        }
        var (x, y) = OffscreenPosition();
        if (!GetWindowRect(window, out var current) || current.Left != x || current.Top != y ||
            current.Right - current.Left != width || current.Bottom - current.Top != height || style != nextStyle)
        {
            if (!SetWindowPos(window, IntPtr.Zero, x, y, width, height, 0x0034))
                throw new Win32Exception();
        }
        int square = 1; // DWMWCP_DONOTROUND, on supported Windows versions.
        _ = DwmSetWindowAttribute(window, 33, ref square, sizeof(int));
        }
        finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }

    internal static (int X, int Y) OffscreenPosition()
    {
        IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            // WE's CLI treats negative coordinates as missing options and falls
            // back to (0,0). Positive physical coordinates avoid the visible flash.
            int right = checked(GetSystemMetrics(76) + GetSystemMetrics(78));
            return (checked(Math.Max(0, right) + 2048), 0);
        }
        finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }

    internal static string ReadSharedText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 32 * 1024 * 1024) throw new InvalidDataException("Wallpaper Engine JSON exceeds the supported size.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static bool IsOwnedName(string name) =>
        name.StartsWith("PCL-Lite-Wallpaper-", StringComparison.Ordinal) &&
        name.Length < 160;

    internal static bool EngineIsAlive(Engine engine)
    {
        try
        {
            using var process = Process.GetProcessById(engine.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == engine.Started;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
        { return false; }
    }

    internal static async Task CleanOrphanedViewsAsync(Engine engine, CancellationToken token)
    {
        var names = new List<string>();
        EnumerateWindow enumerate = (window, _) =>
        {
            GetWindowThreadProcessId(window, out uint processId);
            if (processId != engine.ProcessId) return true;
            var title = new StringBuilder(160);
            GetWindowText(window, title, title.Capacity);
            string name = title.ToString();
            if (!IsOwnedName(name)) return true;
            string[] parts = name["PCL-Lite-Wallpaper-".Length..].Split('-');
            if (parts.Length != 3 || !int.TryParse(parts[0], out int owner) ||
                !long.TryParse(parts[1], out long started) || !Guid.TryParseExact(parts[2], "N", out Guid ignoredId))
                return true;
            try
            {
                using var launcher = Process.GetProcessById(owner);
                if (!launcher.HasExited && launcher.StartTime.ToUniversalTime().Ticks == started) return true;
            }
            catch (ArgumentException) { }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException) { return true; }
            names.Add(name);
            return true;
        };
        EnumWindows(enumerate, IntPtr.Zero);
        foreach (string name in names)
            await ControlAsync(engine, token, "-control", "closeWallpaper", "-location", name).ConfigureAwait(false);
    }

    internal static void CloseAtExit(Engine? engine, string? name)
    {
        if (engine == null || name == null || !IsOwnedName(name) || !EngineIsAlive(engine)) return;
        try
        {
            var info = new ProcessStartInfo(engine.Executable)
            {
                WorkingDirectory = engine.Directory, UseShellExecute = false, CreateNoWindow = true
            };
            foreach (string value in new[] { "-control", "closeWallpaper", "-location", name })
                info.ArgumentList.Add(value);
            using var controller = Process.Start(info);
            // This is a controller process; never terminate the shared WE renderer.
            controller?.WaitForExit(1000);
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException)
        { Trace.TraceWarning("Wallpaper Engine renderer cleanup: " + error.Message); }
    }

    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out Rectangle rectangle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(
        IntPtr window, StringBuilder text, int capacity);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumerateWindow callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(
        IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(
        IntPtr window, uint attribute, ref int value, int size);
}
