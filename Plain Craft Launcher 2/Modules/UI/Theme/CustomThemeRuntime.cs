using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.UI.Theme;
using PCL.Core.UI.Theme.Packages;

namespace PCL;

/// <summary>UI adapter for the existing ThemeService; owns one optional visual resource layer.</summary>
public sealed class CustomThemeRuntime : IThemePackageHost
{
    public static CustomThemeRuntime? Current { get; private set; }
    public static bool AllowsGlass => Current?._active?.Manifest.SupportsGlass ?? true;
    private readonly FormMain _window;
    private readonly ThemePackageService _packages;
    private readonly ThemeSelectionStore _store;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly ThemeResourceLoader _loader = new();
    private ThemePackage? _active;
    private ResourceDictionary? _layer;
    private ThemeSelection _selection = new();
    private CustomThemePreviewBar? _preview;
    private bool _closed, _refreshing;
    private bool _visualRecoveryQueued;
    private (bool Dark, bool Glass, bool Contrast) _appliedMode;
    public string Directory => _packages.ThemesDirectory;
    public ThemeManifest? ActiveTheme => _active?.Manifest;
    public bool IsPreviewing => _preview is not null;
    public event EventHandler? Changed;
    public string? LastError { get; private set; }
    internal object? FindOverride(string key) =>
        _layer is not null && _layer.Contains(key) ? _layer[key] : null;

