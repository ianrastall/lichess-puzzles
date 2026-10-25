using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace Jupiter.LichessPuzzles.App.Controls;

public enum BoardMarker
{
    Correct,
    Wrong,
}

/// <summary>
/// A chessboard drawn with ordinary XAML shapes and the piece SVGs. It shows a position and
/// accepts moves by click, drag or keyboard; the owner decides what a move means.
/// Squares are numbered 0 (a1) to 63 (h8).
/// </summary>
public sealed partial class ChessBoard : UserControl
{
    private static readonly Dictionary<char, SvgImageSource> PieceImages = [];

    private readonly Canvas _canvas = new();
    private readonly Rectangle[] _squares = new Rectangle[64];
    private readonly Rectangle _lastFrom = Highlight();
    private readonly Rectangle _lastTo = Highlight();
    private readonly Rectangle _check = new() { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Rectangle _selected = Outline(3);
    private readonly Rectangle _hint = Outline(3);
    private readonly Rectangle _cursor = Outline(3);
    private readonly List<UIElement> _dots = [];
    private readonly Image?[] _pieces = new Image?[64];
    private readonly TextBlock[] _files = new TextBlock[8];
    private readonly TextBlock[] _ranks = new TextBlock[8];
    private readonly Grid _marker = new() { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Border _promotion = new() { Visibility = Visibility.Collapsed };

    private string _board = new('.', 64);
    private HashSet<string> _legal = [];
    private bool _interactive;
    private int _lastFromSquare = -1, _lastToSquare = -1, _checkSquare = -1, _hintSquare = -1;
    private int _selectedSquare = -1;
    private int _cursorSquare = 12;
    private bool _cursorShown;
    private int _markerSquare = -1;
    private string _pendingPromotion = "";

    private double _size;
    private double _x0, _y0;

    private Image? _dragImage;
    private int _dragFrom = -1;
    private Point _dragStart;
    private bool _dragMoved;
    private bool _skipNextAnimation;

    private Color _light = Color.FromArgb(255, 0xEA, 0xD7, 0xB7);
    private Color _dark = Color.FromArgb(255, 0xB0, 0x8A, 0x62);

    /// <summary>Raised with a UCI move (with a promotion letter when needed).</summary>
    public event Action<string>? MoveRequested;

    /// <summary>Raised for Ctrl+Left/Right/Home/End with -1, +1, int.MinValue or int.MaxValue.</summary>
    public event Action<int>? NavigateRequested;

    public ChessBoard()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        AutomationProperties.SetName(this, "Chess board");
        Content = _canvas;
        _canvas.Background = new SolidColorBrush(Colors.Transparent);

        for (var sq = 0; sq < 64; sq++)
        {
            _squares[sq] = new Rectangle();
            _canvas.Children.Add(_squares[sq]);
        }
        foreach (var r in new[] { _lastFrom, _lastTo, _check })
        {
            Canvas.SetZIndex(r, 1);
            _canvas.Children.Add(r);
        }
        _hint.StrokeDashArray = [2, 1];
        foreach (var r in new[] { _hint, _selected })
        {
            Canvas.SetZIndex(r, 2);
            _canvas.Children.Add(r);
        }
        for (var i = 0; i < 8; i++)
        {
            _files[i] = Coordinate();
            _ranks[i] = Coordinate();
            _canvas.Children.Add(_files[i]);
            _canvas.Children.Add(_ranks[i]);
        }
        Canvas.SetZIndex(_marker, 6);
        _canvas.Children.Add(_marker);
        Canvas.SetZIndex(_cursor, 7);
        _canvas.Children.Add(_cursor);
        Canvas.SetZIndex(_promotion, 9);
        _canvas.Children.Add(_promotion);

        SizeChanged += (_, _) => Layout();
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => CancelDrag();
        LostFocus += (_, _) =>
        {
            _cursorShown = false;
            UpdateOverlays();
        };
        RefreshAccent();
    }

    /// <summary>Loads the piece images once; call before the first board is shown.</summary>
    public static async Task LoadPiecesAsync()
    {
        foreach (var piece in "PNBRQKpnbrqk")
        {
            var name = (char.IsUpper(piece) ? "w" : "b") + char.ToUpperInvariant(piece);
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "pieces", name + ".svg");
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var source = new SvgImageSource();
            await source.SetSourceAsync(stream);
            PieceImages[piece] = source;
        }
    }

