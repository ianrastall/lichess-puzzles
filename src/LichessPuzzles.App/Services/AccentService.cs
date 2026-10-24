using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Jupiter.LichessPuzzles.App.Services;

/// <summary>
/// Applies the Jupiter accent convention: the Windows accent by default, following system changes,
/// or a per-app custom color.
/// </summary>
public sealed class AccentService
{
    private static readonly string[] Keys =
    [
        "SystemAccentColor",
        "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
        "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3",
    ];

    private readonly UISettings _ui = new();
    private readonly JsonStore<AppSettings> _settings;
    private readonly DispatcherQueue _dispatcher;
    private FrameworkElement? _root;

    public AccentService(JsonStore<AppSettings> settings, DispatcherQueue dispatcher)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        _ui.ColorValuesChanged += (_, _) => _dispatcher.TryEnqueue(() =>
        {
            if (!_settings.Value.UseCustomAccent)
            {
                Apply();
            }
        });
    }

    public event Action? Changed;

    public bool UseCustomAccent => _settings.Value.UseCustomAccent;

    public Color CustomColor => TryParse(_settings.Value.CustomAccent) ?? WindowsAccent;

    public Color WindowsAccent => _ui.GetColorValue(UIColorType.Accent);

    public Color Current => UseCustomAccent ? CustomColor : WindowsAccent;

    public void Attach(FrameworkElement root)
    {
        _root = root;
        Apply();
    }

    public void UseWindows()
    {
        _settings.Value.UseCustomAccent = false;
        _settings.Save();
        Apply();
    }

    public void UseCustom(Color color)
    {
        _settings.Value.UseCustomAccent = true;
        _settings.Value.CustomAccent = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        _settings.Save();
        Apply();
    }

    private void Apply()
    {
        Color[] palette = UseCustomAccent
            ? Derive(CustomColor)
            :
            [
                _ui.GetColorValue(UIColorType.Accent),
                _ui.GetColorValue(UIColorType.AccentLight1), _ui.GetColorValue(UIColorType.AccentLight2), _ui.GetColorValue(UIColorType.AccentLight3),
                _ui.GetColorValue(UIColorType.AccentDark1), _ui.GetColorValue(UIColorType.AccentDark2), _ui.GetColorValue(UIColorType.AccentDark3),
            ];
        var resources = Application.Current.Resources;
        for (var i = 0; i < Keys.Length; i++)
        {
            resources[Keys[i]] = palette[i];
        }
        // Re-resolve theme resources so existing brushes pick up the new colors.
        if (_root is not null)
        {
            _root.RequestedTheme = ElementTheme.Light;
            _root.RequestedTheme = ElementTheme.Dark;
        }
        Changed?.Invoke();
    }

    private static Color[] Derive(Color c) =>
    [
        c,
        Mix(c, 255, 0.25), Mix(c, 255, 0.45), Mix(c, 255, 0.65),
        Mix(c, 0, 0.2), Mix(c, 0, 0.4), Mix(c, 0, 0.6),
    ];

    private static Color Mix(Color c, byte target, double amount)
    {
        byte Channel(byte v) => (byte)Math.Round(v + (target - v) * amount);
        return Color.FromArgb(255, Channel(c.R), Channel(c.G), Channel(c.B));
    }

    public static Color? TryParse(string text)
    {
        var hex = text.Trim().TrimStart('#');
        if (hex.Length != 6 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v))
        {
            return null;
        }
        return Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }
}
