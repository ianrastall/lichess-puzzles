using LichessPuzzleDesktop.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace LichessPuzzleDesktop.Controls;

/// <summary>
/// Draws a position and turns clicks / drags into move requests. It holds no chess knowledge:
/// legality and promotion questions go to <see cref="IBoardInteraction"/>.
/// </summary>
public sealed partial class ChessBoard : UserControl
{
    private const double Cell = 80;
    private const double BoardSize = Cell * 8;

    private static readonly Color LightSquare = Color.FromArgb(255, 0xEC, 0xDA, 0xB9);
    private static readonly Color DarkSquare = Color.FromArgb(255, 0xAE, 0x8A, 0x68);
    private static readonly Brush LastMoveBrush = new SolidColorBrush(Color.FromArgb(110, 0xF7, 0xEC, 0x5A));
    private static readonly Brush SelectedBrush = new SolidColorBrush(Color.FromArgb(130, 0x4C, 0x9A, 0x5E));
    private static readonly Brush HintBrush = new SolidColorBrush(Color.FromArgb(170, 0x3B, 0xA5, 0xF5));
    private static readonly Brush TargetBrush = new SolidColorBrush(Color.FromArgb(90, 0x14, 0x55, 0x1E));
    private static readonly Dictionary<char, SvgImageSource> PieceImages = [];

    private readonly Canvas _marks = new() { IsHitTestVisible = false };
    private readonly Canvas _labels = new() { IsHitTestVisible = false };
    private readonly Canvas _pieces = new() { IsHitTestVisible = false };
    private readonly Canvas _surface = new() { Width = BoardSize, Height = BoardSize, Background = new SolidColorBrush(Colors.Transparent) };
    private readonly FrameworkElement?[] _pieceAt = new FrameworkElement?[64];

    private int _selected = -1;
    private ulong _targets;
    private bool _dragging;
    private Point _pressPoint;
    private FrameworkElement? _dragPiece;

    public static readonly DependencyProperty SnapshotProperty = DependencyProperty.Register(
        nameof(Snapshot), typeof(BoardSnapshot), typeof(ChessBoard),
        new PropertyMetadata(BoardSnapshot.Empty, (d, _) => ((ChessBoard)d).OnSnapshotChanged()));

    public static readonly DependencyProperty InteractionProperty = DependencyProperty.Register(
        nameof(Interaction), typeof(IBoardInteraction), typeof(ChessBoard), new PropertyMetadata(null));

    public ChessBoard()
    {
        for (int row = 0; row < 8; row++)
        {
            for (int col = 0; col < 8; col++)
            {
                var square = new Rectangle
                {
                    Width = Cell,
                    Height = Cell,
                    Fill = new SolidColorBrush((row + col) % 2 == 0 ? LightSquare : DarkSquare),
                };
                Canvas.SetLeft(square, col * Cell);
                Canvas.SetTop(square, row * Cell);
                _surface.Children.Add(square);
            }
        }
        _surface.Children.Add(_marks);
        _surface.Children.Add(_labels);
        _surface.Children.Add(_pieces);

        _surface.PointerPressed += OnPointerPressed;
        _surface.PointerMoved += OnPointerMoved;
        _surface.PointerReleased += OnPointerReleased;
        _surface.PointerCaptureLost += (_, _) => EndDrag();

        Content = new Viewbox
        {
            Stretch = Stretch.Uniform,
            Child = new Border { CornerRadius = new CornerRadius(6), Child = _surface },
        };
        Render();
    }

    public BoardSnapshot Snapshot
    {
        get => (BoardSnapshot)GetValue(SnapshotProperty);
        set => SetValue(SnapshotProperty, value);
    }

    public IBoardInteraction? Interaction
    {
        get => (IBoardInteraction?)GetValue(InteractionProperty);
        set => SetValue(InteractionProperty, value);
    }

    private void OnSnapshotChanged()
    {
        EndDrag();
        ClearSelection();
        Render();
    }

    // ---------------------------------------------------------------- drawing

