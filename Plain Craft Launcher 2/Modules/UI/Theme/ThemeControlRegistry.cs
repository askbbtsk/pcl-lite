using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using PCL.Core.UI.Theme;

namespace PCL;

/// <summary>
/// Adapts visuals in place. Input stays on existing PCL controls; theme presenters
/// carry only documented display state and never become a replacement button.
/// </summary>
internal static class ThemeControlRegistry
{
    private static readonly ConditionalWeakTable<FrameworkElement, Entry> Entries = new();
    private static readonly List<WeakReference<Entry>> Active = [];
    private static FormMain? _window;
    private static bool _registered, _refreshing;
    public static readonly DependencyProperty ProtectedProperty = DependencyProperty.RegisterAttached(
        "Protected", typeof(bool), typeof(ThemeControlRegistry),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static void SetProtected(DependencyObject target, bool value) => target.SetValue(ProtectedProperty, value);
    private static bool IsProtected(DependencyObject target) => (bool)target.GetValue(ProtectedProperty);

    public static void Initialize(FormMain window, CustomThemeRuntime runtime)
    {
        _window = window;
        if (!_registered)
        {
            EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnLoaded), true);
            _registered = true;
        }
    }
    public static bool HasTemplate(FrameworkElement element) =>
        !IsProtected(element) && !SystemParameters.HighContrast
        && CustomThemeRuntime.Current?.ActiveTheme is not null && Role(element) is { } role
        && Resource("Pcl.Theme.Template." + role) is ControlTemplate;
    public static Grid? ContentGrid(FrameworkElement element) =>
        Entries.TryGetValue(element, out var entry) ? entry.ContentGrid : null;
    public static object? Resource(string key, object? fallback = null) =>
        _window?.PanForm.TryFindResource(key) ?? fallback;
    public static void RefreshState(FrameworkElement element)
    {
        if (Entries.TryGetValue(element, out var entry)) entry.UpdateState();
    }

    private static void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_window is null || CustomThemeRuntime.Current?.ActiveTheme is null || sender is not FrameworkElement element
            || IsProtected(element) || !BelongsToWindow(element)) return;
        try { Attach(element); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { CustomThemeRuntime.Current?.RecoverVisualFailure(error); }
    }

    private static bool BelongsToWindow(FrameworkElement element) =>
        ReferenceEquals(Window.GetWindow(element), _window)
        || element.TemplatedParent is FrameworkElement parent && ReferenceEquals(Window.GetWindow(parent), _window);

    private static void Attach(FrameworkElement element)
    {
        if (Role(element) is null || Entries.TryGetValue(element, out _)) return;
        var entry = new Entry(element, Role(element)!);
        Entries.Add(element, entry);
        Active.Add(new WeakReference<Entry>(entry));
        element.Unloaded += OnUnloaded;
        entry.Apply();
    }
    private static void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;
        element.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            // Retemplating can briefly unload and reload the same subtree. Do
            // not tear down its state during that transient reparenting.
            if (element.IsLoaded || !Entries.TryGetValue(element, out var entry)) return;
            element.Unloaded -= OnUnloaded;
            try { entry.Restore(); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { CustomThemeRuntime.Current?.RecoverVisualFailure(error); }
            Entries.Remove(element);
        });
    }

    public static void Refresh()
    {
        if (_window is null || _refreshing) return;
        _refreshing = true;
        try
        {
            for (int index = Active.Count - 1; index >= 0; index--)
            {
                if (!Active[index].TryGetTarget(out var entry) || !Entries.TryGetValue(entry.Host, out var current)
                    || !ReferenceEquals(current, entry)) { Active.RemoveAt(index); continue; }
                entry.Apply();
            }
            foreach (var element in Descendants(_window.PanForm).OfType<FrameworkElement>().ToArray())
            {
                if (!IsProtected(element)) Attach(element);
                if (element is PCL.Core.UI.Controls.SvgIcon.SvgIcon) element.InvalidateVisual();
            }
            ApplyRootResources();
        }
        finally { _refreshing = false; }
    }

    private static readonly Dictionary<DependencyProperty, object> RootOriginal = [];
    private static readonly Dictionary<DependencyProperty, object> RootLocal = [];
    private static bool _backgroundApplied;
    private static void ApplyRootResources()
    {
        if (_window is null) return;
        foreach (var setting in new[]
        {
            ("LaunchFontFamily", TextElement.FontFamilyProperty),
            ("Pcl.Theme.FontSize", TextElement.FontSizeProperty),
            ("ColorBrush1", TextElement.ForegroundProperty)
        })
        {
            if (!RootOriginal.ContainsKey(setting.Item2))
            {
                RootOriginal[setting.Item2] = _window.PanForm.GetValue(setting.Item2);
                RootLocal[setting.Item2] = _window.PanForm.ReadLocalValue(setting.Item2);
            }
            if (CustomThemeRuntime.Current?.ActiveTheme is null || SystemParameters.HighContrast)
            {
                if (RootLocal[setting.Item2] == DependencyProperty.UnsetValue)
                    _window.PanForm.ClearValue(setting.Item2);
                else _window.PanForm.SetCurrentValue(setting.Item2, RootOriginal[setting.Item2]);
                if (SystemParameters.HighContrast && setting.Item2 == TextElement.ForegroundProperty)
                    _window.PanForm.SetCurrentValue(setting.Item2, SystemColors.WindowTextBrush);
                continue;
            }
            object value = CustomThemeRuntime.Current?.ActiveTheme is not null && !SystemParameters.HighContrast
                ? Resource(setting.Item1, RootOriginal[setting.Item2])! : RootOriginal[setting.Item2];
            if (SystemParameters.HighContrast && setting.Item2 == TextElement.ForegroundProperty)
                value = SystemColors.WindowTextBrush;
            _window.PanForm.SetCurrentValue(setting.Item2, value);
        }
        Brush? background = !SystemParameters.HighContrast
            ? CustomThemeRuntime.Current?.FindOverride("ColorBrushBackground") as Brush : null;
        if (background is not null)
        {
            _window.LauncherBackgroundLayer.SetCurrentValue(Border.BackgroundProperty, background);
            _backgroundApplied = true;
        }
        else if (_backgroundApplied)
        {
            _backgroundApplied = false;
            // Recompute from the user's current settings, including changes made
            // while a package was active, rather than restoring a stale brush.
            ThemeManager.RefreshBackground();
        }
    }

    public static void Detach(FormMain window)
    {
        foreach (var weak in Active)
            if (weak.TryGetTarget(out var entry))
            {
                entry.Host.Unloaded -= OnUnloaded;
                entry.Restore();
                Entries.Remove(entry.Host);
            }
        Active.Clear();
        RootOriginal.Clear();
        RootLocal.Clear();
        _backgroundApplied = false;
        if (ReferenceEquals(window, _window)) _window = null;
    }

    internal static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static string? Role(FrameworkElement element) => element switch
    {
        MyButton => "Button", MyIconButton => "IconButton", MyIconTextButton => "IconTextButton",
        MyExtraButton => "ExtraButton", MyExtraTextButton => "ExtraTextButton",
        MyTextBox => "TextBox", MyComboBox => "ComboBox", MyComboBoxItem => "ComboBoxItem",
        MyRadioButton => "Navigation", MyCheckBox => "CheckBox", MyRadioBox => "RadioBox",
        MyListItem => "ListItem", MyCard => "Card", MySlider => "Slider",
        MyScrollBar => "ScrollBar", MyScrollViewer => "ScrollViewer",
        MyHint => "Hint", MyToast => "Toast", MyMenuItem => "MenuItem", MyTextButton => "TextButton",
        MyMsgText or MyMsgInput or MyMsgSelect or MyMsgMarkdown or MyMsgLogin or MyMsgAuthServer => "Dialog",
        _ => null
    };

    public static void ValidateTemplates(ResourceDictionary dictionary)
    {
        foreach (var nested in dictionary.MergedDictionaries) ValidateTemplates(nested);
        foreach (object key in dictionary.Keys)
        {
            if (dictionary[key] is not ControlTemplate template) continue;
            Type? hostType = key?.ToString() switch
            {
                "Pcl.Theme.Template.TextBox" => typeof(MyTextBox),
                "Pcl.Theme.Template.ComboBox" => typeof(MyComboBox),
                "Pcl.Theme.Template.ComboBoxItem" => typeof(MyComboBoxItem),
                "Pcl.Theme.Template.ScrollBar" => typeof(MyScrollBar),
                "Pcl.Theme.Template.ScrollViewer" => typeof(MyScrollViewer),
                "Pcl.Theme.Template.MenuItem" => typeof(MyMenuItem),
                "Pcl.Theme.Template.TextButton" => typeof(MyTextButton), _ => null
            };
            if (hostType is not null && !template.TargetType.IsAssignableFrom(hostType))
                throw new InvalidDataException($"{key} has an incompatible target type.");
            if (key is string role && role.StartsWith("Pcl.Theme.Template.", StringComparison.Ordinal)
                && role is not ("Pcl.Theme.Template.TextBox" or "Pcl.Theme.Template.ComboBox"
                    or "Pcl.Theme.Template.ComboBoxItem" or "Pcl.Theme.Template.ScrollBar"
                    or "Pcl.Theme.Template.ScrollViewer" or "Pcl.Theme.Template.MenuItem"
                    or "Pcl.Theme.Template.TextButton")
                && template.TargetType != typeof(ThemePresenter))
                throw new InvalidDataException($"{key} must target ThemePresenter.");
            if (template.LoadContent() is not FrameworkElement root) continue;
            var parts = TemplateElements(root).ToArray();
            if (key?.ToString()?.EndsWith(".TextBox", StringComparison.Ordinal) == true
                && !parts.Any(part => part.Name == "PART_ContentHost" && part is ScrollViewer))
                throw new InvalidDataException("TextBox requires PART_ContentHost as a ScrollViewer.");
            if (key?.ToString()?.EndsWith(".ComboBox", StringComparison.Ordinal) == true
                && (!parts.Any(part => part.Name == "PART_Popup" && part is Popup)
                    || !parts.Any(part => part.Name == "PART_EditableTextBox" && part is MyTextBox)
                    || !parts.Any(part => part.Name == "PART_Content" && part is ContentPresenter)
                    || !parts.Any(part => part.Name == "PanPopup" && part is Grid)))
                throw new InvalidDataException("ComboBox requires PART_Popup, MyTextBox PART_EditableTextBox, PART_Content and PanPopup.");
        }
    }

    private static IEnumerable<FrameworkElement> TemplateElements(DependencyObject root)
    {
        var pending = new Stack<DependencyObject>();
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        pending.Push(root);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current)) continue;
            if (current is FrameworkElement element) yield return element;
            if (current is Visual)
                for (int index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
                    pending.Push(VisualTreeHelper.GetChild(current, index));
            foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>()) pending.Push(child);
        }
    }

    private sealed class Entry(FrameworkElement host, string role)
    {
        public FrameworkElement Host { get; } = host;
        public bool HasTemplate => _presenter is not null || _controlTemplateApplied;
        public Grid? ContentGrid { get; private set; }
        private ThemePresenter? _presenter;
        private System.Windows.Shapes.Path? _originalIcon;
        private UIElement? _originalChild;
        private ControlTemplate? _originalTemplate;
        private object? _templateLocal;
        private bool _controlTemplateApplied;
        private readonly List<Action> _restore = [];
        private readonly List<(DependencyPropertyDescriptor Descriptor, EventHandler Handler)> _stateSubscriptions = [];

        public void Apply()
        {
            if (IsProtected(Host)) { Restore(); return; }
            ControlTemplate? template = CustomThemeRuntime.Current?.ActiveTheme is not null && !SystemParameters.HighContrast
                ? Resource("Pcl.Theme.Template." + role) as ControlTemplate : null;
            if (template is null) { Restore(); return; }
            if (Host is Control control)
            {
                if (!_controlTemplateApplied)
                {
                    _templateLocal = control.ReadLocalValue(Control.TemplateProperty);
                    _originalTemplate = control.Template;
                    // Preserve caller-supplied local bindings/resource expressions.
                    if (DependencyPropertyHelper.GetValueSource(control, Control.TemplateProperty).IsExpression) return;
                    _controlTemplateApplied = true;
                }
                if (!ReferenceEquals(control.Template, template))
                {
                    int selection = control is MyTextBox text ? text.SelectionStart : 0;
                    int length = control is MyTextBox selected ? selected.SelectionLength : 0;
                    control.SetCurrentValue(Control.TemplateProperty, template);
                    control.ApplyTemplate();
                    if (control is MyTextBox editor) editor.Select(Math.Min(selection, editor.Text.Length),
                        Math.Min(length, editor.Text.Length - Math.Min(selection, editor.Text.Length)));
                }
                return;
            }
            if (_presenter is null) CreatePresenter();
            if (_presenter is null) return;
            _presenter.Template = template;
            _presenter.ApplyTemplate();
            _presenter.SetCurrentValue(Control.PaddingProperty,
                Resource("Pcl.Theme." + role + ".Padding", new Thickness(0))!);
            _presenter.SetCurrentValue(ThemePresenter.CornerRadiusProperty,
                Resource("Pcl.Theme." + role + ".CornerRadius", new CornerRadius(3))!);
            UpdateState();
        }

        private void CreatePresenter()
        {
            _originalIcon = Descendants(Host).OfType<System.Windows.Shapes.Path>().FirstOrDefault();
            _presenter = new ThemePresenter { Role = role, IsHitTestVisible = true, Focusable = false };
            if (Host is MyCard card)
            {
                // The card's public Children collection and collapse target retain
                // their identities. Only its background/chrome is templated.
                _originalChild = card.BorderChild;
                card.BorderChild = _presenter;
                _presenter.Content = _originalChild;
                Save(card.MainChrome, UIElement.OpacityProperty, 0d);
                _restore.Add(() => { card.BorderChild = _originalChild; });
            }
            else if (Host is Border border)
            {
                _originalChild = border.Child;
                border.Child = null;
                _presenter.Content = _originalChild;
                border.Child = _presenter;
                _restore.Add(() => { border.Child = null; border.Child = _originalChild; });
                Save(border, Border.BackgroundProperty, Brushes.Transparent);
                Save(border, Border.BorderThicknessProperty, new Thickness(0));
                if (Host is MyButton button)
                {
                    Save(button.PanFore, Border.BackgroundProperty, Brushes.Transparent);
                    Save(button.PanFore, Border.BorderThicknessProperty, new Thickness(0));
                    ModAnimation.AniStop("MyButton Color " + button.Uuid);
                }
            }
            else if (Host is Grid grid)
            {
                // Static layout nodes keep their original names/objects/bindings.
                // MyListItem directs later dynamic child additions to ContentGrid.
                ContentGrid = new Grid();
                foreach (var column in grid.ColumnDefinitions.ToArray())
                { grid.ColumnDefinitions.Remove(column); ContentGrid.ColumnDefinitions.Add(column); }
                foreach (var row in grid.RowDefinitions.ToArray())
                { grid.RowDefinitions.Remove(row); ContentGrid.RowDefinitions.Add(row); }
                foreach (UIElement child in grid.Children.Cast<UIElement>().ToArray())
                { grid.Children.Remove(child); ContentGrid.Children.Add(child); }
                _presenter.Content = ContentGrid;
                grid.Children.Add(_presenter);
                _restore.Add(() =>
                {
                    grid.Children.Remove(_presenter!);
                    foreach (var column in ContentGrid.ColumnDefinitions.ToArray())
                    { ContentGrid.ColumnDefinitions.Remove(column); grid.ColumnDefinitions.Add(column); }
                    foreach (var row in ContentGrid.RowDefinitions.ToArray())
                    { ContentGrid.RowDefinitions.Remove(row); grid.RowDefinitions.Add(row); }
                    foreach (UIElement child in ContentGrid.Children.Cast<UIElement>().ToArray())
                    { ContentGrid.Children.Remove(child); grid.Children.Add(child); }
                    ContentGrid = null;
                });
            }
            else { _presenter = null; return; }
            Host.IsEnabledChanged += StateChanged;
            Host.IsMouseDirectlyOverChanged += StateChanged;
            Host.IsKeyboardFocusWithinChanged += StateChanged;
            Host.PreviewMouseLeftButtonDown += MouseState;
            Host.PreviewMouseLeftButtonUp += MouseState;
            Host.MouseLeave += MouseState;
            Host.MouseEnter += MouseState;
            if (Host is IMyRadio radio) radio.Changed += RadioChanged;
            if (Host is MyRadioButton navigation) navigation.Check += NavigationChanged;
            if (Host is MySlider slider) slider.Change += SliderChanged;
            if (Host is MyCard cardHost) cardHost.Swap += CardChanged;
            Host.PreviewKeyDown += KeyState;
            Host.PreviewKeyUp += KeyState;
            foreach (var property in StateProperties())
            {
                var descriptor = DependencyPropertyDescriptor.FromProperty(property, Host.GetType());
                EventHandler handler = (_, _) => UpdateState();
                descriptor.AddValueChanged(Host, handler);
                _stateSubscriptions.Add((descriptor, handler));
            }
            ApplyTextResources();
        }

        private void ApplyTextResources()
        {
            foreach (var text in Descendants(Host).OfType<TextBlock>())
            {
                if (Resource("Pcl.Theme." + role + ".FontSize") is double size)
                    Save(text, TextBlock.FontSizeProperty, size);
                if (Resource("Pcl.Theme." + role + ".FontFamily") is FontFamily font)
                    Save(text, TextBlock.FontFamilyProperty, font);
            }
        }

        private IEnumerable<DependencyProperty> StateProperties()
        {
            if (Host is MyButton) yield return MyButton.TextProperty;
            if (Host is MyRadioButton) yield return MyRadioButton.TextProperty;
            if (Host is MyCard) yield return MyCard.TitleProperty;
            if (Host is MyListItem) { yield return MyListItem.TitleProperty; yield return MyListItem.InfoProperty; }
            if (Host is MyCheckBox) yield return MyCheckBox.CheckedProperty;
            if (Host is MyCheckBox) yield return MyCheckBox.TextProperty;
            if (Host is MyRadioBox) { yield return MyRadioBox.CheckedProperty; yield return MyRadioBox.TextProperty; }
        }
        private void StateChanged(object sender, DependencyPropertyChangedEventArgs args) => UpdateState();
        private void RadioChanged(object sender, ModBase.RouteEventArgs args) => UpdateState();
        private void NavigationChanged(MyRadioButton sender, bool user) => UpdateState();
        private void SliderChanged(object sender, bool user) => UpdateState();
        private void CardChanged(object sender, ModBase.RouteEventArgs args) => UpdateState();
        private void KeyState(object sender, KeyEventArgs args)
        {
            if (_presenter is not null && args.Key is Key.Space or Key.Enter)
                _presenter.IsPressed = args.IsDown;
            UpdateState();
        }
        private void MouseState(object sender, MouseEventArgs args)
        {
            if (_presenter is not null) _presenter.IsPressed = Host.IsMouseOver && Mouse.LeftButton == MouseButtonState.Pressed;
            UpdateState();
        }
        public void UpdateState()
        {
            if (_presenter is null) return;
            _presenter.IsEnabled = Host.IsEnabled;
            _presenter.HasFocus = Host.IsKeyboardFocusWithin;
            _presenter.IsHovered = Host.IsMouseOver;
            _presenter.IsGlass = LiquidGlassCore.IsEnabled;
            _presenter.Title = Host switch
            {
                MyButton button => button.Text, MyCard titleCard => titleCard.Title, MyListItem item => item.Title,
                MyRadioBox radio => radio.Text, MyCheckBox check => check.Text,
                MyRadioButton navigation => navigation.Text, _ => ""
            };
            _presenter.Description = Host is MyListItem list ? list.Info : "";
            _presenter.IsChecked = Host switch
            {
                MyCheckBox check => check.Checked == true, MyRadioBox radio => radio.Checked,
                MyListItem item => item.Checked, MyRadioButton navigation => navigation.Checked, _ => false
            };
            _presenter.IsSelected = _presenter.IsChecked;
            _presenter.IsExpanded = Host is MyCard card && !card.IsSwapped;
            _presenter.IsHighlighted = Host is MyButton highlighted && highlighted.ColorType == MyButton.ColorState.Highlight;
            _presenter.IsDanger = Host is MyButton danger && danger.ColorType == MyButton.ColorState.Red;
            _presenter.Progress = Host is MySlider slider && slider.MaxValue > 0 ? slider.Value / (double)slider.MaxValue : 0;
            _presenter.IconData = _originalIcon?.Data;
        }

        private void Save(DependencyObject target, DependencyProperty property, object value)
        {
            object original = target.GetValue(property);
            object local = target.ReadLocalValue(property);
            target.SetCurrentValue(property, value);
            _restore.Add(() =>
            {
                if (local == DependencyProperty.UnsetValue) target.ClearValue(property);
                else target.SetCurrentValue(property, original);
            });
        }

        public void Restore()
        {
            if (_controlTemplateApplied && Host is Control control)
            {
                if (_templateLocal == DependencyProperty.UnsetValue) control.ClearValue(Control.TemplateProperty);
                else control.SetCurrentValue(Control.TemplateProperty, _originalTemplate);
                control.ApplyTemplate();
                _controlTemplateApplied = false;
            }
            if (_presenter is null) return;
            Host.IsEnabledChanged -= StateChanged;
            Host.IsMouseDirectlyOverChanged -= StateChanged;
            Host.IsKeyboardFocusWithinChanged -= StateChanged;
            Host.PreviewMouseLeftButtonDown -= MouseState;
            Host.PreviewMouseLeftButtonUp -= MouseState;
            Host.MouseLeave -= MouseState;
            Host.MouseEnter -= MouseState;
            if (Host is IMyRadio radio) radio.Changed -= RadioChanged;
            if (Host is MyRadioButton navigation) navigation.Check -= NavigationChanged;
            if (Host is MySlider slider) slider.Change -= SliderChanged;
            if (Host is MyCard card) card.Swap -= CardChanged;
            Host.PreviewKeyDown -= KeyState;
            Host.PreviewKeyUp -= KeyState;
            foreach (var subscription in _stateSubscriptions)
                subscription.Descriptor.RemoveValueChanged(Host, subscription.Handler);
            _stateSubscriptions.Clear();
            // Reparenting restores children only after removing them from the presenter.
            _presenter.Content = null;
            foreach (var undo in _restore.AsEnumerable().Reverse()) undo();
            _restore.Clear();
            _presenter.Template = null;
            _presenter = null;
            _originalIcon = null;
        }
    }
}
