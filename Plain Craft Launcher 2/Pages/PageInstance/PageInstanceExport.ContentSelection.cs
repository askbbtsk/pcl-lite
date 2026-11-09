using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using DotNet.Globbing;
using PCL.Core.App;
using PCL.Core.Minecraft.Packs;
using PCL.Core.Minecraft.Java.UserPreference;
using PCL.Core.UI;
using PCL.Core.App.Localization;
using PCL.Core.Utils;

namespace PCL;

public partial class PageInstanceExport
{

    /// <summary>
    ///     动态生成子文件夹下的选项，例如资源包、存档等。
    /// </summary>
    private void ReloadAllSubOptions()
    {
        ReloadSubOptions(PanOptionsResourcePacks, true, true, "resourcepacks", "texturepacks");
        ReloadSubOptions(PanOptionsSaves, false, true, "saves");
        ReloadSubOptions(PanOptionsShaderPacks, true, true, "shaderpacks");
        ReloadOtherFolders();
    }


    /// <summary>
    ///     扫描实例根目录下未被已有选项覆盖的文件夹，生成独立的复选框。
    /// </summary>
    private void ReloadOtherFolders()
    {
        PanOptionsOtherFolders.Children.Clear();

        var pathIndie = PageInstanceLeft.McInstance.PathIndie;
        var rootDir = new DirectoryInfo(pathIndie);
        if (!rootDir.Exists)
        {
            CheckOptionsOtherFolders.Visibility = Visibility.Collapsed;
            return;
        }

        var coveredFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // 模组
            "mods", "coremods", "lib",
            // 整合包重要数据
            "addons", "multiblocked", "modpack-update-checker", "global_packs",
            "global_resource_packs", "global_data_packs", "optional_data_packs", "maps",
            "mods-resourcepacks", "matmos", "resource_assorts",
            "patchouli_books", "datapacks",
            "openloader", "worldshape", "resources", "scripts", "structures",
            "fontfiles", "oresources", "packmenu", "craftpresence", "pointblanks",
            // 模组设置
            "config", "defaultconfigs", "journeymap", "local", "essential", "gg.essential.mod",
            "CustomSkinLoader",
            // 地图
            "xaero", "XaeroWaypoints", "XaeroWorldMap",
            // 资源包
            "resourcepacks", "texturepacks",
            // 光影
            "shaderpacks",
            // 截图 / 结构 / 录像
            "screenshots", "schematics",
            "replay_recordings", "replay_videos",
            // 存档 / 设置文件夹
            "saves", "configureddefaults",
            // 始终跳过（大量文件或无用缓存）
            "assets", "versions", "libraries", "structureCacheV1",
            ".fabric", ".git", "avatar-cache", "cosmetic-cache",
            // PCL 单独处理
            "PCL",
        };

        var coveredPrefixes = new[] { "kubejs", "template" };
        var coveredSuffixes = new[] { "-natives" };

