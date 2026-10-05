using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LichessPuzzleDesktop.Core;

namespace LichessPuzzleDesktop.ViewModels;

/// <summary>Everything the board control needs to draw one position.</summary>
public sealed record BoardSnapshot(string Pieces, bool Flipped, int LastFrom, int LastTo, int CheckSquare,
    int HintSquare, bool CanMove, bool WhiteToMove)
{
    public static BoardSnapshot Empty { get; } = new(new string('.', 64), false, -1, -1, -1, -1, false, true);
}

/// <summary>Callbacks from the board control into the view model.</summary>
public interface IBoardInteraction
{
    ulong GetLegalTargets(int from);
    bool IsPromotion(int from, int to);
    void PlayMove(int from, int to, char promotion);
}

public sealed partial class MoveCellViewModel(string san, int view, Action<int>? select) : ObservableObject
{
    public string San { get; } = san;
    /// <summary>The view index (plies applied) that shows the position after this move.</summary>
    public int View { get; } = view;
    public bool IsMove => select is not null;

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    [RelayCommand]
    private void Select() => select?.Invoke(View);
}

public sealed record MoveRowViewModel(string Number, MoveCellViewModel White, MoveCellViewModel Black);

public sealed partial class ThemeOption(string key, int count, Action changed) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = $"{ThemeNames.Humanize(key)}  ({count:N0})";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value) => changed();
}

/// <summary>A player as shown in the player picker; updated in place when the rating changes.</summary>
public sealed partial class PlayerOption : ObservableObject
{
    // Lichess marks a rating as provisional above this deviation.
    private const double ProvisionalDeviation = 110;

    public PlayerOption(PlayerInfo info)
    {
        Id = info.Id;
        Info = info;
        Label = "";
        Update(info);
    }

    public int Id { get; }
    public PlayerInfo Info { get; private set; }

    [ObservableProperty]
    public partial string Label { get; set; }

    public string RatingText => Info.Rating.ToString("0") + (Info.Deviation > ProvisionalDeviation ? "?" : "");

    public void Update(PlayerInfo info)
    {
        Info = info;
        Label = $"{info.Name}  ·  {RatingText}";
    }
}

public sealed record OpeningOption(string? Key, string Label)
{
    public override string ToString() => Label;
}

public static class ThemeNames
{
    /// <summary>"mateIn2" -> "Mate in 2", "Sicilian_Defense" -> "Sicilian Defense".</summary>
    public static string Humanize(string key)
    {
        if (key.Contains('_')) return key.Replace('_', ' ');
        var sb = new StringBuilder(key.Length + 4);
        for (int i = 0; i < key.Length; i++)
        {
            char c = key[i];
            bool boundary = i > 0 && (char.IsUpper(c) || (char.IsDigit(c) && !char.IsDigit(key[i - 1])));
            if (boundary) sb.Append(' ');
            sb.Append(i == 0 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