    private Point Origin(int square)
    {
        int file = square & 7, rank = square >> 3;
        bool flipped = Snapshot.Flipped;
        return new Point((flipped ? 7 - file : file) * Cell, (flipped ? rank : 7 - rank) * Cell);
    }

    private int SquareAt(Point p)
    {
        if (p.X < 0 || p.Y < 0 || p.X >= BoardSize || p.Y >= BoardSize) return -1;
        int col = (int)(p.X / Cell), row = (int)(p.Y / Cell);
        bool flipped = Snapshot.Flipped;
        return (flipped ? row : 7 - row) * 8 + (flipped ? 7 - col : col);
    }

    private void Render()
    {
        BoardSnapshot snapshot = Snapshot ?? BoardSnapshot.Empty;

        _labels.Children.Clear();
        for (int i = 0; i < 8; i++)
        {
            int file = snapshot.Flipped ? 7 - i : i;
            int rank = snapshot.Flipped ? i : 7 - i;
            AddLabel(((char)('a' + file)).ToString(), i * Cell + Cell - 13, BoardSize - 19, (i + 7) % 2 == 0);
            AddLabel(((char)('1' + rank)).ToString(), 4, i * Cell + 2, i % 2 == 0);
        }

        _pieces.Children.Clear();
        Array.Clear(_pieceAt);
        for (int square = 0; square < 64; square++)
        {
            char piece = snapshot.Pieces[square];
            if (piece == '.') continue;
            FrameworkElement element = CreatePiece(piece);
            Point origin = Origin(square);
            Canvas.SetLeft(element, origin.X);
            Canvas.SetTop(element, origin.Y);
            _pieces.Children.Add(element);
            _pieceAt[square] = element;
        }
        RenderMarks();
    }