    public bool WhiteAtBottom { get; private set; } = true;

    public bool ShowCoordinates { get; set { field = value; Layout(); } } = true;

    /// <summary>Move animation length; zero turns animation off.</summary>
    public TimeSpan AnimationDuration { get; set; } = TimeSpan.FromMilliseconds(180);

    public void SetOrientation(bool whiteAtBottom)
    {
        if (WhiteAtBottom == whiteAtBottom)
        {
            return;
        }
        WhiteAtBottom = whiteAtBottom;
        AutomationProperties.SetName(this, whiteAtBottom ? "Chess board, White at the bottom" : "Chess board, Black at the bottom");
        Layout();
    }

    public void SetColors(Color light, Color dark)
    {
        _light = light;
        _dark = dark;
        Layout();
    }

    /// <summary>Re-reads the accent color after it changes.</summary>
    public void RefreshAccent()
    {
        var accent = Application.Current.Resources.TryGetValue("SystemAccentColor", out var value) && value is Color c ? c : Color.FromArgb(255, 0, 120, 212);
        _lastFrom.Fill = _lastTo.Fill = new SolidColorBrush(Color.FromArgb(110, accent.R, accent.G, accent.B));
        _selected.Stroke = new SolidColorBrush(accent);
        _hint.Stroke = new SolidColorBrush(accent);
        _cursor.Stroke = Jupiter.UI.JupiterTheme.Brush("JupiterFocusStrokeBrush");
        _check.Fill = new SolidColorBrush(Color.FromArgb(150, 0xE9, 0x6A, 0x5E));
    }

    /// <summary>Shows a position. <paramref name="legalMoves"/> are the moves the board accepts.</summary>
    public void SetPosition(string board, int lastFrom, int lastTo, int checkSquare, int hintSquare,
        IEnumerable<string> legalMoves, bool interactive, bool animate)
    {
        if (board.Length != 64)
        {
            return;
        }
        var previous = _board;
        _board = board;
        _lastFromSquare = lastFrom;
        _lastToSquare = lastTo;
        _checkSquare = checkSquare;
        _hintSquare = hintSquare;
        _legal = [.. legalMoves];
        _interactive = interactive;
        _markerSquare = -1;
        ClosePromotion();
        CancelDrag();
        if (!_interactive || (_selectedSquare >= 0 && !HasMoves(_selectedSquare)))
        {
            _selectedSquare = -1;
        }
        RebuildPieces();
        Layout();
        var skip = _skipNextAnimation;
        _skipNextAnimation = false;
        if (animate && !skip && lastFrom >= 0 && lastTo >= 0 && previous[lastTo] != board[lastTo] && AnimationDuration > TimeSpan.Zero)
        {
            Animate(lastFrom, lastTo);
        }
    }

    /// <summary>Shows a move on the board without accepting it (a wrong attempt), until the next position.</summary>
    public void ShowAttempt(int from, int to)
    {
        if (_pieces[from] is not { } image)
        {
            return;
        }
        if (_pieces[to] is { } captured && captured != image)
        {
            captured.Visibility = Visibility.Collapsed;
        }
        _pieces[from] = null;
        _pieces[to] = image;
        PlaceSquareElement(image, to);
        _selectedSquare = -1;
        UpdateOverlays();
    }

