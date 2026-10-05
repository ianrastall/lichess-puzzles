// Self-checks for the native core. Usage: lpd_core_tests <path to sample_puzzles.csv>
#include "../include/lpd_core.h"
#include "../src/chess.hpp"

#include <chrono>
#include <cstdio>
#include <cstring>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

using namespace lpd;

static int g_failures = 0;

#define CHECK(cond)                                                        \
    do {                                                                   \
        if (!(cond)) {                                                     \
            ++g_failures;                                                  \
            std::printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond);    \
        }                                                                  \
    } while (0)

#define CHECK_EQ(a, b)                                                                      \
    do {                                                                                    \
        auto va = (a);                                                                      \
        auto vb = (b);                                                                      \
        if (!(va == vb)) {                                                                  \
            ++g_failures;                                                                   \
            std::ostringstream os;                                                          \
            os << "FAIL " << __FILE__ << ":" << __LINE__ << ": " #a " == " #b " (" << va    \
               << " vs " << vb << ")";                                                      \
            std::puts(os.str().c_str());                                                    \
        }                                                                                   \
    } while (0)

static long long perft(const Position& pos, int depth) {
    MoveList moves;
    pos.gen_legal(moves);
    if (depth == 1) return moves.count;
    long long nodes = 0;
    for (Move m : moves) {
        Position next = pos;
        next.make(m);
        nodes += perft(next, depth - 1);
    }
    return nodes;
}

static void test_perft() {
    struct Case {
        const char* fen;
        int depth;
        long long nodes;
    };
    const Case cases[] = {
        {"rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", 4, 197281},
        {"r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1", 3, 97862},
        {"8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1", 4, 43238},
        {"r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1", 3, 9467},
        {"rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8", 3, 62379},
    };
    for (const Case& c : cases) {
        Position pos;
        CHECK(pos.set_fen(c.fen));
        CHECK_EQ(perft(pos, c.depth), c.nodes);
        CHECK_EQ(pos.fen(), std::string(c.fen));
    }
}

static void test_san() {
    Position pos;
    // Two knights can reach d2; two rooks on the same file need the rank.
    CHECK(pos.set_fen("4k3/8/8/8/8/1N3N2/8/R3K2R w KQ - 0 1"));
    Move m;
    CHECK(pos.parse_uci("b3d2", m));
    CHECK_EQ(pos.san(m), std::string("Nbd2"));
    CHECK(pos.parse_uci("e1g1", m));
    CHECK_EQ(pos.san(m), std::string("O-O"));
    CHECK(pos.parse_uci("e1c1", m));
    CHECK_EQ(pos.san(m), std::string("O-O-O"));

    CHECK(pos.set_fen("R7/7k/8/8/8/8/8/R3K3 w - - 0 1"));
    CHECK(pos.parse_uci("a1a4", m));
    CHECK_EQ(pos.san(m), std::string("R1a4"));

    CHECK(pos.set_fen("1n5k/P7/8/8/8/8/8/4K3 w - - 0 1"));
    CHECK(pos.parse_uci("a7b8q", m));
    CHECK_EQ(pos.san(m), std::string("axb8=Q+"));
    CHECK(!pos.parse_uci("a7b8", m));

    // En passant, and the FEN only keeps the square when a capture is possible.
    CHECK(pos.set_fen("4k3/8/8/8/1p6/8/P7/4K3 w - - 0 1"));
    CHECK(pos.parse_uci("a2a4", m));
    pos.make(m);
    CHECK_EQ(pos.fen(), std::string("4k3/8/8/8/Pp6/8/8/4K3 b - a3 0 1"));
    CHECK(pos.parse_uci("b4a3", m));
    CHECK_EQ(pos.san(m), std::string("bxa3"));
}

static std::vector<std::string> words(const char* text) {
    std::vector<std::string> out;
    std::istringstream in(text);
    for (std::string w; in >> w;) out.push_back(w);
    return out;
}

static int square(const std::string& s, size_t at) { return (s[at] - 'a') + (s[at + 1] - '1') * 8; }

