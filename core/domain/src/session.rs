//! The puzzle being solved: the opponent's first move, the solver's moves and the opponent's
//! replies, hints, the solution and navigation over the line.
//!
//! The shell drives the timing: after `new` and after each correct move that leaves a reply to
//! play, it waits briefly and calls `play_opponent`.

use cozy_chess::util::{display_san_move, display_uci_move, parse_uci_move};
use cozy_chess::{Board, Color, GameStatus, Move, Piece, Square};
use serde::Serialize;

use crate::Side;
use crate::chess::{self, LineError};
use crate::store::Puzzle;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
pub enum SessionState {
    /// The opponent's first move has not been played yet.
    OpponentToMove,
    /// The solver is to move.
    Solving,
    /// The solver's move was correct; the opponent's reply is next.
    ReplyPending,
    Solved,
    SolutionShown,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
pub enum MoveOutcome {
    /// Not a legal move in the position; nothing changes.
    Illegal,
    /// Legal but not the solution; the position is unchanged and a mistake is counted.
    Wrong,
    /// The expected move; the opponent's reply is next.
    Correct,
    /// The last move of the solution, or any move that gives checkmate.
    Solved,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SessionError {
    /// The command does not apply in the current state.
    State(&'static str),
    Line(LineError),
}

impl std::fmt::Display for SessionError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            SessionError::State(message) => f.write_str(message),
            SessionError::Line(e) => write!(f, "the puzzle's moves cannot be played: {e}"),
        }
    }
}

impl std::error::Error for SessionError {}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct MoveEntry {
    pub san: String,
    pub uci: String,
    pub solver: bool,
    pub white: bool,
    /// Full-move number as written before the move.
    pub number: u32,
}

/// Everything the shell shows for the session at its viewed position.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct SessionView {
    pub puzzle: Puzzle,
    pub state: SessionState,
    pub mistakes: u32,
    pub hints: u32,
    pub solution_shown: bool,
    pub replay: bool,
    /// Moves played so far (the whole line once the puzzle ends).
    pub moves: Vec<MoveEntry>,
    /// Number of moves played.
    pub played: usize,
    /// Position shown: 0 is the puzzle's FEN, n is after the nth move.
    pub view: usize,
    /// 64 characters from a1 to h8: `.` for empty, `PNBRQK` for White and `pnbrqk` for Black.
    pub board: String,
    pub fen: String,
    pub side_to_move: Side,
    /// Squares 0 (a1) to 63 (h8).
    pub last_move: Option<(u8, u8)>,
    pub check_square: Option<u8>,
    pub hint_square: Option<u8>,
    /// The solver's legal moves in UCI, when a move can be entered (solving, at the latest position).
    pub legal_moves: Vec<String>,
}

pub struct Session {
    puzzle: Puzzle,
    start: Board,
    /// The line played or to be played; an alternative mating move replaces the rest.
    line: Vec<Move>,
    original_line: Vec<Move>,
    solver: Color,
    played: usize,
    view: usize,
    state: SessionState,
    mistakes: u32,
    hints: u32,
    solution_shown: bool,
    hint_square: Option<Square>,
    replay: bool,
}

impl Session {
    pub fn new(puzzle: Puzzle) -> Result<Session, SessionError> {
        let start = chess::parse_fen(&puzzle.fen).map_err(SessionError::Line)?;
        let uci: Vec<&str> = puzzle.moves.iter().map(String::as_str).collect();
        let line = chess::replay(&start, &uci).map_err(SessionError::Line)?;
        if line.len() < 2 {
            return Err(SessionError::State("the puzzle has no solution moves"));
        }
        let solver = chess::solver(start.side_to_move());
        Ok(Session {
            puzzle,
            start,
            original_line: line.clone(),
            line,
            solver,
            played: 0,
            view: 0,
            state: SessionState::OpponentToMove,
            mistakes: 0,
            hints: 0,
            solution_shown: false,
            hint_square: None,
            replay: false,
        })
    }

    pub fn puzzle(&self) -> &Puzzle {
        &self.puzzle
    }

    pub fn state(&self) -> SessionState {
        self.state
    }

    pub fn mistakes(&self) -> u32 {
        self.mistakes
    }

