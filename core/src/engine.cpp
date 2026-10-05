// Built-in analysis engine: iterative-deepening alpha-beta running on a background thread.
// It is a compact searcher meant to give a quick evaluation, not a Stockfish replacement.
#include "puzzle.hpp"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdlib>
#include <memory>
#include <mutex>
#include <thread>

using namespace lpd;

namespace {

constexpr int kMaxPly = 64;
constexpr int kMate = 32000;
constexpr int kMateBound = kMate - 2 * kMaxPly;
constexpr int kInfinity = 32500;

constexpr int kValue[7] = {0, 100, 320, 330, 500, 900, 0};
constexpr int kPhase[7] = {0, 0, 1, 1, 2, 4, 0};
constexpr int kPassedBonus[8] = {0, 5, 10, 20, 40, 70, 120, 0};

// Piece-square tables from White's side, rank 8 first (the "simplified evaluation function").
constexpr int8_t kPst[7][64] = {
    {},
    {0,  0,  0,  0,   0,   0,   0,  0,  50, 50, 50,  50, 50, 50,  50, 50, 10, 10, 20, 30, 30, 20,
     10, 10, 5,  5,   10,  25,  25, 10, 5,  5,  0,   0,  0,  20,  20, 0,  0,  0,  5,  -5, -10, 0,
     0,  -10, -5, 5,  5,   10,  10, -20, -20, 10, 10, 5,  0,  0,   0,  0,  0,  0,  0,  0},
    {-50, -40, -30, -30, -30, -30, -40, -50, -40, -20, 0,   0,   0,   0,   -20, -40,
     -30, 0,   10,  15,  15,  10,  0,   -30, -30, 5,   15,  20,  20,  15,  5,   -30,
     -30, 0,   15,  20,  20,  15,  0,   -30, -30, 5,   10,  15,  15,  10,  5,   -30,
     -40, -20, 0,   5,   5,   0,   -20, -40, -50, -40, -30, -30, -30, -30, -40, -50},
    {-20, -10, -10, -10, -10, -10, -10, -20, -10, 0,   0,   0,   0,   0,   0,   -10,
     -10, 0,   5,   10,  10,  5,   0,   -10, -10, 5,   5,   10,  10,  5,   5,   -10,
     -10, 0,   10,  10,  10,  10,  0,   -10, -10, 10,  10,  10,  10,  10,  10,  -10,
     -10, 5,   0,   0,   0,   0,   5,   -10, -20, -10, -10, -10, -10, -10, -10, -20},
    {0,  0, 0, 0, 0, 0, 0, 0,  5,  10, 10, 10, 10, 10, 10, 5,  -5, 0, 0, 0, 0, 0, 0, -5, -5, 0, 0, 0, 0, 0, 0, -5,
     -5, 0, 0, 0, 0, 0, 0, -5, -5, 0,  0,  0,  0,  0,  0,  -5, -5, 0, 0, 0, 0, 0, 0, -5, 0,  0, 0, 5, 5, 0, 0, 0},
    {-20, -10, -10, -5, -5, -10, -10, -20, -10, 0,  0,   0,  0,  0,   0,   -10,
     -10, 0,   5,   5,  5,  5,   0,   -10, -5,  0,  5,   5,  5,  5,   0,   -5,
     0,   0,   5,   5,  5,  5,   0,   -5,  -10, 5,  5,   5,  5,  5,   0,   -10,
     -10, 0,   5,   0,  0,  0,   0,   -10, -20, -10, -10, -5, -5, -10, -10, -20},
    {-30, -40, -40, -50, -50, -40, -40, -30, -30, -40, -40, -50, -50, -40, -40, -30,
     -30, -40, -40, -50, -50, -40, -40, -30, -30, -40, -40, -50, -50, -40, -40, -30,
     -20, -30, -30, -40, -40, -30, -30, -20, -10, -20, -20, -20, -20, -20, -20, -10,
     20,  20,  0,   0,   0,   0,   20,  20,  20,  30,  10,  0,   0,   10,  30,  20},
};

constexpr int8_t kKingEndgame[64] = {
    -50, -40, -30, -20, -20, -30, -40, -50, -30, -20, -10, 0,   0,   -10, -20, -30,
    -30, -10, 20,  30,  30,  20,  -10, -30, -30, -10, 30,  40,  40,  30,  -10, -30,
    -30, -10, 30,  40,  40,  30,  -10, -30, -30, -10, 20,  30,  30,  20,  -10, -30,
    -30, -30, 0,   0,   0,   0,   -30, -30, -50, -30, -30, -30, -30, -30, -30, -50,
};

// Static evaluation in centipawns from the side to move's point of view.
int evaluate(const Position& pos) {
    int mg[2] = {0, 0}, eg[2] = {0, 0}, phase = 0;
    int white_min[10], black_max[10]; // pawn extremes per file, padded by one file on each side
    std::fill(std::begin(white_min), std::end(white_min), 8);
    std::fill(std::begin(black_max), std::end(black_max), -1);

    for (int sq = 0; sq < 64; ++sq) {
        uint8_t p = pos.board[sq];
        if (!p) continue;
        Color c = color_of(p);
        PieceType t = type_of(p);
        int idx = c == White ? (7 - rank_of(sq)) * 8 + file_of(sq) : sq;
        int base = kValue[t];
        mg[c] += base + kPst[t][idx];
        eg[c] += base + (t == King ? kKingEndgame[idx] : kPst[t][idx]);
        phase += kPhase[t];
        if (t == Pawn) {
            int f = file_of(sq) + 1, r = rank_of(sq);
            if (c == White) white_min[f] = std::min(white_min[f], r);
            else black_max[f] = std::max(black_max[f], r);
        }
    }

    for (int sq = 8; sq < 56; ++sq) {
        uint8_t p = pos.board[sq];
        if (type_of(p) != Pawn) continue;
        int f = file_of(sq) + 1, r = rank_of(sq);
        if (color_of(p) == White) {
            if (black_max[f - 1] <= r && black_max[f] <= r && black_max[f + 1] <= r) {
                mg[White] += kPassedBonus[r] / 2;
                eg[White] += kPassedBonus[r];
            }
        } else if (white_min[f - 1] >= r && white_min[f] >= r && white_min[f + 1] >= r) {
            mg[Black] += kPassedBonus[7 - r] / 2;
            eg[Black] += kPassedBonus[7 - r];
        }
    }

    phase = std::min(phase, 24);
    int score = ((mg[White] - mg[Black]) * phase + (eg[White] - eg[Black]) * (24 - phase)) / 24;
    return (pos.stm == White ? score : -score) + 10;
}

enum Bound : uint8_t { BoundNone, BoundExact, BoundLower, BoundUpper };

struct TTEntry {
    uint64_t key = 0;
    int16_t score = 0;
    int8_t depth = 0;
    uint8_t bound = BoundNone;
    Move move;
};

constexpr size_t kTTSize = size_t{1} << 20;

using Clock = std::chrono::steady_clock;

} // namespace

