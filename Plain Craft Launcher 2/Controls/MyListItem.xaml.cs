using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using PCL.Core.UI.Controls.SvgIcon;

namespace PCL;

[ContentProperty("Inlines")]
public partial class MyListItem : IMyRadio
{

    public bool isMouseOverAnimationEnabled = true;

    private string stateLast;
    public event IMyRadio.CheckEventHandler? Check;
    public event IMyRadio.ChangedEventHandler? Changed;

    public event ClickEventHandler? Click;
    public event LogoClickEventHandler? LogoClick;


    // 指向背景
    private Border _RectBack;

    // 按钮
    public FrameworkElement buttonStack;

    // 图标
    public FrameworkElement pathLogo;

    // 勾选条
    public Border rectCheck;



    // Uuid
    public int Uuid = ModBase.GetUuid();

    /// <summary>
    ///     是否启用缩放动画。
    /// </summary>
    public bool IsScaleAnimationEnabled
    {
        get => field;
        set
        {
            field = value;
            if (_RectBack is not null)
                RectBack.CornerRadius = new CornerRadius(value ? 6 : 0);
        }
    } = true;

    /// <summary>
    ///     右边距的最小值。
    ///     在存在右侧按钮时，右边距会被自动设置为 5 + 按钮数 * 25。
    /// </summary>
    public int MinPaddingRight { get; set; } = 4;

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register("Title", typeof(string), typeof(MyListItem));

    public static readonly DependencyProperty FontSizeProperty =
        DependencyProperty.Register("FontSize", typeof(double), typeof(MyListItem), new PropertyMetadata(14d));

    public static readonly DependencyProperty InfoProperty = DependencyProperty.Register("Info", typeof(string),
        typeof(MyListItem), new PropertyMetadata("", OnInfoChanged));

    public MyListItem()
    {
        InitializeComponent();

        SizeChanged += (_, _) => OnSizeChanged();
        PreviewMouseLeftButtonUp += Button_MouseUp;
        PreviewMouseLeftButtonDown += Button_MouseDown;
        MouseLeave += Button_MouseLeave;
        PreviewMouseLeftButtonUp += Button_MouseLeave;
        MouseEnter += RefreshColor;
        MouseLeave += RefreshColor;
        MouseLeftButtonDown += RefreshColor;
        MouseLeftButtonUp += RefreshColor;
        Loaded += MyListItem_Loaded;
        Loaded += TahoeNavigationLoaded;
        Unloaded += TahoeNavigationUnloaded;
    }

    public static readonly DependencyProperty LogoProperty = DependencyProperty.Register(
        nameof(Logo),
        typeof(string),
        typeof(MyListItem),
        new PropertyMetadata("", OnLogoChanged));

    public static readonly DependencyProperty SvgIconProperty = DependencyProperty.Register(
        nameof(SvgIcon),
        typeof(string),
        typeof(MyListItem),
        new PropertyMetadata("", OnSvgIconChanged));

    public double LogoScale
    {
        get => field;
        set
        {
            field = value;
            if (pathLogo is not null)
            {
                var scale = IsUsingSvgIcon ? 1D : LogoScale;
                pathLogo.RenderTransform = new ScaleTransform { ScaleX = scale, ScaleY = scale };
            }
        }
    } = 1d;

    // 图标的点击
    /// <summary>
    ///     该 Logo 是否可用点击触发事件。需要在 Logo 属性之前设置。
    /// </summary>
    public bool LogoClickable { get; set; } = false;

    private bool isLogoDown;

    private CheckType _Type = CheckType.None;

    // 勾选状态
    private bool _Checked;

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register("Foreground",
        typeof(Brush), typeof(MyListItem), new PropertyMetadata(ThemeManager.AppResources["ColorBrush1"]));
    
    /// <summary>
    /// 图标可选的圆角。仅当 Logo 为位图（MyImage 或 Canvas）时生效。
    /// 当所有角的圆角半径均 ≥ 0 时，应用圆角效果；否则不应用。
    /// </summary>
    public CornerRadius LogoCornerRadius
    {
        get => field;
        set
        {
            field = value;
            if (pathLogo is Canvas) _canvasClipHandlerAdded = false;
            ApplyLogoCornerRadius();
        }
    } = new CornerRadius(-1);

    private bool _canvasClipHandlerAdded = false;

    // 鼠标点击判定
    private bool isMouseDown;

}
