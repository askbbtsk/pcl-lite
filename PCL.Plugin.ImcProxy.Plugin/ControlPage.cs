using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCL.Plugin.SDK;

namespace PCL.Plugin.ImcProxy;

public sealed class ControlPage
{
    private readonly IPluginContext _context;
    private readonly ImcProxyRuntime _runtime;
    private readonly ImcProxyApi _api;

    private readonly TextBlock _status = new() { Text = "正在读取状态…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _hint = new() { Text = "", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Opacity = 0.75 };
    private readonly StackPanel _settings = new();
    private bool _loaded;
    private TextBox? _configEditor;
    private readonly TextBlock _versionInfo = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8), Opacity = 0.85 };
    private readonly TextBox _changelogBox = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        Height = 180,
        FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
        FontSize = 12,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        TextWrapping = TextWrapping.NoWrap,
        Margin = new Thickness(0, 4, 0, 0),
    };

    public ControlPage(IPluginContext context, ImcProxyRuntime runtime, ImcProxyApi api)
    {
        _context = context;
        _runtime = runtime;
        _api = api;
    }

    public FrameworkElement CreateView()
    {
        var root = new StackPanel { Margin = new Thickness(24) };

        root.Children.Add(new TextBlock
        {
            Text = "IMCProxy",
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
        });
        root.Children.Add(new TextBlock
        {
            Text = "从 GitHub Release 下载 IMCProxy-NextGen-Free（C++ 版），在后台静默运行，通过 HTTP API 控制。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 16),
            Opacity = 0.8,
        });

        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        buttons.Children.Add(WithMargin(_context.Ui.CreateButton("下载 / 更新", DownloadAsync, PluginButtonStyle.Highlight)));
        buttons.Children.Add(WithMargin(_context.Ui.CreateButton("启动代理", StartAsync)));
        buttons.Children.Add(WithMargin(_context.Ui.CreateButton("停止代理", StopAsync, PluginButtonStyle.Danger)));
        buttons.Children.Add(WithMargin(_context.Ui.CreateButton("刷新状态", RefreshAsync)));
        buttons.Children.Add(WithMargin(_context.Ui.CreateButton("打开安装目录", OpenFolderAsync)));
        root.Children.Add(buttons);

        root.Children.Add(new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Child = _status,
        });
        root.Children.Add(_hint);

        root.Children.Add(new TextBlock
        {
            Text = "IMCProxy-NextGen-Free 版本与更新",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 16, 0, 4),
        });
        root.Children.Add(new TextBlock
        {
            Text = "检测的是被管理的代理版本，与插件自身版本无关。",
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        });
        root.Children.Add(_versionInfo);
        var versionRow = new WrapPanel();
        versionRow.Children.Add(WithMargin(_context.Ui.CreateButton("检查更新", CheckUpdatesAsync)));
        versionRow.Children.Add(WithMargin(_context.Ui.CreateButton("加载更新日志", LoadChangelogAsync)));
        root.Children.Add(versionRow);
        root.Children.Add(_changelogBox);

        root.Children.Add(new TextBlock
        {
            Text = "配置",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 16, 0, 4),
        });
        root.Children.Add(new TextBlock
        {
            Text = "config.json、账号文件与 logs/ 都保存在安装目录。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Margin = new Thickness(0, 0, 0, 8),
        });
        var folderRow = new WrapPanel();
        folderRow.Children.Add(WithMargin(_context.Ui.CreateButton("打开文件夹", OpenFolderAsync)));
        folderRow.Children.Add(WithMargin(_context.Ui.CreateButton("重新载入", ReloadConfigAsync)));
        folderRow.Children.Add(WithMargin(_context.Ui.CreateButton("保存 config.json", SaveConfigAsync)));
        folderRow.Children.Add(WithMargin(_context.Ui.CreateButton("保存并重启", SaveAndRestartAsync, PluginButtonStyle.Highlight)));
        root.Children.Add(folderRow);

        var configEditor = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            Height = 200,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(0, 0, 0, 6),
        };
        root.Children.Add(configEditor);
        _configEditor = configEditor;

        root.Children.Add(new TextBlock
        {
            Text = "修改端口、上游等需要重启代理生效。",
            Opacity = 0.7,
            Margin = new Thickness(0, 0, 0, 8),
        });

        root.Children.Add(new TextBlock
        {
            Text = "逐项配置",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 20, 0, 8),
        });

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 420,
            Content = _settings,
        };
        root.Children.Add(scroll);

        root.Loaded += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            await LoadConfigTextAsync();
            await RefreshAsync();
        };

        return root;
    }

    private static FrameworkElement WithMargin(FrameworkElement element)
    {
        element.Margin = new Thickness(0, 0, 8, 8);
        return element;
    }

    private async Task DownloadAsync()
    {
        var confirmed = await _context.Ui.ConfirmAsync("下载 IMCProxy", "将从 GitHub Release 下载最新的 IMCProxy-NextGen-Free 并覆盖安装目录（保留 config.json 与账号文件），继续？");
        if (!confirmed) return;

        var progress = new Progress<PluginDownloadProgress>(p =>
        {
            var text = p.TotalBytes is > 0
                ? $"下载中 {p.BytesReceived / 1024.0 / 1024.0:F1} / {p.TotalBytes.Value / 1024.0 / 1024.0:F1} MB"
                : $"下载中 {p.BytesReceived / 1024.0 / 1024.0:F1} MB";
            _hint.Text = text;
        });

        try
        {
            _hint.Text = "正在读取最新 release…";
            await _runtime.DownloadAsync(progress, CancellationToken.None);
            _hint.Text = "已安装 " + _runtime.InstalledTag + " 到 " + _runtime.InstallDirectory;
            _context.Ui.ShowNotification("IMCProxy 下载完成：" + _runtime.InstalledTag, PluginNotificationKind.Success);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _hint.Text = "下载失败：" + ex.Message;
            _context.Ui.ShowNotification("下载失败", PluginNotificationKind.Error);
            _context.Logger.Error("download failed", ex);
        }
    }

    private async Task StartAsync()
    {
        try
        {
            await _runtime.StartAsync(CancellationToken.None);
            await Task.Delay(1200);
            await RefreshAsync();
            _context.Ui.ShowNotification("已启动代理", PluginNotificationKind.Success);
        }
        catch (Exception ex)
        {
            _hint.Text = "启动失败：" + ex.Message;
            _context.Ui.ShowNotification("启动失败", PluginNotificationKind.Error);
            _context.Logger.Error("start failed", ex);
        }
    }

    private async Task StopAsync()
    {
        await _runtime.StopAsync(CancellationToken.None);
        await RefreshAsync();
        _context.Ui.ShowNotification("已停止代理", PluginNotificationKind.Info);
    }

    private Task OpenFolderAsync()
    {
        if (!_runtime.HasInstallation())
        {
            _context.Ui.ShowNotification("尚未下载，安装目录不存在", PluginNotificationKind.Warning);
            return Task.CompletedTask;
        }
        _runtime.OpenInstallFolder();
        return Task.CompletedTask;
    }

    private bool TrySyncApiBaseFromConfig()
    {
        try
        {
            var configFile = _runtime.ConfigPath;
            if (!File.Exists(configFile)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(configFile));
            if (!document.RootElement.TryGetProperty("api", out var api)) return false;
            var host = api.TryGetProperty("host", out var hostNode) ? hostNode.GetString() ?? "127.0.0.1" : "127.0.0.1";
            if (host == "0.0.0.0") host = "127.0.0.1";
            var port = api.TryGetProperty("port", out var portNode) ? portNode.GetInt32() : 8790;
            var token = api.TryGetProperty("token", out var tokenNode) ? tokenNode.GetString() ?? "" : "";
            _api.BaseUrl = $"http://{host}:{port}/api";
            _api.Token = token;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task ReloadConfigAsync()
    {
        await LoadConfigTextAsync();
        _context.Ui.ShowNotification("已重新载入 config.json", PluginNotificationKind.Info);
    }

    private async Task LoadConfigTextAsync()
    {
        if (_configEditor is null) return;
        try
        {
            if (!File.Exists(_runtime.ConfigPath))
            {
                _configEditor.Text = "{}";
                _context.Ui.ShowNotification("config.json 尚未生成，先启动一次代理", PluginNotificationKind.Info);
                return;
            }
            _configEditor.Text = await File.ReadAllTextAsync(_runtime.ConfigPath);
        }
        catch (Exception ex)
        {
            _context.Ui.ShowNotification("读取失败：" + ex.Message, PluginNotificationKind.Error);
        }
    }

    private async Task SaveConfigAsync()
    {
        if (_configEditor is null) return;
        var content = _configEditor.Text;
        try
        {
            using var _ = JsonDocument.Parse(content);
        }
        catch (Exception ex)
        {
            _context.Ui.ShowNotification("JSON 格式错误，未保存：" + ex.Message, PluginNotificationKind.Warning);
            return;
        }
        try
        {
            Directory.CreateDirectory(_runtime.InstallDirectory);
            await File.WriteAllTextAsync(_runtime.ConfigPath, content);
            _context.Ui.ShowNotification("config.json 已保存", PluginNotificationKind.Success);
        }
        catch (Exception ex)
        {
            _context.Ui.ShowNotification("保存失败：" + ex.Message, PluginNotificationKind.Error);
        }
    }

    private async Task SaveAndRestartAsync()
    {
        await SaveConfigAsync();
        try
        {
            await _runtime.StopAsync(CancellationToken.None);
            await _runtime.StartAsync(CancellationToken.None);
            await Task.Delay(1200);
            await RefreshAsync();
            _context.Ui.ShowNotification("已保存并重启代理", PluginNotificationKind.Success);
        }
        catch (Exception ex)
        {
            _context.Ui.ShowNotification("重启失败：" + ex.Message, PluginNotificationKind.Error);
        }
    }

    private async Task CheckUpdatesAsync()
    {
        var local = _runtime.InstalledTag;
        try
        {
            var latest = await _runtime.GetLatestReleaseAsync(CancellationToken.None);
            var hasUpdate = !string.Equals(latest.Tag, local, StringComparison.OrdinalIgnoreCase);
            var state = string.IsNullOrWhiteSpace(local)
                ? "尚未安装，可点「下载 / 更新」"
                : hasUpdate ? "有更新，可点「下载 / 更新」" : "已是最新";
            _versionInfo.Text =
                "本地：" + (string.IsNullOrWhiteSpace(local) ? "未安装" : local) +
                (string.IsNullOrWhiteSpace(_runtime.InstalledAt) ? "" : "（" + _runtime.InstalledAt + "）") +
                Environment.NewLine +
                "远程：" + latest.Tag + "    " + latest.AssetName + Environment.NewLine +
                state;
            _context.Ui.ShowNotification(state, hasUpdate ? PluginNotificationKind.Warning : PluginNotificationKind.Success);
        }
        catch (Exception ex)
        {
            _versionInfo.Text = "检查失败：" + ex.Message;
            _context.Ui.ShowNotification("更新检查失败", PluginNotificationKind.Warning);
        }
    }

    private async Task LoadChangelogAsync()
    {
        try
        {
            var node = await _api.GetAsync("changelog");
            if (node["entries"] is JsonArray entries)
            {
                var builder = new StringBuilder();
                foreach (var entry in entries)
                {
                    if (entry is not JsonObject item) continue;
                    builder.AppendLine("v" + (item["version"]?.GetValue<string>() ?? "") + " (" + (item["date"]?.GetValue<string>() ?? "") + ")");
                    if (item["changes"] is JsonArray changes)
                    {
                        foreach (var change in changes) builder.AppendLine("  - " + change?.GetValue<string>());
                    }
                    builder.AppendLine();
                }
                _changelogBox.Text = builder.ToString().TrimEnd();
                _context.Ui.ShowNotification("已从代理接口读取更新日志", PluginNotificationKind.Success);
                return;
            }
        }
        catch
        {
        }

        try
        {
            var latest = await _runtime.GetLatestReleaseAsync(CancellationToken.None);
            _changelogBox.Text = string.IsNullOrWhiteSpace(latest.Notes)
                ? "该 release 没有填写更新日志"
                : latest.Notes;
            _context.Ui.ShowNotification("已从 release 说明读取更新日志", PluginNotificationKind.Info);
        }
        catch (Exception ex)
        {
            _changelogBox.Text = "读取失败：" + ex.Message;
            _context.Ui.ShowNotification("更新日志读取失败", PluginNotificationKind.Warning);
        }
    }

    private async Task RefreshAsync()
    {
        TrySyncApiBaseFromConfig();
        try
        {
            var status = await _api.StatusAsync();
            var lines = new List<string>
            {
                "运行状态：" + (status["running"]?.GetValue<bool>() == true ? "运行中" : "未运行"),
                "1.8.9 监听：" + (status["listen"]?["v189"]?.GetValue<string>() ?? "-"),
                "1.12.2 监听：" + (status["listen"]?["v1122"]?.GetValue<string>() ?? "-"),
                "上游服务器：" + (status["upstream"]?.GetValue<string>() ?? "-"),
                "在线玩家：" + (status["playerCount"]?.GetValue<int>().ToString() ?? "0"),
                "安装版本：" + (string.IsNullOrWhiteSpace(_runtime.InstalledTag) ? "未安装" : _runtime.InstalledTag),
            };
            _status.Text = string.Join("\n", lines);
            _hint.Text = "安装目录：" + _runtime.InstallDirectory;
            await LoadSettingsAsync();
        }
        catch (Exception ex)
        {
            _status.Text = "接口未连接（" + _api.BaseUrl + "）\n" + ex.Message;
            _hint.Text = "安装目录：" + _runtime.InstallDirectory;
        }
    }

    private async Task LoadSettingsAsync()
    {
        _settings.Children.Clear();
        JsonNode? response;
        try
        {
            response = await _api.ConfigAsync();
        }
        catch
        {
            return;
        }

        if (response?["settings"] is not JsonArray items) return;

        foreach (var item in items)
        {
            if (item is not JsonObject setting) continue;
            var key = setting["key"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(key)) continue;
            var value = setting["value"];

            var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 3, 0, 3) };

            var label = new TextBlock
            {
                Text = key,
                Width = 280,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            DockPanel.SetDock(label, Dock.Left);
            row.Children.Add(label);

            FrameworkElement editor;
            Func<string> readValue;
            if (value is JsonValue jsonValue && jsonValue.TryGetValue<bool>(out var boolean))
            {
                var check = new CheckBox { IsChecked = boolean, VerticalAlignment = VerticalAlignment.Center };
                editor = check;
                readValue = () => check.IsChecked == true ? "true" : "false";
            }
            else
            {
                var box = new TextBox
                {
                    Text = value?.ToString() ?? "",
                    Width = 260,
                    Margin = new Thickness(0, 0, 8, 0),
                };
                editor = box;
                readValue = () => box.Text;
            }

            var apply = _context.Ui.CreateButton("应用", async () =>
            {
                try
                {
                    var result = await _api.SetSettingAsync(key, readValue());
                    var updated = result["value"]?.ToString() ?? readValue();
                    _context.Ui.ShowNotification($"{key} = {updated}", PluginNotificationKind.Success);
                }
                catch (ImcProxyApiException ex)
                {
                    _context.Ui.ShowNotification($"{key} 未修改：{ex.Message}", PluginNotificationKind.Warning);
                }
                catch (Exception ex)
                {
                    _context.Ui.ShowNotification($"{key} 未修改：{ex.Message}", PluginNotificationKind.Error);
                }
            });

            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            right.Children.Add(editor);
            right.Children.Add(apply);
            DockPanel.SetDock(right, Dock.Right);
            row.Children.Add(right);

            _settings.Children.Add(row);
        }

        if (_settings.Children.Count == 0)
        {
            _settings.Children.Add(new TextBlock { Text = "没有读取到配置项，请先启动代理。", Opacity = 0.7 });
        }
    }
}