    pub fn hints(&self) -> u32 {
        self.hints
    }

    pub fn solution_shown(&self) -> bool {
        self.solution_shown
    }

    pub fn is_finished(&self) -> bool {
        matches!(self.state, SessionState::Solved | SessionState::SolutionShown)
    }

    /// Marks the session as a replay (opened again from history); replays are never rated.
    pub fn set_replay(&mut self, replay: bool) {
        self.replay = replay;
    }

    fn board_after(&self, plies: usize) -> Board {
        let mut board = self.start.clone();
        for &mv in &self.line[..plies] {
            board.play(mv);
        }
        board
    }

    /// Plays the opponent's first move or the reply to a correct move.
    pub fn play_opponent(&mut self) -> Result<(), SessionError> {
        if !matches!(self.state, SessionState::OpponentToMove | SessionState::ReplyPending) {
            return Err(SessionError::State("it is not the opponent's turn"));
        }
        self.played += 1;
        self.view = self.played;
        self.state = SessionState::Solving;
        Ok(())
    }

    /// Checks a move from the solver, given in UCI (`e1g1` for castling).
    pub fn make_move(&mut self, uci: &str) -> Result<MoveOutcome, SessionError> {
        if self.state != SessionState::Solving {
            return Err(SessionError::State("it is not the solver's turn"));
        }
        if self.view != self.played {
            return Err(SessionError::State("return to the latest position to move"));
        }
        let board = self.board_after(self.played);
        let Ok(mv) = parse_uci_move(&board, uci) else { return Ok(MoveOutcome::Illegal) };
        if !board.is_legal(mv) {
            return Ok(MoveOutcome::Illegal);
        }
        let expected = self.line[self.played];
        let mut after = board.clone();
        after.play(mv);
        let mates = after.status() == GameStatus::Won;
        if mv != expected && !mates {
            self.mistakes += 1;
            return Ok(MoveOutcome::Wrong);
        }
        if mv != expected {
            // Any checkmate solves the puzzle; it replaces the rest of the line.
            self.line.truncate(self.played);
            self.line.push(mv);
        }
        self.played += 1;
        self.view = self.played;
        self.hint_square = None;
        if self.played == self.line.len() || mates {
            self.line.truncate(self.played);
            self.state = SessionState::Solved;
            Ok(MoveOutcome::Solved)
        } else {
            self.state = SessionState::ReplyPending;
            Ok(MoveOutcome::Correct)
        }
    }

    /// Counts a hint and returns the square of the piece to move (0 = a1).
    pub fn hint(&mut self) -> Result<u8, SessionError> {
        if self.state != SessionState::Solving {
            return Err(SessionError::State("a hint is available only while solving"));
        }
        let square = self.line[self.played].from;
        self.hints += 1;
        self.hint_square = Some(square);
        self.view = self.played;
        Ok(square as u8)
    }

    /// Ends the puzzle by playing the remaining line, and shows the first move of the answer.
    pub fn show_solution(&mut self) -> Result<(), SessionError> {
        match self.state {
            SessionState::OpponentToMove | SessionState::ReplyPending => self.played += 1,
            SessionState::Solving => {}
            SessionState::Solved | SessionState::SolutionShown => {
                return Err(SessionError::State("the puzzle has already ended"));
            }
        }
        let answer = self.played + 1;
        self.played = self.line.len();
        self.view = answer.min(self.played);
        self.solution_shown = true;
        self.hint_square = None;
        self.state = SessionState::SolutionShown;
        Ok(())
    }

    /// Starts the same puzzle again as a replay.
    pub fn retry(&mut self) {
        self.line = self.original_line.clone();
        self.played = 0;
        self.view = 0;
        self.state = SessionState::OpponentToMove;
        self.mistakes = 0;
        self.hints = 0;
        self.solution_shown = false;
        self.hint_square = None;
        self.replay = true;
    }

    /// Shows the position after `ply` moves, clamped to the moves played.
    pub fn navigate(&mut self, ply: usize) {
        self.view = ply.min(self.played);
    }

