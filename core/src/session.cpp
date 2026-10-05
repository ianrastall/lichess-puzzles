// Puzzle session: validates the player's moves against the solution and tracks the displayed position.
#include "puzzle.hpp"

using namespace lpd;

struct lpd_session {
    lpd_puzzle_info puzzle{};
    Position start;
    std::vector<SolutionPly> solution; // every ply, starting with the opponent's first move
    std::vector<SolutionPly> played;   // plies revealed so far
    Color player = White;
    int status = LPD_STATUS_EMPTY;
    int view = 0;
    int mistakes = 0;
    int hints = 0;
    bool solution_shown = false;

    int end() const { return static_cast<int>(played.size()); }
    const Position& position() const { return view == 0 ? start : played[static_cast<size_t>(view - 1)].after; }
    bool can_move() const {
        return status == LPD_STATUS_IN_PROGRESS && view == end() && position().stm == player;
    }

    void restart() {
        played.assign(solution.begin(), solution.begin() + 1);
        view = 1;
        status = LPD_STATUS_IN_PROGRESS;
    }

    // Appends the next solution move and, if there is one, the opponent's reply.
    int advance() {
        played.push_back(solution[played.size()]);
        if (played.size() < solution.size()) {
            played.push_back(solution[played.size()]);
            if (played.size() < solution.size()) {
                view = end() - 1; // show the player's move first; the UI steps on to the reply
                return LPD_MOVE_CORRECT;
            }
        }
        view = end();
        status = LPD_STATUS_SOLVED;
        return LPD_MOVE_SOLVED;
    }
};

namespace {

int promotion_piece(int c) {
    switch (c | 0x20) {
    case 'q': return Queen;
    case 'r': return Rook;
    case 'b': return Bishop;
    case 'n': return Knight;
    default: return 0;
    }
}

bool valid_square(int sq) { return sq >= 0 && sq < 64; }

} // namespace

