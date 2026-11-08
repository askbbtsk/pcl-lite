using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCL.Plugin.SDK;

namespace PCL.Plugin.TestAll;

/// <summary>
/// Test plugin that exercises every host API (API v1: 12 interfaces / 69 members).
/// Each API has a button on the plugin page; results and errors are shown inline.
/// Buttons are created by PCL's themed host control factory. The "run all" button executes every non-interactive test sequentially.
/// </summary>
public sealed class TestAllPlugin : IPclPlugin
{
    private IPluginContext? _context;
    private readonly List<IDisposable> _subscriptions = [];
    private ObservableCollection<string>? _logLines;
    private TextBox? _logBox;
    private int _counter;

    // ---------------------------------------------------------------- lifecycle

    public Task StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        var log = context.Logger;

        // --- IPluginLogger: all five levels
        log.Trace("TestAll Trace: lifecycle start");
        log.Debug("TestAll Debug: lifecycle start");
        log.Info("TestAll Info: plugin starting");
        log.Warn("TestAll Warn: this is an intentional warning");
        log.Error("TestAll Error: this is an intentional (harmless) error", new InvalidOperationException("intentional"));

        // --- IPluginMetadata + context.DataDirectory
        var meta = context.Plugin;
        log.Info($"Metadata: id={meta.Id} name={meta.Name} version={meta.Version} author={meta.Author} apiVersion={meta.ApiVersion}");
        log.Info($"Description: {meta.Description}");
        log.Info($"DataDirectory: {context.DataDirectory}");

        // --- IPluginLauncher: host info properties
        var launcher = context.Launcher;
        log.Info($"Launcher: name={launcher.Name} version={launcher.Version} coreVersion={launcher.CoreVersion}");
        log.Info($"Launcher.DataDirectory={launcher.DataDirectory}");
        log.Info($"Launcher.PluginsDirectory={launcher.PluginsDirectory}");

        // --- IPluginEvents: subscribe to host game and application events
        _subscriptions.Add(context.Events.Subscribe<PluginGameLaunchingEvent>(OnGameLaunching));
        _subscriptions.Add(context.Events.Subscribe<PluginGameStartedEvent>(OnGameStarted));
        _subscriptions.Add(context.Events.Subscribe<PluginGameExitedEvent>(OnGameExited));
        _subscriptions.Add(context.Events.Subscribe<PluginThemeChangedEvent>((e, _) => { LogEvent($"Theme changed: {e.ThemeId}"); return Task.CompletedTask; }));
        _subscriptions.Add(context.Events.Subscribe<PluginInstanceListChangedEvent>((e, _) => { LogEvent($"Instance list changed: {e.GameDirectory}"); return Task.CompletedTask; }));
        _subscriptions.Add(context.Events.Subscribe<PluginPageNavigationEvent>((e, _) => { LogEvent($"Page navigation: {e.PreviousPageId} -> {e.CurrentPageId}"); return Task.CompletedTask; }));
        _subscriptions.Add(context.Events.Subscribe<PluginStateChangedEvent>((e, _) => { LogEvent($"Plugin state changed: {e.PluginId}, enabled={e.Enabled}, action={e.Action}"); return Task.CompletedTask; }));

