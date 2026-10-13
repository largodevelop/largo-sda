using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace LargoSda.App;

/// <summary>Steam-style code entry: five underlined cells, auto-uppercase, raises <see cref="Completed"/> on the 5th char.</summary>
public partial class CodeInput : UserControl
{
    public const int Length = 5;

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<CodeInput, string?>(nameof(Text), defaultBindingMode: BindingMode.TwoWay);

    public event EventHandler? Completed;

    private readonly Border[] _cells = new Border[Length];
    private readonly TextBlock[] _chars = new TextBlock[Length];

    public CodeInput()
    {
        InitializeComponent();
        for (var i = 0; i < Length; i++)
        {
            _chars[i] = new TextBlock();
            _cells[i] = new Border { Child = _chars[i], Classes = { "cell" } };
            Cells.Children.Add(_cells[i]);
        }

        Input.TextChanged += (_, _) => OnInputChanged();
        Input.GotFocus += (_, _) => Render();
        Input.LostFocus += (_, _) => Render();
        Input.KeyDown += OnKeyDown;
        AddHandler(PointerPressedEvent, (_, _) => FocusInput(), RoutingStrategies.Tunnel);
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(FocusInput);
        Render();
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public void FocusInput()
    {
        Input.Focus();
        Input.CaretIndex = Input.Text?.Length ?? 0;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty && Input.Text != Text)
            Input.Text = Text;
        else if (change.Property == IsVisibleProperty && IsVisible)
            Dispatcher.UIThread.Post(FocusInput);
    }

    private void OnInputChanged()
    {
        var clean = new string((Input.Text ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).Take(Length).ToArray());
        if (Input.Text != clean)
        {
            Input.Text = clean;
            Input.CaretIndex = clean.Length;
            return;
        }

        Text = clean;
        Render();
        if (clean.Length == Length) Completed?.Invoke(this, EventArgs.Empty);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Typing always appends at the end, like separate boxes.
        if (e.Key is Key.Left or Key.Right or Key.Home or Key.End) e.Handled = true;
        if (e.Key == Key.Enter && (Input.Text?.Length ?? 0) == Length) Completed?.Invoke(this, EventArgs.Empty);
    }

    private void Render()
    {
        var text = Input.Text ?? "";
        var focused = Input.IsFocused;
        for (var i = 0; i < Length; i++)
        {
            _chars[i].Text = i < text.Length ? text[i].ToString() : "";
            _cells[i].Classes.Set("active", focused && i == Math.Min(text.Length, Length - 1));
        }
    }
}
