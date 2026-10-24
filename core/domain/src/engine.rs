//! A small chess engine for the evaluation shown beside a puzzle. It is a guide, not an
//! authority: iterative-deepening alpha-beta (principal variation search) with quiescence search,
//! a transposition table, killer and history move ordering, and an evaluation of material and
//! piece-square tables (Tomasz Michniewski's "simplified evaluation function").

use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

use cozy_chess::util::{display_san_move, display_uci_move};
use cozy_chess::{BitBoard, Board, Color, Move, Piece, Square};

const MATE: i32 = 30_000;
const INFINITY: i32 = 32_000;
const MAX_PLY: usize = 96;
const TT_SIZE: usize = 1 << 19;

/// Score from White's point of view.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Score {
    /// Centipawns.
    Centipawns(i32),
    /// Mate in this many moves; negative when Black mates.
    Mate(i32),
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SearchInfo {
    pub depth: u32,
    pub score: Score,
    /// Principal variation in SAN, from the searched position.
    pub pv_san: Vec<String>,
    pub pv_uci: Vec<String>,
    pub nodes: u64,
    pub elapsed: Duration,
}

#[derive(Clone, Copy, Default)]
struct TtEntry {
    key: u64,
    best: Option<Move>,
    depth: i8,
    score: i32,
    /// 0 exact, 1 lower bound, 2 upper bound.
    bound: u8,
}

struct Searcher<'a> {
    tt: Vec<TtEntry>,
    killers: [[Option<Move>; 2]; MAX_PLY],
    history: Box<[[i32; 64]; 64]>,
    path: Vec<u64>,
    nodes: u64,
    deadline: Instant,
    stop: &'a AtomicBool,
    stopped: bool,
}

/// Searches `board` until `limit` passes, `max_depth` is reached, a forced mate is proven or
/// `stop` is set. `report` receives each completed depth. Returns the last completed result.
pub fn analyse(
    board: &Board,
    limit: Duration,
    max_depth: u32,
    stop: &AtomicBool,
    report: &mut dyn FnMut(&SearchInfo),
) -> Option<SearchInfo> {
    let started = Instant::now();
    let mut searcher = Searcher {
        tt: vec![TtEntry::default(); TT_SIZE],
        killers: [[None; 2]; MAX_PLY],
        history: Box::new([[0; 64]; 64]),
        path: Vec::with_capacity(MAX_PLY),
        nodes: 0,
        deadline: started + limit,
        stop,
        stopped: false,
    };
    if !board.generate_moves(|_| true) {
        return None;
    }
    let mut last = None;
    for depth in 1..=max_depth.clamp(1, MAX_PLY as u32 - 8) {
        let score = searcher.negamax(board, depth as i32, 0, -INFINITY, INFINITY);
        if searcher.stopped && last.is_some() {
            break;
        }
        let pv = searcher.principal_variation(board);
        if pv.is_empty() {
            break;
        }
        let info = SearchInfo {
            depth,
            score: white_score(board, score),
            pv_san: san_line(board, &pv),
            pv_uci: uci_line(board, &pv),
            nodes: searcher.nodes,
            elapsed: started.elapsed(),
        };
        report(&info);
        last = Some(info);
        let proven_mate = score.abs() > MATE - MAX_PLY as i32 && (MATE - score.abs()) < depth as i32;
        // Starting a deeper iteration that cannot finish wastes the remaining time.
        if searcher.stopped || proven_mate || started.elapsed() * 2 > limit {
            break;
        }
    }
    last
}

fn white_score(board: &Board, score: i32) -> Score {
    let white = if board.side_to_move() == Color::White { score } else { -score };
    if white.abs() > MATE - MAX_PLY as i32 {
        let plies = MATE - white.abs();
        let moves = (plies + 1) / 2;
        Score::Mate(if white > 0 { moves } else { -moves })
    } else {
        Score::Centipawns(white)
    }
}