        context.Ui.RegisterPage("testall", "API TestAll", CreateView, new PluginPageLifecycle(
            OnShown: _ => { Log("Page lifecycle: OnShown"); return Task.CompletedTask; },
            OnHidden: _ => { Log("Page lifecycle: OnHidden"); return Task.CompletedTask; }));
        log.Info("TestAll plugin started, page registered.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _context?.Logger.Info("TestAll plugin stopping; disposing event subscriptions.");
        foreach (var s in _subscriptions)
        {
            s.Dispose();
        }
        _subscriptions.Clear();
        _context = null;
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- event handlers

    private Task OnGameLaunching(PluginGameLaunchingEvent e, CancellationToken ct)
    {
        _context?.Logger.Info($"Event PluginGameLaunchingEvent: id={e.InstanceId} name={e.InstanceName}");
        AppendLog($"[Event] Launching: {e.InstanceName} ({e.InstanceId})");
        return Task.CompletedTask;
    }

    private Task OnGameStarted(PluginGameStartedEvent e, CancellationToken ct)
    {
        _context?.Logger.Info($"Event PluginGameStartedEvent: id={e.InstanceId} pid={e.ProcessId}");
        AppendLog($"[Event] Started: {e.InstanceName} PID {e.ProcessId}");
        return Task.CompletedTask;
    }

    private Task OnGameExited(PluginGameExitedEvent e, CancellationToken ct)
    {
        _context?.Logger.Info($"Event PluginGameExitedEvent: id={e.InstanceId} exit={e.ExitCode}");
        AppendLog($"[Event] Exited: {e.InstanceName} code {e.ExitCode}");
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- page UI

    private FrameworkElement CreateView()
    {
        _logLines = [];
        var root = new DockPanel { Margin = new Thickness(16), LastChildFill = true };

        var header = new TextBlock
        {
            Text = "PCL Lite 宿主 API 全量测试",
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 8)
        };
        header.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        _logBox = new TextBox
        {
            IsReadOnly = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(8, 6, 8, 6)
        };
        _logBox.SetResourceReference(TextBox.BackgroundProperty, "ColorBrushBackground");
        _logBox.SetResourceReference(TextBox.ForegroundProperty, "ColorBrush1");
        _logBox.SetResourceReference(TextBox.BorderBrushProperty, "ColorBrushGray6");
        root.Children.Add(_logBox);
        _logLines.CollectionChanged += (_, _) =>
        {
            _logBox.Text = string.Join(Environment.NewLine, _logLines);
            _logBox.ScrollToEnd();
        };

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var groups = new StackPanel();
        scroll.Content = groups;
        DockPanel.SetDock(scroll, Dock.Top);
        root.Children.Add(scroll);

        // --- 一键全测：串行执行所有无副作用测试
        groups.Children.Add(Group("全部测试",
            Btn("▶ 一键运行全部测试（不含弹窗/通知/启动游戏/打开浏览器）", RunAllTestsAsync)));

        // --- 生命周期 / 元信息
        groups.Children.Add(Group("生命周期 / 信息",
            Btn("输出 5 级日志 (Logger)", TestLoggerLevels),
            Btn("读取插件元信息 (Plugin)", TestMetadata),
            Btn("读取宿主信息 (Launcher 属性)", TestLauncherInfo),
            Btn("读取数据目录 (DataDirectory)", TestDataDirectory)));

        // --- Storage
        groups.Children.Add(Group("存储 (Storage)",
            Btn("WriteTextAsync / ReadTextAsync 往返", TestStorageText),
            Btn("WriteJsonAsync / ReadJsonAsync 往返", TestStorageJson),
            Btn("字节/流/Exists/List/Delete 全量往返", TestStorageBinaryAndStreams),
            Btn("读取不存在的文件 (应为 null)", TestStorageMissing)));

        // --- Settings
        groups.Children.Add(Group("设置 (Settings)",
            Btn("SetAsync / GetAsync / RemoveAsync 往返", TestSettingsRoundTrip),
            Btn("GetAsync 未设置的 key (应为 null)", TestSettingsMissing)));

        // --- UI
        groups.Children.Add(Group("界面 (UI)",
            Btn("ShowNotification 四种级别", TestNotifications),
            Btn("ShowNotification 点击动作", TestNotificationAction),
            Btn("ConfirmAsync 确认对话框", TestConfirm),
            Btn("ChooseAsync 默认本地化按钮", TestChooseDefault),
            Btn("ChooseAsync 自定义三按钮", TestChooseCustom),
            Btn("UnregisterPage 注册/注销探针", TestUnregisterPage)));

        // --- Events
        groups.Children.Add(Group("事件 (Events)",
            Btn("PublishAsync 发布测试事件 (自订阅)", TestEventPublish),
            Btn("显示新事件订阅状态", TestNewEventSubscriptions)));

        // --- Network
        groups.Children.Add(Group("网络 (Network)",
            Btn("Network.GetAsync 请求 example.com", TestNetworkGet),
            Btn("GetAsync 自定义请求头/超时", TestNetworkGetHeaders),
            Btn("Network.SendAsync POST httpbin.org/post", TestNetworkPost),
            Btn("SendAsync 自定义请求头/超时", TestNetworkSendHeaders),
            Btn("Network.GetAsync 二进制响应 (ContentBytes)", TestNetworkBinary)));

        // --- Minecraft
        groups.Children.Add(Group("Minecraft",
            Btn("GetInstancesAsync 查询实例列表", TestMinecraftInstances),
            Btn("GetSelectedInstanceAsync 当前实例", TestMinecraftSelectedInstance),
            Btn("GetInstanceConfigurationAsync 实例配置", TestMinecraftInstanceConfiguration),
            Btn("LaunchAsync 启动选中的实例 (需确认)", TestMinecraftLaunch),
            Btn("LaunchAsync PluginLaunchOptions (需确认)", TestMinecraftLaunchWithOptions)));

        // --- Launcher actions
        groups.Children.Add(Group("启动器动作 (Launcher)",
            Btn("DownloadAsync 下载小文件到数据目录", TestLauncherDownload),
            Btn("DownloadAsync SHA-256 校验下载", TestLauncherDownloadSha256),
            Btn("IPluginServices 注册/获取/注销", TestServices),
            Btn("OpenUrl 打开 https://example.com", TestOpenUrl),
            Btn("OpenPath 打开插件数据目录", TestOpenPath)));

        return root;
    }

    // ---------------------------------------------------------------- run all

    /// <summary>Sequentially runs every non-interactive test and prints a summary.</summary>
    private Task RunAllTestsAsync()
    {
        async Task RunAsync()
        {
            Log("===== 一键全部测试开始（不含弹窗/通知/启动游戏/打开浏览器）=====");
            var failures = 0;
            var total = 0;

            async Task CheckAsync(string name, Func<Task> test)
            {
                total++;
                var before = _logLines?.Count ?? 0;
                try
                {
                    await test();
                }
                catch (Exception ex)
                {
                    failures++;
                    Fail(name, ex);
                    return;
                }
                var after = _logLines?.Count ?? 0;
                // 若测试方法自己没输出结果行，补一行 OK，保证每项都有结论
                if (after == before) Log($"[OK] {name}");
            }

            await CheckAsync("Logger 5 级日志", TestLoggerLevels);
            await CheckAsync("Plugin 元信息", TestMetadata);
            await CheckAsync("Launcher 宿主信息", TestLauncherInfo);
            await CheckAsync("DataDirectory", TestDataDirectory);
            await CheckAsync("Storage 文本往返", TestStorageText);
            await CheckAsync("Storage JSON 往返", TestStorageJson);
            await CheckAsync("Storage 字节/流/Exists/List/Delete", TestStorageBinaryAndStreams);
            await CheckAsync("Storage 缺失文件", TestStorageMissing);
            await CheckAsync("Settings 往返", TestSettingsRoundTrip);
            await CheckAsync("Settings 缺失 key", TestSettingsMissing);
            await CheckAsync("Events 发布/订阅回路", TestEventPublish);
            await CheckAsync("Network.GetAsync", TestNetworkGet);
            await CheckAsync("Network.GetAsync headers/timeout", TestNetworkGetHeaders);
            await CheckAsync("Network.SendAsync POST", TestNetworkPost);
            await CheckAsync("Network.SendAsync headers/timeout", TestNetworkSendHeaders);
            await CheckAsync("Network 二进制响应", TestNetworkBinary);
            await CheckAsync("Minecraft.GetInstancesAsync", TestMinecraftInstances);
            await CheckAsync("Minecraft.GetSelectedInstanceAsync", TestMinecraftSelectedInstance);
            await CheckAsync("Minecraft.GetInstanceConfigurationAsync", TestMinecraftInstanceConfiguration);
            await CheckAsync("Launcher.DownloadAsync", TestLauncherDownload);
            await CheckAsync("Launcher.DownloadAsync SHA-256", TestLauncherDownloadSha256);
            await CheckAsync("IPluginServices", TestServices);

            var summary = failures == 0
                ? $"===== 全部测试完成：{total}/{total} 项无异常 ====="
                : $"===== 全部测试完成：{total - failures}/{total} 项无异常，{failures} 项异常（见上方 [FAIL]）=====";
            Log(summary);
            Ctx().Ui.ShowNotification(summary, failures == 0 ? PluginNotificationKind.Success : PluginNotificationKind.Warning);
        }

        return RunOnPage(RunAsync());
    }

    /// <summary>Runs an async test from a UI event handler, capturing faults on the page log.</summary>
    private async Task RunOnPage(Task test)
    {
        try
        {
            await test;
        }
        catch (Exception ex)
        {
            Fail("按钮测试", ex);
        }
    }

    // ---------------------------------------------------------------- test implementations

    private Task TestLoggerLevels()
    {
        var log = Ctx().Logger;
        var n = Interlocked.Increment(ref _counter);
        log.Trace($"TestAll Trace #{n}");
        log.Debug($"TestAll Debug #{n}");
        log.Info($"TestAll Info #{n}");
        log.Warn($"TestAll Warn #{n}");
        log.Error($"TestAll Error #{n}", new InvalidOperationException("intentional exception"));
        Log("Logger: 5 个等级已输出，检查启动器日志文件确认分级记录。");
        return Task.CompletedTask;
    }

    private Task TestMetadata()
    {
        var m = Ctx().Plugin;
        Log($"Plugin: id={m.Id} | name={m.Name} | version={m.Version} | author={m.Author} | apiVersion={m.ApiVersion}\n  description={m.Description}");
        return Task.CompletedTask;
    }

    private Task TestLauncherInfo()
    {
        var l = Ctx().Launcher;
        Log($"Launcher: name={l.Name} | version={l.Version} | coreVersion={l.CoreVersion}\n  DataDirectory={l.DataDirectory}\n  PluginsDirectory={l.PluginsDirectory}");
        return Task.CompletedTask;
    }

    private Task TestDataDirectory()
    {
        var dir = Ctx().DataDirectory;
        var exists = Directory.Exists(dir);
        Log($"DataDirectory={dir} | Exists={exists}");
        return Task.CompletedTask;
    }

    private sealed record TestData(int Id, string Name, string[] Tags, double Score);

    private async Task TestStorageText()
    {
        var storage = Ctx().Storage;
        const string path = "test/text-roundtrip.txt";
        const string payload = "TestAll 文本往返 ✓ ascii+中文+emoji 🎮";
        await storage.WriteTextAsync(path, payload);
        var read = await storage.ReadTextAsync(path);
        Log(read == payload
            ? $"Storage text roundtrip OK: \"{payload}\""
            : $"Storage text MISMATCH! wrote=\"{payload}\" read=\"{read}\"");
    }

    private async Task TestStorageJson()
    {
        var storage = Ctx().Storage;
        const string path = "test/data.json";
        var payload = new TestData(42, "测试实例", ["vanilla", "fabric"], 3.14);
        await storage.WriteJsonAsync(path, payload);
        var read = await storage.ReadJsonAsync<TestData>(path);
        Log(read is not null && read.Id == payload.Id && read.Name == payload.Name
                && read.Tags.SequenceEqual(payload.Tags) && Math.Abs(read.Score - payload.Score) < 1e-9
            ? $"Storage JSON roundtrip OK: {JsonSerializer.Serialize(read)}"
            : $"Storage JSON MISMATCH! read={JsonSerializer.Serialize(read ?? new TestData(-1, "?", [], 0))}");
    }

    private async Task TestStorageMissing()
    {
        var read = await Ctx().Storage.ReadTextAsync("test/does-not-exist.txt");
        Log(read is null ? "Storage: 不存在文件返回 null，符合预期。" : $"Storage: 意外读到内容 \"{read}\"");
    }

    private async Task TestStorageBinaryAndStreams()
    {
        var storage = Ctx().Storage;
        const string bytesPath = "test/binary-roundtrip.bin";
        const string streamPath = "test/stream-roundtrip.bin";
        var payload = Encoding.UTF8.GetBytes("TestAll binary ✓ 🎮");
        await storage.WriteBytesAsync(bytesPath, payload);
        var readBytes = await storage.ReadBytesAsync(bytesPath);
        var bytesOk = readBytes is not null && readBytes.SequenceEqual(payload);

        await using (var source = new MemoryStream(payload, writable: false))
            await storage.WriteStreamAsync(streamPath, source, payload.Length);
        byte[] readStream;
        await using (var input = await storage.OpenReadAsync(streamPath) ?? throw new InvalidDataException("stream missing"))
        await using (var copy = new MemoryStream())
        {
            await input.CopyToAsync(copy);
            readStream = copy.ToArray();
        }
        var streamOk = readStream.SequenceEqual(payload);
        var existsBefore = await storage.ExistsAsync(streamPath);
        var entries = await storage.ListAsync("test");
        var listedBothFiles = entries.Contains(Path.GetFileName(bytesPath)) && entries.Contains(Path.GetFileName(streamPath));
        await storage.DeleteAsync(streamPath);
        await storage.DeleteAsync(bytesPath);
        var existsAfter = await storage.ExistsAsync(streamPath);
        var bytesExistsAfter = await storage.ExistsAsync(bytesPath);
        Log($"Storage binary={bytesOk}, stream={streamOk}, Exists before/after={existsBefore}/{existsAfter}, List found both={listedBothFiles}, Delete removed both={!existsAfter && !bytesExistsAfter}");
        Require(bytesOk && streamOk && existsBefore && listedBothFiles && !existsAfter && !bytesExistsAfter,
            "Storage binary/stream/Exists/List/Delete roundtrip failed.");
    }

    private async Task TestSettingsRoundTrip()
    {
        var settings = Ctx().Settings;
        const string key = "test-roundtrip";
        await settings.SetAsync(key, _counter + 1);
        var got = await settings.GetAsync<int>(key);
        await settings.RemoveAsync(key);
        var afterRemove = await settings.GetAsync<int?>(key);
        Log(got == _counter + 1 && afterRemove is null
            ? $"Settings roundtrip OK: set={_counter + 1}, get={got}, after remove=null"
            : $"Settings MISMATCH! set={_counter + 1} get={got} afterRemove={afterRemove}");
    }

    private async Task TestSettingsMissing()
    {
        var got = await Ctx().Settings.GetAsync<string>("test-never-set");
        Log(got is null ? "Settings: 未设置 key 返回 null，符合预期。" : $"Settings: 意外读到 \"{got}\"");
    }

    private Task TestNotifications()
    {
        var ui = Ctx().Ui;
        ui.ShowNotification("TestAll: Info 通知", PluginNotificationKind.Info);
        ui.ShowNotification("TestAll: Success 通知", PluginNotificationKind.Success);
        ui.ShowNotification("TestAll: Warning 通知", PluginNotificationKind.Warning);
        ui.ShowNotification("TestAll: Error 通知", PluginNotificationKind.Error);
        Log("UI: 4 种通知已发送。");
        return Task.CompletedTask;
    }

    private Task TestNotificationAction()
    {
        Ctx().Ui.ShowNotification("TestAll：点击此通知验证动作回调", PluginNotificationKind.Info, () =>
        {
            Log("UI notification click action: callback invoked");
            return Task.CompletedTask;
        });
        Log("UI: 已发送带点击动作的通知，请点击通知条验证回调。");
        return Task.CompletedTask;
    }

    private async Task TestConfirm()
    {
        var result = await Ctx().Ui.ConfirmAsync("API TestAll",
            "这是 ConfirmAsync 测试。请选择「确定」或「取消」，结果会记录在日志里。");
        Log($"UI.ConfirmAsync 返回: {result}");
    }

    private async Task TestChooseDefault()
    {
        var result = await Ctx().Ui.ChooseAsync("API TestAll", "默认按钮文案由宿主当前语言提供。",
            new PluginDialogOptions());
        Log($"UI.ChooseAsync 默认按钮返回索引: {result}");
    }

    private async Task TestChooseCustom()
    {
        var result = await Ctx().Ui.ChooseAsync("API TestAll", "测试自定义三按钮文案。",
            new PluginDialogOptions("继续", "取消", "稍后"));
        Log($"UI.ChooseAsync 自定义按钮返回索引: {result}");
    }

    private Task TestUnregisterPage()
    {
        var ui = Ctx().Ui;
        const string id = "testall-unregister-probe";
        ui.RegisterPage(id, "TestAll temporary page", () => new TextBlock { Text = "temporary" });
        var removed = ui.UnregisterPage(id);
        Log($"UI.UnregisterPage: 注册后注销结果={removed}");
        Require(removed, "UI.UnregisterPage did not remove the registered probe page.");
        return Task.CompletedTask;
    }

    private sealed record TestPublishedEvent(string Message, int Seq);

    private async Task TestEventPublish()
    {
        // 自订阅同一记录类型再发布，验证 Subscribe/PublishAsync 回路
        var seen = new TaskCompletionSource<TestPublishedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = Ctx().Events.Subscribe<TestPublishedEvent>((e, _) =>
        {
            seen.TrySetResult(e);
            return Task.CompletedTask;
        });
        var payload = new TestPublishedEvent("hello from TestAll", _counter + 1);
        await Ctx().Events.PublishAsync(payload);
        var done = await Task.WhenAny(seen.Task, Task.Delay(3000));
        Log(done == seen.Task
            ? $"Events: PublishAsync→Subscribe 回路 OK，收到 \"{seen.Task.Result.Message}\" seq={seen.Task.Result.Seq}"
            : "Events: 3 秒内未收到自发布事件（宿主可能只向注册插件分发）");
    }

    private async Task TestNewEventSubscriptions()
    {
        var events = Ctx().Events;
        var seen = new List<string>();
        using var a = events.Subscribe<PluginThemeChangedEvent>((e, _) => { seen.Add($"theme:{e.ThemeId}"); return Task.CompletedTask; });
        using var b = events.Subscribe<PluginInstanceListChangedEvent>((e, _) => { seen.Add($"instances:{e.GameDirectory}"); return Task.CompletedTask; });
        using var c = events.Subscribe<PluginPageNavigationEvent>((e, _) => { seen.Add($"navigation:{e.CurrentPageId}"); return Task.CompletedTask; });
        using var d = events.Subscribe<PluginStateChangedEvent>((e, _) => { seen.Add($"state:{e.Action}"); return Task.CompletedTask; });
        await events.PublishAsync(new PluginThemeChangedEvent("testall-theme"));
        await events.PublishAsync(new PluginInstanceListChangedEvent(Ctx().DataDirectory));
        await events.PublishAsync(new PluginPageNavigationEvent("previous", "testall"));
        await events.PublishAsync(new PluginStateChangedEvent(Ctx().Plugin.Id, true, "test"));
        Log($"Events: 4 个新增事件均可订阅/发布，收到 {seen.Count} 次 ({string.Join(", ", seen)})");
        Require(seen.Count == 4, "Not all four new plugin events were delivered.");
    }

    private async Task TestNetworkGet()
    {
        var sw = Stopwatch.StartNew();
        var resp = await Ctx().Network.GetAsync(new Uri("https://example.com/"));
        sw.Stop();
        Log($"Network.GetAsync: status={resp.StatusCode} bytes={resp.ContentBytes.Length} contentType={resp.ContentType} elapsed={sw.ElapsedMilliseconds}ms\n  body[:120]={Truncate(resp.Content, 120)}");
    }

    private async Task TestNetworkGetHeaders()
    {
        var headers = new Dictionary<string, string> { ["X-PCL-TestAll"] = "headers" };
        var response = await Ctx().Network.GetAsync(new Uri("https://httpbin.org/headers"), headers,
            TimeSpan.FromSeconds(20));
        Log($"Network.GetAsync headers/timeout: status={response.StatusCode}, bytes={response.ContentBytes.Length}, echoed={response.Content.Contains("X-PCL-TestAll", StringComparison.Ordinal)}");
    }

    private async Task TestNetworkPost()
    {
        var sw = Stopwatch.StartNew();
        var resp = await Ctx().Network.SendAsync(HttpMethod.Post,
            new Uri("https://httpbin.org/post"),
            content: """{"from":"PCL.Plugin.TestAll","n":1}""",
            contentType: "application/json");
        sw.Stop();
        var echoed = resp.Content.Contains("PCL.Plugin.TestAll", StringComparison.Ordinal);
        Log($"Network.SendAsync POST: status={resp.StatusCode} bytes={resp.ContentBytes.Length} elapsed={sw.ElapsedMilliseconds}ms echoFound={echoed}");
    }

    private async Task TestNetworkSendHeaders()
    {
        var headers = new Dictionary<string, string> { ["X-PCL-TestAll"] = "send-headers" };
        var response = await Ctx().Network.SendAsync(HttpMethod.Post, new Uri("https://httpbin.org/post"),
            "{\"from\":\"TestAll headers\"}", "application/json", headers, TimeSpan.FromSeconds(20));
        Log($"Network.SendAsync headers/timeout: status={response.StatusCode}, bytes={response.ContentBytes.Length}, echoed={response.Content.Contains("send-headers", StringComparison.Ordinal)}");
    }

    private async Task TestNetworkBinary()
    {
        // 小而稳定的 HTTPS 二进制响应，验证 ContentBytes
        var resp = await Ctx().Network.GetAsync(new Uri("https://www.google.com/favicon.ico"));
        var bytes = resp.ContentBytes;
        Log($"Network binary: status={resp.StatusCode} contentType={resp.ContentType} bytes={bytes.Length} firstBytes={BitConverter.ToString(bytes.Span[..Math.Min(8, bytes.Length)].ToArray())}");
    }

    private async Task TestMinecraftInstances()
    {
        var instances = await Ctx().Minecraft.GetInstancesAsync();
        if (instances.Count == 0)
        {
            Log("Minecraft.GetInstancesAsync: 0 个实例（宿主尚未扫描到任何版本目录）。");
            return;
        }
        var sb = new StringBuilder("Minecraft instances:\n");
        foreach (var i in instances)
        {
            sb.AppendLine($"  [{(i.IsSelected ? "x" : " ")}] id={i.Id} name={i.Name} version={i.Version} dir={i.GameDirectory}");
        }
        Log(sb.ToString());
    }

    private async Task TestMinecraftSelectedInstance()
    {
        var selected = await Ctx().Minecraft.GetSelectedInstanceAsync();
        Log(selected is null
            ? "Minecraft.GetSelectedInstanceAsync: 当前没有选中实例。"
            : $"Minecraft.GetSelectedInstanceAsync: {selected.Id} / {selected.Name} / {selected.Version}");
    }

    private async Task TestMinecraftInstanceConfiguration()
    {
        var ctx = Ctx();
        var selected = await ctx.Minecraft.GetSelectedInstanceAsync();
        if (selected is null)
        {
            Log("Minecraft.GetInstanceConfigurationAsync: 当前没有实例，跳过。");
            return;
        }
        var config = await ctx.Minecraft.GetInstanceConfigurationAsync(selected.Id);
        Log($"Minecraft.GetInstanceConfigurationAsync: {config.Count} 项 ({string.Join(", ", config.Select(kv => $"{kv.Key}={kv.Value}"))})");
    }

    private async Task TestMinecraftLaunch()
    {
        var ctx = Ctx();
        var instances = await ctx.Minecraft.GetInstancesAsync();
        var target = instances.FirstOrDefault(i => i.IsSelected) ?? instances.FirstOrDefault();
        if (target is null)
        {
            Log("Minecraft.LaunchAsync: 没有可用实例，跳过。");
            return;
        }
        var confirmed = await ctx.Ui.ConfirmAsync("API TestAll", $"通过 LaunchAsync 启动实例「{target.Name}」？");
        if (!confirmed)
        {
            Log("Minecraft.LaunchAsync: 用户取消。");
            return;
        }
        var ok = await ctx.Minecraft.LaunchAsync(target.Id);
        Log($"Minecraft.LaunchAsync({target.Id}) 返回: {ok}");
    }

    private async Task TestMinecraftLaunchWithOptions()
    {
        var ctx = Ctx();
        var selected = await ctx.Minecraft.GetSelectedInstanceAsync();
        if (selected is null)
        {
            Log("Minecraft.LaunchAsync(options): 没有选中实例，跳过。");
            return;
        }
        if (!await ctx.Ui.ConfirmAsync("API TestAll", $"通过带 PluginLaunchOptions 的 API 启动「{selected.Name}」？"))
        {
            Log("Minecraft.LaunchAsync(options): 用户取消。");
            return;
        }
        var options = new PluginLaunchOptions(["-Dtestall.plugin=true"],
            new Dictionary<string, string> { ["PCL_TESTALL"] = "1" });
        var ok = await ctx.Minecraft.LaunchAsync(selected.Id, options);
        Log($"Minecraft.LaunchAsync({selected.Id}, PluginLaunchOptions) 返回: {ok}");
    }

    private async Task TestLauncherDownload()
    {
        var ctx = Ctx();
        const string rel = "test/downloaded.json";
        var progressReports = 0;
        var progress = new Progress<PluginDownloadProgress>(p => Interlocked.Increment(ref progressReports));
        await ctx.Launcher.DownloadAsync(new Uri("https://httpbin.org/json"), rel, progress);
        var read = await ctx.Storage.ReadTextAsync(rel);
        Log($"Launcher.DownloadAsync OK: {rel} ({read?.Length ?? 0} chars, progress 回调 {progressReports} 次)\n  content[:120]={Truncate(read ?? "", 120)}");
    }

    private async Task TestLauncherDownloadSha256()
    {
        var ctx = Ctx();
        var uri = new Uri("https://httpbin.org/json");
        var expected = Convert.ToHexString(SHA256.HashData((await ctx.Network.GetAsync(uri)).ContentBytes.Span));
        const string rel = "test/downloaded-sha256.json";
        await ctx.Launcher.DownloadAsync(uri, rel, expected);
        var bytes = await ctx.Storage.ReadBytesAsync(rel);
        var actual = bytes is null ? "missing" : Convert.ToHexString(SHA256.HashData(bytes));
        var matches = string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
        Log($"Launcher.DownloadAsync SHA-256: expected={expected}, actual={actual}, match={matches}");
        Require(matches, "SHA-256 download contents do not match the expected hash.");
    }

    private sealed class TestService(string value)
    {
        public string Value { get; } = value;
    }

    private Task TestServices()
    {
        var service = new TestService("TestAll");
        var services = Ctx().Services;
        var registered = services.Register(service);
        var fetched = services.Get<TestService>();
        var unregistered = services.Unregister<TestService>();
        var after = services.Get<TestService>();
        Log($"IPluginServices: register={registered}, get={fetched?.Value}, unregister={unregistered}, after={(after is null ? "null" : "present")}");
        Require(registered && ReferenceEquals(service, fetched) && unregistered && after is null,
            "IPluginServices register/get/unregister roundtrip failed.");
        return Task.CompletedTask;
    }

    private Task TestOpenUrl()
    {
        Ctx().Launcher.OpenUrl(new Uri("https://example.com/"));
        Log("Launcher.OpenUrl: 已请求打开 https://example.com/");
        return Task.CompletedTask;
    }

    private Task TestOpenPath()
    {
        var dir = Ctx().DataDirectory;
        Ctx().Launcher.OpenPath(dir);
        Log($"Launcher.OpenPath: 已请求打开 {dir}");
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- helpers

    private IPluginContext Ctx() => _context ?? throw new InvalidOperationException("插件尚未启动");

    private void Log(string message)
    {
        Ctx().Logger.Info($"[TestAll-Page] {message.ReplaceLineEndings(" | ")}");
        AppendLog(message);
    }

    private void Fail(string api, Exception ex)
    {
        Ctx().Logger.Error($"[TestAll-Page] {api} failed", ex);
        AppendLog($"[FAIL] {api}: {ex.GetType().Name}: {ex.Message}");
    }

    private void AppendLog(string message)
    {
        if (_logLines is null) return;
        _logLines.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    private void LogEvent(string message)
    {
        _context?.Logger.Info($"[TestAll-Event] {message}");
        AppendLog($"[Event] {message}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static UIElement Group(string title, params UIElement[] buttons)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var titleBlock = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray2");
        panel.Children.Add(titleBlock);
        var wrap = new WrapPanel();
        foreach (var b in buttons) wrap.Children.Add(b);
        panel.Children.Add(wrap);
        return panel;
    }

    private FrameworkElement Btn(string label, Func<Task> onClick)
    {
        return Ctx().Ui.CreateButton(label, async () =>
        {
            try
            {
                await onClick();
            }
            catch (Exception ex)
            {
                Fail("按钮测试", ex);
            }
        });
    }
}