struct lpd_engine {
    std::thread thread;
    std::atomic<bool> stop{false};
    std::mutex mutex;
    lpd_engine_info info{};
    std::vector<TTEntry> tt;

    ~lpd_engine() { halt(); }

    void halt() {
        stop = true;
        if (thread.joinable()) thread.join();
    }
};

namespace {

class Searcher {
public:
    Searcher(lpd_engine& engine, int generation, int max_depth, int movetime_ms)
        : engine_(engine), generation_(generation), max_depth_(max_depth), movetime_ms_(movetime_ms) {}

    void run(const Position& root) {
        start_ = Clock::now();
        MoveList legal;
        root.gen_legal(legal);
        if (legal.count == 0) {
            bool mated = root.in_check();
            int score = mated ? (root.stm == White ? -kMate : kMate) : 0;
            publish(root, 0, score, 0, mated ? "checkmate" : "stalemate", false);
            return;
        }

        for (int depth = 1; depth <= max_depth_; ++depth) {
            Position pos = root;
            int score = search(pos, depth, -kInfinity, kInfinity, 0, false);
            if (aborted_) break; // a partial iteration has no trustworthy score

            int white_score = root.stm == White ? score : -score;
            int mate = 0;
            if (std::abs(score) >= kMateBound) {
                int moves = (kMate - std::abs(score) + 1) / 2;
                mate = white_score > 0 ? moves : -moves;
            }
            publish(root, depth, white_score, mate, {}, true);
            // A forced mate will not get shorter by much; stop once it has been confirmed a little deeper.
            if (mate != 0 && depth >= kMate - std::abs(score) + 2) break;
        }
        publish_done();
    }

private:
    bool should_stop() {
        if (aborted_) return true;
        if ((nodes_ & 1023) == 0) {
            if (engine_.stop.load(std::memory_order_relaxed)) aborted_ = true;
            else if (movetime_ms_ > 0 && elapsed_ms() >= movetime_ms_) aborted_ = true;
        }
        return aborted_;
    }

    int elapsed_ms() const {
        return static_cast<int>(std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now() - start_).count());
    }

    int move_score(const Position& pos, Move m, Move tt_move, int ply) const {
        if (m == tt_move) return 1'000'000;
        if (m.flags & MoveCapture) {
            int victim = (m.flags & MoveEnPassant) ? Pawn : type_of(pos.board[m.to]);
            return 100'000 + kValue[victim] * 16 - kValue[type_of(pos.board[m.from])] / 16 + (m.promo == Queen ? 9000 : 0);
        }
        if (m.promo == Queen) return 90'000;
        if (m == killers_[ply][0]) return 80'000;
        if (m == killers_[ply][1]) return 79'000;
        return history_[pos.stm][m.from][m.to];
    }

