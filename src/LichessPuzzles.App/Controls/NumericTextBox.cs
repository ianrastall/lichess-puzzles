using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Jupiter.LichessPuzzles.App.Controls;

/// <summary>
/// A text box for a whole number, used instead of NumberBox (whose spin pop-up has no evident way
/// to close). It accepts digits only, and a leading minus when <see cref="Minimum"/> is negative.
/// The value applies as it is typed; leaving the field or pressing Enter brings it into range.
/// An empty box is <see cref="double.NaN"/>.
/// </summary>
public sealed partial class NumericTextBox : TextBox
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(NumericTextBox), new PropertyMetadata(double.NaN, OnValueChanged));

    private bool _updating;

    public NumericTextBox()
    {
        InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.Number) } };
        BeforeTextChanging += OnBeforeTextChanging;
        TextChanged += OnTextChanged;
        LostFocus += (_, _) => Normalize();
        KeyDown += OnKeyDown;
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum { get; set; } = 0;

    public double Maximum { get; set; } = int.MaxValue;

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (NumericTextBox)d;
        if (box._updating)
        {
            return;
        }
        box._updating = true;
        var value = (double)e.NewValue;
        box.Text = double.IsNaN(value) ? "" : value.ToString("0", CultureInfo.CurrentCulture);
        box._updating = false;
    }

    private void OnBeforeTextChanging(TextBox sender, TextBoxBeforeTextChangingEventArgs args)
    {
        var text = args.NewText;
        var start = Minimum < 0 && text.StartsWith('-') ? 1 : 0;
        for (var i = start; i < text.Length; i++)
        {
            if (!char.IsAsciiDigit(text[i]))
            {
                args.Cancel = true;
                return;
            }
        }
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating)
        {
            return;
        }
        double value;
        if (Text.Length == 0)
        {
            value = double.NaN;
        }
        else if (!long.TryParse(Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) || n < Minimum || n > Maximum)
        {
            // Out of range or a lone minus: wait until the field is left.
            return;
        }
        else
        {
            value = n;
        }
        _updating = true;
        Value = value;
        _updating = false;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            Normalize();
        }
    }

    /// <summary>Brings the text into range, or clears it when it holds no number.</summary>
    private void Normalize()
    {
        double value = double.NaN;
        if (long.TryParse(Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
        {
            value = Math.Clamp(n, Minimum, Maximum);
        }
        _updating = true;
        Value = value;
        Text = double.IsNaN(value) ? "" : value.ToString("0", CultureInfo.CurrentCulture);
        _updating = false;
    }
}
