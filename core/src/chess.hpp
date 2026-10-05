// Chess rules: position, move generation, FEN, UCI and SAN.
#pragma once

#include <cstdint>
#include <string>
#include <string_view>

namespace lpd {

enum Color : uint8_t { White = 0, Black = 1 };
enum PieceType : uint8_t { NoPiece = 0, Pawn, Knight, Bishop, Rook, Queen, King };

inline constexpr uint8_t make_piece(Color c, PieceType t) { return static_cast<uint8_t>(t | (c << 3)); }
inline constexpr PieceType type_of(uint8_t p) { return static_cast<PieceType>(p & 7); }
inline constexpr Color color_of(uint8_t p) { return static_cast<Color>(p >> 3); }
inline constexpr int file_of(int sq) { return sq & 7; }
inline constexpr int rank_of(int sq) { return sq >> 3; }
inline constexpr int make_square(int file, int rank) { return rank * 8 + file; }

enum MoveFlags : uint8_t { MoveCapture = 1, MoveEnPassant = 2, MoveCastle = 4, MoveDoublePush = 8 };

struct Move {
    uint8_t from = 0;
    uint8_t to = 0;
    uint8_t promo = 0; // PieceType or 0
    uint8_t flags = 0;

    bool operator==(const Move& o) const { return from == o.from && to == o.to && promo == o.promo; }
    bool is_null() const { return from == to; }
};

struct MoveList {
    Move moves[256];
    int count = 0;

    void add(Move m) { moves[count++] = m; }
    const Move* begin() const { return moves; }
    const Move* end() const { return moves + count; }
    Move* begin() { return moves; }
    Move* end() { return moves + count; }
};

enum Castling : uint8_t { WhiteKingSide = 1, WhiteQueenSide = 2, BlackKingSide = 4, BlackQueenSide = 8 };

class Position {
public:
    uint8_t board[64] = {};
    Color stm = White;
    uint8_t castling = 0;
    int8_t ep = -1;
    uint16_t halfmove = 0;
    uint16_t fullmove = 1;
    uint8_t king_sq[2] = {4, 60};

    bool set_fen(std::string_view fen);
    std::string fen() const;

    bool attacked(int sq, Color by) const;
    bool in_check() const { return attacked(king_sq[stm], static_cast<Color>(stm ^ 1)); }

    void gen_pseudo(MoveList& out, bool captures_only = false) const;
    void gen_legal(MoveList& out) const;
    bool has_legal_move() const;
    // Makes a pseudo-legal move; returns false (position undefined) if it leaves the king in check.
    bool make(Move m);
    void make_null();

    // Resolves a UCI string ("e2e4", "e7e8q") against the legal moves.
    bool parse_uci(std::string_view uci, Move& out) const;
    // Resolves from/to/promotion against the legal moves. promo: PieceType or 0.
    bool find_legal(int from, int to, int promo, Move& out) const;
    std::string san(Move m) const;

    uint64_t hash() const;
};

std::string move_uci(Move m);
char piece_char(uint8_t piece);
std::string square_name(int sq);

} // namespace lpd
