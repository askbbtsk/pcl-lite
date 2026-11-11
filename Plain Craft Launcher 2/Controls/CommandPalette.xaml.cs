using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PCL.Core.App.Localization;
using PCL.Core.UI.Navigation;

namespace PCL;

public sealed record PaletteCommand(CommandPaletteEntry Entry, Action Execute)
{
    public string Title => Entry.Title;
    public string Description => Entry.Description;
    public string Category => Entry.Category;
}

public partial class CommandPalette : UserControl
{
    private readonly Dictionary<string, PaletteCommand> _commands = new(StringComparer.Ordinal);
    private IInputElement? _previousFocus;
    public event Action? Dismissed;
    public event Action<Exception>? ExecutionFailed;

    public CommandPalette()
    {
        InitializeComponent();
        Query.Loaded += (_, _) =>
        {
            if (Query.Template.FindName("labHint", Query) is TextBlock hint)
            {
                hint.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1");
                hint.Opacity = 0.8;
            }
        };
        SizeChanged += (_, _) => Surface.MaxHeight = Math.Max(180, ActualHeight - 48);
        PreviewKeyDown += HandleKey;
        // Consume bubbling shortcuts after the input/list controls have processed them.
        KeyDown += (_, e) => e.Handled = true;
    }

    public void Open(IEnumerable<PaletteCommand> commands)
    {
        ApplyContrastResources();
        _previousFocus = Keyboard.FocusedElement;
        _commands.Clear();
        foreach (var command in commands) _commands.TryAdd(command.Entry.Id, command);
        Query.Clear();
        Status.SetResourceReference(TextBlock.TextProperty, "CommandPalette.Loading");
        Visibility = Visibility.Visible;
        RefreshResults();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (Visibility == Visibility.Visible) Query.Focus();
        }));
    }

    private void ApplyContrastResources()
    {
        string[] keys = ["ColorBrushBackground", "ColorBrush1", "ColorBrushGray7", "ColorBrush7", "ColorBrush3"];
        foreach (var key in keys) Resources.Remove(key);
        if (!SystemParameters.HighContrast) return;
        Resources["ColorBrushBackground"] = SystemColors.WindowBrush;
        Resources["ColorBrush1"] = SystemColors.WindowTextBrush;
        Resources["ColorBrushGray7"] = SystemColors.WindowBrush;
        Resources["ColorBrush7"] = SystemColors.WindowBrush;
        Resources["ColorBrush3"] = SystemColors.HighlightBrush;
    }

    public void AddCommands(IEnumerable<PaletteCommand> commands)
    {
        foreach (var command in commands) _commands.TryAdd(command.Entry.Id, command);
        RefreshResults();
    }

    public void CompleteLoading(string? status = null)
    {
        Status.Text = status ?? Lang.Text("CommandPalette.Keys");
    }

    public void Close()
    {
        if (Visibility != Visibility.Visible) return;
        Visibility = Visibility.Collapsed;
        _commands.Clear();
        Results.ItemsSource = null;
        Dismissed?.Invoke();
        if (_previousFocus is UIElement { IsVisible: true, IsEnabled: true } element) element.Focus();
        _previousFocus = null;
    }

    private void Query_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (Results is not null) RefreshResults();
    }

    private void RefreshResults()
    {
        var selectedId = (Results.SelectedItem as PaletteCommand)?.Entry.Id;
        var matches = CommandPaletteSearch.Search(_commands.Values.Select(command => command.Entry), Query.Text);
        Results.ItemsSource = matches.Select(entry => _commands[entry.Id]).ToArray();
        Results.SelectedIndex = Math.Max(0, matches.ToList().FindIndex(entry => entry.Id == selectedId));
        Empty.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (Results.SelectedItem is not null) Results.ScrollIntoView(Results.SelectedItem);
    }

    private void HandleKey(object sender, KeyEventArgs e)
    {
        // Do not interpret Enter while an IME is composing text.
        if (e.Key == Key.ImeProcessed || e.Key == Key.DeadCharProcessed) return;
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Enter:
                if (Keyboard.FocusedElement is Button) return;
                e.Handled = true;
                if (!e.IsRepeat) ExecuteSelected();
                break;
            case Key.Down:
            case Key.Up:
                if (Results.Items.Count > 0)
                {
                    Results.SelectedIndex = Math.Clamp(Results.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, Results.Items.Count - 1);
                    Results.ScrollIntoView(Results.SelectedItem);
                }
                e.Handled = true;
                break;
        }
    }

    private void ExecuteSelected()
    {
        if (Results.SelectedItem is not PaletteCommand command) return;
        Close();
        try { command.Execute(); }
        catch (Exception ex) { ExecutionFailed?.Invoke(ex); }
    }

    private void Results_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(Results, e.OriginalSource as DependencyObject) is not ListBoxItem item) return;
        Results.SelectedItem = item.Content;
        e.Handled = true;
        ExecuteSelected();
    }

    private void Backdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender)) Close();
        e.Handled = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