static void test_database(const char* csv) {
    lpd_db* db = lpd_db_create();
    CHECK_EQ(lpd_db_load(db, csv), LPD_OK);
    CHECK_EQ(lpd_db_count(db), 13);
    CHECK_EQ(lpd_db_progress(db), 1000);
    CHECK(lpd_db_theme_count(db) > 10);
    CHECK_EQ(lpd_db_opening_count(db), 4);

    int row = lpd_db_find_id(db, "000Vc");
    CHECK_EQ(row, 8);
    CHECK_EQ(lpd_db_find_id(db, "zzzzz"), LPD_ERR_NOT_FOUND);
    lpd_puzzle_info info;
    CHECK_EQ(lpd_db_get(db, row, &info), LPD_OK);
    CHECK_EQ(std::string(info.themes), std::string("crushing endgame long pawnEndgame"));
    CHECK_EQ(info.rating, 1569);

    CHECK_EQ(lpd_db_filter(db, 0, 4000, -100, 0, nullptr, nullptr, nullptr), 13);
    CHECK_EQ(lpd_db_filter(db, 1500, 1600, -100, 0, nullptr, nullptr, nullptr), 4);
    CHECK_EQ(lpd_db_filter(db, 0, 4000, -100, 0, "endgame short", nullptr, nullptr), 4);
    CHECK_EQ(lpd_db_filter(db, 0, 4000, -100, 0, "mate", nullptr, nullptr), 2);
    CHECK_EQ(lpd_db_filter(db, 0, 4000, -100, 0, nullptr, "French_Defense", nullptr), 2);
    CHECK_EQ(lpd_db_filter(db, 0, 4000, 95, 1000, nullptr, nullptr, nullptr), 3);

    lpd_history* history = lpd_history_open(nullptr);
    lpd_history_entry entry{};
    std::strcpy(entry.id, "0000H");
    entry.solved = 1;
    CHECK_EQ(lpd_history_add(history, &entry), LPD_OK);
    CHECK_EQ(lpd_history_contains(history, "0000H"), 1);
    CHECK_EQ(lpd_db_filter(db, 0, 4000, -100, 0, "mate", nullptr, history), 1);
    int random_row = lpd_db_random_row(db);
    CHECK_EQ(random_row, lpd_db_find_id(db, "00sHx"));
    lpd_history_close(history);

    // Every sample puzzle must replay cleanly through the session API.
    lpd_session* session = lpd_session_create();
    for (int r = 0; r < lpd_db_count(db); ++r) {
        CHECK_EQ(lpd_db_get(db, r, &info), LPD_OK);
        CHECK_EQ(lpd_session_load(session, &info), LPD_OK);
        auto moves = words(info.moves);
        lpd_session_state st;
        lpd_session_state_get(session, &st);
        CHECK_EQ(st.view, 1);
        CHECK_EQ(st.can_move, 1);
        CHECK_EQ(st.total_plies, static_cast<int>(moves.size()));

        for (size_t i = 1; i < moves.size(); i += 2) {
            int from = square(moves[i], 0), to = square(moves[i], 2);
            int promo = moves[i].size() > 4 ? moves[i][4] : 0;
            CHECK(lpd_session_legal_targets(session, from) & (1ull << to));
            int result = lpd_session_try_move(session, from, to, promo);
            bool last = i + 1 >= moves.size();
            CHECK_EQ(result, last ? LPD_MOVE_SOLVED : LPD_MOVE_CORRECT);
            if (!last) {
                lpd_session_state_get(session, &st);
                CHECK_EQ(st.can_move, 0); // the reply is revealed by stepping forward
                lpd_session_goto(session, st.ply_count);
            }
        }
        lpd_session_state_get(session, &st);
        CHECK_EQ(st.status, LPD_STATUS_SOLVED);
        CHECK_EQ(st.ply_count, st.total_plies);
        CHECK_EQ(st.mistakes, 0);
    }

    // SAN and PGN of the first puzzle.
    lpd_db_get(db, 0, &info);
    lpd_session_load(session, &info);
    lpd_session_reveal_all(session);
    const char* expected[] = {"Bxg3", "Rxe7", "Qb1+", "Nc1", "Qxc1+", "Qxc1"};
    for (int i = 0; i < 6; ++i) {
        lpd_ply_info ply;
        CHECK_EQ(lpd_session_ply(session, i, &ply), LPD_OK);
        CHECK_EQ(std::string(ply.san), std::string(expected[i]));
    }
    char pgn[4096];
    CHECK(lpd_session_pgn(session, 0, pgn, sizeof pgn) > 0);
    CHECK(std::strstr(pgn, "[FEN \"r6k/pp2r2p/4Rp1Q/3p4/8/1N1P2R1/PqP2bPP/7K b - - 0 24\"]"));
    CHECK(std::strstr(pgn, "24... Bxg3 { Puzzle starts here: White to move. } 25. Rxe7 Qb1+ 26. Nc1 Qxc1+"));
    CHECK(lpd_session_pgn(session, LPD_PGN_PLAYER_START, pgn, sizeof pgn) > 0);
    CHECK(std::strstr(pgn, "[FEN \"r6k/pp2r2p/4Rp1Q/3p4/8/1N1P2b1/PqP3PP/7K w - - 0 25\"]"));
    CHECK(std::strstr(pgn, "{ White to move. } 25. Rxe7 Qb1+"));
    std::printf("%s\n", pgn);

    // Wrong moves are rejected and counted; any mate wins.
    lpd_db_get(db, lpd_db_find_id(db, "0000H"), &info);
    lpd_session_load(session, &info);
    CHECK_EQ(lpd_session_try_move(session, square("a7a6", 0), square("a7a6", 2), 0), LPD_MOVE_WRONG);
    CHECK_EQ(lpd_session_try_move(session, square("a7a5", 0), square("a7a4", 2), 0), LPD_MOVE_ILLEGAL);
    lpd_session_state st;
    lpd_session_state_get(session, &st);
    CHECK_EQ(st.mistakes, 1);
    CHECK_EQ(lpd_session_hint(session), square("d5g2", 0));
    CHECK_EQ(lpd_session_try_move(session, square("d5g2", 0), square("d5g2", 2), 0), LPD_MOVE_SOLVED);
    lpd_ply_info ply;
    lpd_session_ply(session, 1, &ply);
    CHECK_EQ(std::string(ply.san), std::string("Qg2#"));
    CHECK(ply.flags & LPD_PLY_MATE);

    // Exports.
    std::string out = std::string(csv) + ".test-export";
    lpd_db_filter(db, 0, 4000, -100, 0, "endgame", nullptr, nullptr);
    CHECK_EQ(lpd_db_export_pgn(db, (out + ".pgn").c_str(), nullptr, 0, 0, 0), 6);
    CHECK_EQ(lpd_db_export_csv(db, (out + ".csv").c_str(), nullptr, 0, 3), 3);
    lpd_db* copy = lpd_db_create();
    CHECK_EQ(lpd_db_load(copy, (out + ".csv").c_str()), LPD_OK);
    CHECK_EQ(lpd_db_count(copy), 3);
    lpd_db_destroy(copy);
    std::remove((out + ".pgn").c_str());
    std::remove((out + ".csv").c_str());

    lpd_session_destroy(session);
    lpd_db_destroy(db);
}

