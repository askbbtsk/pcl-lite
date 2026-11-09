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

public class ExportOption : DependencyObject
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(ExportOption)
    );

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(ExportOption)
    );

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string Rules { get; set; }

    /// <summary>
    ///     如果 Rules 为空，则根据 ShowRules 的内容判断是否应该显示这个复选框。
    ///     如果 ShowRules 也为空，则始终显示。
    /// </summary>
    public string ShowRules { get; set; }

    public bool DefaultChecked { get; set; }
    public bool RequireModLoader { get; set; }
    public bool RequireOptiFine { get; set; }
    public bool RequireModLoaderOrOptiFine { get; set; }
}

public partial class PageInstanceExport : IRefreshable
{
    private string currentVersion = "";
    private InstanceJavaBundle? _instanceJava;
    private BundledLauncher SelectedLauncher => (BundledLauncher)Math.Max(0, ComboLauncherType.SelectedIndex);

    private void BundleOptionsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PanLightArchitecture is null || CheckOptionsPclCustom is null || CheckAdvancedModrinth is null) return;
        PanLightArchitecture.Visibility = SelectedLauncher == BundledLauncher.Light ? Visibility.Visible : Visibility.Collapsed;
        CheckOptionsPclCustom.Visibility = SelectedLauncher == BundledLauncher.Lite ? Visibility.Visible : Visibility.Collapsed;
        if (SelectedLauncher != BundledLauncher.None) CheckAdvancedModrinth.Checked = false;
    }

    public PageInstanceExport()
    {
        InitializeComponent();
        Loaded += (_, _) => PageInstanceExport_Loaded();
        CardOptions.MouseLeftButtonDown += CardOptions_MouseLeftButtonDown;
        BtnAdvancedExport.Click += ExportConfig;
        BtnAdvancedImport.Click += ImportConfig;
        BtnExport.Click += StartExport;
        TextExportName.GotFocus += TextExportName_GotFocus;
        CheckAdvancedModrinth.Change += CheckAdvancedModrinth_Change;
        CheckAdvancedInclude.Change += CheckAdvancedInclude_Change;
    }

    void IRefreshable.Refresh()
    {
        RefreshAll();
    }

    private void PageInstanceExport_Loaded()
    {
        ModAnimation.AniControlEnabled += 1;
        if ((currentVersion ?? "") != (PageInstanceLeft.McInstance.PathInstance ?? ""))
            RefreshAll(); // 切换到了另一个实例，重置页面
        ModAnimation.AniControlEnabled -= 1;
    }

    public void RefreshAll()
    {
        ModBase.Log("[Export] 刷新导出页面");
        HintOptiFine.Visibility =
            PageInstanceLeft.McInstance.Info.HasOptiFine ? Visibility.Visible : Visibility.Collapsed;
        currentVersion = PageInstanceLeft.McInstance.PathInstance;
        TextExportName.Text = "";
        TextExportName.HintText = PageInstanceLeft.McInstance.Name;
        TextExportVersion.Text = "";
        TextExportVersion.HintText = "1.0.0";
        ComboLauncherType.SelectedIndex = 0;
        ComboLightArchitecture.SelectedIndex = 0;
        ComboExportIsolation.SelectedIndex = 0;
        CheckOptionsPclCustom.Checked = true;
        CheckIncludeVersionJava.Checked = false;
        _instanceJava = null;
        try { _instanceJava = ModPackExportJava.Find(PageInstanceLeft.McInstance); }
        catch (Exception ex) { ModBase.Log(ex, "检查实例内 Java 失败", ModBase.LogLevel.Debug); }
        CheckIncludeVersionJava.IsEnabled = _instanceJava is not null;
        LabVersionJava.Text = _instanceJava is null ? Lang.Text("Instance.Export.Java.Missing")
            : $"{_instanceJava.Installation.ToDetailedString()} · {_instanceJava.RelativeExePath}";
        CheckAdvancedInclude.Checked = false;
        CheckAdvancedModrinth.Checked = false;
        GetExportOption(CheckOptionsBasic).Description = PageInstanceLeft.McInstance.GetDefaultDescription();
        ResetConfigOverrides();
        ReloadAllSubOptions();
        RefreshAllOptionsUI();
        PanBack.ScrollToHome();
    }

    // 自动填写整合包名称
    private void TextExportName_GotFocus(object sender, RoutedEventArgs routedEventArgs)
    {
        if (string.IsNullOrEmpty(TextExportName.Text))
        {
            TextExportName.Text = TextExportName.HintText;
            TextExportName.SelectionStart = TextExportName.Text.Length;
        }
    }

    // 勾选 Modrinth 上传模式时，禁止打包 PCL
    private void CheckAdvancedModrinth_Change(object sender, bool user)
    {
        if (CheckAdvancedModrinth.Checked == true)
        {
            ComboLauncherType.SelectedIndex = 0;
            CheckAdvancedInclude.Checked = false;
            CheckIncludeVersionJava.Checked = false;
        }
        ComboLauncherType.IsEnabled = CheckAdvancedModrinth.Checked != true;
        CheckIncludeVersionJava.IsEnabled = _instanceJava is not null && CheckAdvancedModrinth.Checked != true;
    }

    // 勾选"其他文件夹"时，同步勾选/取消所有子选项
    private void CheckOptionsOtherFolders_Change(object sender, bool user)
    {
        if (!user) return;
        foreach (var child in PanOptionsOtherFolders.Children)
            if (child is MyCheckBox childBox)
                childBox.Checked = CheckOptionsOtherFolders.Checked;
    }

    // 勾选打包资源文件时，禁止开启 Modrinth 上传模式
    private void CheckAdvancedInclude_Change(object sender, bool user)
    {
        if (CheckAdvancedInclude.Checked == true)
            CheckAdvancedModrinth.Checked = false;
        CheckAdvancedModrinth.IsEnabled = (bool)!CheckAdvancedInclude.Checked;
    }


    private readonly string[] subOptionBlackList = new[] { "Quark Programmer Art.zip", "+ EuphoriaPatches_" };



    private const string sperator = "==============================================================";

    // ================ 导出内容段 ================

    /// <summary>
    ///     从配置文件中读取的规则。
    ///     如果不为 Nothing，则会覆写当前勾选的规则并禁用对应 UI。
    /// </summary>
    private List<string> RulesOverrides
    {
        get => field;
        set
        {
            field = value;
            if (value is null)
            {
                BtnOverrideCancel.Visibility = Visibility.Collapsed;
                PanOptions.Visibility = Visibility.Visible;
                CardOptions.Inlines.Clear();
                CardOptions.Inlines.Add(new Run(Lang.Text("Instance.Export.OptionListTitle")) { FontWeight = FontWeights.Bold });
            }
            else
            {
                BtnOverrideCancel.Visibility = Visibility.Visible;
                PanOptions.Visibility = Visibility.Collapsed;
                CardOptions.Inlines.Clear();
                CardOptions.Inlines.Add(new Run(Lang.Text("Instance.Export.OptionListTitle") + ":    ") { FontWeight = FontWeights.Bold });
                CardOptions.Inlines.Add(new Run(Lang.Text("Instance.Export.OptionList.FromConfig")) { FontWeight = FontWeights.Normal });
            }
        }
    }

    /// <summary>
    ///     获取当前实际生效的所有规则。
    /// </summary>
    private IEnumerable<string> GetAllRules()
    {
        if (RulesOverrides is not null)
        {
            // 返回覆盖的列表
            foreach (var Rule in RulesOverrides)
                yield return Rule;
        }
        else
        {
            // 从当前勾选的所有选项中获取所有规则行
            yield return "";
            yield return "# " + Lang.Text("Instance.Export.Config.Comment.ModifyRules");
            yield return "# " + Lang.Text("Instance.Export.Config.Comment.ReverseMatch");
            yield return "";
            foreach (var CheckBox in GetAllOptions(false))
            {
                if (CheckBox.Checked == false)
                    continue;
                var targetOption = GetExportOption(CheckBox);
                if (targetOption.Rules is null)
                    continue;
                yield return $"# {targetOption.Title}";
                foreach (var Rule in targetOption.Rules.Split('|'))
                    yield return Rule;
                yield return "";
            }

            yield return "# " + Lang.Text("Instance.Export.Config.Comment.ExcludedFiles");
            yield return "!*.log";
            yield return "!*.dat_old";
            yield return "!*.BakaCoreInfo";
            yield return "!hmclversion.cfg";
            yield return "!log4j2.xml";
            yield return "";
        }
    }

    // ================ 追加内容段 ================

    private List<string> extraFiles;

    /// <summary>
    ///     获取当前实际生效的追加内容。
    /// </summary>
    private IEnumerable<string> GetExtraFileLines()
    {
        if (extraFiles is not null)
        {
            // 返回覆盖的列表
            foreach (var File in extraFiles)
                yield return File;
        }
        else
        {
            // 从当前勾选的所有选项中获取所有规则行
            yield return "";
            yield return "# " + Lang.Text("Instance.Export.Config.Comment.ExtraFiles");
            yield return "# " + Lang.Text("Instance.Export.Config.Comment.ExtraFiles2");
            yield return "";
        }
    }

    // ================ 重置 ================

    /// <summary>
    ///     重置配置文件所带来的影响。
    /// </summary>
    private void ResetConfigOverrides()
    {
        RulesOverrides = null;
        configPackPath = null;
        extraFiles = null;
        PanBack.ScrollToHome();
    }

    private void CardOptions_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (RulesOverrides is null)
            return;
        ResetConfigOverrides();
    }




    /// <summary>
    ///     配置文件中指定的导出位置。
    /// </summary>
    private string configPackPath;
}
