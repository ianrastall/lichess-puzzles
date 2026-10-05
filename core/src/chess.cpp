#include "chess.hpp"

#include <charconv>
#include <cstring>

namespace lpd {

namespace {

const int kKnightSteps[8][2] = {{1, 2}, {2, 1}, {2, -1}, {1, -2}, {-1, -2}, {-2, -1}, {-2, 1}, {-1, 2}};
const int kKingSteps[8][2] = {{1, 0}, {1, 1}, {0, 1}, {-1, 1}, {-1, 0}, {-1, -1}, {0, -1}, {1, -1}};
const int kBishopDirs[4][2] = {{1, 1}, {-1, 1}, {-1, -1}, {1, -1}};
const int kRookDirs[4][2] = {{1, 0}, {0, 1}, {-1, 0}, {0, -1}};

inline bool on_board(int f, int r) { return static_cast<unsigned>(f) < 8 && static_cast<unsigned>(r) < 8; }

struct Tables {
    uint8_t castle_mask[64];
    uint64_t piece[16][64];
    uint64_t castling[16];
    uint64_t ep_file[8];
    uint64_t black_to_move;

    Tables() {
        std::memset(castle_mask, 0xFF, sizeof castle_mask);
        castle_mask[0] = static_cast<uint8_t>(~WhiteQueenSide);
        castle_mask[4] = static_cast<uint8_t>(~(WhiteKingSide | WhiteQueenSide));
        castle_mask[7] = static_cast<uint8_t>(~WhiteKingSide);
        castle_mask[56] = static_cast<uint8_t>(~BlackQueenSide);
        castle_mask[60] = static_cast<uint8_t>(~(BlackKingSide | BlackQueenSide));
        castle_mask[63] = static_cast<uint8_t>(~BlackKingSide);

        uint64_t seed = 0x9E3779B97F4A7C15ull;
        auto next = [&seed] {
            uint64_t z = (seed += 0x9E3779B97F4A7C15ull);
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ull;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBull;
            return z ^ (z >> 31);
        };
        for (auto& p : piece)
            for (auto& s : p) s = next();
        for (auto& c : castling) c = next();
        for (auto& e : ep_file) e = next();
        black_to_move = next();
    }
};

const Tables kTables;

PieceType piece_from_char(char c) {
    switch (c | 0x20) {
    case 'p': return Pawn;
    case 'n': return Knight;
    case 'b': return Bishop;
    case 'r': return Rook;
    case 'q': return Queen;
    case 'k': return King;
    default: return NoPiece;
    }
}

std::string_view next_token(std::string_view& s) {
    while (!s.empty() && s.front() == ' ') s.remove_prefix(1);
    size_t n = s.find(' ');
    std::string_view tok = s.substr(0, n);
    s.remove_prefix(n == std::string_view::npos ? s.size() : n);
    return tok;
}

} // namespace

char piece_char(uint8_t piece) {
    static const char kChars[] = ".pnbrqk";
    PieceType t = type_of(piece);
    if (t == NoPiece) return '.';
    char c = kChars[t];
    return color_of(piece) == White ? static_cast<char>(c - 32) : c;
}

std::string square_name(int sq) {
    return {static_cast<char>('a' + file_of(sq)), static_cast<char>('1' + rank_of(sq))};
}

std::string move_uci(Move m) {
    std::string s = square_name(m.from) + square_name(m.to);
    if (m.promo) s += ".pnbrqk"[m.promo];
    return s;
}

bool Position::set_fen(std::string_view fen) {
    *this = Position{};
    std::string_view placement = next_token(fen);
    int file = 0, rank = 7, kings[2] = {0, 0};
    for (char c : placement) {
        if (c == '/') {
            if (file != 8 || rank == 0) return false;
            file = 0;
            --rank;
        } else if (c >= '1' && c <= '8') {
            file += c - '0';
            if (file > 8) return false;
        } else {
            PieceType t = piece_from_char(c);
            if (t == NoPiece || file > 7) return false;
            Color col = (c >= 'a') ? Black : White;
            int sq = make_square(file, rank);
            board[sq] = make_piece(col, t);
            if (t == King) {
                king_sq[col] = static_cast<uint8_t>(sq);
                ++kings[col];
            }
            ++file;
        }
    }
    if (rank != 0 || file != 8 || kings[0] != 1 || kings[1] != 1) return false;

    std::string_view side = next_token(fen);
    if (side == "w") stm = White;
    else if (side == "b") stm = Black;
    else return false;

    for (char c : next_token(fen)) {
        switch (c) {
        case 'K': castling |= WhiteKingSide; break;
        case 'Q': castling |= WhiteQueenSide; break;
        case 'k': castling |= BlackKingSide; break;
        case 'q': castling |= BlackQueenSide; break;
        case '-': break;
        default: return false;
        }
    }
    // Drop rights that the piece placement contradicts.
    if (board[4] != make_piece(White, King)) castling &= ~(WhiteKingSide | WhiteQueenSide);
    if (board[7] != make_piece(White, Rook)) castling &= ~WhiteKingSide;
    if (board[0] != make_piece(White, Rook)) castling &= ~WhiteQueenSide;
    if (board[60] != make_piece(Black, King)) castling &= ~(BlackKingSide | BlackQueenSide);
    if (board[63] != make_piece(Black, Rook)) castling &= ~BlackKingSide;
    if (board[56] != make_piece(Black, Rook)) castling &= ~BlackQueenSide;

    std::string_view eps = next_token(fen);
    if (eps.size() == 2 && eps[0] >= 'a' && eps[0] <= 'h' && (eps[1] == '3' || eps[1] == '6'))
        ep = static_cast<int8_t>(make_square(eps[0] - 'a', eps[1] - '1'));
    else if (!eps.empty() && eps != "-")
        return false;

    std::string_view hm = next_token(fen), fm = next_token(fen);
    unsigned v = 0;
    if (!hm.empty() && std::from_chars(hm.data(), hm.data() + hm.size(), v).ec == std::errc{})
        halfmove = static_cast<uint16_t>(v);
    v = 1;
    if (!fm.empty() && std::from_chars(fm.data(), fm.data() + fm.size(), v).ec == std::errc{})
        fullmove = static_cast<uint16_t>(v ? v : 1);

    // The side that just moved must not be in check.
    return !attacked(king_sq[stm ^ 1], stm);
}

std::string Position::fen() const {
    std::string s;
    for (int rank = 7; rank >= 0; --rank) {
        int empty = 0;
        for (int file = 0; file < 8; ++file) {
            uint8_t p = board[make_square(file, rank)];
            if (!p) {
                ++empty;
                continue;
            }
            if (empty) s += static_cast<char>('0' + empty);
            empty = 0;
            s += piece_char(p);
        }
        if (empty) s += static_cast<char>('0' + empty);
        if (rank) s += '/';
    }
    s += stm == White ? " w " : " b ";
    if (!castling) s += '-';
    if (castling & WhiteKingSide) s += 'K';
    if (castling & WhiteQueenSide) s += 'Q';
    if (castling & BlackKingSide) s += 'k';
    if (castling & BlackQueenSide) s += 'q';
    s += ' ';

    // Like Lichess, only report the en passant square when a pawn can actually take.
    bool ep_usable = false;
    if (ep >= 0) {
        int from_rank = rank_of(ep) + (stm == White ? -1 : 1);
        for (int df : {-1, 1}) {
            int f = file_of(ep) + df;
            if (on_board(f, from_rank) && board[make_square(f, from_rank)] == make_piece(stm, Pawn)) ep_usable = true;
        }
    }
    s += ep_usable ? square_name(ep) : "-";
    s += ' ' + std::to_string(halfmove) + ' ' + std::to_string(fullmove);
    return s;
}

bool Position::attacked(int sq, Color by) const {
    int f = file_of(sq), r = rank_of(sq);

    int pawn_rank = r + (by == White ? -1 : 1);
    uint8_t pawn = make_piece(by, Pawn);
    if (on_board(f - 1, pawn_rank) && board[make_square(f - 1, pawn_rank)] == pawn) return true;
    if (on_board(f + 1, pawn_rank) && board[make_square(f + 1, pawn_rank)] == pawn) return true;

    uint8_t knight = make_piece(by, Knight), king = make_piece(by, King);
    for (int i = 0; i < 8; ++i) {
        int nf = f + kKnightSteps[i][0], nr = r + kKnightSteps[i][1];
        if (on_board(nf, nr) && board[make_square(nf, nr)] == knight) return true;
        int kf = f + kKingSteps[i][0], kr = r + kKingSteps[i][1];
        if (on_board(kf, kr) && board[make_square(kf, kr)] == king) return true;
    }

    uint8_t bishop = make_piece(by, Bishop), rook = make_piece(by, Rook), queen = make_piece(by, Queen);
    for (int i = 0; i < 4; ++i) {
        for (int nf = f + kBishopDirs[i][0], nr = r + kBishopDirs[i][1]; on_board(nf, nr);
             nf += kBishopDirs[i][0], nr += kBishopDirs[i][1]) {
            uint8_t p = board[make_square(nf, nr)];
            if (!p) continue;
            if (p == bishop || p == queen) return true;
            break;
        }
        for (int nf = f + kRookDirs[i][0], nr = r + kRookDirs[i][1]; on_board(nf, nr);
             nf += kRookDirs[i][0], nr += kRookDirs[i][1]) {
            uint8_t p = board[make_square(nf, nr)];
            if (!p) continue;
            if (p == rook || p == queen) return true;
            break;
        }
    }
    return false;
}

void Position::gen_pseudo(MoveList& out, bool captures_only) const {
    const Color us = stm, them = static_cast<Color>(stm ^ 1);

    auto add_step = [&](int from, int nf, int nr) {
        if (!on_board(nf, nr)) return;
        int to = make_square(nf, nr);
        uint8_t target = board[to];
        if (!target) {
            if (!captures_only) out.add({static_cast<uint8_t>(from), static_cast<uint8_t>(to), 0, 0});
        } else if (color_of(target) == them) {
            out.add({static_cast<uint8_t>(from), static_cast<uint8_t>(to), 0, MoveCapture});
        }
    };
    auto add_slides = [&](int from, const int dirs[4][2]) {
        int f = file_of(from), r = rank_of(from);
        for (int i = 0; i < 4; ++i) {
            for (int nf = f + dirs[i][0], nr = r + dirs[i][1]; on_board(nf, nr); nf += dirs[i][0], nr += dirs[i][1]) {
                int to = make_square(nf, nr);
                uint8_t target = board[to];
                if (!target) {
                    if (!captures_only) out.add({static_cast<uint8_t>(from), static_cast<uint8_t>(to), 0, 0});
                    continue;
                }
                if (color_of(target) == them)
                    out.add({static_cast<uint8_t>(from), static_cast<uint8_t>(to), 0, MoveCapture});
                break;
            }
        }
    };
    auto add_pawn = [&](int from, int to, uint8_t flags) {
        int r = rank_of(to);
        if (r == 0 || r == 7) {
            out.add({static_cast<uint8_t>(from), static_cast<uint8_t>(to), Queen, flags});
            if (captures_only) return;
            for (uint8_t promo : {Knight, Rook, Bishop})
                out.add({static_cast<uint8_t>(from), static_cast<uint8_t>(to), promo, flags});
        } else {
            out.add({static_cast<uint8_t>(from), static_cast<uint8_t>(to), 0, flags});
        }
    };

    for (int from = 0; from < 64; ++from) {
        uint8_t p = board[from];
        if (!p || color_of(p) != us) continue;
        int f = file_of(from), r = rank_of(from);
        switch (type_of(p)) {
        case Pawn: {
            int dir = us == White ? 1 : -1;
            int nr = r + dir;
            if (!on_board(f, nr)) break;
            int fwd = make_square(f, nr);
            bool promotes = nr == 0 || nr == 7;
            if (!board[fwd] && (!captures_only || promotes)) {
                add_pawn(from, fwd, 0);
                int start = us == White ? 1 : 6;
                if (r == start && !board[fwd + dir * 8])
                    out.add({static_cast<uint8_t>(from), static_cast<uint8_t>(fwd + dir * 8), 0, MoveDoublePush});
            }
            for (int df : {-1, 1}) {
                if (!on_board(f + df, nr)) continue;
                int to = make_square(f + df, nr);
                uint8_t target = board[to];
                if (target && color_of(target) == them) add_pawn(from, to, MoveCapture);
                else if (to == ep && !target) out.add({static_cast<uint8_t>(from), static_cast<uint8_t>(to), 0, MoveCapture | MoveEnPassant});
            }
            break;
        }
        case Knight:
            for (auto& s : kKnightSteps) add_step(from, f + s[0], r + s[1]);
            break;
        case Bishop:
            add_slides(from, kBishopDirs);
            break;
        case Rook:
            add_slides(from, kRookDirs);
            break;
        case Queen:
            add_slides(from, kBishopDirs);
            add_slides(from, kRookDirs);
            break;
        case King: {
            for (auto& s : kKingSteps) add_step(from, f + s[0], r + s[1]);
            if (captures_only) break;
            int home = us == White ? 4 : 60;
            if (from != home) break;
            uint8_t ks = us == White ? WhiteKingSide : BlackKingSide;
            uint8_t qs = us == White ? WhiteQueenSide : BlackQueenSide;
            if (!(castling & (ks | qs)) || attacked(home, them)) break;
            if ((castling & ks) && !board[home + 1] && !board[home + 2] && !attacked(home + 1, them) &&
                !attacked(home + 2, them))
                out.add({static_cast<uint8_t>(home), static_cast<uint8_t>(home + 2), 0, MoveCastle});
            if ((castling & qs) && !board[home - 1] && !board[home - 2] && !board[home - 3] &&
                !attacked(home - 1, them) && !attacked(home - 2, them))
                out.add({static_cast<uint8_t>(home), static_cast<uint8_t>(home - 2), 0, MoveCastle});
            break;
        }
        default:
            break;
        }
    }
}

bool Position::make(Move m) {
    const Color us = stm;
    uint8_t piece = board[m.from];
    bool resets = type_of(piece) == Pawn || (m.flags & MoveCapture);

    board[m.from] = 0;
    if (m.flags & MoveEnPassant) board[m.to + (us == White ? -8 : 8)] = 0;
    if (m.flags & MoveCastle) {
        bool king_side = m.to > m.from;
        int rook_from = king_side ? m.from + 3 : m.from - 4;
        int rook_to = king_side ? m.from + 1 : m.from - 1;
        board[rook_to] = board[rook_from];
        board[rook_from] = 0;
    }
    board[m.to] = m.promo ? make_piece(us, static_cast<PieceType>(m.promo)) : piece;
    if (type_of(piece) == King) king_sq[us] = m.to;

    castling &= kTables.castle_mask[m.from] & kTables.castle_mask[m.to];
    ep = (m.flags & MoveDoublePush) ? static_cast<int8_t>((m.from + m.to) / 2) : static_cast<int8_t>(-1);
    halfmove = resets ? 0 : static_cast<uint16_t>(halfmove + 1);
    if (us == Black) ++fullmove;
    stm = static_cast<Color>(us ^ 1);

    return !attacked(king_sq[us], stm);
}

void Position::make_null() {
    ep = -1;
    ++halfmove;
    if (stm == Black) ++fullmove;
    stm = static_cast<Color>(stm ^ 1);
}

void Position::gen_legal(MoveList& out) const {
    MoveList pseudo;
    gen_pseudo(pseudo);
    for (Move m : pseudo) {
        Position next = *this;
        if (next.make(m)) out.add(m);
    }
}

bool Position::has_legal_move() const {
    MoveList pseudo;
    gen_pseudo(pseudo);
    for (Move m : pseudo) {
        Position next = *this;
        if (next.make(m)) return true;
    }
    return false;
}

bool Position::find_legal(int from, int to, int promo, Move& out) const {
    MoveList legal;
    gen_legal(legal);
    for (Move m : legal) {
        if (m.from == from && m.to == to && m.promo == promo) {
            out = m;
            return true;
        }
    }
    return false;
}

bool Position::parse_uci(std::string_view uci, Move& out) const {
    if (uci.size() < 4 || uci.size() > 5) return false;
    int ff = uci[0] - 'a', fr = uci[1] - '1', tf = uci[2] - 'a', tr = uci[3] - '1';
    if (!on_board(ff, fr) || !on_board(tf, tr)) return false;
    int promo = 0;
    if (uci.size() == 5) {
        promo = piece_from_char(uci[4]);
        if (promo == NoPiece || promo == Pawn || promo == King) return false;
    }
    return find_legal(make_square(ff, fr), make_square(tf, tr), promo, out);
}

std::string Position::san(Move m) const {
    std::string s;
    PieceType t = type_of(board[m.from]);

    if (m.flags & MoveCastle) {
        s = m.to > m.from ? "O-O" : "O-O-O";
    } else if (t == Pawn) {
        if (m.flags & MoveCapture) {
            s += static_cast<char>('a' + file_of(m.from));
            s += 'x';
        }
        s += square_name(m.to);
        if (m.promo) {
            s += '=';
            s += ".PNBRQK"[m.promo];
        }
    } else {
        s += ".PNBRQK"[t];
        MoveList legal;
        gen_legal(legal);
        bool ambiguous = false, same_file = false, same_rank = false;
        for (Move o : legal) {
            if (o.to != m.to || o.from == m.from || board[o.from] != board[m.from]) continue;
            ambiguous = true;
            if (file_of(o.from) == file_of(m.from)) same_file = true;
            if (rank_of(o.from) == rank_of(m.from)) same_rank = true;
        }
        if (ambiguous) {
            if (!same_file) s += static_cast<char>('a' + file_of(m.from));
            else if (!same_rank) s += static_cast<char>('1' + rank_of(m.from));
            else s += square_name(m.from);
        }
        if (m.flags & MoveCapture) s += 'x';
        s += square_name(m.to);
    }

    Position next = *this;
    if (next.make(m) && next.in_check()) s += next.has_legal_move() ? '+' : '#';
    return s;
}

uint64_t Position::hash() const {
    uint64_t h = stm == Black ? kTables.black_to_move : 0;
    for (int sq = 0; sq < 64; ++sq)
        if (board[sq]) h ^= kTables.piece[board[sq]][sq];
    h ^= kTables.castling[castling];
    if (ep >= 0) h ^= kTables.ep_file[file_of(ep)];
    return h;
}

} // namespace lpd