fn san_line(board: &Board, moves: &[Move]) -> Vec<String> {
    let mut b = board.clone();
    moves
        .iter()
        .map(|&mv| {
            let san = display_san_move(&b, mv).to_string();
            b.play(mv);
            san
        })
        .collect()
}

fn uci_line(board: &Board, moves: &[Move]) -> Vec<String> {
    let mut b = board.clone();
    moves
        .iter()
        .map(|&mv| {
            let uci = display_uci_move(&b, mv).to_string();
            b.play(mv);
            uci
        })
        .collect()
}

fn legal_moves(board: &Board) -> Vec<Move> {
    let mut moves = Vec::with_capacity(48);
    board.generate_moves(|piece_moves| {
        moves.extend(piece_moves);
        false
    });
    moves
}

fn is_capture(board: &Board, mv: Move) -> bool {
    board.colors(!board.side_to_move()).has(mv.to)
        || (board.piece_on(mv.from) == Some(Piece::Pawn) && mv.from.file() != mv.to.file())
}

fn piece_value(piece: Piece) -> i32 {
    match piece {
        Piece::Pawn => 100,
        Piece::Knight => 320,
        Piece::Bishop => 330,
        Piece::Rook => 500,
        Piece::Queen => 900,
        Piece::King => 0,
    }
}