    public void ShowMarker(int square, BoardMarker marker)
    {
        _markerSquare = square;
        _marker.Children.Clear();
        var color = marker == BoardMarker.Correct ? Color.FromArgb(255, 0x6F, 0xA8, 0x7D) : Color.FromArgb(255, 0xC9, 0x5C, 0x52);
        _marker.Children.Add(new Ellipse { Fill = new SolidColorBrush(color), Stroke = new SolidColorBrush(Colors.White), StrokeThickness = 1.5 });
        _marker.Children.Add(new FontIcon
        {
            Glyph = marker == BoardMarker.Correct ? ((char)0xE8FB).ToString() : ((char)0xE711).ToString(),
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            Foreground = new SolidColorBrush(Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Layout();
    }

    private static Rectangle Highlight() => new() { Visibility = Visibility.Collapsed, IsHitTestVisible = false };

    private static Rectangle Outline(double thickness) => new()
    {
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false,
        StrokeThickness = thickness,
        Fill = new SolidColorBrush(Colors.Transparent),
    };

    private static TextBlock Coordinate() => new() { FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, IsHitTestVisible = false };

    private static bool IsWhite(char piece) => char.IsUpper(piece);

    private static string SquareName(int sq) => $"{(char)('a' + sq % 8)}{sq / 8 + 1}";

    private static int ParseSquare(string text) => text.Length == 2 ? (text[1] - '1') * 8 + (text[0] - 'a') : -1;

    private (int Col, int Row) ScreenCell(int sq) =>
        WhiteAtBottom ? (sq % 8, 7 - sq / 8) : (7 - sq % 8, sq / 8);

    private int SquareAt(Point p)
    {
        if (_size <= 0)
        {
            return -1;
        }
        var col = (int)Math.Floor((p.X - _x0) / _size);
        var row = (int)Math.Floor((p.Y - _y0) / _size);
        if (col is < 0 or > 7 || row is < 0 or > 7)
        {
            return -1;
        }
        return WhiteAtBottom ? (7 - row) * 8 + col : row * 8 + (7 - col);
    }

    private bool HasMoves(int from)
    {
        var prefix = SquareName(from);
        return _legal.Any(m => m.StartsWith(prefix, StringComparison.Ordinal));
    }

    private List<string> MovesBetween(int from, int to)
    {
        var uci = SquareName(from) + SquareName(to);
        return _legal.Where(m => m.StartsWith(uci, StringComparison.Ordinal)).ToList();
    }

    private void RebuildPieces()
    {
        for (var sq = 0; sq < 64; sq++)
        {
            if (_pieces[sq] is { } old)
            {
                _canvas.Children.Remove(old);
                _pieces[sq] = null;
            }
            var piece = _board[sq];
            if (piece == '.' || !PieceImages.TryGetValue(piece, out var source))
            {
                continue;
            }
            var image = new Image { Source = source, IsHitTestVisible = false, RenderTransform = new TranslateTransform() };
            Canvas.SetZIndex(image, 4);
            _pieces[sq] = image;
            _canvas.Children.Add(image);
        }
    }

    private void PlaceSquareElement(FrameworkElement element, int sq, double inset = 0)
    {
        var (col, row) = ScreenCell(sq);
        Canvas.SetLeft(element, _x0 + col * _size + inset);
        Canvas.SetTop(element, _y0 + row * _size + inset);
        element.Width = Math.Max(0, _size - 2 * inset);
        element.Height = Math.Max(0, _size - 2 * inset);
    }

    private void Layout()
    {
        var side = Math.Floor(Math.Min(ActualWidth, ActualHeight) / 8);
        _size = Math.Max(0, side);
        _x0 = Math.Floor((ActualWidth - 8 * _size) / 2);
        _y0 = Math.Floor((ActualHeight - 8 * _size) / 2);
        var light = new SolidColorBrush(_light);
        var dark = new SolidColorBrush(_dark);
        for (var sq = 0; sq < 64; sq++)
        {
            var isLight = (sq % 8 + sq / 8) % 2 == 1;
            _squares[sq].Fill = isLight ? light : dark;
            PlaceSquareElement(_squares[sq], sq);
        }
        for (var sq = 0; sq < 64; sq++)
        {
            if (_pieces[sq] is { } image)
            {
                PlaceSquareElement(image, sq, _size * 0.02);
            }
        }
        for (var i = 0; i < 8; i++)
        {
            // Files along the bottom edge, ranks along the left edge, as seen by the viewer.
            var file = WhiteAtBottom ? i : 7 - i;
            var bottomSquare = WhiteAtBottom ? file : 56 + file;
            var rankSquare = WhiteAtBottom ? (7 - i) * 8 : i * 8 + 7;
            Label(_files[i], ((char)('a' + file)).ToString(), bottomSquare, bottom: true);
            Label(_ranks[i], (rankSquare / 8 + 1).ToString(), rankSquare, bottom: false);
        }
        UpdateOverlays();
    }

    private void Label(TextBlock label, string text, int sq, bool bottom)
    {
        label.Text = text;
        label.Visibility = ShowCoordinates && _size > 24 ? Visibility.Visible : Visibility.Collapsed;
        var isLight = (sq % 8 + sq / 8) % 2 == 1;
        label.Foreground = new SolidColorBrush(isLight ? _dark : _light);
        var (col, row) = ScreenCell(sq);
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var pad = Math.Max(2, _size * 0.04);
        if (bottom)
        {
            Canvas.SetLeft(label, _x0 + (col + 1) * _size - label.DesiredSize.Width - pad);
            Canvas.SetTop(label, _y0 + (row + 1) * _size - label.DesiredSize.Height - pad / 2);
        }
        else
        {
            Canvas.SetLeft(label, _x0 + col * _size + pad);
            Canvas.SetTop(label, _y0 + row * _size + pad / 2);
        }
        Canvas.SetZIndex(label, 3);
    }

    private void UpdateOverlays()
    {
        Show(_lastFrom, _lastFromSquare);
        Show(_lastTo, _lastToSquare);
        Show(_check, _checkSquare);
        Show(_hint, _interactive ? _hintSquare : -1);
        Show(_selected, _selectedSquare);
        Show(_cursor, _cursorShown && FocusState == FocusState.Keyboard ? _cursorSquare : -1);
        _check.RadiusX = _check.RadiusY = _size / 2;

        foreach (var dot in _dots)
        {
            _canvas.Children.Remove(dot);
        }
        _dots.Clear();
        if (_selectedSquare >= 0)
        {
            var targets = _legal.Where(m => m.StartsWith(SquareName(_selectedSquare), StringComparison.Ordinal))
                .Select(m => ParseSquare(m.Substring(2, 2))).Distinct();
            foreach (var to in targets)
            {
                var capture = _board[to] != '.';
                var dot = new Ellipse
                {
                    IsHitTestVisible = false,
                    Fill = capture ? new SolidColorBrush(Colors.Transparent) : new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)),
                    Stroke = capture ? new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)) : null,
                    StrokeThickness = _size * 0.08,
                };
                Canvas.SetZIndex(dot, capture ? 3 : 5);
                PlaceSquareElement(dot, to, capture ? _size * 0.02 : _size * 0.36);
                _canvas.Children.Add(dot);
                _dots.Add(dot);
            }
        }