    pub fn view(&self) -> SessionView {
        let board = self.board_after(self.view);
        let mut moves = Vec::with_capacity(self.played);
        let mut position = self.start.clone();
        let fullmove = u32::from(self.start.fullmove_number());
        let black_first = self.start.side_to_move() == Color::Black;
        for (i, &mv) in self.line[..self.played].iter().enumerate() {
            let white = position.side_to_move() == Color::White;
            let half = i as u32 + u32::from(black_first);
            moves.push(MoveEntry {
                san: display_san_move(&position, mv).to_string(),
                uci: display_uci_move(&position, mv).to_string(),
                solver: position.side_to_move() == self.solver,
                white,
                number: fullmove + half / 2,
            });
            position.play(mv);
        }
        let last_move = self.view.checked_sub(1).map(|i| {
            let before = self.board_after(i);
            let shown: Move = display_uci_move(&before, self.line[i]).to_string().parse().unwrap_or(self.line[i]);
            (shown.from as u8, shown.to as u8)
        });
        let check_square = (!board.checkers().is_empty()).then(|| board.king(board.side_to_move()) as u8);
        let can_move = self.state == SessionState::Solving && self.view == self.played;
        let mut legal_moves = Vec::new();
        if can_move {
            board.generate_moves(|piece_moves| {
                for mv in piece_moves {
                    legal_moves.push(display_uci_move(&board, mv).to_string());
                }
                false
            });
        }
        SessionView {
            puzzle: self.puzzle.clone(),
            state: self.state,
            mistakes: self.mistakes,
            hints: self.hints,
            solution_shown: self.solution_shown,
            replay: self.replay,
            moves,
            played: self.played,
            view: self.view,
            board: board_string(&board),
            fen: board.to_string(),
            side_to_move: side(board.side_to_move()),
            last_move,
            check_square,
            hint_square: if can_move { self.hint_square.map(|s| s as u8) } else { None },
            legal_moves,
        }
    }
}

fn side(color: Color) -> Side {
    match color {
        Color::White => Side::White,
        Color::Black => Side::Black,
    }
}