    private void AddLabel(string text, double x, double y, bool onLightSquare)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(onLightSquare ? DarkSquare : LightSquare),
        };
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y);
        _labels.Children.Add(label);
    }

    private void RenderMarks()
    {
        BoardSnapshot snapshot = Snapshot ?? BoardSnapshot.Empty;
        _marks.Children.Clear();

        if (snapshot.LastFrom >= 0) AddSquareMark(snapshot.LastFrom, LastMoveBrush);
        if (snapshot.LastTo >= 0) AddSquareMark(snapshot.LastTo, LastMoveBrush);
        if (snapshot.HintSquare >= 0) AddSquareMark(snapshot.HintSquare, HintBrush);
        if (_selected >= 0) AddSquareMark(_selected, SelectedBrush);

        if (snapshot.CheckSquare >= 0)
        {
            var glow = new RadialGradientBrush();
            glow.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 0xFF, 0x20, 0x20), Offset = 0 });
            glow.GradientStops.Add(new GradientStop { Color = Color.FromArgb(200, 0xE0, 0x20, 0x20), Offset = 0.35 });
            glow.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, 0xA0, 0x00, 0x00), Offset = 1 });
            AddSquareMark(snapshot.CheckSquare, glow);
        }

        for (int square = 0; square < 64; square++)
        {
            if ((_targets & (1UL << square)) == 0) continue;
            bool capture = snapshot.Pieces[square] != '.';
            double size = capture ? Cell - 8 : Cell * 0.3;
            var dot = new Ellipse { Width = size, Height = size };
            if (capture)
            {
                dot.Stroke = TargetBrush;
                dot.StrokeThickness = 7;
            }
            else
            {
                dot.Fill = TargetBrush;
            }
            Point origin = Origin(square);
            Canvas.SetLeft(dot, origin.X + (Cell - size) / 2);
            Canvas.SetTop(dot, origin.Y + (Cell - size) / 2);
            _marks.Children.Add(dot);
        }
    }

    private void AddSquareMark(int square, Brush brush)
    {
        var mark = new Rectangle { Width = Cell, Height = Cell, Fill = brush };
        Point origin = Origin(square);
        Canvas.SetLeft(mark, origin.X);
        Canvas.SetTop(mark, origin.Y);
        _marks.Children.Add(mark);
    }

    // Merida piece set (Assets/pieces, from lichess-org/lila). Each SVG is decoded once and shared.
    private static FrameworkElement CreatePiece(char piece)
    {
        if (!PieceImages.TryGetValue(piece, out SvgImageSource? source))
        {
            string name = (char.IsUpper(piece) ? "w" : "b") + char.ToUpperInvariant(piece);
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "pieces", name + ".svg");
            // Rasterise well above the 80 px cell: the surrounding Viewbox scales the board up.
            source = new SvgImageSource(new Uri(path)) { RasterizePixelWidth = 320, RasterizePixelHeight = 320 };
            PieceImages[piece] = source;
        }
        return new Image { Width = Cell, Height = Cell, Source = source, Stretch = Stretch.Uniform };
    }

    // ------------------------------------------------------------ interaction

    private bool OwnsPiece(int square)
    {
        BoardSnapshot snapshot = Snapshot;
        if (square < 0 || !snapshot.CanMove) return false;
        char piece = snapshot.Pieces[square];
        return piece != '.' && char.IsUpper(piece) == snapshot.WhiteToMove;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(_surface);
        if (point.Properties.IsRightButtonPressed || Interaction is null) return;
        int square = SquareAt(point.Position);
        if (square < 0) return;

        if (_selected >= 0 && (_targets & (1UL << square)) != 0)
        {
            Commit(_selected, square, point.Position);
            return;
        }

        if (!OwnsPiece(square))
        {
            ClearSelection();
            RenderMarks();
            return;
        }

        _selected = square;
        _targets = Interaction.GetLegalTargets(square);
        _pressPoint = point.Position;
        _dragging = false;
        _dragPiece = _pieceAt[square];
        _surface.CapturePointer(e.Pointer);
        RenderMarks();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPiece is null) return;
        Point p = e.GetCurrentPoint(_surface).Position;
        if (!_dragging)
        {
            if (Math.Abs(p.X - _pressPoint.X) < 4 && Math.Abs(p.Y - _pressPoint.Y) < 4) return;
            _dragging = true;
            Canvas.SetZIndex(_dragPiece, 10);
        }
        Canvas.SetLeft(_dragPiece, p.X - Cell / 2);
        Canvas.SetTop(_dragPiece, p.Y - Cell / 2);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPiece is null) return;
        bool wasDragging = _dragging;
        int from = _selected;
        Point p = e.GetCurrentPoint(_surface).Position;
        EndDrag();
        _surface.ReleasePointerCapture(e.Pointer);
        if (!wasDragging) return; // a plain click keeps the piece selected

        int square = SquareAt(p);
        if (square >= 0 && from >= 0 && (_targets & (1UL << square)) != 0) Commit(from, square, p);
    }

    // Puts a dragged piece back on its square.
    private void EndDrag()
    {
        if (_dragPiece is not null && _selected >= 0)
        {
            Point origin = Origin(_selected);
            Canvas.SetLeft(_dragPiece, origin.X);
            Canvas.SetTop(_dragPiece, origin.Y);
            Canvas.SetZIndex(_dragPiece, 0);
        }
        _dragPiece = null;
        _dragging = false;
    }

    private void ClearSelection()
    {
        _selected = -1;
        _targets = 0;
    }

    private void Commit(int from, int to, Point at)
    {
        IBoardInteraction? interaction = Interaction;
        ClearSelection();
        RenderMarks();
        if (interaction is null) return;

        if (!interaction.IsPromotion(from, to))
        {
            interaction.PlayMove(from, to, '\0');
            return;
        }

        var menu = new MenuFlyout();
        foreach (var (label, piece) in new[] { ("Queen", 'q'), ("Knight", 'n'), ("Rook", 'r'), ("Bishop", 'b') })
        {
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) => interaction.PlayMove(from, to, piece);
            menu.Items.Add(item);
        }
        menu.ShowAt(_surface, new FlyoutShowOptions { Position = at });
    }
}
