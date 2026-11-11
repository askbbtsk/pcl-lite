using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    private readonly List<PaletteCommand> _displayedCommands = [];
    private readonly List<Border> _resultRows = [];
    private int _selectedIndex = -1;
    private IInputElement? _previousFocus;
    public event Action? Dismissed;
    public event Action<Exception>? ExecutionFailed;

    public IReadOnlyList<PaletteCommand> DisplayedCommands => _displayedCommands;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set => SelectIndex(value);
    }

    public PaletteCommand? SelectedItem =>
        _selectedIndex >= 0 && _selectedIndex < _displayedCommands.Count
            ? _displayedCommands[_selectedIndex]
            : null;

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
        string[] keys = ["ColorBrushBackground", "ColorBrush1", "ColorBrushGray7", "ColorBrush7", "ColorBrush3", "ColorBrushHalfWhite"];
        foreach (var key in keys) Resources.Remove(key);
        if (!SystemParameters.HighContrast) return;
        Resources["ColorBrushBackground"] = SystemColors.WindowBrush;
        Resources["ColorBrush1"] = SystemColors.WindowTextBrush;
        Resources["ColorBrushGray7"] = SystemColors.WindowBrush;
        Resources["ColorBrush7"] = SystemColors.WindowBrush;
        Resources["ColorBrush3"] = SystemColors.HighlightBrush;
        Resources["ColorBrushHalfWhite"] = SystemColors.ControlLightBrush;
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
        _displayedCommands.Clear();
        _resultRows.Clear();
        _selectedIndex = -1;
        Results.Children.Clear();
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
        var selectedId = SelectedItem?.Entry.Id;
        var matches = CommandPaletteSearch.Search(_commands.Values.Select(command => command.Entry), Query.Text);
        _displayedCommands.Clear();
        _resultRows.Clear();
        Results.Children.Clear();

        for (var i = 0; i < matches.Count; i++)
        {
            var command = _commands[matches[i].Id];
            var rowIndex = i;
            var row = CreateResultRow(command, rowIndex);
            _displayedCommands.Add(command);
            _resultRows.Add(row);
            Results.Children.Add(row);
        }

        Empty.Visibility = _displayedCommands.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_displayedCommands.Count == 0)
        {
            _selectedIndex = -1;
            return;
        }

        var nextIndex = Math.Max(0, _displayedCommands.FindIndex(command => command.Entry.Id == selectedId));
        SelectIndex(nextIndex);
    }

    private Border CreateResultRow(PaletteCommand command, int index)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var contentStack = new StackPanel();
        contentStack.Children.Add(new TextBlock
        {
            Text = command.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = FontWeights.SemiBold
        });
        contentStack.Children.Add(new TextBlock
        {
            Text = command.Description,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0)
        });
        grid.Children.Add(contentStack);

        var categoryText = new TextBlock
        {
            Text = command.Category,
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12
        };
        Grid.SetColumn(categoryText, 1);
        grid.Children.Add(categoryText);

        var row = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 2, 0, 2),
            Cursor = Cursors.Hand,
            Focusable = false,
            Tag = command,
            Child = grid
        };
        AutomationProperties.SetName(row, command.Title);
        row.MouseEnter += (_, _) => UpdateRowVisual(index);
        row.MouseLeave += (_, _) => UpdateRowVisual(index);
        row.MouseLeftButtonUp += (_, e) =>
        {
            SelectIndex(index);
            e.Handled = true;
            ExecuteSelected();
        };
        return row;
    }

    private void SelectIndex(int index)
    {
        if (_displayedCommands.Count == 0)
        {
            _selectedIndex = -1;
            return;
        }

        var clamped = Math.Clamp(index, 0, _displayedCommands.Count - 1);
        var previous = _selectedIndex;
        _selectedIndex = clamped;
        if (previous >= 0 && previous < _resultRows.Count && previous != clamped)
            UpdateRowVisual(previous);
        if (clamped >= 0 && clamped < _resultRows.Count)
        {
            UpdateRowVisual(clamped);
            _resultRows[clamped].BringIntoView();
        }
    }

    private void UpdateRowVisual(int index)
    {
        if (index < 0 || index >= _resultRows.Count) return;
        var row = _resultRows[index];
        if (index == _selectedIndex)
        {
            row.SetResourceReference(Border.BackgroundProperty, "ColorBrush7");
            row.SetResourceReference(Border.BorderBrushProperty, "ColorBrush3");
        }
        else if (row.IsMouseOver)
        {
            row.SetResourceReference(Border.BackgroundProperty, "ColorBrushGray7");
            row.BorderBrush = Brushes.Transparent;
        }
        else
        {
            row.Background = Brushes.Transparent;
            row.BorderBrush = Brushes.Transparent;
        }
    }

    private void HandleKey(object sender, KeyEventArgs e)
    {
        // Do not interpret Enter while an IME is composing text.
        if (e.Key == Key.ImeProcessed || e.Key == Key.DeadCharProcessed) return;
        var isCloseFocused = ReferenceEquals(Keyboard.FocusedElement, CloseButton) || CloseButton.IsKeyboardFocusWithin;
        if (CommandPaletteSearch.ShouldActivateCloseButtonOnKey(e.Key, isCloseFocused))
        {
            e.Handled = true;
            if (!e.IsRepeat) Close();
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Enter:
                e.Handled = true;
                if (!e.IsRepeat) ExecuteSelected();
                break;
            case Key.Down:
            case Key.Up:
                if (_displayedCommands.Count > 0)
                    SelectIndex(Math.Clamp(_selectedIndex + (e.Key == Key.Down ? 1 : -1), 0, _displayedCommands.Count - 1));
                e.Handled = true;
                break;
        }
    }

    private void ExecuteSelected()
    {
        if (SelectedItem is not PaletteCommand command) return;
        Close();
        try { command.Execute(); }
        catch (Exception ex) { ExecutionFailed?.Invoke(ex); }
    }

    private void Backdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender)) Close();
        e.Handled = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
