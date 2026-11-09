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

    // ================ 保存 / 读取 ================

    // 保存配置文件
    private void ExportConfig(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var configPath = SystemDialogs.SelectSaveFile(Lang.Text("Instance.Export.SelectFileLocation"), "export_config.txt", Lang.Text("Instance.Export.Config.FileFilter"),
                (string?)States.System.ExportConfigPath);
            if (string.IsNullOrEmpty(configPath))
                return;
            States.System.ExportConfigPath = configPath;
            var configLines = new List<string>();
            // ini 段
            configLines.Add("Name:" + TextExportName.Text);
            configLines.Add("Version:" + TextExportVersion.Text);
            configLines.Add("");
            configLines.Add("# " + Lang.Text("Instance.Export.Config.Comment.IncludeLauncher"));
            configLines.Add("IncludeLauncher:" + (SelectedLauncher == BundledLauncher.Lite));
            configLines.Add("LauncherType:" + SelectedLauncher);
            configLines.Add("LightArchitecture:" + (ComboLightArchitecture.SelectedIndex == 1 ? "ARM64" : "x64"));
            configLines.Add("IncludeVersionJava:" + CheckIncludeVersionJava.Checked);
            configLines.Add("VersionArgumentIndie:" + (ComboExportIsolation.SelectedIndex == 0));
            configLines.Add("IncludeJava:" + CheckIncludeVersionJava.Checked);
            configLines.Add("VersionIsolation:" + (ComboExportIsolation.SelectedIndex == 0));
            configLines.Add("");
            configLines.Add("# " + Lang.Text("Instance.Export.Config.Comment.IncludeLauncherCustom"));
            configLines.Add("IncludeLauncherCustom:" + CheckOptionsPclCustom.Checked);
            configLines.Add("");
            configLines.Add("# " + Lang.Text("Instance.Export.Config.Comment.BundleFiles"));
            configLines.Add("# " + Lang.Text("Instance.Export.Config.Comment.BundleFiles2"));
            configLines.Add("# " + Lang.Text("Instance.Export.Config.Comment.BundleFiles3"));
            configLines.Add("DontCheckHostedAssets:" + CheckAdvancedInclude.Checked);
            configLines.Add("");
            configLines.Add("# " + Lang.Text("Instance.Export.Config.Comment.Modrinth"));
            configLines.Add("# " + Lang.Text("Instance.Export.Config.Comment.Modrinth2"));
            configLines.Add("# " + Lang.Text("Instance.Export.Config.Comment.Modrinth3"));
            configLines.Add("ModrinthUploadMode:" + CheckAdvancedModrinth.Checked);
            configLines.Add("");
            configLines.Add("# " + Lang.Text("Instance.Export.Config.Comment.PackPath"));
            configLines.Add("# " + Lang.Text("Instance.Export.Config.Comment.PackPath2"));
            configLines.Add("# " + Lang.Text("Instance.Export.Config.Comment.PackPath3"));
            configLines.Add("PackPath:" + (configPackPath ?? ""));
            configLines.Add("");
            // 导出内容段
            configLines.Add(sperator);
            configLines.AddRange(GetAllRules());
            // 追加内容段
            configLines.Add(sperator);
            configLines.AddRange(GetExtraFileLines());
            // 结束
            ModBase.WriteFile(configPath, configLines.Join("\r\n"));
            HintService.Hint(Lang.Text("Instance.Export.SaveSuccess", configPath), HintType.Success);
            ModBase.OpenExplorer(configPath);
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "保存配置失败",
                ModBase.LogLevel.Msgbox,
                userSummary: Lang.Text("Instance.Export.Error.OperationFailed"));
        }
    }



    /// <summary>
    ///     从指定路径读取配置文件（供按钮和拖放调用）
    /// </summary>
    /// <param name="configPath">配置文件路径</param>
    private void ReadConfigFile(string configPath)
    {
        try
        {
            // 保存配置文件路径到缓存
            States.System.ExportConfigPath = configPath;

            var fileContent = ModBase.ReadFile(configPath);
            var segments = fileContent.Split(sperator);

            if (segments.Length == 0)
            {
                HintService.Hint(Lang.Text("Instance.Export.Config.Invalid"), HintType.Error);
                return;
            }

            // === 解析INI段 ===
            var ini = new Dictionary<string, string>();
            foreach (var LineRaw in segments[0].Split("\r\n".ToCharArray()))
            {
                var line = LineRaw;
                line = line.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWithF("#") || line.StartsWithF("="))
                    continue;
                var index = line.IndexOfF(":");
                if (index > 0) ini[line.Substring(0, index)] = line.Substring(index + 1);
            }

            // 赋值到界面控件
            TextExportName.Text = ini.GetOrDefault("Name", "");
            TextExportVersion.Text = ini.GetOrDefault("Version", "");
            var options = PackExportOptions.Parse(ini);
            ComboLauncherType.SelectedIndex = (int)options.Launcher;
            ComboLightArchitecture.SelectedIndex = options.LightArchitecture == "ARM64" ? 1 : 0;
            ComboExportIsolation.SelectedIndex = options.VersionArgumentIndie ? 0 : 1;
            if (options.IncludeVersionJava && _instanceJava is null)
                throw new InvalidDataException(Lang.Text("Instance.Export.Java.Missing"));
            CheckIncludeVersionJava.Checked = options.IncludeVersionJava;
            if (options.IgnoredModpackMode) HintService.Hint(Lang.Text("Instance.Export.Config.ModeIgnored"));
            CheckOptionsPclCustom.Checked =
                Convert.ToBoolean(ini.GetOrDefault("IncludeLauncherCustom", true.ToString()));
            CheckAdvancedModrinth.Checked =
                Convert.ToBoolean(ini.GetOrDefault("ModrinthUploadMode", false.ToString()));
            CheckAdvancedInclude.Checked =
                Convert.ToBoolean(ini.GetOrDefault("DontCheckHostedAssets", false.ToString()));
            configPackPath = ini.GetOrDefault("PackPath");

            // === 解析导出内容段 ===
            if (segments.Length < 2) throw new InvalidDataException(Lang.Text("Instance.Export.Config.Invalid"));
            RulesOverrides = segments[1].Replace("\r", "\n")
                .Replace("\n" + "\n", "\n").Split("\n").ToList();

            // === 解析追加内容段 ===
            if (segments.Length > 2)
                extraFiles = segments[2].Replace("\r", "\n")
                    .Replace("\n" + "\n", "\n").Split("\n").ToList();
            else
                extraFiles = null;

            // 提示成功
            HintService.Hint(Lang.Text("Instance.Export.ReadSuccess", configPath), HintType.Success);
        }

        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                $"读取配置文件失败：{configPath}",
                ModBase.LogLevel.Msgbox,
                userSummary: Lang.Text("Instance.Export.Error.OperationFailed"));
        }
    }



    // 读取配置文件
    private void ImportConfig(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var configPath = SystemDialogs.SelectFile(Lang.Text("Instance.Export.Config.FileFilter"), Lang.Text("Instance.Export.SelectConfigFile"),
                (string?)States.System.ExportConfigPath);
            if (string.IsNullOrEmpty(configPath))
                return;

            // 调用核心读取逻辑
            ReadConfigFile(configPath);
        }

        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "选择配置文件失败",
                ModBase.LogLevel.Msgbox,
                userSummary: Lang.Text("Instance.Export.Error.OperationFailed"));
        }
    }



    /// <summary>
    ///     文件拖入界面时触发：验证文件类型
    /// </summary>
    private void PanAllBack_DragEnter(object sender, DragEventArgs e)
    {
        // 检查是否包含文件拖放数据
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            // 获取拖入的文件路径数组
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);

            // 验证：仅允许单个.txt文件
            if (files.Length == 1 &&
                files[0].EndsWithF(".txt", true))
                e.Effects = DragDropEffects.Copy; // 设置拖放效果为“复制”
            else
                e.Effects = DragDropEffects.None; // 不允许拖放
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }


    /// <summary>
    ///     文件放下时触发：读取配置文件
    /// </summary>
    private void PanAllBack_Drop(object sender, DragEventArgs e)
    {
        // 获取拖入的文件路径
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            var configPath = files[0];

            // 调用核心读取逻辑
            ReadConfigFile(configPath);
        }

        e.Handled = true;
    }

}