static lpd_engine_info analyse(lpd_engine* engine, const char* fen, int depth, int movetime) {
    CHECK_EQ(lpd_engine_start(engine, fen, depth, movetime), LPD_OK);
    lpd_engine_info info{};
    for (int i = 0; i < 2000; ++i) {
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
        lpd_engine_poll(engine, &info);
        if (!info.running) break;
    }
    std::printf("depth %d  cp %d  mate %d  nodes %lld  %d ms  pv %s\n", info.depth, info.score_cp, info.mate,
                static_cast<long long>(info.nodes), info.time_ms, info.pv_san);
    return info;
}

static void test_engine() {
    lpd_engine* engine = lpd_engine_create();

    // Mate in one for Black (puzzle 0000H after 15. Qxh5).
    auto info = analyse(engine, "r4rk1/pp3ppp/8/2bqp2Q/3N4/P2Bn2P/1PPN1P2/R1B2RK1 b - - 0 15", 6, 0);
    CHECK_EQ(info.mate, -1);
    CHECK_EQ(std::string(info.best_uci), std::string("d5g2"));

    // Mate in two for White (puzzle 00sHx after 17... Kd7).
    info = analyse(engine, "q5nr/1ppknQpp/3p4/1P2p3/4P3/B1PP1b2/B5PP/5K2 w - - 1 18", 8, 0);
    CHECK_EQ(info.mate, 2);
    CHECK_EQ(std::string(info.best_uci), std::string("a2e6"));

    // White wins a queen for a rook (puzzle 0000D is the reverse; here a simple hanging queen).
    info = analyse(engine, "4k3/8/8/3q4/8/8/3R4/4K3 w - - 0 1", 6, 0);
    CHECK(info.score_cp > 300);
    CHECK_EQ(std::string(info.best_uci), std::string("d2d5"));

    // A time-limited search from the start position stops on its own and stays balanced.
    info = analyse(engine, "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", 0, 300);
    CHECK(info.depth >= 4);
    CHECK(info.score_cp > -150 && info.score_cp < 150);

    info = analyse(engine, "7k/5Q2/6K1/8/8/8/8/8 b - - 0 1", 4, 0);
    CHECK_EQ(std::string(info.pv_san), std::string("stalemate"));

    lpd_engine_destroy(engine);
}

int main(int argc, char** argv) {
    if (argc < 2) {
        std::puts("usage: lpd_core_tests <sample_puzzles.csv>");
        return 2;
    }
    CHECK_EQ(lpd_abi_version(), LPD_ABI_VERSION);
    test_perft();
    test_san();
    test_database(argv[1]);
    test_engine();
    std::printf(g_failures ? "%d check(s) FAILED\n" : "all checks passed\n", g_failures);
    return g_failures ? 1 : 0;
}
