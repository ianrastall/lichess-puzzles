using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Jupiter.LichessPuzzles.App.Controls;

/// <summary>
/// A NumberBox whose spin buttons, arrow keys and mouse wheel also work while it is empty. WinUI's
/// NumberBox only steps from an existing number, so an empty filter box ("Any") would never change;
/// here the first step from empty sets <see cref="StartValue"/>.
/// </summary>
public sealed partial class StepNumberBox : NumberBox
{
    private readonly List<ButtonBase> _spinButtons = [];

    public StepNumberBox()
    {
        AddHandler(KeyDownEvent, new KeyEventHandler(OnAnyKeyDown), handledEventsToo: true);
        AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnAnyWheel), handledEventsToo: true);
    }

    /// <summary>The value a step from empty sets.</summary>
    public double StartValue { get; set; }

    protected override void OnApplyTemplate()
    {
        foreach (var button in _spinButtons)
        {
            button.Click -= OnSpin;
        }
        _spinButtons.Clear();
        base.OnApplyTemplate();
        foreach (var name in new[] { "UpSpinButton", "DownSpinButton", "PopupUpSpinButton", "PopupDownSpinButton" })
        {
            if (GetTemplateChild(name) is ButtonBase button)
            {
                button.Click += OnSpin;
                _spinButtons.Add(button);
            }
        }
    }

    private bool StartIfEmpty()
    {
        if (!double.IsNaN(Value))
        {
            return false;
        }
        Value = Math.Clamp(StartValue, Minimum, Maximum);
        return true;
    }

    private void OnSpin(object sender, RoutedEventArgs e) => StartIfEmpty();

    private void OnAnyKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.PageUp or VirtualKey.PageDown && StartIfEmpty())
        {
            e.Handled = true;
        }
    }

    private void OnAnyWheel(object sender, PointerRoutedEventArgs e)
    {
        if (FocusState != FocusState.Unfocused && StartIfEmpty())
        {
            e.Handled = true;
        }
    }
}
