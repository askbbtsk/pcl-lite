using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.UI.WallpaperEngine;

namespace PCL;

/// <summary>Owns one independent WE renderer, never reparents or captures the desktop.</summary>
internal static class WallpaperEngineBackground
{
    private static FormMain? _window;
    private static CancellationTokenSource? _lifetime;
    private static WallpaperEngineWindows.Engine? _engine;
    private static string? _name;
    private static WallpaperEngineCapture? _capture;
    private static WriteableBitmap? _bitmap;
    private static int _generation, _monitor = -1;
    private static string _status = "Off";
    internal static bool IsActive { get; private set; }
    internal static string StatusText => Lang.Text("Setup.Ui.WallpaperEngine." + _status);
    internal static event Action? StatusChanged;

    internal static void Attach(FormMain window)
    {
        if (_window is not null) return;
        _window = window;
        _lifetime = new CancellationTokenSource();
        window.Closed += (_, _) =>
        {
            _lifetime.Cancel();
            _capture?.Dispose();
            WallpaperEngineWindows.CloseAtExit(_engine, _name);
        };
        _ = Task.Run(() => RunAsync(window, _lifetime.Token));
    }

    private sealed record View(bool Enabled, bool Paused, int Width, int Height, int Quality,
        int Monitor, string Device, string Directory);

    private static View ReadView(FormMain window)
    {
        var monitors = WallpaperEngineWindows.Monitors();
        var point = window.PointToScreen(new Point());
        var scale = VisualTreeHelper.GetDpi(window);
        var bounds = Array.ConvertAll(monitors, m => (m.Left, m.Top, m.Width, m.Height));
        _monitor = WallpaperEngineSource.SelectMonitor(point.X, point.Y,
            window.ActualWidth * scale.DpiScaleX, window.ActualHeight * scale.DpiScaleY, bounds, _monitor);
        return new(Config.Preference.Background.FollowWallpaperEngine,
            window.WindowState == WindowState.Minimized || !window.IsVisible ||
            ModVideoBack.IsGaming && Config.Preference.Background.AutoPauseVideo,
            Math.Max(16, (int)(window.ActualWidth * scale.DpiScaleX)),
            Math.Max(16, (int)(window.ActualHeight * scale.DpiScaleY)),
            Config.Preference.Background.WallpaperEngineQuality,
            _monitor, _monitor >= 0 ? monitors[_monitor].Device : "",
            Config.Preference.Background.WallpaperEngineDirectory);
    }

