namespace Lichess_Puzzles.Models;

public record Theme
{
    public required string ThemeId { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
}
