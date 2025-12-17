using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using ChessDotNet;
using ChessDotNet.Pieces;
using Lichess_Puzzles.Models;
using Lichess_Puzzles.Services;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace Lichess_Puzzles
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private ChessGame _game = null!;
        private Position? _selectedPosition;
        private readonly Border[,] _squares = new Border[8, 8];
        private readonly ObservableCollection<MoveDisplayEntry> _moveList = [];
        private readonly List<GameState> _gameHistory = [];
        private readonly Dictionary<string, DrawingImage> _pieceImages = [];
        private PuzzleService _puzzleService;
        private readonly LichessGameService _lichessGameService;

        // Puzzle state
        private Puzzle? _currentPuzzle;
        private string[] _solutionMoves = [];
        private int _currentMoveIndex;
        private int _displayedMoveIndex = -1; // -1 means showing current position
        private bool _puzzleSolved;
        private bool _puzzleFailed;
        private bool _boardFlipped;
        private Player _playerColor;
        private string _initialFen = "";
        
        // Source game data
        private GameData? _sourceGame;
        private int _puzzleStartPly; // The ply number where the puzzle begins
        private int _lastPuzzleMoveIndex; // Index in _gameHistory of the last puzzle move
        private Position? _dragStartPosition;
        private Point _dragStartPoint;
        private bool _isDragging;
        private UIElement? _dragVisualElement;

        private static readonly SolidColorBrush LightSquareBrush = new(Color.FromRgb(240, 217, 181));
        private static readonly SolidColorBrush DarkSquareBrush = new(Color.FromRgb(181, 136, 99));
        private static readonly SolidColorBrush SelectedBrush = new(Color.FromRgb(130, 151, 105));
        private static readonly SolidColorBrush ValidMoveBrush = new(Color.FromRgb(130, 151, 105));
        private static readonly SolidColorBrush CorrectMoveBrush = new(Color.FromRgb(100, 180, 100));
        private static readonly SolidColorBrush CurrentMoveBrush = new(Color.FromRgb(82, 130, 180));
        private static readonly SolidColorBrush PuzzleMoveBrush = new(Color.FromRgb(160, 130, 200)); // Light purple for puzzle moves
        private static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);

        public MainWindow()
        {
            InitializeComponent();
            _puzzleService = new PuzzleService();
            _lichessGameService = new LichessGameService();
            MoveListControl.ItemsSource = _moveList;
            LoadPieceImages();
            InitializeBoard();
            
            try
            {
                LoadNewPuzzle();
            }
            catch (Exception ex)
            {
                SetStatus($"Error loading puzzle: {ex.Message}", false);
            }
        }

        private void LoadPieceImages()
        {
            string[] pieceKeys = ["wK", "wQ", "wR", "wB", "wN", "wP", "bK", "bQ", "bR", "bB", "bN", "bP"];
            var settings = new WpfDrawingSettings
            {
                IncludeRuntime = true,
                TextAsGeometry = false
            };

            foreach (var key in pieceKeys)
            {
                try
                {
                    var uri = new Uri($"pack://application:,,,/Assets/Pieces/{key}.svg");
                    var streamInfo = Application.GetResourceStream(uri);
                    if (streamInfo != null)
                    {
                        using var stream = streamInfo.Stream;
                        using var reader = new FileSvgReader(settings);
                        var drawing = reader.Read(stream);
                        if (drawing != null)
                        {
                            _pieceImages[key] = new DrawingImage(drawing);
                        }
                    }
                }
                catch
                {
                    // SVG not found - will fall back to text
                }
            }
        }

        private void InitializeBoard()
        {
            for (int row = 0; row < 8; row++)
            {
                for (int col = 0; col < 8; col++)
                {
                    var border = new Border
                    {
                        Background = (row + col) % 2 == 0 ? LightSquareBrush : DarkSquareBrush
                    };

                    border.MouseLeftButtonDown += Square_Click;
                    border.MouseMove += Square_MouseMove;
                    border.MouseLeftButtonUp += Square_MouseLeftButtonUp;

                    _squares[row, col] = border;
                    ChessBoardGrid.Children.Add(border);
                }
            }
            UpdateSquareTags();
        }

        private void UpdateSquareTags()
        {
            for (int row = 0; row < 8; row++)
            {
                for (int col = 0; col < 8; col++)
                {
                    int actualRow = _boardFlipped ? row : 7 - row;
                    int actualCol = _boardFlipped ? 7 - col : col;
                    var position = new Position((File)actualCol, actualRow + 1);
                    _squares[row, col].Tag = position;
                }
            }
        }

        private void UpdateBoardLabels()
        {
            var files = _boardFlipped ? "hgfedcba" : "abcdefgh";
            var ranks = _boardFlipped ? "12345678" : "87654321";

            var fileLabels = FileLabelsTop.Children.OfType<TextBlock>().ToList();
            var rankLabels = RankLabels.Children.OfType<TextBlock>().ToList();

            for (int i = 0; i < 8; i++)
            {
                fileLabels[i].Text = files[i].ToString();
                rankLabels[i].Text = ranks[i].ToString();
            }
        }

        private void LoadNewPuzzle()
        {
            if (!int.TryParse(MinRatingBox.Text, out int minRating) || minRating < 0 || minRating > 3500)
            {
                minRating = 800;
                MinRatingBox.Text = "800";
            }

            if (!int.TryParse(MaxRatingBox.Text, out int maxRating) || maxRating < 0 || maxRating > 3500)
            {
                maxRating = 1500;
                MaxRatingBox.Text = "1500";
            }

            if (maxRating < minRating)
            {
                maxRating = minRating + 200;
                MaxRatingBox.Text = maxRating.ToString();
            }

            _currentPuzzle = _puzzleService.GetRandomPuzzle(minRating, maxRating);

            if (_currentPuzzle == null)
            {
                SetStatus("No puzzles found in the selected rating range.", false);
                return;
            }

            // Initialize game from FEN
            _initialFen = _currentPuzzle.Fen;
            _game = new ChessGame(_initialFen);
            
            // Get all moves for this puzzle
            var allMoves = _currentPuzzle.GetMoveList();
            
            _solutionMoves = allMoves;
            _currentMoveIndex = 0;
            _displayedMoveIndex = -1;
            _puzzleSolved = false;
            _puzzleFailed = false;
            _moveList.Clear();
            _gameHistory.Clear();

            // Save initial state
            _gameHistory.Add(new GameState(_game.GetFen(), null, null, 0));

            // Determine player color
            _playerColor = _game.WhoseTurn;
            
            // First, make the opponent's setup move
            if (_solutionMoves.Length > 0)
            {
                _playerColor = _game.WhoseTurn == Player.White ? Player.Black : Player.White;
                MakeComputerMove(_solutionMoves[0]);
                _currentMoveIndex = 1;
            }

            // Flip board if player is black
            _boardFlipped = _playerColor == Player.Black;
            UpdateSquareTags();
            UpdateBoardLabels();

            // Update UI
            UpdatePuzzleInfo();
            UpdateBoard();
            UpdatePlayerIndicator();
            UpdateMoveListHighlight();
            SetStatus("Find the best move!", false);
            
            // Start fetching the source game in the background
            _sourceGame = null;
            _ = FetchSourceGameAsync();
        }
        
        private async Task FetchSourceGameAsync()
        {
            if (_currentPuzzle?.GameUrl == null) return;
            
            try
            {
                _sourceGame = await _lichessGameService.GetGameAsync(_currentPuzzle.GameUrl);
                
                // Calculate the puzzle start ply from the URL
                var plyFromUrl = LichessGameService.ExtractPlyFromUrl(_currentPuzzle.GameUrl);
                _puzzleStartPly = plyFromUrl ?? 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to fetch source game: {ex.Message}");
                _sourceGame = null;
            }
        }

        private void UpdatePuzzleInfo()
        {
            if (_currentPuzzle == null) return;

            PuzzleRatingText.Text = _currentPuzzle.Rating.ToString();
            PuzzleIdText.Text = _currentPuzzle.PuzzleId;
            ThemesControl.ItemsSource = _currentPuzzle.Themes;
        }

        private void UpdatePlayerIndicator()
        {
            var isWhiteTurn = _game.WhoseTurn == Player.White;
            PlayerIndicator.Background = isWhiteTurn ? Brushes.White : Brushes.Black;
            PlayerToMoveText.Text = isWhiteTurn ? "White to move" : "Black to move";
        }

        private void SetStatus(string message, bool? isCorrect)
        {
            StatusText.Text = message;
            StatusBorder.Background = isCorrect switch
            {
                true => new SolidColorBrush(Color.FromRgb(46, 80, 46)),
                false when _puzzleFailed => new SolidColorBrush(Color.FromRgb(80, 46, 46)),
                _ => (SolidColorBrush)FindResource("SurfaceBrush")
            };
        }

        private void UpdateBoard()
        {
            for (int row = 0; row < 8; row++)
            {
                for (int col = 0; col < 8; col++)
                {
                    var border = _squares[row, col];
                    var position = (Position)border.Tag!;
                    var piece = _game.GetPieceAt(position);

                    // Reset square color
                    int visualRow = _boardFlipped ? row : 7 - row;
                    int visualCol = _boardFlipped ? 7 - col : col;
                    border.Background = (visualRow + visualCol) % 2 == 0 ? LightSquareBrush : DarkSquareBrush;

                    var currentPieceKey = piece != null ? GetPieceKey(piece) : null;
                    var existingChild = border.Child;
                    var needsUpdate = false;

                    if (piece == null)
                    {
                        if (existingChild != null)
                        {
                            needsUpdate = true;
                        }
                    }
                    else
                    {
                        if (existingChild is Image existingImage && existingImage.Tag is string tag)
                        {
                            needsUpdate = tag != currentPieceKey;
                        }
                        else if (existingChild is TextBlock existingText && existingText.Tag is string textTag)
                        {
                            needsUpdate = textTag != currentPieceKey;
                        }
                        else
                        {
                            needsUpdate = true;
                        }
                    }

                    if (!needsUpdate) continue;

                    if (piece != null)
                    {
                        var pieceKey = currentPieceKey!;
                        if (_pieceImages.TryGetValue(pieceKey, out var image))
                        {
                            border.Child = new Image
                            {
                                Source = image,
                                Width = 52,
                                Height = 52,
                                HorizontalAlignment = HorizontalAlignment.Center,
                                VerticalAlignment = VerticalAlignment.Center,
                                Tag = pieceKey
                            };
                        }
                        else
                        {
                            border.Child = new TextBlock
                            {
                                Text = GetPieceUnicode(piece),
                                FontSize = 40,
                                HorizontalAlignment = HorizontalAlignment.Center,
                                VerticalAlignment = VerticalAlignment.Center,
                                Foreground = piece.Owner == Player.White ? Brushes.White : Brushes.Black,
                                Tag = pieceKey
                            };
                        }
                    }
                    else
                    {
                        border.Child = null;
                    }
                }
            }
            UpdatePlayerIndicator();
        }

        private void Square_Click(object sender, MouseButtonEventArgs e)
        {
            // If viewing history, return to current position first
            if (_displayedMoveIndex >= 0 && _displayedMoveIndex < _gameHistory.Count - 1)
            {
                GoToMove(_gameHistory.Count - 1);
                return;
            }

            if (_puzzleSolved || _puzzleFailed) return;
            if (_game.WhoseTurn != _playerColor) return;

            if (sender is not Border border || border.Tag is not Position clickedPosition)
                return;

            var pieceOnSquare = _game.GetPieceAt(clickedPosition);
            if (pieceOnSquare != null && pieceOnSquare.Owner == _game.WhoseTurn)
            {
                _dragStartPosition = clickedPosition;
                _dragStartPoint = e.GetPosition(ChessBoardGrid);
                _isDragging = false;
            }
            else
            {
                ResetDragState();
            }

            if (_selectedPosition == null)
            {
                if (pieceOnSquare != null && pieceOnSquare.Owner == _game.WhoseTurn)
                {
                    _selectedPosition = clickedPosition;
                    HighlightSelectedSquare(clickedPosition);
                    HighlightValidMoves(clickedPosition);
                }
            }
            else
            {
                HandleUserMove(_selectedPosition, clickedPosition);
            }
        }

        private void Square_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragStartPosition == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var current = e.GetPosition(ChessBoardGrid);

            if (!_isDragging)
            {
                var dx = current.X - _dragStartPoint.X;
                var dy = current.Y - _dragStartPoint.Y;

                if (Math.Abs(dx) > 4 || Math.Abs(dy) > 4)
                {
                    _isDragging = true;
                    ShowDragVisual(_dragStartPosition!);
                }
            }
            else
            {
                UpdateDragVisualPosition(current);
            }
        }

        private void Square_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDragging || _dragStartPosition == null)
            {
                ResetDragState();
                return;
            }

            if (_displayedMoveIndex >= 0 && _displayedMoveIndex < _gameHistory.Count - 1)
            {
                GoToMove(_gameHistory.Count - 1);
                ResetDragState();
                return;
            }

            if (_puzzleSolved || _puzzleFailed || _game.WhoseTurn != _playerColor)
            {
                ResetDragState();
                return;
            }

            var target = GetBoardPositionFromPoint(e.GetPosition(ChessBoardGrid));
            if (target != null)
            {
                _selectedPosition = _dragStartPosition;
                HandleUserMove(_dragStartPosition!, target);
            }

            ResetDragState();
        }

        private void ResetDragState()
        {
            _dragStartPosition = null;
            _isDragging = false;
            if (_dragVisualElement != null)
            {
                DragCanvas.Children.Remove(_dragVisualElement);
                _dragVisualElement = null;
            }
        }

        private Position? GetBoardPositionFromPoint(Point point)
        {
            if (ChessBoardGrid.ActualWidth <= 0 || ChessBoardGrid.ActualHeight <= 0)
                return null;

            var squareSize = ChessBoardGrid.ActualWidth / 8.0;
            if (squareSize <= 0) return null;

            var col = (int)(point.X / squareSize);
            var row = (int)(point.Y / squareSize);

            if (row < 0 || row > 7 || col < 0 || col > 7)
                return null;

            return (Position)_squares[row, col].Tag!;
        }

        private void HandleUserMove(Position from, Position to)
        {
            var move = new Move(from, to, _game.WhoseTurn);

            // Check for pawn promotion
            var piece = _game.GetPieceAt(from);
            char? promotion = null;
            if (piece is Pawn)
            {
                int promotionRank = _game.WhoseTurn == Player.White ? 8 : 1;
                if (to.Rank == promotionRank)
                {
                    // Check what the solution expects for promotion
                    if (_currentMoveIndex < _solutionMoves.Length)
                    {
                        var expectedMove = _solutionMoves[_currentMoveIndex];
                        if (expectedMove.Length == 5)
                        {
                            promotion = char.ToUpper(expectedMove[4]);
                        }
                    }
                    promotion ??= 'Q';
                    move = new Move(from, to, _game.WhoseTurn, promotion.Value);
                }
            }

            if (_game.IsValidMove(move))
            {
                var uciMove = GetUciMove(from, to, promotion);
                CheckAndMakeMove(uciMove, move);
            }

            _selectedPosition = null;
            UpdateBoard();
        }

        private void ShowDragVisual(Position position)
        {
            var piece = _game.GetPieceAt(position);
            if (piece == null) return;

            UIElement visual;
            var pieceKey = GetPieceKey(piece);
            if (_pieceImages.TryGetValue(pieceKey, out var image))
            {
                visual = new Image
                {
                    Source = image,
                    Width = 52,
                    Height = 52,
                    Opacity = 0.9,
                    IsHitTestVisible = false,
                    RenderTransform = new ScaleTransform(1.08, 1.08),
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    Effect = CreateDragShadow()
                };
            }
            else
            {
                visual = new TextBlock
                {
                    Text = GetPieceUnicode(piece),
                    FontSize = 40,
                    Foreground = piece.Owner == Player.White ? Brushes.White : Brushes.Black,
                    Opacity = 0.9,
                    IsHitTestVisible = false,
                    RenderTransform = new ScaleTransform(1.08, 1.08),
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    Effect = CreateDragShadow()
                };
            }

            _dragVisualElement = visual;
            DragCanvas.Children.Add(visual);
            UpdateDragVisualPosition(_dragStartPoint);
        }

        private void UpdateDragVisualPosition(Point point)
        {
            if (_dragVisualElement == null) return;

            if (_dragVisualElement is FrameworkElement fe)
            {
                var size = double.IsNaN(fe.Width) || fe.Width <= 0 ? 52 : fe.Width;
                Canvas.SetLeft(_dragVisualElement, point.X - size / 2);
                Canvas.SetTop(_dragVisualElement, point.Y - size / 2);
            }
            else
            {
                Canvas.SetLeft(_dragVisualElement, point.X - 26);
                Canvas.SetTop(_dragVisualElement, point.Y - 26);
            }
        }

        private static Effect CreateDragShadow()
        {
            return new DropShadowEffect
            {
                Color = Color.FromArgb(160, 0, 0, 0),
                BlurRadius = 10,
                Opacity = 0.8,
                ShadowDepth = 3,
                Direction = 315
            };
        }

        private void CheckAndMakeMove(string uciMove, Move move)
        {
            if (_currentMoveIndex >= _solutionMoves.Length) return;

            var expectedMove = _solutionMoves[_currentMoveIndex];

            if (uciMove.Equals(expectedMove, StringComparison.OrdinalIgnoreCase))
            {
                // Correct move
                var san = GetSanNotation(_game, move);
                _game.MakeMove(move, true);
                AddMoveToList(move, san);
                _currentMoveIndex++;

                if (_currentMoveIndex >= _solutionMoves.Length)
                {
                    // Puzzle solved!
                    _puzzleSolved = true;
                    SetStatus("✓ Puzzle solved!", true);
                    ShowFullGameMoveList();
                    UpdateMoveListHighlight();
                }
                else
                {
                    // Make opponent's response
                    SetStatus("Correct! Keep going...", true);
                    UpdateMoveListHighlight();
                    Dispatcher.BeginInvoke(async () =>
                    {
                        await Task.Delay(500);
                        if (_currentMoveIndex < _solutionMoves.Length)
                        {
                            MakeComputerMove(_solutionMoves[_currentMoveIndex]);
                            _currentMoveIndex++;
                            
                            if (_currentMoveIndex >= _solutionMoves.Length)
                            {
                                _puzzleSolved = true;
                                SetStatus("✓ Puzzle solved!", true);
                                ShowFullGameMoveList();
                            }
                            else
                            {
                                SetStatus("Your turn!", false);
                            }
                            UpdateMoveListHighlight();
                        }
                    });
                }
            }
            else
            {
                // Wrong move
                _puzzleFailed = true;
                SetStatus($"✗ Incorrect. The best move was {FormatMove(expectedMove)}", false);
            }
        }

        private void MakeComputerMove(string uciMove)
        {
            var from = ParsePosition(uciMove[..2]);
            var to = ParsePosition(uciMove[2..4]);
            char? promotion = uciMove.Length == 5 ? char.ToUpper(uciMove[4]) : null;

            var move = promotion.HasValue
                ? new Move(from, to, _game.WhoseTurn, promotion.Value)
                : new Move(from, to, _game.WhoseTurn);

            if (_game.IsValidMove(move))
            {
                var san = GetSanNotation(_game, move);
                _game.MakeMove(move, true);
                AddMoveToList(move, san);
                UpdateBoard();
            }
        }

        private string GetSanNotation(ChessGame game, Move move)
        {
            var piece = game.GetPieceAt(move.OriginalPosition);
            if (piece == null) return "";

            var san = new System.Text.StringBuilder();
            bool isCapture = game.GetPieceAt(move.NewPosition) != null;
            
            // Handle en passant capture detection
            bool isEnPassant = piece is Pawn && 
                               move.OriginalPosition.File != move.NewPosition.File && 
                               game.GetPieceAt(move.NewPosition) == null;
            
            // Handle castling
            if (piece is King)
            {
                int fileDiff = (int)move.NewPosition.File - (int)move.OriginalPosition.File;
                if (fileDiff == 2) return "O-O";
                if (fileDiff == -2) return "O-O-O";
            }

            // Piece letter (not for pawns)
            if (piece is not Pawn)
            {
                san.Append(GetPieceLetter(piece));
                
                // Add disambiguation if needed (file, rank, or both)
                var validMoves = game.GetValidMoves(game.WhoseTurn)
                    .Where(m => game.GetPieceAt(m.OriginalPosition)?.GetType() == piece.GetType() &&
                                m.NewPosition.Equals(move.NewPosition) &&
                                !m.OriginalPosition.Equals(move.OriginalPosition))
                    .ToList();
                
                if (validMoves.Count > 0)
                {
                    bool sameFile = validMoves.Any(m => m.OriginalPosition.File == move.OriginalPosition.File);
                    bool sameRank = validMoves.Any(m => m.OriginalPosition.Rank == move.OriginalPosition.Rank);
                    
                    if (!sameFile)
                        san.Append((char)('a' + (int)move.OriginalPosition.File));
                    else if (!sameRank)
                        san.Append(move.OriginalPosition.Rank);
                    else
                    {
                        san.Append((char)('a' + (int)move.OriginalPosition.File));
                        san.Append(move.OriginalPosition.Rank);
                    }
                }
            }
            else if (isCapture || isEnPassant)
            {
                // For pawn captures, only show the origin file
                san.Append((char)('a' + (int)move.OriginalPosition.File));
            }

            // Capture symbol
            if (isCapture || isEnPassant)
            {
                san.Append('x');
            }

            // Destination square
            san.Append((char)('a' + (int)move.NewPosition.File));
            san.Append(move.NewPosition.Rank);

            // Promotion
            if (move.Promotion.HasValue)
            {
                san.Append('=');
                san.Append(char.ToUpper(move.Promotion.Value));
            }

            return san.ToString();
        }

        private static char GetPieceLetter(Piece piece)
        {
            return piece switch
            {
                King => 'K',
                Queen => 'Q',
                Rook => 'R',
                Bishop => 'B',
                Knight => 'N',
                _ => ' '
            };
        }
        
        private static string ConvertToFigurine(string san)
        {
            // Convert piece letters to figurine symbols for display
            // Only convert the first character if it's a piece letter
            if (string.IsNullOrEmpty(san)) return san;
            
            return san[0] switch
            {
                'K' => "♔" + san[1..],
                'Q' => "♕" + san[1..],
                'R' => "♖" + san[1..],
                'B' => "♗" + san[1..],
                'N' => "♘" + san[1..],
                _ => san
            };
        }

        private static Position ParsePosition(string pos)
        {
            var file = (File)(pos[0] - 'a');
            var rank = pos[1] - '0';
            return new Position(file, rank);
        }

        private static string GetUciMove(Position from, Position to, char? promotion)
        {
            var move = $"{(char)('a' + (int)from.File)}{from.Rank}{(char)('a' + (int)to.File)}{to.Rank}";
            if (promotion.HasValue)
                move += char.ToLower(promotion.Value);
            return move;
        }

        private static string FormatMove(string uciMove)
        {
            return uciMove.Length >= 4 ? $"{uciMove[..2]}-{uciMove[2..4]}" : uciMove;
        }

        private void HighlightSelectedSquare(Position position)
        {
            var border = GetSquareAt(position);
            if (border != null)
                border.Background = SelectedBrush;
        }

        private void HighlightValidMoves(Position from)
        {
            var validMoves = _game.GetValidMoves(from);
            foreach (var move in validMoves)
            {
                var square = GetSquareAt(move.NewPosition);
                if (square == null) continue;

                if (square.Child == null)
                {
                    square.Child = new Ellipse
                    {
                        Width = 15,
                        Height = 15,
                        Fill = ValidMoveBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                }
                else
                {
                    square.Background = ValidMoveBrush;
                }
            }
        }

        private Border? GetSquareAt(Position position)
        {
            for (int row = 0; row < 8; row++)
            {
                for (int col = 0; col < 8; col++)
                {
                    if (_squares[row, col].Tag is Position pos && pos.Equals(position))
                        return _squares[row, col];
                }
            }
            return null;
        }

        private void AddMoveToList(Move move, string san)
        {
            // The player who just moved (opposite of whose turn it is now)
            bool isWhiteMove = _game.WhoseTurn == Player.Black;
            
            // Get the move number from the FEN - parse the fullmove number from initial FEN
            // and calculate based on moves made
            int initialFullmove = GetFullmoveFromFen(_initialFen);
            bool initialWhiteToMove = _initialFen.Split(' ')[1] == "w";
            
            // Calculate the actual move number
            // _moveList.Count is the number of half-moves already recorded
            int halfMovesMade = _moveList.Count;
            int moveNumber;
            
            if (initialWhiteToMove)
            {
                // If white moved first in puzzle, move numbers are straightforward
                moveNumber = initialFullmove + (halfMovesMade / 2);
                if (!isWhiteMove) moveNumber = initialFullmove + ((halfMovesMade + 1) / 2);
            }
            else
            {
                // If black moved first, first move is black's at initialFullmove
                if (halfMovesMade == 0)
                    moveNumber = initialFullmove;
                else
                    moveNumber = initialFullmove + ((halfMovesMade + 1) / 2);
            }

            // Save game state
            var fen = _game.GetFen();
            _gameHistory.Add(new GameState(fen, move, san, _gameHistory.Count));

            // Add check/checkmate suffix
            var currentSan = san;
            if (_game.IsInCheck(_game.WhoseTurn))
            {
                currentSan += _game.IsCheckmated(_game.WhoseTurn) ? "#" : "+";
            }

            // Create display entry
            // Show move number for white moves, or for black's first move if puzzle starts with black
            bool showMoveNumber = isWhiteMove || (_moveList.Count == 0 && !initialWhiteToMove);
            string moveNumberDisplay = isWhiteMove ? $"{moveNumber}." : $"{moveNumber}...";

            var entry = new MoveDisplayEntry
            {
                Index = _gameHistory.Count - 1,
                San = ConvertToFigurine(currentSan),
                MoveNumber = moveNumber,
                IsWhiteMove = isWhiteMove,
                ShowMoveNumber = showMoveNumber,
                MoveNumberDisplay = moveNumberDisplay,
                Background = TransparentBrush
            };

            _moveList.Add(entry);
            _displayedMoveIndex = _gameHistory.Count - 1;
            UpdateMoveListHighlight();

            // Scroll to end
            Dispatcher.BeginInvoke(() =>
            {
                MoveListScrollViewer.ScrollToEnd();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        private static int GetFullmoveFromFen(string fen)
        {
            var parts = fen.Split(' ');
            if (parts.Length >= 6 && int.TryParse(parts[5], out int fullmove))
                return fullmove;
            return 1;
        }

        private void UpdateMoveListHighlight()
        {
            int currentIndex = _displayedMoveIndex >= 0 ? _displayedMoveIndex : _gameHistory.Count - 1;

            for (int i = 0; i < _moveList.Count; i++)
            {
                var entry = _moveList[i];
                var isCurrentMove = entry.Index == currentIndex;
                
                SolidColorBrush background;
                if (isCurrentMove)
                    background = CurrentMoveBrush;
                else if (entry.IsPuzzleMove)
                    background = PuzzleMoveBrush;
                else
                    background = TransparentBrush;
                    
                _moveList[i] = entry with { Background = background };
            }
        }
        
        private void ShowFullGameMoveList()
        {
            if (_sourceGame == null || _sourceGame.Moves.Count == 0) return;
            
            // Save the current board position (FEN) to restore after rebuilding history
            var currentFen = _game.GetFen();
            
            // Clear current move list and game history
            _moveList.Clear();
            _gameHistory.Clear();
            
            // Start from the beginning of the game
            var game = new ChessGame();
            _gameHistory.Add(new GameState(game.GetFen(), null, null, 0));
            
            // Determine which ply range contains the puzzle moves
            // _puzzleStartPly is the ply where the puzzle position is shown (before the setup move)
            int puzzleStartPly = _puzzleStartPly;
            int puzzleEndPly = puzzleStartPly + _solutionMoves.Length;
            _lastPuzzleMoveIndex = -1;
            int currentPositionIndex = -1;
            
            // Process all moves from the source game
            for (int i = 0; i < _sourceGame.Moves.Count; i++)
            {
                var sanMove = _sourceGame.Moves[i];
                int currentPly = i + 1; // Ply is 1-indexed
                
                try
                {
                    // Parse SAN and make the move
                    var move = ParseSanMove(game, sanMove);
                    if (move == null) break;
                    
                    // Determine if this is a puzzle move
                    bool isPuzzleMove = currentPly > puzzleStartPly && currentPly <= puzzleEndPly;
                    
                    // Calculate move number
                    bool isWhiteMove = game.WhoseTurn == Player.White;
                    int moveNumber = (i / 2) + 1;
                    
                    // Make the move
                    game.MakeMove(move, true);
                    
                    // Add check/checkmate suffix
                    var displaySan = sanMove;
                    if (!displaySan.EndsWith('+') && !displaySan.EndsWith('#'))
                    {
                        if (game.IsInCheck(game.WhoseTurn))
                        {
                            displaySan += game.IsCheckmated(game.WhoseTurn) ? "#" : "+";
                        }
                    }
                    
                    // Save game state
                    var fen = game.GetFen();
                    _gameHistory.Add(new GameState(fen, move, displaySan, _gameHistory.Count));
                    
                    // Track the last puzzle move index
                    if (isPuzzleMove)
                    {
                        _lastPuzzleMoveIndex = _gameHistory.Count - 1;
                    }
                    
                    // Check if this position matches the current board position
                    if (fen.Split(' ')[0] == currentFen.Split(' ')[0])
                    {
                        currentPositionIndex = _gameHistory.Count - 1;
                    }
                    
                    // Create display entry
                    bool showMoveNumber = isWhiteMove || _moveList.Count == 0;
                    string moveNumberDisplay = isWhiteMove ? $"{moveNumber}." : $"{moveNumber}...";
                    
                    var entry = new MoveDisplayEntry
                    {
                        Index = _gameHistory.Count - 1,
                        San = ConvertToFigurine(displaySan),
                        MoveNumber = moveNumber,
                        IsWhiteMove = isWhiteMove,
                        ShowMoveNumber = showMoveNumber,
                        MoveNumberDisplay = moveNumberDisplay,
                        IsPuzzleMove = isPuzzleMove,
                        Background = isPuzzleMove ? PuzzleMoveBrush : TransparentBrush
                    };
                    
                    _moveList.Add(entry);
                }
                catch
                {
                    // Stop processing if we can't parse a move
                    break;
                }
            }
            
            // Restore the displayed position to match the current board
            // Use the index we found that matches the current FEN, or fall back to last puzzle move
            if (currentPositionIndex > 0)
            {
                _displayedMoveIndex = currentPositionIndex;
            }
            else if (_lastPuzzleMoveIndex > 0)
            {
                _displayedMoveIndex = _lastPuzzleMoveIndex;
            }
            else
            {
                _displayedMoveIndex = _gameHistory.Count - 1;
            }
            
            // Don't change the board - just update the highlight
            UpdateMoveListHighlight();
        }
        
        private static Move? ParseSanMove(ChessGame game, string san)
        {
            // Remove check/checkmate symbols for parsing
            san = san.TrimEnd('+', '#');
            
            // Handle castling
            if (san == "O-O" || san == "0-0")
            {
                var from = game.WhoseTurn == Player.White ? new Position(File.E, 1) : new Position(File.E, 8);
                var to = game.WhoseTurn == Player.White ? new Position(File.G, 1) : new Position(File.G, 8);
                return new Move(from, to, game.WhoseTurn);
            }
            if (san == "O-O-O" || san == "0-0-0")
            {
                var from = game.WhoseTurn == Player.White ? new Position(File.E, 1) : new Position(File.E, 8);
                var to = game.WhoseTurn == Player.White ? new Position(File.C, 1) : new Position(File.C, 8);
                return new Move(from, to, game.WhoseTurn);
            }
            
            // Parse promotion
            char? promotion = null;
            if (san.Contains('='))
            {
                promotion = san[^1];
                san = san[..^2];
            }
            else if (san.Length > 2 && "QRBN".Contains(san[^1]) && char.IsDigit(san[^2]))
            {
                // Handle promotion without = (e.g., e8Q)
                promotion = san[^1];
                san = san[..^1];
            }
            
            // Remove capture symbol
            san = san.Replace("x", "");
            
            // Determine piece type
            Type pieceType = typeof(Pawn);
            if (san.Length > 0 && "KQRBN".Contains(san[0]))
            {
                pieceType = san[0] switch
                {
                    'K' => typeof(King),
                    'Q' => typeof(Queen),
                    'R' => typeof(Rook),
                    'B' => typeof(Bishop),
                    'N' => typeof(Knight),
                    _ => typeof(Pawn)
                };
                san = san[1..];
            }
            
            // Parse destination square (last 2 characters)
            if (san.Length < 2) return null;
            var destFile = (File)(san[^2] - 'a');
            var destRank = san[^1] - '0';
            var destination = new Position(destFile, destRank);
            
            // Parse disambiguation (everything before destination)
            string disambiguation = san[..^2];
            File? disambigFile = null;
            int? disambigRank = null;
            
            foreach (char c in disambiguation)
            {
                if (c >= 'a' && c <= 'h')
                    disambigFile = (File)(c - 'a');
                else if (c >= '1' && c <= '8')
                    disambigRank = c - '0';
            }
            
            // Find the matching move
            var validMoves = game.GetValidMoves(game.WhoseTurn)
                .Where(m =>
                {
                    var piece = game.GetPieceAt(m.OriginalPosition);
                    return piece != null && piece.GetType() == pieceType;
                })
                .Where(m => m.NewPosition.Equals(destination));
            
            if (disambigFile.HasValue)
                validMoves = validMoves.Where(m => m.OriginalPosition.File == disambigFile.Value);
            if (disambigRank.HasValue)
                validMoves = validMoves.Where(m => m.OriginalPosition.Rank == disambigRank.Value);
            
            var move = validMoves.FirstOrDefault();
            if (move == null) return null;
            
            // Add promotion if needed
            if (promotion.HasValue)
            {
                return new Move(move.OriginalPosition, move.NewPosition, game.WhoseTurn, char.ToUpper(promotion.Value));
            }
            
            return move;
        }

        private void MoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is int index)
            {
                GoToMove(index);
            }
        }

        private void GoToMove(int historyIndex)
        {
            if (historyIndex < 0 || historyIndex >= _gameHistory.Count) return;

            var state = _gameHistory[historyIndex];
            _game = new ChessGame(state.Fen);
            _displayedMoveIndex = historyIndex;

            UpdateBoard();
            UpdateMoveListHighlight();
        }

        private static string GetPieceKey(Piece piece)
        {
            string color = piece.Owner == Player.White ? "w" : "b";
            string type = piece switch
            {
                King => "K",
                Queen => "Q",
                Rook => "R",
                Bishop => "B",
                Knight => "N",
                Pawn => "P",
                _ => ""
            };
            return $"{color}{type}";
        }

        private static string GetPieceUnicode(Piece piece)
        {
            bool isWhite = piece.Owner == Player.White;
            return piece switch
            {
                King => isWhite ? "♔" : "♚",
                Queen => isWhite ? "♕" : "♛",
                Rook => isWhite ? "♖" : "♜",
                Bishop => isWhite ? "♗" : "♝",
                Knight => isWhite ? "♘" : "♞",
                Pawn => isWhite ? "♙" : "♟",
                _ => ""
            };
        }

        private void BtnHint_Click(object sender, RoutedEventArgs e)
        {
            if (_puzzleSolved || _puzzleFailed || _currentMoveIndex >= _solutionMoves.Length) return;

            // Make sure we're at the current position
            if (_displayedMoveIndex >= 0 && _displayedMoveIndex < _gameHistory.Count - 1)
            {
                GoToMove(_gameHistory.Count - 1);
            }

            var hint = _solutionMoves[_currentMoveIndex];
            var from = ParsePosition(hint[..2]);
            HighlightSquareTemporary(from, CorrectMoveBrush);
            SetStatus($"Hint: Try moving from {hint[..2]}", false);
        }

        private void HighlightSquareTemporary(Position position, SolidColorBrush brush)
        {
            var square = GetSquareAt(position);
            if (square != null)
            {
                square.Background = brush;
                
                Dispatcher.BeginInvoke(async () =>
                {
                    await Task.Delay(1500);
                    if (!_puzzleSolved && !_puzzleFailed)
                        UpdateBoard();
                });
            }
        }

        private void BtnRetry_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPuzzle == null) return;

            // Reload the same puzzle
            _game = new ChessGame(_currentPuzzle.Fen);
            _solutionMoves = _currentPuzzle.GetMoveList();
            _currentMoveIndex = 0;
            _displayedMoveIndex = -1;
            _puzzleSolved = false;
            _puzzleFailed = false;
            _moveList.Clear();
            _gameHistory.Clear();

            // Save initial state
            _gameHistory.Add(new GameState(_game.GetFen(), null, null, 0));

            // Make setup move
            if (_solutionMoves.Length > 0)
            {
                MakeComputerMove(_solutionMoves[0]);
                _currentMoveIndex = 1;
            }

            UpdateBoard();
            UpdateMoveListHighlight();
            SetStatus("Find the best move!", false);
        }

        private void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LoadNewPuzzle();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading puzzle: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        private void BtnRefreshDb_Click(object sender, RoutedEventArgs e)
        {
            // IMPORTANT: Dispose the puzzle service BEFORE opening the setup window
            // so that the database file is not locked when we try to replace it
            _puzzleService.Dispose();
            
            var setupWindow = new DatabaseSetupWindow(isRefresh: true);
            setupWindow.Owner = this;
            var result = setupWindow.ShowDialog();
            
            try
            {
                // Always recreate the puzzle service, whether refresh succeeded or was cancelled
                _puzzleService = new PuzzleService();
                
                if (result == true && setupWindow.DatabaseReady)
                {
                    // Load a new puzzle to verify it works
                    LoadNewPuzzle();
                    
                    MessageBox.Show("Database updated successfully!", 
                        "Database Updated", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error reloading database: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        private void BtnCopyPgn_Click(object sender, RoutedEventArgs e)
        {
            var pgn = GeneratePgn();
            if (!string.IsNullOrEmpty(pgn))
            {
                Clipboard.SetText(pgn);
                SetStatus("PGN copied to clipboard!", null);
            }
        }
        
        private string GeneratePgn()
        {
            var sb = new System.Text.StringBuilder();
            
            // Add headers
            if (_sourceGame != null)
            {
                if (!string.IsNullOrEmpty(_sourceGame.Event))
                    sb.AppendLine($"[Event \"{_sourceGame.Event}\"]");
                if (!string.IsNullOrEmpty(_sourceGame.Date))
                    sb.AppendLine($"[Date \"{_sourceGame.Date}\"]");
                if (!string.IsNullOrEmpty(_sourceGame.WhitePlayer))
                    sb.AppendLine($"[White \"{_sourceGame.WhitePlayer}\"]");
                if (!string.IsNullOrEmpty(_sourceGame.BlackPlayer))
                    sb.AppendLine($"[Black \"{_sourceGame.BlackPlayer}\"]");
                if (_sourceGame.WhiteElo.HasValue)
                    sb.AppendLine($"[WhiteElo \"{_sourceGame.WhiteElo}\"]");
                if (_sourceGame.BlackElo.HasValue)
                    sb.AppendLine($"[BlackElo \"{_sourceGame.BlackElo}\"]");
                if (!string.IsNullOrEmpty(_sourceGame.Result))
                    sb.AppendLine($"[Result \"{_sourceGame.Result}\"]");
                if (!string.IsNullOrEmpty(_sourceGame.Eco))
                    sb.AppendLine($"[ECO \"{_sourceGame.Eco}\"]");
                if (!string.IsNullOrEmpty(_sourceGame.Opening))
                    sb.AppendLine($"[Opening \"{_sourceGame.Opening}\"]");
                if (!string.IsNullOrEmpty(_sourceGame.TimeControl))
                    sb.AppendLine($"[TimeControl \"{_sourceGame.TimeControl}\"]");
                if (!string.IsNullOrEmpty(_sourceGame.GameUrl))
                    sb.AppendLine($"[Site \"{_sourceGame.GameUrl}\"]");
            }
            else if (_currentPuzzle != null)
            {
                sb.AppendLine($"[Event \"Lichess Puzzle {_currentPuzzle.PuzzleId}\"]");
                sb.AppendLine($"[FEN \"{_currentPuzzle.Fen}\"]");
                sb.AppendLine($"[SetUp \"1\"]");
            }
            
            sb.AppendLine();
            
            // Add moves
            if (_sourceGame != null && _sourceGame.Moves.Count > 0)
            {
                for (int i = 0; i < _sourceGame.Moves.Count; i++)
                {
                    if (i % 2 == 0)
                    {
                        if (i > 0) sb.Append(' ');
                        sb.Append($"{(i / 2) + 1}.");
                    }
                    sb.Append($" {_sourceGame.Moves[i]}");
                }
                if (!string.IsNullOrEmpty(_sourceGame.Result))
                    sb.Append($" {_sourceGame.Result}");
            }
            else
            {
                // Generate PGN from game history (puzzle moves only)
                bool initialWhiteToMove = _initialFen.Split(' ')[1] == "w";
                int initialFullmove = GetFullmoveFromFen(_initialFen);
                
                for (int i = 1; i < _gameHistory.Count; i++)
                {
                    var state = _gameHistory[i];
                    if (state.San == null) continue;
                    
                    bool isWhiteMove = (initialWhiteToMove && (i % 2 == 1)) || (!initialWhiteToMove && (i % 2 == 0));
                    int moveNum = initialFullmove + ((i - 1) / 2);
                    if (!initialWhiteToMove && i == 1)
                        moveNum = initialFullmove;
                    else if (!initialWhiteToMove)
                        moveNum = initialFullmove + (i / 2);
                    
                    if (isWhiteMove)
                    {
                        if (i > 1) sb.Append(' ');
                        sb.Append($"{moveNum}. {state.San}");
                    }
                    else
                    {
                        if (i == 1)
                            sb.Append($"{moveNum}... {state.San}");
                        else
                            sb.Append($" {state.San}");
                    }
                }
            }
            
            return sb.ToString();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _puzzleService.Dispose();
            _lichessGameService.Dispose();
        }
    }

    public record MoveDisplayEntry
    {
        public int Index { get; init; }
        public string San { get; init; } = "";
        public int MoveNumber { get; init; }
        public bool IsWhiteMove { get; init; }
        public bool ShowMoveNumber { get; init; }
        public string MoveNumberDisplay { get; init; } = "";
        public bool IsPuzzleMove { get; init; }
        public SolidColorBrush Background { get; init; } = new(Colors.Transparent);
    }

    public record GameState(string Fen, Move? Move, string? San, int Index);
}