extern "C" {

LPD_API lpd_session* LPD_CALL lpd_session_create(void) { return new lpd_session; }
LPD_API void LPD_CALL lpd_session_destroy(lpd_session* s) { delete s; }

LPD_API int LPD_CALL lpd_session_load(lpd_session* s, const lpd_puzzle_info* puzzle) {
    if (!s || !puzzle) return LPD_ERR_ARG;
    lpd_session next;
    next.puzzle = *puzzle;
    if (!build_solution(next.puzzle, next.start, next.solution)) return LPD_ERR_FORMAT;
    next.player = next.solution[1].color;
    next.restart();
    *s = std::move(next);
    return LPD_OK;
}

LPD_API int LPD_CALL lpd_session_retry(lpd_session* s) {
    if (!s || s->status == LPD_STATUS_EMPTY) return LPD_ERR_STATE;
    s->restart();
    return LPD_OK;
}

LPD_API int LPD_CALL lpd_session_state_get(const lpd_session* s, lpd_session_state* out) {
    if (!s || !out) return LPD_ERR_ARG;
    *out = {};
    out->status = s->status;
    out->last_from = out->last_to = out->check_square = -1;
    if (s->status == LPD_STATUS_EMPTY) return LPD_OK;

    const Position& pos = s->position();
    out->player_color = s->player;
    out->ply_count = s->end();
    out->total_plies = static_cast<int>(s->solution.size());
    out->view = s->view;
    out->side_to_move = pos.stm;
    if (s->view > 0) {
        const SolutionPly& last = s->played[static_cast<size_t>(s->view - 1)];
        out->last_from = last.move.from;
        out->last_to = last.move.to;
    }
    if (pos.in_check()) out->check_square = pos.king_sq[pos.stm];
    out->mistakes = s->mistakes;
    out->hints = s->hints;
    out->solution_shown = s->solution_shown;
    out->can_move = s->can_move();
    return LPD_OK;
}

LPD_API int LPD_CALL lpd_session_board(const lpd_session* s, char out[64]) {
    if (!s || !out) return LPD_ERR_ARG;
    if (s->status == LPD_STATUS_EMPTY) {
        std::memset(out, '.', 64);
        return LPD_OK;
    }
    const Position& pos = s->position();
    for (int sq = 0; sq < 64; ++sq) out[sq] = piece_char(pos.board[sq]);
    return LPD_OK;
}

LPD_API int LPD_CALL lpd_session_fen(const lpd_session* s, char* buf, int cap) {
    if (!s || s->status == LPD_STATUS_EMPTY) return LPD_ERR_STATE;
    // Position 0 is the FEN from the file; keep it verbatim.
    return copy_out(s->view == 0 ? std::string(s->puzzle.fen) : s->position().fen(), buf, cap);
}

LPD_API int LPD_CALL lpd_session_ply(const lpd_session* s, int index, lpd_ply_info* out) {
    if (!s || !out || index < 0 || index >= s->end()) return LPD_ERR_ARG;
    const SolutionPly& ply = s->played[static_cast<size_t>(index)];
    *out = {};
    copy_field(out->san, ply.san);
    copy_field(out->uci, ply.uci);
    out->from = ply.move.from;
    out->to = ply.move.to;
    out->move_number = ply.move_number;
    out->color = ply.color;
    out->flags = ((ply.move.flags & MoveCapture) ? LPD_PLY_CAPTURE : 0) | (ply.check ? LPD_PLY_CHECK : 0) |
                 (ply.mate ? LPD_PLY_MATE : 0) | (ply.color == s->player ? LPD_PLY_PLAYER : 0);
    return LPD_OK;
}

LPD_API uint64_t LPD_CALL lpd_session_legal_targets(const lpd_session* s, int from) {
    if (!s || !valid_square(from) || !s->can_move()) return 0;
    MoveList legal;
    s->position().gen_legal(legal);
    uint64_t mask = 0;
    for (Move m : legal)
        if (m.from == from) mask |= 1ull << m.to;
    return mask;
}

LPD_API int LPD_CALL lpd_session_is_promotion(const lpd_session* s, int from, int to) {
    if (!s || !valid_square(from) || !valid_square(to) || !s->can_move()) return 0;
    Move m;
    return s->position().find_legal(from, to, Queen, m) ? 1 : 0;
}

LPD_API int LPD_CALL lpd_session_try_move(lpd_session* s, int from, int to, int promotion) {
    if (!s || !valid_square(from) || !valid_square(to) || !s->can_move()) return LPD_MOVE_ILLEGAL;
    const Position& pos = s->position();
    Move move;
    if (!pos.find_legal(from, to, promotion_piece(promotion), move)) return LPD_MOVE_ILLEGAL;

    if (move == s->solution[s->played.size()].move) return s->advance();

    // Any move that delivers checkmate wins the puzzle, even if it is not the listed one.
    Position after = pos;
    after.make(move);
    if (after.in_check() && !after.has_legal_move()) {
        SolutionPly ply;
        ply.move = move;
        ply.san = pos.san(move);
        ply.uci = move_uci(move);
        ply.move_number = pos.fullmove;
        ply.color = pos.stm;
        ply.after = after;
        ply.check = ply.mate = true;
        s->played.push_back(std::move(ply));
        s->view = s->end();
        s->status = LPD_STATUS_SOLVED;
        return LPD_MOVE_SOLVED;
    }

    ++s->mistakes;
    return LPD_MOVE_WRONG;
}

LPD_API int LPD_CALL lpd_session_hint(lpd_session* s) {
    if (!s || s->status != LPD_STATUS_IN_PROGRESS) return -1;
    s->view = s->end();
    if (!s->can_move()) return -1;
    ++s->hints;
    return s->solution[s->played.size()].move.from;
}

LPD_API int LPD_CALL lpd_session_reveal_next(lpd_session* s) {
    if (!s || s->status != LPD_STATUS_IN_PROGRESS) return LPD_MOVE_ILLEGAL;
    s->view = s->end();
    if (!s->can_move()) return LPD_MOVE_ILLEGAL;
    s->solution_shown = true;
    return s->advance();
}

LPD_API int LPD_CALL lpd_session_reveal_all(lpd_session* s) {
    if (!s || s->status != LPD_STATUS_IN_PROGRESS) return LPD_ERR_STATE;
    int first_new = s->end() + 1;
    s->solution_shown = true;
    s->played = s->solution;
    s->status = LPD_STATUS_SOLVED;
    // Leave the board on the first move of the remaining solution so it can be stepped through.
    s->view = first_new < s->end() ? first_new : s->end();
    return LPD_OK;
}

LPD_API int LPD_CALL lpd_session_goto(lpd_session* s, int view) {
    if (!s || s->status == LPD_STATUS_EMPTY) return 0;
    s->view = view < 0 ? 0 : (view > s->end() ? s->end() : view);
    return s->view;
}

LPD_API int LPD_CALL lpd_session_pgn(const lpd_session* s, int flags, char* buf, int cap) {
    if (!s || s->status == LPD_STATUS_EMPTY) return LPD_ERR_STATE;
    return copy_out(puzzle_pgn(s->puzzle, flags), buf, cap);
}

} // extern "C"