impl Searcher<'_> {
    fn out_of_time(&mut self) -> bool {
        if self.nodes & 2047 == 0 && (Instant::now() >= self.deadline || self.stop.load(Ordering::Relaxed)) {
            self.stopped = true;
        }
        self.stopped
    }

    fn order(&self, board: &Board, moves: &mut [Move], tt_move: Option<Move>, ply: usize) {
        let score = |mv: &Move| -> i32 {
            if Some(*mv) == tt_move {
                return 1_000_000;
            }
            if is_capture(board, *mv) {
                let victim = board.piece_on(mv.to).map_or(100, piece_value);
                let attacker = board.piece_on(mv.from).map_or(0, piece_value);
                return 100_000 + victim * 10 - attacker / 10;
            }
            if mv.promotion.is_some() {
                return 90_000;
            }
            if self.killers[ply][0] == Some(*mv) {
                return 80_000;
            }
            if self.killers[ply][1] == Some(*mv) {
                return 79_000;
            }
            self.history[mv.from as usize][mv.to as usize]
        };
        moves.sort_by_cached_key(|mv| -score(mv));
    }

    fn negamax(&mut self, board: &Board, mut depth: i32, ply: usize, mut alpha: i32, beta: i32) -> i32 {
        self.nodes += 1;
        if self.out_of_time() {
            return 0;
        }
        let hash = board.hash();
        if ply > 0 && (board.halfmove_clock() >= 100 || self.path.contains(&hash)) {
            return 0;
        }
        let in_check = !board.checkers().is_empty();
        let mut moves = legal_moves(board);
        if moves.is_empty() {
            return if in_check { -(MATE - ply as i32) } else { 0 };
        }
        if ply >= MAX_PLY - 2 {
            return evaluate(board);
        }
        if in_check {
            depth += 1;
        }
        if depth <= 0 {
            return self.quiescence(board, ply, alpha, beta);
        }

        let slot = (hash as usize) & (TT_SIZE - 1);
        let entry = self.tt[slot];
        let tt_move = (entry.key == hash).then_some(entry.best).flatten();
        if ply > 0 && entry.key == hash && i32::from(entry.depth) >= depth {
            let score = from_tt(entry.score, ply);
            match entry.bound {
                0 => return score,
                1 if score >= beta => return score,
                2 if score <= alpha => return score,
                _ => {}
            }
        }

        self.order(board, &mut moves, tt_move, ply);
        self.path.push(hash);
        let original_alpha = alpha;
        let mut best = -INFINITY;
        let mut best_move = None;
        for (i, &mv) in moves.iter().enumerate() {
            let mut child = board.clone();
            child.play_unchecked(mv);
            let score = if i == 0 {
                -self.negamax(&child, depth - 1, ply + 1, -beta, -alpha)
            } else {
                let probe = -self.negamax(&child, depth - 1, ply + 1, -alpha - 1, -alpha);
                if probe > alpha && probe < beta {
                    -self.negamax(&child, depth - 1, ply + 1, -beta, -alpha)
                } else {
                    probe
                }
            };
            if self.stopped {
                self.path.pop();
                return 0;
            }
            if score > best {
                best = score;
                best_move = Some(mv);
            }
            if score > alpha {
                alpha = score;
            }
            if alpha >= beta {
                if !is_capture(board, mv) {
                    let killers = &mut self.killers[ply];
                    if killers[0] != Some(mv) {
                        killers[1] = killers[0];
                        killers[0] = Some(mv);
                    }
                    self.history[mv.from as usize][mv.to as usize] += depth * depth;
                }
                break;
            }
        }
        self.path.pop();
        let bound = if best <= original_alpha {
            2
        } else if best >= beta {
            1
        } else {
            0
        };
        self.tt[slot] =
            TtEntry { key: hash, best: best_move, depth: depth.min(127) as i8, score: to_tt(best, ply), bound };
        best
    }

    fn quiescence(&mut self, board: &Board, ply: usize, mut alpha: i32, beta: i32) -> i32 {
        self.nodes += 1;
        if self.out_of_time() {
            return 0;
        }
        let in_check = !board.checkers().is_empty();
        let mut moves = legal_moves(board);
        if moves.is_empty() {
            return if in_check { -(MATE - ply as i32) } else { 0 };
        }
        if ply >= MAX_PLY - 2 {
            return evaluate(board);
        }
        if !in_check {
            let stand_pat = evaluate(board);
            if stand_pat >= beta {
                return stand_pat;
            }
            alpha = alpha.max(stand_pat);
            moves.retain(|&mv| is_capture(board, mv) || mv.promotion == Some(Piece::Queen));
        }
        self.order(board, &mut moves, None, ply);
        let mut best = if in_check { -INFINITY } else { alpha };
        for mv in moves {
            let mut child = board.clone();
            child.play_unchecked(mv);
            let score = -self.quiescence(&child, ply + 1, -beta, -alpha);
            if self.stopped {
                return 0;
            }
            if score > best {
                best = score;
            }
            if score > alpha {
                alpha = score;
                if alpha >= beta {
                    break;
                }
            }
        }
        best
    }

    /// Follows best moves through the transposition table.
    fn principal_variation(&self, board: &Board) -> Vec<Move> {
        let mut line = Vec::new();
        let mut b = board.clone();
        let mut seen = Vec::new();
        while line.len() < 12 {
            let hash = b.hash();
            let entry = self.tt[(hash as usize) & (TT_SIZE - 1)];
            let Some(mv) = entry.best.filter(|_| entry.key == hash) else { break };
            if seen.contains(&hash) || !b.is_legal(mv) {
                break;
            }
            seen.push(hash);
            line.push(mv);
            b.play(mv);
        }
        line
    }
}

fn to_tt(score: i32, ply: usize) -> i32 {
    if score > MATE - MAX_PLY as i32 {
        score + ply as i32
    } else if score < -(MATE - MAX_PLY as i32) {
        score - ply as i32
    } else {
        score
    }
}

fn from_tt(score: i32, ply: usize) -> i32 {
    if score > MATE - MAX_PLY as i32 {
        score - ply as i32
    } else if score < -(MATE - MAX_PLY as i32) {
        score + ply as i32
    } else {
        score
    }
}