        if (_markerSquare >= 0)
        {
            var (col, row) = ScreenCell(_markerSquare);
            var d = Math.Max(14, _size * 0.32);
            _marker.Width = _marker.Height = d;
            Canvas.SetLeft(_marker, _x0 + (col + 1) * _size - d * 0.8);
            Canvas.SetTop(_marker, _y0 + row * _size - d * 0.2);
            if (_marker.Children.Count > 1 && _marker.Children[1] is FontIcon icon)
            {
                icon.FontSize = d * 0.55;
            }
            _marker.Visibility = Visibility.Visible;
        }
        else
        {
            _marker.Visibility = Visibility.Collapsed;
        }
    }

    private void Show(Rectangle r, int sq)
    {
        if (sq < 0 || _size <= 0)
        {
            r.Visibility = Visibility.Collapsed;
            return;
        }
        var inset = r.StrokeThickness > 0 ? r.StrokeThickness / 2 : 0;
        PlaceSquareElement(r, sq, inset);
        r.Visibility = Visibility.Visible;
    }

    private void Animate(int from, int to)
    {
        if (_pieces[to] is not { } image || image.RenderTransform is not TranslateTransform transform)
        {
            return;
        }
        var (fc, fr) = ScreenCell(from);
        var (tc, tr) = ScreenCell(to);
        var duration = new Duration(AnimationDuration);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var x = new DoubleAnimation { From = (fc - tc) * _size, To = 0, Duration = duration, EasingFunction = ease };
        var y = new DoubleAnimation { From = (fr - tr) * _size, To = 0, Duration = duration, EasingFunction = ease };
        var story = new Storyboard();
        Storyboard.SetTarget(x, transform);
        Storyboard.SetTargetProperty(x, "X");
        Storyboard.SetTarget(y, transform);
        Storyboard.SetTargetProperty(y, "Y");
        story.Children.Add(x);
        story.Children.Add(y);
        Canvas.SetZIndex(image, 8);
        story.Completed += (_, _) => Canvas.SetZIndex(image, 4);
        story.Begin();
    }

    // Pointer input --------------------------------------------------------------------------

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        _cursorShown = false;
        if (!_interactive || _promotion.Visibility == Visibility.Visible)
        {
            return;
        }
        var point = e.GetCurrentPoint(_canvas).Position;
        var sq = SquareAt(point);
        if (sq < 0)
        {
            return;
        }
        if (_selectedSquare >= 0 && sq != _selectedSquare && MovesBetween(_selectedSquare, sq).Count > 0)
        {
            TryMove(_selectedSquare, sq, dragged: false);
            e.Handled = true;
            return;
        }
        if (HasMoves(sq))
        {
            _selectedSquare = sq;
            _dragFrom = sq;
            _dragImage = _pieces[sq];
            _dragStart = point;
            _dragMoved = false;
            CapturePointer(e.Pointer);
        }
        else
        {
            _selectedSquare = -1;
        }
        UpdateOverlays();
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragImage is null)
        {
            return;
        }
        var point = e.GetCurrentPoint(_canvas).Position;
        if (!_dragMoved && Math.Abs(point.X - _dragStart.X) + Math.Abs(point.Y - _dragStart.Y) < 4)
        {
            return;
        }
        _dragMoved = true;
        Canvas.SetZIndex(_dragImage, 8);
        Canvas.SetLeft(_dragImage, point.X - _size / 2);
        Canvas.SetTop(_dragImage, point.Y - _size / 2);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragImage is null)
        {
            return;
        }
        var from = _dragFrom;
        var moved = _dragMoved;
        var to = SquareAt(e.GetCurrentPoint(_canvas).Position);
        ReleasePointerCapture(e.Pointer);
        CancelDrag();
        if (moved && to >= 0 && to != from && MovesBetween(from, to).Count > 0)
        {
            TryMove(from, to, dragged: true);
        }
        e.Handled = true;
    }

    private void CancelDrag()
    {
        if (_dragImage is not null && _dragFrom >= 0 && _pieces[_dragFrom] == _dragImage)
        {
            Canvas.SetZIndex(_dragImage, 4);
            PlaceSquareElement(_dragImage, _dragFrom, _size * 0.02);
        }
        _dragImage = null;
        _dragFrom = -1;
        _dragMoved = false;
    }

    private void TryMove(int from, int to, bool dragged)
    {
        var moves = MovesBetween(from, to);
        if (moves.Count == 0)
        {
            return;
        }
        if (moves.Any(m => m.Length == 5))
        {
            OpenPromotion(from, to);
            return;
        }
        if (dragged && _pieces[from] is { } image)
        {
            // Leave the piece where it was dropped; the next position will not animate it again.
            _skipNextAnimation = true;
            ShowAttempt(from, to);
        }
        _selectedSquare = -1;
        UpdateOverlays();
        MoveRequested?.Invoke(moves[0]);
    }

    // Promotion ---------------------------------------------------------------------------------

    private void OpenPromotion(int from, int to)
    {
        _pendingPromotion = SquareName(from) + SquareName(to);
        var white = IsWhite(_board[from]);
        var panel = new StackPanel();
        foreach (var piece in "qrbn")
        {
            var key = white ? char.ToUpperInvariant(piece) : piece;
            var button = new Button
            {
                Width = _size,
                Height = _size,
                Padding = new Thickness(4),
                Content = new Image { Source = PieceImages.GetValueOrDefault(key) },
                Tag = piece.ToString(),
            };
            AutomationProperties.SetName(button, piece switch { 'q' => "Queen", 'r' => "Rook", 'b' => "Bishop", _ => "Knight" });
            button.Click += (s, _) => ChoosePromotion((string)((Button)s).Tag);
            panel.Children.Add(button);
        }
        _promotion.Child = panel;
        // The promotion choice is a flyout: raised surface, separator outline, overlay radius (standard 8).
        _promotion.Background = Jupiter.UI.JupiterTheme.Brush("JupiterRaisedBrush");
        _promotion.BorderBrush = Jupiter.UI.JupiterTheme.Brush("JupiterSeparatorBrush");
        _promotion.BorderThickness = new Thickness(1);
        _promotion.CornerRadius = (CornerRadius)Jupiter.UI.JupiterTheme.Find("OverlayCornerRadius");
        var (col, row) = ScreenCell(to);
        var top = row == 0 ? _y0 : _y0 + (row - 3) * _size;
        Canvas.SetLeft(_promotion, _x0 + col * _size);
        Canvas.SetTop(_promotion, top);
        _promotion.Visibility = Visibility.Visible;
        Announce("Choose a promotion piece: Q, R, B or N.");
    }

    private void ChoosePromotion(string piece)
    {
        var uci = _pendingPromotion + piece;
        ClosePromotion();
        if (_legal.Contains(uci))
        {
            _selectedSquare = -1;
            UpdateOverlays();
            MoveRequested?.Invoke(uci);
        }
    }

    private void ClosePromotion()
    {
        _promotion.Visibility = Visibility.Collapsed;
        _promotion.Child = null;
        _pendingPromotion = "";
    }

    // Keyboard ----------------------------------------------------------------------------------

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        if (_promotion.Visibility == Visibility.Visible)
        {
            var choice = e.Key switch { VirtualKey.Q => "q", VirtualKey.R => "r", VirtualKey.B => "b", VirtualKey.N => "n", _ => null };
            if (choice is not null)
            {
                ChoosePromotion(choice);
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Escape)
            {
                ClosePromotion();
                e.Handled = true;
            }
            return;
        }
        if (ctrl)
        {
            int? step = e.Key switch
            {
                VirtualKey.Left => -1,
                VirtualKey.Right => 1,
                VirtualKey.Home => int.MinValue,
                VirtualKey.End => int.MaxValue,
                _ => null,
            };
            if (step is { } s)
            {
                NavigateRequested?.Invoke(s);
                e.Handled = true;
            }
            return;
        }
        (int dc, int dr)? delta = e.Key switch
        {
            VirtualKey.Left => (-1, 0),
            VirtualKey.Right => (1, 0),
            VirtualKey.Up => (0, -1),
            VirtualKey.Down => (0, 1),
            _ => null,
        };
        if (delta is { } d)
        {
            var (col, row) = ScreenCell(_cursorSquare);
            col = Math.Clamp(col + d.dc, 0, 7);
            row = Math.Clamp(row + d.dr, 0, 7);
            _cursorSquare = WhiteAtBottom ? (7 - row) * 8 + col : row * 8 + (7 - col);
            _cursorShown = true;
            UpdateOverlays();
            Announce(DescribeSquare(_cursorSquare));
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case VirtualKey.Enter:
            case VirtualKey.Space:
                ActivateCursor();
                e.Handled = true;
                break;
            case VirtualKey.Escape when _selectedSquare >= 0:
                _selectedSquare = -1;
                UpdateOverlays();
                e.Handled = true;
                break;
        }
    }

    private void ActivateCursor()
    {
        _cursorShown = true;
        var sq = _cursorSquare;
        if (!_interactive)
        {
            Announce(DescribeSquare(sq));
            return;
        }
        if (_selectedSquare >= 0 && sq != _selectedSquare && MovesBetween(_selectedSquare, sq).Count > 0)
        {
            TryMove(_selectedSquare, sq, dragged: false);
            return;
        }
        if (sq == _selectedSquare)
        {
            _selectedSquare = -1;
            Announce("Selection cleared");
        }
        else if (HasMoves(sq))
        {
            _selectedSquare = sq;
            Announce($"{DescribeSquare(sq)} selected");
        }
        else
        {
            Announce($"{DescribeSquare(sq)}, no moves");
        }
        UpdateOverlays();
    }

    private string DescribeSquare(int sq)
    {
        var piece = _board[sq];
        if (piece == '.')
        {
            return $"{SquareName(sq)}, empty";
        }
        var color = IsWhite(piece) ? "white" : "black";
        var name = char.ToLowerInvariant(piece) switch
        {
            'p' => "pawn",
            'n' => "knight",
            'b' => "bishop",
            'r' => "rook",
            'q' => "queen",
            _ => "king",
        };
        return $"{SquareName(sq)}, {color} {name}";
    }

    /// <summary>Speaks text to screen readers.</summary>
    public void Announce(string text)
    {
        var peer = FrameworkElementAutomationPeer.FromElement(this) ?? FrameworkElementAutomationPeer.CreatePeerForElement(this);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.ImportantMostRecent, text, "ChessBoard");
    }
}
