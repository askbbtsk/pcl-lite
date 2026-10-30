using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using PCL.Core.App;
using PCL.Core.App.Configuration;
using PCL.Core.App.Configuration.Storage;
using PCL.Core.App.Localization;
using PCL.Core.UI;
using PCL.Core.Utils.Secret;

namespace PCL;

/// <summary>
///     百宝箱：把 PCL2 或 PCLCE 的设置搬过来。
///     <para>
///         两种来源：PCL CE / PCL Lite 的 <c>config.v1.json</c>（直接复制），
///         以及 PCL2 原版的 <c>setup.ini</c> + <c>HKCU\Software\PCL</c>（转成本地配置）。
///         源文件夹只读，不会改动。
///     </para>
/// </summary>
public partial class PageToolsImport
{
    /// <summary>一个可以导入的来源。</summary>
    private sealed record Source(string Folder, string Kind, string Detail, bool IsCe, string FileName);

    private readonly List<Source> _sources = [];
    private Source? _selected;

    public PageToolsImport()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_sources.Count == 0) Rescan();
        };
    }

    #region 搜索

    private void BtnRescan_Click(object sender, MouseButtonEventArgs e) => Rescan();

    /// <summary>
    ///     回到百宝箱主页面。走左栏的切换逻辑，勾选状态才跟得上
    ///     （<c>MyIconTextButton.Click</c> 的第二参数是 <c>ModBase.RouteEventArgs</c>，写错要到运行时才炸）。
    /// </summary>
    private void BtnBack_Click(object sender, ModBase.RouteEventArgs e)
    {
        ModMain.frmToolsLeft?.SwitchTo(FormMain.PageSubType.ToolsTest);
    }

    private void BtnPick_Click(object sender, MouseButtonEventArgs e)
    {
        var folder = SystemDialogs.SelectFolder(Lang.Text("Tools.Import.Pick"));
        if (string.IsNullOrWhiteSpace(folder)) return;

        var source = Describe(folder);
        if (source is null)
        {
            ModMain.MyMsgBox(Lang.Text("Tools.Import.NotPcl", folder), Lang.Text("Tools.Import.Title"),
                isWarn: true);
            return;
        }

        _sources.RemoveAll(x => SamePath(x.Folder, folder));
        _sources.Insert(0, source);
        Render();
        Select(source);
    }

    private void Rescan()
    {
        var picked = _selected?.Folder;
        _sources.Clear();
        _sources.AddRange(Scan());
        _sources.AddRange(DeepCache.Where(x => !_sources.Any(y => SamePath(y.Folder, x.Folder))));
        Render();

        if (picked is not null)
        {
            var again = _sources.FirstOrDefault(x => SamePath(x.Folder, picked));
            if (again is not null) Select(again);
        }

        _ = DeepScanAsync();
    }

    /// <summary>
    ///     快速扫描：<c>%APPDATA%</c> 一层，加上 PCL 自己记过的 MC 文件夹、它们的父目录和各自的
    ///     <c>PCL</c> 子目录。
    ///     <para>
    ///         父目录也要看：PCL2 便携版习惯把启动器放在 <c>…\mc\PCL\</c>，而 MC 目录是
    ///         <c>…\mc\.minecraft\</c>，只看 MC 目录本身是找不到的。
    ///     </para>
    /// </summary>
    private static IEnumerable<Source> Scan()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var roots = new List<string>();
        foreach (var name in new[] { "PCLCE", "PCL", ".PCL", "PCL2", "PCLLite" })
            roots.Add(Path.Combine(appData, name));
        foreach (var name in new[] { "PCLCE", "PCLLite" })
            roots.Add(Path.Combine(localAppData, name));

        foreach (var folder in KnownMcFolders())
        {
            roots.Add(folder);
            roots.Add(Path.Combine(folder, "PCL"));

            var parent = Path.GetDirectoryName(folder.TrimEnd('\\', '/'));
            if (string.IsNullOrWhiteSpace(parent)) continue;
            roots.Add(parent);
            roots.Add(Path.Combine(parent, "PCL"));
        }

        try
        {
            roots.AddRange(Directory.EnumerateDirectories(appData));
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Import] 枚举 AppData 目录失败", ModBase.LogLevel.Debug);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            string full;
            try
            {
                full = Path.GetFullPath(root);
            }
            catch (Exception)
            {
                continue;
            }

            if (!seen.Add(full)) continue;
            if (SamePath(full, Paths.SharedData)) continue; // 自己不算

            var source = Describe(full);
            if (source is not null) yield return source;
        }
    }

    /// <summary>PCL 记过的 MC 文件夹（<c>LaunchFolders</c>，<c>|</c> 分隔）。</summary>
    private static IEnumerable<string> KnownMcFolders()
    {
        var raw = States.Game.Folders;
        if (string.IsNullOrWhiteSpace(raw)) yield break;

        foreach (var item in raw.Split('|'))
        {
            var folder = item.Trim();
            if (folder.Length > 0) yield return folder;
        }
    }

    /// <summary>
    ///     深度扫描的缓存。整个会话只扫一次 —— 挨个盘翻目录，扫一次就够贵了。
    /// </summary>
    private static readonly List<Source> DeepCache = [];

    private static bool _deepScanned;

    private bool _deepScanning;

    /// <summary>
    ///     挨个固定磁盘往下翻 <c>Setup.ini</c>。
    ///     <para>
    ///         为什么要翻盘：PCL2 便携版把 <c>Setup.ini</c> 放在启动器旁边，而启动器可能在
    ///         任意目录（<c>E:\…\mc\PCL\</c> 这种）。<c>%APPDATA%</c> 和 MC 目录都猜不到的时候，
    ///         只能靠翻。
    ///     </para>
    ///     <para>
    ///         只往下 4 层、跳过系统目录和依赖目录（<c>AppData</c>/<c>node_modules</c>/
    ///         <c>.git</c>…）、不进符号链接，另外还有 3 万个目录和 20 秒的硬上限 ——
    ///         宁可少找到，也不能让页面卡住。
    ///     </para>
    /// </summary>
    private static IEnumerable<Source> DeepScan()
    {
        const int maxDepth = 4;
        const int maxDirs = 30000;
        var deadline = DateTime.UtcNow.AddSeconds(20);

        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Windows", "Windows.old", "Program Files", "Program Files (x86)", "ProgramData",
            "$Recycle.Bin", "System Volume Information", "Recovery", "PerfLogs", "WindowsApps",
            "AppData", "node_modules", ".git", ".svn", ".hg", ".nuget", ".gradle", ".m2",
            ".cargo", ".rustup", ".vscode", ".idea", ".vs", "packages", "bin", "obj",
            "target", "Temp", "tmp", "cache", "Cache", "Installer", "Assembly", "Microsoft"
        };

        var visited = 0;
        var hits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var drive in DriveInfo.GetDrives())
        {
            bool ready;
            try
            {
                ready = drive.DriveType == DriveType.Fixed && drive.IsReady;
            }
            catch (Exception)
            {
                continue;
            }

            if (!ready) continue;

            var queue = new Queue<(string Path, int Depth)>();
            queue.Enqueue((drive.RootDirectory.FullName, 0));

            while (queue.Count > 0)
            {
                if (visited >= maxDirs || DateTime.UtcNow > deadline)
                {
                    ModBase.Log($"[Import] 深度搜索达到上限，已检查 {visited} 个目录",
                        ModBase.LogLevel.Developer);
                    yield break;
                }

                var (dir, depth) = queue.Dequeue();
                visited++;

                if (Describe(dir) is { } source && hits.Add(source.Folder))
                    yield return source;

                if (depth >= maxDepth) continue;

                string[] children;
                try
                {
                    children = Directory.GetDirectories(dir);
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var child in children)
                {
                    if (skip.Contains(Path.GetFileName(child))) continue;
                    try
                    {
                        var attributes = new DirectoryInfo(child).Attributes;
                        // 符号链接和目录联接会绕圈，直接跳过
                        if (attributes.HasFlag(FileAttributes.System) ||
                            attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    queue.Enqueue((child, depth + 1));
                }
            }
        }

        ModBase.Log($"[Import] 深度搜索结束，共检查 {visited} 个目录", ModBase.LogLevel.Developer);
    }

    /// <summary>
    ///     后台跑一遍 <see cref="DeepScan" />，找到就补进列表。
    ///     慢扫描期间页面上会显示一行提示，免得用户以为「没找到」就是最终结论。
    /// </summary>
    private async Task DeepScanAsync()
    {
        if (_deepScanning || _deepScanned) return;

        _deepScanning = true;
        SetScanning(true);
        try
        {
            var found = await Task.Run(() => DeepScan().ToList());
            _deepScanned = true;
            DeepCache.Clear();
            DeepCache.AddRange(found);

            if (!IsLoaded || found.Count == 0) return;

            var added = false;
            foreach (var source in found)
            {
                if (_sources.Any(x => SamePath(x.Folder, source.Folder))) continue;
                _sources.Add(source);
                added = true;
            }

            if (added) Render();
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Import] 深度搜索失败", ModBase.LogLevel.Debug);
        }
        finally
        {
            _deepScanning = false;
            SetScanning(false);
        }
    }

    private void SetScanning(bool scanning)
    {
        if (LabScanHint is null) return;
        LabScanHint.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Source? Describe(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return null;

            var config = Path.Combine(folder, "config.v1.json");
            if (File.Exists(config))
                return new Source(folder, Lang.Text("Tools.Import.Kind.Ce"), Detail(config, "config.v1.json"),
                    true, "config.v1.json");

            var setup = Path.Combine(folder, "setup.ini");
            if (File.Exists(setup))
                return new Source(folder, Lang.Text("Tools.Import.Kind.Pcl2"), Detail(setup, "setup.ini"),
                    false, "setup.ini");

            // 便携版：选的是启动器目录，设置在里面那个 PCL 文件夹里
            var portable = Path.Combine(folder, "PCL", "setup.ini");
            if (File.Exists(portable))
                return new Source(folder, Lang.Text("Tools.Import.Kind.Portable"),
                    Detail(portable, Path.Combine("PCL", "setup.ini")), false, Path.Combine("PCL", "setup.ini"));
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Import] 检查目录失败：" + folder, ModBase.LogLevel.Debug);
        }

        return null;
    }

    private static string Detail(string file, string name)
    {
        var info = new FileInfo(file);
        return $"{name}  ·  {info.LastWriteTime:yyyy-MM-dd HH:mm}  ·  {Size(info.Length)}";
    }

    private static string Size(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024d / 1024d:F1} MB"
        : $"{bytes / 1024d:F0} KB";

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    #endregion

    #region 界面

    private void Render()
    {
        PanResults.Children.Clear();
        foreach (var source in _sources)
        {
            var item = new MyListItem
            {
                Title = source.Kind,
                Info = source.Folder,
                Height = 42,
                Type = MyListItem.CheckType.Clickable,
                Tag = source,
                SvgIcon = source.IsCe ? "lucide/file-box" : "lucide/file-input",
                Margin = new Thickness(0, 0, 0, 6)
            };
            // 必须闭包捕获 source：MyListItem.Click 的 sender 是模板里那个 Button，
            // 不是列表项本身，用 sender 去匹配 Tag 永远匹配不上 —— 表现就是「点了没反应，
            // 开始导入一直是灰的」，以前 PCL2 原版转不了就是这个
            item.Click += (_, _) => Select(source);
            PanResults.Children.Add(item);
        }

        LabEmpty.Visibility = _sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_selected is not null && !_sources.Contains(_selected)) _selected = null;

        // 只有一个来源时直接替用户选上，省得他以为「开始导入」是坏的
        if (_selected is null && _sources.Count == 1) _selected = _sources[0];

        ShowSelected();
    }

    private void Select(Source source)
    {
        _selected = source;
        ShowSelected();
    }

    private void ShowSelected()
    {
        if (_selected is not { } source)
        {
            LabSelected.Text = Lang.Text("Tools.Import.None");
            LabPlan.Text = "";
            BtnImport.IsEnabled = false;
            return;
        }

        LabSelected.Text = source.Folder;
        LabPlan.Text = source.IsCe
            ? Lang.Text("Tools.Import.Plan.Ce")
            : Lang.Text("Tools.Import.Plan.Pcl2");
        BtnImport.IsEnabled = true;
    }

    #endregion

    #region 导入

    private void BtnImport_Click(object sender, MouseButtonEventArgs e)
    {
        if (_selected is not { } source) return;

        var body = Lang.Text("Tools.Import.Confirm.Body", source.Folder,
            source.IsCe ? Paths.SharedData : ConfigService.LocalConfigPath);
        if (ModMain.MyMsgBox(body, Lang.Text("Tools.Import.Confirm.Title"),
                Lang.Text("Common.Action.Continue"), Lang.Text("Common.Action.Cancel"), isWarn: true) != 1)
            return;

        var accounts = 0;
        try
        {
            if (source.IsCe) ImportCe(source);
            else accounts = ImportPcl2(source);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Import] 导入设置失败", ModBase.LogLevel.Msgbox,
                userSummary: Lang.Text("Tools.Import.Failed", ex.Message));
            return;
        }

        // 内容只写进了 .pending，必须重启才换得上：运行中的 provider 会把内存里的旧值写回去
        var done = Lang.Text("Tools.Import.Done");
        if (accounts > 0) done += "\n\n" + Lang.Text("Tools.Import.Done.Accounts", accounts);
        ModMain.MyMsgBox(done, Lang.Text("Tools.Import.Title"), forceWait: true);
        Restart();
    }

    /// <summary>
    ///     重启自己。和设置页的「导入设置」一个做法：先起新进程，再强制结束当前进程。
    /// </summary>
    private static void Restart()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Basics.ExecutablePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Import] 重启失败，请手动重新打开 PCL", ModBase.LogLevel.Msgbox,
                userSummary: Lang.Text("Tools.Import.RestartFailed"));
            return;
        }

        FormMain.EndProgramForce();
    }

    /// <summary>
    ///     PCL CE / PCL Lite：整份配置和密钥一起带过去，正版账号就在里面。
    ///     <para>
    ///         只能写 <c>.pending</c>。运行中直接覆盖 <c>config.v1.json</c> 是没用的：
    ///         配置在启动时已经整份读进内存，十来秒后一次落盘就把文件重写回去了 ——
    ///         以前「转移设置转不了」就是这个原因。
    ///     </para>
    /// </summary>
    private static void ImportCe(Source source)
    {
        Directory.CreateDirectory(Paths.SharedData);

        var pending = ConfigService.SharedConfigPath + ConfigService.PendingSuffix;
        File.Copy(Path.Combine(source.Folder, "config.v1.json"), pending, true);

        // 账号、令牌这些是加密存的，没有源那边的钥匙就是一串解不开的密文
        var key = Path.Combine(source.Folder, "UserKey.bin");
        if (File.Exists(key))
            File.Copy(key, Path.Combine(Paths.SharedData, "UserKey.bin" + ConfigService.PendingSuffix), true);
    }

    /// <summary>
    ///     PCL2 原版把配置拆成两半：<c>Source:=SetupSource.Ini</c> 的键写
    ///     <c>PCL\setup.ini</c>（明文），<c>Source:=SetupSource.Registry</c> 的键写注册表。
    ///     后者有 83 个，其中 11 个还加密过 —— 只读 <c>setup.ini</c> 会漏掉一大半设置。
    ///     <para>
    ///         根名是 <c>PCL</c>，不是 PCL-CE 的 <c>PCLCE</c>：上游特意改过名，
    ///         注释写的是「与 PCL 的注册表隔离，以防数据冲突」，所以两边数据互不可见。
    ///     </para>
    /// </summary>
    private const string Pcl2RegFolder = "PCL";

    /// <summary>
    ///     PCL2 里标了 <c>Encoded:=True</c> 的键。<c>Setup.Set</c> 写注册表之前先
    ///     <c>SecretEncrypt</c> 加密，所以读出来是 Base64 密文，得先解开。
    ///     <para>
    ///         名单是从上游 <c>ModSetup.vb</c> 抄的，不靠「试着解一下」来猜：
    ///         AES-CBC 没有认证，明文偶尔也能「解开」成一串乱码。
    ///     </para>
    /// </summary>
    private static readonly HashSet<string> Pcl2EncodedKeys = new(StringComparer.Ordinal)
    {
        "CacheAuthName", "CacheAuthPass", "CacheAuthServerServer", "CacheAuthUsername", "CacheAuthUuid",
        "LoginLegacyName", "LoginMsJson", "LoginRemember",
        "SystemCount", "SystemLaunchCount", "SystemLastVersionReg",
        "SystemHighestAlphaVersionReg", "SystemHighestBetaVersionReg", "SystemHighestSavedBetaVersionReg",
        "SystemHttpProxy", "ToolUpdateAlpha",
        "UiLauncherThemeGold", "UiLauncherThemeHide", "UiLauncherThemeHide2"
    };

    /// <summary>
    ///     PCL2 的账号相关键。这些能解开，但 <c>LoginMsJson</c> 里只有
    ///     <c>{UserName: OAuthToken}</c> —— PCL2 压根不保存 refreshToken，
    ///     accessToken 二十四小时就过期且没法续，搬过去只会多出一条启动即失败的档案。
    ///     所以只统计、不写入，让用户去账号页重新登录。
    /// </summary>
    private static readonly HashSet<string> Pcl2AccountKeys = new(StringComparer.Ordinal)
    {
        "CacheAuthName", "CacheAuthPass", "CacheAuthServerServer", "CacheAuthUsername", "CacheAuthUuid",
        "LoginLegacyName", "LoginMsJson", "LoginRemember"
    };

    /// <summary>
    ///     读 <c>HKCU\Software\PCL</c>。先 64 位视图再 32 位：PCL2 原版是 x64，
    ///     但手边那份可能是 32 位的，两个视图在注册表里各存一份、互不相通。
    /// </summary>
    /// <param name="usedView">实际读到数据的视图名，只用于日志。</param>
    private static Dictionary<string, string> ReadPcl2Registry(out string? usedView)
    {
        usedView = null;
        var views = new[] { (RegistryView.Registry64, "64 位"), (RegistryView.Registry32, "32 位") };

        foreach (var (view, name) in views)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
                using var key = baseKey.OpenSubKey(@"Software\" + Pcl2RegFolder);
                if (key is null) continue;

                foreach (var valueName in key.GetValueNames())
                {
                    if (string.IsNullOrEmpty(valueName)) continue;
                    var raw = key.GetValue(valueName);
                    if (raw is null) continue;
                    if (raw is byte[]) continue; // 不是 WriteReg 写的那种字符串值，跳过
                    // PCL2 的 WriteReg 一律 SetValue(Key, String)，所以这里都是 REG_SZ；
                    // 万一碰到别的类型，Convert 也能转，转不了就跳过
                    try
                    {
                        values[valueName] = Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "";
                    }
                    catch (Exception)
                    {
                        // 忽略单个坏值
                    }
                }
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, $"[Import] 读注册表 Software\\{Pcl2RegFolder}（{name}视图）失败",
                    ModBase.LogLevel.Debug);
                continue;
            }

            if (values.Count == 0) continue;
            usedView = name;
            return values;
        }

        return [];
    }

    /// <summary>
    ///     解开 <c>Encoded:=True</c> 的值。解不开返回 <c>null</c>，调用方跳过该键 ——
    ///     加密用的钥匙是 <c>SHA512(SHA256(CpuId))</c>，换台机器就解不开了，
    ///     这时候写个空值进去只会把配置弄脏。
    /// </summary>
    private static string? TryDecodePcl2Value(string key, string value)
    {
        if (!Pcl2EncodedKeys.Contains(key)) return value;
        try
        {
            return EncryptHelper.SecretDecrypt(value);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, $"[Import] 解密 {key} 失败（可能是别的机器上写的），跳过", ModBase.LogLevel.Developer);
            return null;
        }
    }

    /// <summary>
    ///     PCL2 原版：<c>setup.ini</c> 是 <c>键:值</c> 的纯文本，先转成 yml 放进暂存。
    ///     <para>
    ///         只写 PCL Lite 真认得的键（<see cref="ConfigService.KeySet" />）：setup.ini 里混着
    ///         <c>UiLauncherEmail</c>、<c>LoginPageType</c> 这类早就没了的键，原样塞进去只会把
    ///         本地配置弄脏。写进去的值保持字符串，和启动时自带的 <c>setup.ini</c> 迁移
    ///         （<c>CatIniMigration</c>）完全一致，读取时由 provider 负责转类型。
    ///     </para>
    ///     <para>
    ///         先复制现有 <c>config.v1.yml</c> 再往上盖，这样导入是合并而不是清空 ——
    ///         用户已经在 PCL Lite 里调过的东西不会因为导入一次别人的配置就全没了。
    ///     </para>
    /// </summary>
    /// <returns>没搬过来的账号数量，用于提示用户重新登录。</returns>
    private static int ImportPcl2(Source source)
    {
        var file = Path.Combine(source.Folder, source.FileName);
        var pending = ConfigService.LocalConfigPath + ConfigService.PendingSuffix;
        if (File.Exists(pending)) File.Delete(pending);

        if (File.Exists(ConfigService.LocalConfigPath))
            File.Copy(ConfigService.LocalConfigPath, pending, true);

        var provider = new YamlFileProvider(pending);
        var known = 0;
        var skipped = 0;
        foreach (var line in File.ReadAllLines(file, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var index = line.IndexOf(':');
            if (index <= 0) continue;

            var key = line[..index].Trim();
            if (key.Length == 0) continue;
            if (!ConfigService.KeySet.Contains(key))
            {
                skipped++;
                continue;
            }

            provider.Set(key, line[(index + 1)..].Trim());
            known++;
        }

        var fromIni = known;

        // 注册表那一半。以前完全没读，于是 SystemHttpProxy、SystemDebugMode、
        // SystemLaunchCount 这些全漏了 —— 而它们才是「设置」的大头
        var registry = ReadPcl2Registry(out var view);
        var fromReg = 0;
        var accounts = 0;
        var broken = 0;
        var empty = 0;
        foreach (var (key, raw) in registry)
        {
            if (Pcl2AccountKeys.Contains(key))
            {
                accounts++;
                continue;
            }

            if (!ConfigService.KeySet.Contains(key))
            {
                skipped++;
                continue;
            }

            if (TryDecodePcl2Value(key, raw) is not { } value)
            {
                broken++;
                continue;
            }

            // 空值不写：PCL2 里没设置过的键就是空串，照搬过去会把用户已经在
            // PCL Lite 里调好的值（比如代理地址）清掉，这不是「导入」该干的事
            if (value.Length == 0)
            {
                empty++;
                continue;
            }

            provider.Set(key, value);
            known++;
            fromReg++;
        }

        provider.Sync();

        ModBase.Log(
            $"[Import] {file}：setup.ini 可用 {fromIni} 个；注册表 Software\\{Pcl2RegFolder}" +
            $"（{view ?? "没有数据"}）读到 {registry.Count} 个，可用 {fromReg} 个；" +
            $"跳过 {skipped} 个不认识的、{accounts} 个账号键、{empty} 个空值、{broken} 个解不开的",
            ModBase.LogLevel.Developer);

        if (known == 0)
            throw new InvalidOperationException(Lang.Text("Tools.Import.NoKeys", file));

        return accounts;
    }

    #endregion
}
