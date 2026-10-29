using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.App.Configuration;
using PCL.Core.App.Configuration.Storage;
using PCL.Core.App.Localization;
using PCL.Core.UI;

namespace PCL;

/// <summary>
///     百宝箱：把 PCL2 或 PCLCE 的设置搬过来。
///     <para>
///         两种来源：PCL CE / PCL Lite 的 <c>config.v1.json</c>（直接复制），
///         以及 PCL2 原版的 <c>setup.ini</c>（转成本地配置）。源文件夹只读，不会改动。
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
        Render();

        if (picked is null) return;
        var again = _sources.FirstOrDefault(x => SamePath(x.Folder, picked));
        if (again is not null) Select(again);
    }

    /// <summary>
    ///     常见位置 + <c>%APPDATA%</c> 下面一层。改名过的（比如自己挪过）也能扫到。
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
            item.Click += Item_Click;
            PanResults.Children.Add(item);
        }

        LabEmpty.Visibility = _sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_selected is not null && !_sources.Contains(_selected)) _selected = null;
        ShowSelected();
    }

    private void Item_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is MyListItem { Tag: Source source }) Select(source);
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

        try
        {
            if (source.IsCe) ImportCe(source);
            else ImportPcl2(source);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Import] 导入设置失败", ModBase.LogLevel.Msgbox,
                userSummary: Lang.Text("Tools.Import.Failed", ex.Message));
            return;
        }

        // 内容只写进了 .pending，必须重启才换得上：运行中的 provider 会把内存里的旧值写回去
        ModMain.MyMsgBox(Lang.Text("Tools.Import.Done"), Lang.Text("Tools.Import.Title"), forceWait: true);
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

    /// <summary>PCL2 原版：setup.ini 是 <c>键:值</c> 的纯文本，先转成 yml 放进暂存。</summary>
    private static void ImportPcl2(Source source)
    {
        var file = Path.Combine(source.Folder, source.FileName);
        var pending = ConfigService.LocalConfigPath + ConfigService.PendingSuffix;
        if (File.Exists(pending)) File.Delete(pending);

        var provider = new YamlFileProvider(pending);
        foreach (var line in File.ReadAllLines(file, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var index = line.IndexOf(':');
            if (index <= 0) continue;

            var key = line[..index].Trim();
            if (key.Length == 0) continue;
            provider.Set(key, line[(index + 1)..].Trim());
        }

        provider.Sync();
    }

    #endregion
}
