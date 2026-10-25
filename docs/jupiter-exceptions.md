# Jupiter style exceptions

Lichess Puzzles follows [Jupiter style standard 1.1](../../_standards/jupiter-style-standard.md) through the shared [Jupiter.UI](../../jupiter-ui/readme.md) library, including the shared dark/light/Windows Theme setting. The style check runs on every build with the default severity, Error, and the XAML has no `jupiter-allow` markers. These are the recorded departures.

| Rule | Where | Departure | Reason | Recorded |
| --- | --- | --- | --- | --- |
| 3.4 Content palettes | `Services/Preferences.cs` (`BoardThemes`), `Controls/ChessBoard.cs` | Square colors (four board themes), move-marker colors, the check tint, the last-move and selection tints derived from the accent, and coordinate labels at 11 px semibold | The chessboard is content, drawn in C#; its colors and labels belong to the board, not the interface. Status colors are not reused for other meanings. | 24 October 2026 |
| 5.2 Side pane minimum | `MainWindow.xaml`, `MainWindow.xaml.cs` | The puzzle pane's minimum width is 300, not 240 | The History view's five columns and the two-column action buttons do not fit below 300. The filter pane uses 240. | 24 October 2026 |
| 7 Canvas content | `MainWindow.xaml` (board area) | Navigation buttons sit under the board instead of in a command bar | They act on the board's position like a media control; every one is also in the **Puzzle** menu with its shortcut. | 24 October 2026 |