    private CustomThemeRuntime(FormMain window)
    {
        _window = window;
        _packages = new ThemePackageService(Path.Combine(Paths.Data, "Themes"), Path.Combine(Paths.Temp, "Themes"));
        _store = new ThemeSelectionStore(Directory);
        ThemeService.RegisterPackageHost(this);
        ThemeControlRegistry.Initialize(window, this);
        window.Closed += (_, _) =>
        {
            _closed = true;
            _preview?.Finish(false);
            ThemeControlRegistry.Detach(window);
        };
        SystemParameters.StaticPropertyChanged += OnSystemSettingsChanged;
        LiquidGlassCore.Changed += OnGlassChanged;
        System.Windows.Application.Current.DispatcherUnhandledException += OnDeferredThemeFailure;
        window.Closed += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= OnSystemSettingsChanged;
            LiquidGlassCore.Changed -= OnGlassChanged;
            System.Windows.Application.Current.DispatcherUnhandledException -= OnDeferredThemeFailure;
        };
    }

    public static async Task InitializeAsync(FormMain window)
    {
        if (Current is not null) return;
        var runtime = Current = new CustomThemeRuntime(window);
        try
        {
            System.IO.Directory.CreateDirectory(runtime.Directory);
            runtime._selection = runtime._store.Read();
            if (runtime._selection.Pending)
            {
                runtime.LastError = Lang.Text("Theme.Pack.Recovered");
                runtime._selection = new();
                runtime._store.Write(runtime._selection);
                runtime.Notify();
                return;
            }
            if (runtime._selection.Id is not { } id) return;
            var package = await runtime._packages.InspectAsync(ThemeFilePaths.Resolve(runtime.Directory, id));
            if (runtime._closed) return;
            if (package.ContentHash != runtime._selection.ContentHash
                || !package.Manifest.IsCompatible(Basics.VersionName)
                    && runtime._selection.ForcedConsent != package.ForceConsentKey(Basics.VersionName))
            {
                runtime.LastError = Lang.Text("Theme.Pack.ChangedOnDisk");
                runtime.Notify();
                return;
            }
            runtime._store.Write(runtime._selection with { Pending = true });
            runtime.Apply(package);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            runtime._store.Write(runtime._selection);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            runtime.ApplyBuiltInAfterError(error);
        }
    }

    public Task<IReadOnlyList<ThemeCatalogItem>> GetInstalledAsync(CancellationToken token = default) =>
        Task.Run(_packages.Scan, token);

    public async Task<bool> InstallAsync(string source, CancellationToken token = default)
    {
        _window.Dispatcher.VerifyAccess();
        if (!await _operation.WaitAsync(0, token)) return false;
        try
        {
            var package = await _packages.InspectAsync(source, token);
            if (_closed) return false;
            var manifest = package.Manifest;
            if (ModMain.MyMsgBox(Lang.Text("Theme.Pack.InstallPrompt", manifest.Name,
                    string.IsNullOrEmpty(manifest.Author) ? Lang.Text("Theme.Pack.Undeclared") : manifest.Author,
                    manifest.Version, manifest.LauncherVersion ?? Lang.Text("Theme.Pack.Undeclared"), source),
                    Lang.Text("Theme.Pack.Title"), Lang.Text("Theme.Pack.InstallPreview"),
                    Lang.Text("Common.Action.Cancel")) != 1) return false;
            bool replace = System.IO.Directory.Exists(ThemeFilePaths.Resolve(Directory, manifest.Id));
            if (replace && ModMain.MyMsgBox(Lang.Text("Theme.Pack.ReplacePrompt", manifest.Name),
                    Lang.Text("Theme.Pack.Title"), Lang.Text("Common.Action.Overwrite"),
                    Lang.Text("Common.Action.Cancel")) != 1) return false;
            // Materialize every variant before replacing a previously working package.
            Preflight(package);
            if (!ConfirmCompatibility(package)) return false;
            await _packages.InstallAsync(package, replace, token);
            return await PreviewCoreAsync(package, token);
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { Report(error); return false; }
        finally { _operation.Release(); }
    }

    public async Task<bool> PreviewAsync(string id, CancellationToken token = default)
    {
        _window.Dispatcher.VerifyAccess();
        if (!await _operation.WaitAsync(0, token)) return false;
        try
        {
            var package = await _packages.InspectAsync(ThemeFilePaths.Resolve(Directory, id), token);
            if (_closed || !ConfirmCompatibility(package)) return false;
            Preflight(package);
            return await PreviewCoreAsync(package, token);
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { Report(error); return false; }
        finally { _operation.Release(); }
    }

    private bool ConfirmCompatibility(ThemePackage package) =>
        package.Manifest.IsCompatible(Basics.VersionName)
        || _selection.ForcedConsent == package.ForceConsentKey(Basics.VersionName)
        || ModMain.MyMsgBox(Lang.Text("Theme.Pack.Incompatible", package.Manifest.Name, Basics.VersionName,
                package.Manifest.LauncherVersion!), Lang.Text("Theme.Pack.Title"),
                Lang.Text("Theme.Pack.Force"), Lang.Text("Common.Action.Cancel"), isWarn: true) == 1;

    private void Preflight(ThemePackage package)
    {
        foreach (bool dark in new[] { false, true })
        foreach (bool glass in package.Manifest.SupportsGlass ? new[] { false, true } : new[] { false })
        {
            var resources = _loader.Load(package, dark, glass, FontCache(package));
            ThemeControlRegistry.ValidateTemplates(resources);
        }
    }

    private async Task<bool> PreviewCoreAsync(ThemePackage package, CancellationToken token)
    {
        var previous = _active;
        var selection = _selection;
        bool kept = false, committed = false;
        try
        {
            _store.Write(new ThemeSelection(package.Manifest.Id, package.ContentHash, Pending: true));
            Apply(package);
            _preview = new CustomThemePreviewBar(_window, package.Manifest.Name);
            Notify();
            kept = await _preview.ShowAsync(token);
            if (!kept || _closed) return false;
            _selection = new ThemeSelection(package.Manifest.Id, package.ContentHash,
                package.Manifest.IsCompatible(Basics.VersionName) ? null : package.ForceConsentKey(Basics.VersionName));
            _store.Write(_selection);
            committed = true;
            LastError = null;
            return true;
        }
        finally
        {
            _preview?.Dispose();
            _preview = null;
            if (!committed || _closed)
            {
                _selection = selection;
                if (!_closed) Apply(previous);
                _store.Write(selection);
            }
            Notify();
        }
    }

    public void RestoreBuiltIn()
    {
        _window.Dispatcher.VerifyAccess();
        if (IsPreviewing || _closed) return;
        Apply(null);
        _selection = new();
        _store.Write(_selection);
        LastError = null;
        Notify();
    }

    internal void Refresh()
    {
        if (_closed || _refreshing || _active is null) return;
        try { Apply(_active); }
        catch (Exception error) when (error is not OutOfMemoryException) { ApplyBuiltInAfterError(error); }
    }

    internal void RecoverVisualFailure(Exception error)
    {
        if (_closed || _visualRecoveryQueued) return;
        _visualRecoveryQueued = true;
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
        {
            _visualRecoveryQueued = false;
            if (_closed) return;
            if (_preview is not null) { Report(error, false); _preview.Finish(false); }
            else ApplyBuiltInAfterError(error);
        });
    }

    private void OnGlassChanged(object? sender, EventArgs args)
    {
        if (_active is not null && !_refreshing && _appliedMode != CurrentMode()) Refresh();
    }
    private static (bool Dark, bool Glass, bool Contrast) CurrentMode() =>
        (ThemeService.IsDarkMode, LiquidGlassCore.IsEnabled, SystemParameters.HighContrast);

    private void Apply(ThemePackage? package)
    {
        _window.Dispatcher.VerifyAccess();
        if (_closed || _refreshing) return;
        _refreshing = true;
        var oldPackage = _active;
        var oldLayer = _layer;
        try
        {
            var resources = package is null || SystemParameters.HighContrast ? null
                : _loader.Load(package, ThemeService.IsDarkMode,
                    package.Manifest.SupportsGlass && Config.Preference.TahoeGlass.IsEnabled
                        && Config.Preference.TahoeGlass.PerformanceConsent, FontCache(package));
            if (resources is not null) ThemeControlRegistry.ValidateTemplates(resources);
            if (_layer is not null) _window.PanForm.Resources.MergedDictionaries.Remove(_layer);
            _layer = resources;
            _active = package;
            _appliedMode = CurrentMode();
            if (resources is not null) _window.PanForm.Resources.MergedDictionaries.Add(resources);
            LiquidGlassCore.Refresh();
            _appliedMode = CurrentMode();
            ThemeControlRegistry.Refresh();
            _window.PanForm.UpdateLayout();
            Notify();
        }
        catch
        {
            if (_layer is not null) _window.PanForm.Resources.MergedDictionaries.Remove(_layer);
            _layer = oldLayer;
            _active = oldPackage;
            if (oldLayer is not null) _window.PanForm.Resources.MergedDictionaries.Add(oldLayer);
            LiquidGlassCore.Refresh();
            ThemeControlRegistry.Refresh();
            throw;
        }
        finally { _refreshing = false; }
    }

    private static string FontCache(ThemePackage package) => Path.Combine(Paths.Temp, "ThemeFonts", package.ContentHash);
    private void OnSystemSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SystemParameters.HighContrast) && !_closed)
            _window.Dispatcher.BeginInvoke(Refresh);
    }
    private void OnDeferredThemeFailure(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        if (_active is null || _closed || _refreshing || args.Exception is OutOfMemoryException) return;
        // WPF may instantiate template resources on a later layout tick. Recover
        // only this visual path, never unrelated game/network/command failures.
        string trace = args.Exception.ToString();
        if (!trace.Contains("System.Windows.StyleHelper", StringComparison.Ordinal)
            && !trace.Contains("System.Windows.FrameworkTemplate", StringComparison.Ordinal)
            && !trace.Contains("PCL.ThemeControlRegistry", StringComparison.Ordinal)) return;
        args.Handled = true;
        RecoverVisualFailure(args.Exception);
    }
    private void ApplyBuiltInAfterError(Exception error)
    {
        Apply(null);
        _selection = new();
        try { _store.Write(_selection); }
        catch (IOException writeError) { ModBase.Log(writeError, "[Theme] Cannot save fallback state", ModBase.LogLevel.Debug); }
        Report(error, false);
    }
    private void Report(Exception error, bool dialog = true)
    {
        LastError = error.Message;
        ModBase.Log(error, "[Theme] " + error.Message, ModBase.LogLevel.Debug);
        if (dialog && !_closed) ModMain.MyMsgBox(Lang.Text("Theme.Pack.Failed", error.Message),
            Lang.Text("Theme.Pack.Title"), Lang.Text("Common.Action.Confirm"));
        Notify();
    }
    private void Notify()
    {
        foreach (EventHandler callback in Changed?.GetInvocationList() ?? [])
        {
            try { callback(this, EventArgs.Empty); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { ModBase.Log(error, "[Theme] Theme observer failed", ModBase.LogLevel.Debug); }
        }
    }
}