    private static async Task RunAsync(FormMain window, CancellationToken token)
    {
        string? current = null;
        long checkedEngine = 0;
        long openedAt = 0;
        int failures = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var view = await window.Dispatcher.InvokeAsync(() => ReadView(window), DispatcherPriority.Background);
                    if (!view.Enabled || view.Paused || view.Monitor < 0)
                    {
                        await StopRendererAsync(window).ConfigureAwait(false);
                        current = null;
                        await SetStatusAsync(window, !view.Enabled ? "Off" : "Paused");
                    }
                    else
                    {
                        var engine = WallpaperEngineWindows.FindEngine(view.Directory);
                        if (engine is null)
                        {
                            await StopRendererAsync(window).ConfigureAwait(false);
                            current = null;
                            await SetStatusAsync(window, "NotRunning");
                        }
                        else
                        {
                            if (checkedEngine != engine.Started)
                            {
                                await WallpaperEngineWindows.CleanOrphanedViewsAsync(engine, token).ConfigureAwait(false);
                                checkedEngine = engine.Started;
                            }
                            var selection = WallpaperEngineSource.ReadSelection(
                                WallpaperEngineWindows.ReadSharedText(Path.Combine(engine.Directory, "config.json")),
                                Environment.UserName, view.Monitor, view.Device)
                                ?? throw new InvalidDataException("No wallpaper assignment for the current display.");
                            var project = WallpaperEngineSource.ResolveProject(selection, WallpaperEngineWindows.ReadSharedText);
                            var size = WallpaperEngineSource.RenderSize(view.Width, view.Height, view.Quality);
                            string key = $"{engine.Started}|{view.Monitor}|{project.File}|{project.PropertiesJson}|{size}";
                            if (_capture?.Completion.IsCompleted == true)
                                throw new IOException("Wallpaper capture ended before the renderer was closed.");
                            if (IsActive) failures = 0;
                            if (current != key)
                            {
                                await StopRendererAsync(window).ConfigureAwait(false);
                                await SetStatusAsync(window, "Connecting");
                                _engine = engine;
                                using var owner = Process.GetCurrentProcess();
                                _name = $"PCL-Lite-Wallpaper-{owner.Id}-{owner.StartTime.ToUniversalTime().Ticks}-{Guid.NewGuid():N}";
                                var origin = WallpaperEngineWindows.OffscreenPosition();
                                await WallpaperEngineWindows.ControlAsync(engine, token,
                                    "-control", "openWallpaper", "-file", project.File, "-playInWindow", _name,
                                    "-width", size.Width.ToString(), "-height", size.Height.ToString(),
                                    "-x", origin.X.ToString(), "-y", origin.Y.ToString(), "-borderless").ConfigureAwait(false);
                                IntPtr source = IntPtr.Zero;
                                for (int i = 0; i < 40 && source == IntPtr.Zero; i++)
                                {
                                    source = WallpaperEngineWindows.FindOwnedWindow(engine, _name);
                                    if (source == IntPtr.Zero) await Task.Delay(100, token).ConfigureAwait(false);
                                }
                                if (source == IntPtr.Zero) throw new IOException("WE did not create the requested rendering window.");
                                WallpaperEngineWindows.ConfigureOwnedWindow(source, size.Width, size.Height, hideTaskbar: true);
                                await WallpaperEngineWindows.ControlAsync(engine, token, "-control", "applyProperties",
                                    "-location", _name, "-properties", "RAW~(" + project.PropertiesJson + ")~END")
                                    .ConfigureAwait(false);
                                int generation = Volatile.Read(ref _generation);
                                _capture = new WallpaperEngineCapture(source,
                                    (frame, release) => Present(window, generation, frame, release),
                                    error => ModBase.Log(error, "[WallpaperEngine] Capture failed", ModBase.LogLevel.Debug));
                                current = key;
                                openedAt = Stopwatch.GetTimestamp();
                            }
                            else if (!IsActive && Stopwatch.GetElapsedTime(openedAt) > TimeSpan.FromSeconds(8))
                                throw new IOException("WE renderer did not deliver a frame within eight seconds.");
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    ModBase.Log(error, "[WallpaperEngine] Background unavailable", ModBase.LogLevel.Debug);
                    await StopRendererAsync(window).ConfigureAwait(false);
                    current = null;
                    await SetStatusAsync(window, "Unavailable");
                    failures = Math.Min(failures + 1, 6);
                    await Task.Delay(TimeSpan.FromSeconds(failures * 5), token).ConfigureAwait(false);
                }
                await Task.Delay(750, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { await StopRendererAsync(window).ConfigureAwait(false); }
    }

    private static void Present(FormMain window, int generation, WallpaperEngineCapture.Frame frame, Action release)
    {
        if (window.Dispatcher.HasShutdownStarted) { release(); return; }
        var operation = window.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                if (generation != Volatile.Read(ref _generation) || !Config.Preference.Background.FollowWallpaperEngine ||
                    window.WindowState == WindowState.Minimized || !window.IsVisible) return;
                if (_bitmap?.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
                    _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
                _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Width * 4, 0);
                window.WallpaperEngineBack.Source = _bitmap;
                window.WallpaperEngineBack.Stretch = Config.Preference.Background.WallpaperSuitMode switch
                { 1 => Stretch.None, 2 => Stretch.Uniform, 3 => Stretch.Fill, _ => Stretch.UniformToFill };
                window.WallpaperEngineBack.Visibility = Visibility.Visible;
                if (!IsActive)
                {
                    IsActive = true;
                    window.VideoBack.Pause();
                    LiquidGlassCore.Refresh();
                    ModMain.frmSetupUI?.BackgroundRefreshUI(true, 0);
                    SetStatus("Live");
                }
            }
            catch (Exception error)
            {
                _capture?.Dispose();
                ModBase.Log(error, "[WallpaperEngine] Frame presentation failed", ModBase.LogLevel.Debug);
            }
            finally { release(); }
        }, DispatcherPriority.Background);
        // Shutdown can abort a queued operation before its finally block executes.
        _ = operation.Task.ContinueWith(task => { if (task.IsCanceled || task.IsFaulted) release(); }, TaskScheduler.Default);
    }

    private static async Task StopRendererAsync(FormMain window)
    {
        Interlocked.Increment(ref _generation);
        var capture = _capture; _capture = null;
        capture?.Dispose();
        if (capture is not null) await capture.Completion.ConfigureAwait(false);
        var engine = _engine; var name = _name;
        _engine = null; _name = null;
        if (engine is not null && name is not null)
        {
            try
            {
                await WallpaperEngineWindows.ControlAsync(engine, CancellationToken.None,
                    "-control", "closeWallpaper", "-location", name).ConfigureAwait(false);
            }
            catch (Exception error) { ModBase.Log(error, "[WallpaperEngine] Renderer cleanup failed", ModBase.LogLevel.Debug); }
        }
        if (window.Dispatcher.HasShutdownStarted) return;
        await window.Dispatcher.InvokeAsync(() =>
        {
            bool wasActive = IsActive;
            IsActive = false;
            _bitmap = null;
            window.WallpaperEngineBack.Source = null;
            window.WallpaperEngineBack.Visibility = Visibility.Collapsed;
            if (wasActive)
            {
                LiquidGlassCore.Refresh();
                ModVideoBack.VideoPlay();
                ModMain.frmSetupUI?.BackgroundRefreshUI(window.ImgBack.Background is not null || window.VideoBack.Source is not null, 0);
            }
        }, DispatcherPriority.Background);
    }

    private static Task SetStatusAsync(FormMain window, string status)
        => window.Dispatcher.HasShutdownStarted ? Task.CompletedTask
            : window.Dispatcher.InvokeAsync(() => SetStatus(status), DispatcherPriority.Background).Task;

    private static void SetStatus(string status)
    {
        if (_status == status) return;
        _status = status;
        StatusChanged?.Invoke();
    }
}