fn board_string(board: &Board) -> String {
    Square::ALL
        .iter()
        .map(|&square| match (board.piece_on(square), board.color_on(square)) {
            (Some(piece), Some(color)) => {
                let c = match piece {
                    Piece::Pawn => 'p',
                    Piece::Knight => 'n',
                    Piece::Bishop => 'b',
                    Piece::Rook => 'r',
                    Piece::Queen => 'q',
                    Piece::King => 'k',
                };
                if color == Color::White { c.to_ascii_uppercase() } else { c }
            }
            _ => '.',
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn puzzle(fen: &str, moves: &str) -> Puzzle {
        Puzzle {
            index: 0,
            id: "test1".into(),
            fen: fen.into(),
            moves: moves.split(' ').map(str::to_owned).collect(),
            rating: 1500,
            deviation: 80,
            popularity: 90,
            plays: 100,
            themes: vec![],
            opening_family: None,
            opening_variation: None,
            game_url: String::new(),
            daily_date: None,
            solver: Side::White,
        }
    }

    const FEN: &str = "r6k/pp2r2p/4Rp1Q/3p4/8/1N1P2R1/PqP2bPP/7K b - - 0 24";
    const LINE: &str = "f2g3 e6e7 b2b1 b3c1 b1c1 h6c1";

    #[test]
    fn plays_through_a_puzzle() {
        let mut s = Session::new(puzzle(FEN, LINE)).unwrap();
        let v = s.view();
        assert_eq!((v.state, v.played, v.view), (SessionState::OpponentToMove, 0, 0));
        assert!(v.legal_moves.is_empty());
        assert_eq!(&v.board[0..8], ".......K");
        assert_eq!(v.side_to_move, Side::Black);

        s.play_opponent().unwrap();
        let v = s.view();
        assert_eq!(v.state, SessionState::Solving);
        assert_eq!(v.moves[0].san, "Bxg3");
        assert_eq!((v.moves[0].number, v.moves[0].white, v.moves[0].solver), (24, false, false));
        assert_eq!(v.last_move, Some((Square::F2 as u8, Square::G3 as u8)));
        assert!(v.legal_moves.contains(&"e6e7".to_owned()));

        assert_eq!(s.make_move("h6f6").unwrap(), MoveOutcome::Wrong);
        assert_eq!(s.make_move("e6e9").unwrap(), MoveOutcome::Illegal);
        assert_eq!(s.make_move("a2a5").unwrap(), MoveOutcome::Illegal);
        assert_eq!(s.hint().unwrap(), Square::E6 as u8);
        assert_eq!(s.view().hint_square, Some(Square::E6 as u8));
        assert_eq!(s.make_move("e6e7").unwrap(), MoveOutcome::Correct);
        assert_eq!(s.state(), SessionState::ReplyPending);
        assert!(s.make_move("h6h7").is_err());
        s.play_opponent().unwrap();
        assert_eq!(s.make_move("b3c1").unwrap(), MoveOutcome::Correct);
        s.play_opponent().unwrap();
        assert_eq!(s.make_move("h6c1").unwrap(), MoveOutcome::Solved);
        let v = s.view();
        assert_eq!((v.state, v.mistakes, v.hints, v.played, v.view), (SessionState::Solved, 1, 1, 6, 6));
        assert_eq!(
            v.moves.iter().map(|m| m.san.as_str()).collect::<Vec<_>>(),
            ["Bxg3", "Rxe7", "Qb1+", "Nc1", "Qxc1+", "Qxc1"]
        );
        assert_eq!(v.moves.iter().map(|m| m.number).collect::<Vec<_>>(), [24, 25, 25, 26, 26, 27]);

        s.navigate(2);
        assert_eq!(s.view().view, 2);
        s.navigate(99);
        assert_eq!(s.view().view, 6);
    }

    #[test]
    fn navigation_while_solving_blocks_moves() {
        let mut s = Session::new(puzzle(FEN, LINE)).unwrap();
        s.play_opponent().unwrap();
        s.navigate(0);
        assert!(s.view().legal_moves.is_empty());
        assert!(s.make_move("e6e7").is_err());
        s.navigate(1);
        assert_eq!(s.make_move("e6e7").unwrap(), MoveOutcome::Correct);
    }

    #[test]
    fn any_mate_solves() {
        // After a6, both Re8# (the line) and Rb8# mate; the move not in the line is accepted too.
        let fen = "6k1/p4ppp/8/8/8/8/8/1R2R1K1 b - - 0 1";
        let mut s = Session::new(puzzle(fen, "a7a6 e1e8")).unwrap();
        s.play_opponent().unwrap();
        assert_eq!(s.make_move("b1b8").unwrap(), MoveOutcome::Solved);
        let v = s.view();
        assert_eq!(v.moves.last().unwrap().san, "Rb8#");
        assert_eq!(v.state, SessionState::Solved);
        assert_eq!(v.check_square, Some(Square::G8 as u8));

        s.retry();
        let v = s.view();
        assert_eq!((v.state, v.played, v.replay), (SessionState::OpponentToMove, 0, true));
        s.play_opponent().unwrap();
        assert_eq!(s.make_move("e1e8").unwrap(), MoveOutcome::Solved);
    }

    #[test]
    fn solution_and_castling_display() {
        let mut s = Session::new(puzzle(FEN, LINE)).unwrap();
        s.show_solution().unwrap();
        let v = s.view();
        assert_eq!((v.state, v.played, v.view, v.solution_shown), (SessionState::SolutionShown, 6, 2, true));
        assert!(s.show_solution().is_err());
        assert!(s.hint().is_err());

        let castle = "r3k2r/pppq1ppp/8/8/8/8/PPPQ1PPP/R3K2R b KQkq - 0 1";
        let mut s = Session::new(puzzle(castle, "e8c8 e1g1")).unwrap();
        s.play_opponent().unwrap();
        let v = s.view();
        assert_eq!(v.moves[0].uci, "e8c8");
        assert_eq!(v.moves[0].san, "O-O-O");
        assert_eq!(v.last_move, Some((Square::E8 as u8, Square::C8 as u8)));
        assert!(v.legal_moves.contains(&"e1g1".to_owned()));
        assert_eq!(s.make_move("e1g1").unwrap(), MoveOutcome::Solved);
        assert_eq!(s.view().moves[1].san, "O-O");
    }
}