        foreach (var subDir in rootDir.EnumerateDirectories())
        {
            if (PackExportContent.IsInternalPath(subDir.Name)
                || (_instanceJava is not null && _instanceJava.RuntimeRoot.StartsWith(subDir.FullName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                || (_instanceJava is not null && string.Equals(_instanceJava.RuntimeRoot, subDir.FullName, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (coveredFolders.Contains(subDir.Name))
                continue;
            if (coveredPrefixes.Any(p => subDir.Name.StartsWithF(p)))
                continue;
            if (coveredSuffixes.Any(s => subDir.Name.EndsWithF(s, true)))
                continue;

            PanOptionsOtherFolders.Children.Add(new MyCheckBox
            {
                Tag = new ExportOption
                {
                    Title = subDir.Name,
                    DefaultChecked = false,
                    Rules = ModBase.EscapeLikePattern($"{subDir.Name}/")
                }
            });
        }

        CheckOptionsOtherFolders.Visibility = PanOptionsOtherFolders.Children.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }


    private void ReloadSubOptions(StackPanel panel, bool acceptCompressedFile, bool acceptFolder,
        params string[] folders)
    {
        panel.Children.Clear();
        foreach (var Folder in folders)
        {
            var targetFolder = new DirectoryInfo(PageInstanceLeft.McInstance.PathIndie + Folder);
            if (!targetFolder.Exists)
                continue;
            // 查找文件夹下的对应项
            if (acceptCompressedFile)
                foreach (var File in targetFolder.EnumerateFiles("*.zip").Concat(targetFolder.EnumerateFiles("*.rar")))
                {
                    if (subOptionBlackList.Any(b => File.Name.ContainsF(b)))
                        continue;
                    panel.Children.Add(new MyCheckBox
                    {
                        Tag = new ExportOption
                        {
                            Title = File.Name, DefaultChecked = true,
                            Rules = ModBase.EscapeLikePattern($"{Folder}/{File.Name}")
                        }
                    });
                    if (Folder == "shaderpacks") // 处理光影包的配置文件
                    {
                        var shaderConfig = new FileInfo(Path.Combine(File.Directory.FullName,
                            $"{File.Name}.txt"));
                        if (shaderConfig.Exists)
                            panel.Children.Add(new MyCheckBox
                            {
                                Margin = new Thickness(30, 0, 0, 0),
                                Tag = new ExportOption
                                {
                                    Title = $"{shaderConfig.Name}", DefaultChecked = true,
                                    Description = Lang.Text("Instance.Export.Config.ShaderConfigSuffix"),
                                    Rules = ModBase.EscapeLikePattern($"{Folder}/{shaderConfig.Name}")
                                }
                            });
                    }
                }

            if (acceptFolder)
                foreach (var SubFolder in targetFolder.EnumerateDirectories().OrderByDescending(f => f.LastWriteTime))
                {
                    if (subOptionBlackList.Any(b => SubFolder.Name.ContainsF(b)))
                        continue;
                    if (!SubFolder.EnumerateFileSystemInfos().Any())
                        continue;
                    var newCheckBox = new MyCheckBox
                    {
                        Tag = new ExportOption
                        {
                            Title = SubFolder.Name, DefaultChecked = true,
                            Rules = ModBase.EscapeLikePattern($"{Folder}/{SubFolder.Name}/")
                        }
                    };
                    if (ReferenceEquals(panel, PanOptionsSaves))
                        GetExportOption(newCheckBox).Description =
                            Lang.Date(SubFolder.LastWriteTime, "g");
                    panel.Children.Add(newCheckBox);
                    if (Folder == "shaderpacks") // 处理文件夹形式光影包的配置文件
                    {
                        var shaderConfig = new FileInfo(Path.Combine(targetFolder.FullName,
                            $"{SubFolder.Name}.txt"));
                        if (shaderConfig.Exists)
                            panel.Children.Add(new MyCheckBox
                            {
                                Margin = new Thickness(30, 0, 0, 0),
                                Tag = new ExportOption
                                {
                                    Title = $"{shaderConfig.Name}", DefaultChecked = true,
                                    Description = Lang.Text("Instance.Export.Config.ShaderConfigSuffix"),
                                    Rules = ModBase.EscapeLikePattern($"{Folder}/{shaderConfig.Name}")
                                }
                            });
                    }
                }
        }
    }




    /// <summary>
    ///     重新确认是否应该显示每个选项，并将 ExportOption 同步到 UI。
    /// </summary>
    private void RefreshAllOptionsUI()
    {
        // 预先归纳所有至多二级的文件/文件夹
        var allEntries = new List<string>();

        bool IsValidDirectory(DirectoryInfo folder)
        {
            try
            {
                return folder.Exists && folder.EnumerateFileSystemInfos()
                    .Any(i => !subOptionBlackList.Any(b => i.Name.ContainsF(b)));
            }
            catch
            {
                return false;
            }
        }

        ; // 检查文件夹不为空
        // 一般是由于无法访问，或是一个指向已不存在的文件夹的链接（例如使用 mklink 创造的 resource 文件夹链接）
        var pathInfo = new DirectoryInfo(PageInstanceLeft.McInstance.PathIndie);
        allEntries.AddRange(pathInfo.EnumerateFiles().Select(f => f.Name));
        foreach (var SubFolder in pathInfo.EnumerateDirectories().Where(IsValidDirectory))
        {
            allEntries.Add($@"{SubFolder.Name}\");
            allEntries.AddRange(SubFolder.EnumerateFiles().Select(f => $@"{SubFolder.Name}\{f.Name}"));
            allEntries.AddRange(SubFolder.EnumerateDirectories().Where(IsValidDirectory)
                .Select(d => $@"{SubFolder.Name}\{d.Name}\"));
        }

        ModBase.Log($"[Export] 共发现 {allEntries.Count} 个可行的二级文件/文件夹");

        // 确认选项是否应该被显示
        bool IsVisible(ExportOption targetOption)
        {
            // 检查需要 OptiFine 或 Mod 加载器
            if (targetOption.RequireOptiFine && !PageInstanceLeft.McInstance.Info.HasOptiFine)
                return false;
            if (targetOption.RequireModLoader && !PageInstanceLeft.McInstance.Modable)
                return false;
            if (targetOption.RequireModLoaderOrOptiFine && !PageInstanceLeft.McInstance.Info.HasOptiFine &&
                !PageInstanceLeft.McInstance.Modable)
                return false;
            // 粗略检查是否可能有符合规则的文件/文件夹
            return StandardizeLines((targetOption.Rules ?? targetOption.ShowRules).Split('|'), true).Any(rule =>
            {
                if (rule.StartsWithF("!"))
                    return false; // 只看正向规则
                // 检查前两级
                try
                {
                    if (allEntries.Any(entry => LikeString(entry, rule)))
                        return true;
                }
                catch (Exception ex)
                {
                    ModBase.Log(
                        ex,
                        $"错误的规则：{rule}",
                        ModBase.LogLevel.Hint,
                        userSummary: Lang.Text("Instance.Export.Error.OperationFailed"));
                    return false;
                }

                // 粗略检查所有级
                rule = rule.Trim("*?".ToCharArray());
                if (rule.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries).Count() >= 3)
                {
                    if (rule.EndsWithF(@"\"))
                        return IsValidDirectory(new DirectoryInfo(PageInstanceLeft.McInstance.PathIndie + rule)); // 文件夹有效

                    return File.Exists(PageInstanceLeft.McInstance.PathIndie + rule);
                    // 文件有效
                }

                return false;
            });
        }

        ;
        // 逐个检查选项
        foreach (var CheckBox in GetAllOptions(true))
        {
            var targetOption = GetExportOption(CheckBox);
            // 名称与简介
            CheckBox.Inlines.Clear();
            CheckBox.Inlines.Add(new Run(targetOption.Title));
            if (!string.IsNullOrEmpty(targetOption.Description))
                CheckBox.Inlines.Add(new Run("   " + targetOption.Description) { Foreground = ThemeManager.colorGray5 });
            // 可见性、默认勾选
            if (string.IsNullOrEmpty(targetOption.Rules) && string.IsNullOrEmpty(targetOption.ShowRules))
            {
                CheckBox.Visibility = Visibility.Visible;
                CheckBox.Checked = targetOption.DefaultChecked;
            }
            else
            {
                var pass = IsVisible(targetOption);
                CheckBox.Visibility = pass ? Visibility.Visible : Visibility.Collapsed;
                CheckBox.Checked = targetOption.DefaultChecked && pass;
            }
        }
    }


    /// <summary>
    ///     对文本行进行标准化处理，以便使用 Like 进行匹配。
    /// </summary>
    private IEnumerable<string> StandardizeLines(IEnumerable<string> raw, bool addSuffixStarToFolderPath)
    {
        foreach (var IgnoreLineRaw in raw)
        {
            var ignoreLine = IgnoreLineRaw;
            ignoreLine = ignoreLine.Trim();
            if (string.IsNullOrEmpty(ignoreLine) || ignoreLine.StartsWithF("#") || ignoreLine.StartsWithF("="))
                continue;
            ignoreLine = ignoreLine.Replace("/", @"\");
            yield return ignoreLine + (ignoreLine.EndsWithF(@"\") && addSuffixStarToFolderPath ? "**" : "");
        }
    }


    /// <summary>
    ///     获取所有可作为选项的 CheckBox。
    /// </summary>
    private IEnumerable<MyCheckBox> GetAllOptions(bool includeHidden)
    {
        foreach (var Element in PanOptions.Children)
        {
            if (!includeHidden &&
                ((UIElement)Element).Visibility != Visibility.Visible)
                continue;
            if (Element is MyCheckBox)
                yield return (MyCheckBox)Element;
            else if (Element is StackPanel)
                foreach (var SubElement in ((StackPanel)Element).Children)
                {
                    if (!includeHidden && ((UIElement)SubElement).Visibility != Visibility.Visible)
                        continue;
                    if (SubElement is MyCheckBox)
                        yield return (MyCheckBox)SubElement;
                }
        }
    }


    /// <summary>
    ///     获取该 CheckBox 对应的 ExportOption。
    /// </summary>
    private ExportOption GetExportOption(MyCheckBox checkBox)
    {
        return (ExportOption)checkBox.Tag;
    }

}