// Piece-square tables from White's side, rank 8 first (as usually printed).
#[rustfmt::skip]
const PAWN: [i32; 64] = [
     0,  0,  0,  0,  0,  0,  0,  0,
    50, 50, 50, 50, 50, 50, 50, 50,
    10, 10, 20, 30, 30, 20, 10, 10,
     5,  5, 10, 25, 25, 10,  5,  5,
     0,  0,  0, 20, 20,  0,  0,  0,
     5, -5,-10,  0,  0,-10, -5,  5,
     5, 10, 10,-20,-20, 10, 10,  5,
     0,  0,  0,  0,  0,  0,  0,  0,
];
#[rustfmt::skip]
const KNIGHT: [i32; 64] = [
    -50,-40,-30,-30,-30,-30,-40,-50,
    -40,-20,  0,  0,  0,  0,-20,-40,
    -30,  0, 10, 15, 15, 10,  0,-30,
    -30,  5, 15, 20, 20, 15,  5,-30,
    -30,  0, 15, 20, 20, 15,  0,-30,
    -30,  5, 10, 15, 15, 10,  5,-30,
    -40,-20,  0,  5,  5,  0,-20,-40,
    -50,-40,-30,-30,-30,-30,-40,-50,
];
#[rustfmt::skip]
const BISHOP: [i32; 64] = [
    -20,-10,-10,-10,-10,-10,-10,-20,
    -10,  0,  0,  0,  0,  0,  0,-10,
    -10,  0,  5, 10, 10,  5,  0,-10,
    -10,  5,  5, 10, 10,  5,  5,-10,
    -10,  0, 10, 10, 10, 10,  0,-10,
    -10, 10, 10, 10, 10, 10, 10,-10,
    -10,  5,  0,  0,  0,  0,  5,-10,
    -20,-10,-10,-10,-10,-10,-10,-20,
];
#[rustfmt::skip]
const ROOK: [i32; 64] = [
     0,  0,  0,  0,  0,  0,  0,  0,
     5, 10, 10, 10, 10, 10, 10,  5,
    -5,  0,  0,  0,  0,  0,  0, -5,
    -5,  0,  0,  0,  0,  0,  0, -5,
    -5,  0,  0,  0,  0,  0,  0, -5,
    -5,  0,  0,  0,  0,  0,  0, -5,
    -5,  0,  0,  0,  0,  0,  0, -5,
     0,  0,  0,  5,  5,  0,  0,  0,
];
#[rustfmt::skip]
const QUEEN: [i32; 64] = [
    -20,-10,-10, -5, -5,-10,-10,-20,
    -10,  0,  0,  0,  0,  0,  0,-10,
    -10,  0,  5,  5,  5,  5,  0,-10,
     -5,  0,  5,  5,  5,  5,  0, -5,
      0,  0,  5,  5,  5,  5,  0, -5,
    -10,  5,  5,  5,  5,  5,  0,-10,
    -10,  0,  5,  0,  0,  0,  0,-10,
    -20,-10,-10, -5, -5,-10,-10,-20,
];
#[rustfmt::skip]
const KING_MIDDLE: [i32; 64] = [
    -30,-40,-40,-50,-50,-40,-40,-30,
    -30,-40,-40,-50,-50,-40,-40,-30,
    -30,-40,-40,-50,-50,-40,-40,-30,
    -30,-40,-40,-50,-50,-40,-40,-30,
    -20,-30,-30,-40,-40,-30,-30,-20,
    -10,-20,-20,-20,-20,-20,-20,-10,
     20, 20,  0,  0,  0,  0, 20, 20,
     20, 30, 10,  0,  0, 10, 30, 20,
];
#[rustfmt::skip]
const KING_END: [i32; 64] = [
    -50,-40,-30,-20,-20,-30,-40,-50,
    -30,-20,-10,  0,  0,-10,-20,-30,
    -30,-10, 20, 30, 30, 20,-10,-30,
    -30,-10, 30, 40, 40, 30,-10,-30,
    -30,-10, 30, 40, 40, 30,-10,-30,
    -30,-10, 20, 30, 30, 20,-10,-30,
    -30,-30,  0,  0,  0,  0,-30,-30,
    -50,-30,-30,-30,-30,-30,-30,-50,
];

fn table_index(square: Square, color: Color) -> usize {
    let (file, rank) = (square as usize % 8, square as usize / 8);
    match color {
        Color::White => (7 - rank) * 8 + file,
        Color::Black => rank * 8 + file,
    }
}