    // Moves the best remaining move to position `i`.
    static void pick(MoveList& list, int* scores, int i) {
        int best = i;
        for (int j = i + 1; j < list.count; ++j)
            if (scores[j] > scores[best]) best = j;
        std::swap(list.moves[i], list.moves[best]);
        std::swap(scores[i], scores[best]);
    }

    int quiesce(const Position& pos, int alpha, int beta, int ply) {
        ++nodes_;
        if (should_stop()) return 0;
        int stand = evaluate(pos);
        if (ply >= kMaxPly - 1 || stand >= beta) return stand;
        alpha = std::max(alpha, stand);

        MoveList moves;
        pos.gen_pseudo(moves, true);
        int scores[256];
        for (int i = 0; i < moves.count; ++i) scores[i] = move_score(pos, moves.moves[i], Move{}, ply);
        for (int i = 0; i < moves.count; ++i) {
            pick(moves, scores, i);
            Position next = pos;
            if (!next.make(moves.moves[i])) continue;
            int score = -quiesce(next, -beta, -alpha, ply + 1);
            if (aborted_) return 0;
            if (score >= beta) return score;
            alpha = std::max(alpha, score);
        }
        return alpha;
    }

    int search(const Position& pos, int depth, int alpha, int beta, int ply, bool allow_null) {
        pv_len_[ply] = 0;
        const uint64_t key = pos.hash();
        path_[ply] = key;
        if (ply > 0) {
            if (pos.halfmove >= 100) return 0;
            for (int i = ply - 2; i >= 0 && i >= ply - pos.halfmove; i -= 2)
                if (path_[i] == key) return 0;
        }

        const bool in_check = pos.in_check();
        if (in_check) ++depth;
        if (depth <= 0) return quiesce(pos, alpha, beta, ply);
        if (ply >= kMaxPly - 1) return evaluate(pos);

        ++nodes_;
        if (should_stop()) return 0;

        const bool pv_node = beta - alpha > 1;
        TTEntry& entry = engine_.tt[key & (kTTSize - 1)];
        Move tt_move{};
        if (entry.key == key && entry.bound != BoundNone) {
            tt_move = entry.move;
            if (!pv_node && entry.depth >= depth) {
                int score = entry.score;
                if (score >= kMateBound) score -= ply;
                else if (score <= -kMateBound) score += ply;
                if (entry.bound == BoundExact || (entry.bound == BoundLower && score >= beta) ||
                    (entry.bound == BoundUpper && score <= alpha))
                    return score;
            }
        }

        if (allow_null && !pv_node && !in_check && depth >= 3 && std::abs(beta) < kMateBound) {
            bool has_pieces = false;
            for (int sq = 0; sq < 64 && !has_pieces; ++sq) {
                uint8_t p = pos.board[sq];
                has_pieces = p && color_of(p) == pos.stm && type_of(p) != Pawn && type_of(p) != King;
            }
            if (has_pieces && evaluate(pos) >= beta) {
                Position next = pos;
                next.make_null();
                int score = -search(next, depth - 3 - depth / 6, -beta, -beta + 1, ply + 1, false);
                if (aborted_) return 0;
                if (score >= beta) return beta;
            }
        }

        MoveList moves;
        pos.gen_pseudo(moves);
        int scores[256];
        for (int i = 0; i < moves.count; ++i) scores[i] = move_score(pos, moves.moves[i], tt_move, ply);

        const int original_alpha = alpha;
        int best = -kInfinity, legal = 0;
        Move best_move{};
        for (int i = 0; i < moves.count; ++i) {
            pick(moves, scores, i);
            const Move m = moves.moves[i];
            Position next = pos;
            if (!next.make(m)) continue;
            ++legal;
            const bool quiet = !(m.flags & MoveCapture) && !m.promo;

            int score;
            if (legal == 1) {
                score = -search(next, depth - 1, -beta, -alpha, ply + 1, true);
            } else {
                int reduction = (quiet && legal > 4 && depth >= 3 && !in_check) ? 1 + (legal > 12) : 0;
                score = -search(next, depth - 1 - reduction, -alpha - 1, -alpha, ply + 1, true);
                if (score > alpha && reduction) score = -search(next, depth - 1, -alpha - 1, -alpha, ply + 1, true);
                if (score > alpha && score < beta) score = -search(next, depth - 1, -beta, -alpha, ply + 1, true);
            }
            if (aborted_) return 0;

            if (score > best) {
                best = score;
                best_move = m;
                if (score > alpha) {
                    alpha = score;
                    pv_[ply][0] = m;
                    std::copy_n(pv_[ply + 1], pv_len_[ply + 1], pv_[ply] + 1);
                    pv_len_[ply] = pv_len_[ply + 1] + 1;
                }
            }
            if (alpha >= beta) {
                if (quiet) {
                    if (!(m == killers_[ply][0])) {
                        killers_[ply][1] = killers_[ply][0];
                        killers_[ply][0] = m;
                    }
                    int& h = history_[pos.stm][m.from][m.to];
                    h = std::min(h + depth * depth, 70'000);
                }
                break;
            }
        }

        if (legal == 0) return in_check ? -kMate + ply : 0;

        int stored = best;
        if (stored >= kMateBound) stored += ply;
        else if (stored <= -kMateBound) stored -= ply;
        entry.key = key;
        entry.score = static_cast<int16_t>(stored);
        entry.depth = static_cast<int8_t>(std::min(depth, 127));
        entry.bound = best >= beta ? BoundLower : (best > original_alpha ? BoundExact : BoundUpper);
        entry.move = best_move;
        return best;
    }

    void publish(const Position& root, int depth, int white_score, int mate, const std::string& text, bool with_pv) {
        std::string pv_text = text, best;
        if (with_pv) {
            Position pos = root;
            bool first = true;
            for (int i = 0; i < pv_len_[0]; ++i) {
                Move m = pv_[0][i];
                Move checked;
                if (!pos.find_legal(m.from, m.to, m.promo, checked)) break;
                if (i == 0) best = move_uci(checked);
                std::string token;
                if (pos.stm == White) token = std::to_string(pos.fullmove) + ". ";
                else if (first) token = std::to_string(pos.fullmove) + "... ";
                token += pos.san(checked);
                if (pv_text.size() + token.size() + 2 >= sizeof engine_.info.pv_san) break;
                if (!pv_text.empty()) pv_text += ' ';
                pv_text += token;
                pos.make(checked);
                first = false;
            }
        }

        std::lock_guard lock(engine_.mutex);
        if (engine_.info.generation != generation_) return;
        lpd_engine_info& info = engine_.info;
        info.depth = depth;
        info.score_cp = white_score;
        info.mate = mate;
        info.nodes = nodes_;
        info.time_ms = elapsed_ms();
        if (!best.empty() || !with_pv) copy_field(info.best_uci, best);
        if (!pv_text.empty() || !with_pv) copy_field(info.pv_san, pv_text);
    }

    void publish_done() {
        std::lock_guard lock(engine_.mutex);
        if (engine_.info.generation != generation_) return;
        engine_.info.nodes = nodes_;
        engine_.info.time_ms = elapsed_ms();
    }

    lpd_engine& engine_;
    int generation_;
    int max_depth_;
    int movetime_ms_;
    Clock::time_point start_;
    int64_t nodes_ = 0;
    bool aborted_ = false;
    Move killers_[kMaxPly][2] = {};
    int history_[2][64][64] = {};
    uint64_t path_[kMaxPly + 1] = {};
    Move pv_[kMaxPly + 1][kMaxPly + 1] = {};
    int pv_len_[kMaxPly + 1] = {};
};

} // namespace

