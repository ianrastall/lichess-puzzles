//! Chess rules over cozy-chess: replaying puzzle moves and writing them as SAN.

use cozy_chess::util::{display_san_move, parse_uci_move};
use cozy_chess::{Board, Color, Move};

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum LineError {
    Fen(String),
    Move { index: usize, uci: String },
}

impl std::fmt::Display for LineError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            LineError::Fen(fen) => write!(f, "invalid FEN `{fen}`"),
            LineError::Move { index, uci } => write!(f, "illegal move {} `{uci}`", index + 1),
        }
    }
}

pub fn parse_fen(fen: &str) -> Result<Board, LineError> {
    Board::from_fen(fen, false).map_err(|_| LineError::Fen(fen.to_owned()))
}

/// Plays UCI moves from `board`, returning each move in cozy-chess form (castling as king takes rook).
pub fn replay(board: &Board, uci_moves: &[&str]) -> Result<Vec<Move>, LineError> {
    let mut board = board.clone();
    let mut moves = Vec::with_capacity(uci_moves.len());
    for (index, &uci) in uci_moves.iter().enumerate() {
        let illegal = || LineError::Move { index, uci: uci.to_owned() };
        let mv = parse_uci_move(&board, uci).map_err(|_| illegal())?;
        board.try_play(mv).map_err(|_| illegal())?;
        moves.push(mv);
    }
    Ok(moves)
}

/// SAN for each UCI move played in order from `fen`.
pub fn san_line(fen: &str, uci_moves: &[&str]) -> Result<Vec<String>, LineError> {
    let mut board = parse_fen(fen)?;
    let moves = replay(&board, uci_moves)?;
    let mut san = Vec::with_capacity(moves.len());
    for mv in moves {
        san.push(display_san_move(&board, mv).to_string());
        board.play(mv);
    }
    Ok(san)
}

/// The side that solves a puzzle: the FEN's side to move plays the opponent's first move.
pub fn solver(fen_side_to_move: Color) -> Color {
    !fen_side_to_move
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn san_of_puzzle_line() {
        let fen = "r6k/pp2r2p/4Rp1Q/3p4/8/1N1P2R1/PqP2bPP/7K b - - 0 24";
        let line = ["f2g3", "e6e7", "b2b1", "b3c1", "b1c1", "h6c1"];
        assert_eq!(san_line(fen, &line).unwrap(), ["Bxg3", "Rxe7", "Qb1+", "Nc1", "Qxc1+", "Qxc1"]);
    }

    #[test]
    fn castling_from_uci() {
        let fen = "r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1";
        assert_eq!(san_line(fen, &["e1g1", "e8c8"]).unwrap(), ["O-O", "O-O-O"]);
    }

    #[test]
    fn illegal_move_is_reported() {
        let fen = "r6k/pp2r2p/4Rp1Q/3p4/8/1N1P2R1/PqP2bPP/7K b - - 0 24";
        assert_eq!(san_line(fen, &["f2g3", "e6e8"]), Err(LineError::Move { index: 1, uci: "e6e8".into() }));
        assert!(matches!(san_line("not a fen", &[]), Err(LineError::Fen(_))));
    }
}