/// Static evaluation from the side to move's point of view.
pub fn evaluate(board: &Board) -> i32 {
    let count = |piece: Piece| board.pieces(piece).len() as i32;
    let phase =
        (count(Piece::Knight) + count(Piece::Bishop) + 2 * count(Piece::Rook) + 4 * count(Piece::Queen)).min(24);
    let mut white = 0;
    for color in [Color::White, Color::Black] {
        let mut side = 0;
        for piece in Piece::ALL {
            let squares: BitBoard = board.colored_pieces(color, piece);
            for square in squares {
                let i = table_index(square, color);
                side += piece_value(piece)
                    + match piece {
                        Piece::Pawn => PAWN[i],
                        Piece::Knight => KNIGHT[i],
                        Piece::Bishop => BISHOP[i],
                        Piece::Rook => ROOK[i],
                        Piece::Queen => QUEEN[i],
                        Piece::King => (KING_MIDDLE[i] * phase + KING_END[i] * (24 - phase)) / 24,
                    };
            }
        }
        if board.colored_pieces(color, Piece::Bishop).len() >= 2 {
            side += 30;
        }
        white += if color == Color::White { side } else { -side };
    }
    if board.side_to_move() == Color::White { white + 10 } else { -white + 10 }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn run(fen: &str, millis: u64) -> SearchInfo {
        let board = Board::from_fen(fen, false).unwrap();
        analyse(&board, Duration::from_millis(millis), 64, &AtomicBool::new(false), &mut |_| {}).unwrap()
    }

    #[test]
    fn finds_mate_in_one_and_two() {
        let info = run("6k1/5ppp/8/8/8/8/8/1R2R1K1 w - - 0 1", 2000);
        assert_eq!(info.score, Score::Mate(1));
        assert!(info.pv_san[0] == "Re8#" || info.pv_san[0] == "Rb8#", "{info:?}");
        // Black to move mates: the score is from White's side.
        let info = run("1r2r1k1/8/8/8/8/8/5PPP/6K1 b - - 0 1", 2000);
        assert_eq!(info.score, Score::Mate(-1));
        // Mate in two with a queen sacrifice: 1. Qg8+ Rxg8 2. Nf7#.
        let info = run("4r2k/6pp/7N/3Q4/8/8/8/6K1 w - - 0 1", 3000);
        assert_eq!(info.score, Score::Mate(2), "{info:?}");
        assert_eq!(info.pv_san[0], "Qg8+");
    }

    #[test]
    fn wins_hanging_material_and_respects_stop() {
        let info = run("4k3/8/8/3q4/8/8/3R4/3K4 w - - 0 1", 1000);
        assert_eq!(info.pv_uci[0], "d2d5", "{info:?}");
        assert!(matches!(info.score, Score::Centipawns(cp) if cp > 300));

        let board = Board::default();
        let stop = AtomicBool::new(true);
        let started = Instant::now();
        let info = analyse(&board, Duration::from_secs(10), 64, &stop, &mut |_| {});
        assert!(started.elapsed() < Duration::from_secs(2));
        assert!(info.is_some(), "the first iteration always completes");
        let stalemate = Board::from_fen("7k/5Q2/6K1/8/8/8/8/8 b - - 0 1", false).unwrap();
        assert!(analyse(&stalemate, Duration::from_millis(50), 4, &AtomicBool::new(false), &mut |_| {}).is_none());
    }

    #[test]
    fn evaluation_is_symmetric() {
        let white = Board::from_fen("r1bqkbnr/pppp1ppp/2n5/4p3/4P3/5N2/PPPP1PPP/RNBQKB1R w KQkq - 2 3", false).unwrap();
        let black = Board::from_fen("rnbqkb1r/pppp1ppp/5n2/4p3/4P3/2N5/PPPP1PPP/R1BQKBNR b KQkq - 2 3", false).unwrap();
        assert_eq!(evaluate(&white), evaluate(&black));
    }
}