extern "C" {

LPD_API lpd_engine* LPD_CALL lpd_engine_create(void) { return new lpd_engine; }
LPD_API void LPD_CALL lpd_engine_destroy(lpd_engine* e) { delete e; }

LPD_API int LPD_CALL lpd_engine_start(lpd_engine* e, const char* fen, int max_depth, int movetime_ms) {
    if (!e || !fen) return LPD_ERR_ARG;
    Position root;
    if (!root.set_fen(fen)) return LPD_ERR_FORMAT;

    e->halt();
    e->stop = false;
    if (e->tt.empty()) e->tt.resize(kTTSize);

    int generation;
    {
        std::lock_guard lock(e->mutex);
        generation = e->info.generation + 1;
        e->info = {};
        e->info.generation = generation;
        e->info.running = 1;
    }
    int depth = max_depth > 0 ? std::min(max_depth, kMaxPly - 4) : kMaxPly - 4;
    e->thread = std::thread([e, root, generation, depth, movetime_ms] {
        auto searcher = std::make_unique<Searcher>(*e, generation, depth, movetime_ms);
        searcher->run(root);
        std::lock_guard lock(e->mutex);
        if (e->info.generation == generation) e->info.running = 0;
    });
    return LPD_OK;
}

LPD_API void LPD_CALL lpd_engine_stop(lpd_engine* e) {
    if (!e) return;
    e->halt();
    std::lock_guard lock(e->mutex);
    e->info.running = 0;
}

LPD_API int LPD_CALL lpd_engine_poll(lpd_engine* e, lpd_engine_info* out) {
    if (!e || !out) return LPD_ERR_ARG;
    std::lock_guard lock(e->mutex);
    *out = e->info;
    return LPD_OK;
}

} // extern "C"
