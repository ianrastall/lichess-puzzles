using LichessPuzzleDesktop.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LichessPuzzleDesktop;

/// <summary>Small conversion functions for x:Bind.</summary>
public static class Ui
{
    private static readonly Brush Transparent = new SolidColorBrush(Colors.Transparent);

    public static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility ShowText(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    public static bool Not(bool value) => !value;

    /// <summary>Star height for the white part of the evaluation bar.</summary>
    public static GridLength Star(double fraction) => new(Math.Clamp(fraction, 0.0, 1.0), GridUnitType.Star);
    public static GridLength StarRest(double fraction) => Star(1 - fraction);

    public static InfoBarSeverity Severity(StatusKind kind) => kind switch
    {
        StatusKind.Success => InfoBarSeverity.Success,
        StatusKind.Warning => InfoBarSeverity.Warning,
        StatusKind.Error => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational,
    };

    public static Brush CurrentMoveBackground(bool isCurrent) =>
        isCurrent && Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out object brush)
            ? (Brush)brush
            : Transparent;

    public static Brush MoveForeground(bool isCurrent) =>
        (Brush)Application.Current.Resources[isCurrent ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorPrimaryBrush"];
}
