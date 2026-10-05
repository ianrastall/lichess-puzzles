/*
 * lpd_core - native core of Lichess Puzzle Desktop.
 *
 * Plain C ABI. All strings are UTF-8. Squares are 0..63 with a1 = 0, b1 = 1, ... h8 = 63.
 * Functions that return text take (buf, cap) and return the full length required
 * (excluding the terminating NUL); output is always NUL-terminated when cap > 0.
 *
 * Threading: a handle must not be used from two threads at once, except where noted
 * (progress / cancel / engine poll are safe to call from any thread).
 */
#ifndef LPD_CORE_H
#define LPD_CORE_H

#include <stdint.h>

#if defined(_WIN32)
#  if defined(LPD_CORE_BUILD)
#    define LPD_API __declspec(dllexport)
#  elif defined(LPD_CORE_STATIC)
#    define LPD_API
#  else
#    define LPD_API __declspec(dllimport)
#  endif
#  define LPD_CALL __cdecl
#else
#  define LPD_API __attribute__((visibility("default")))
#  define LPD_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define LPD_ABI_VERSION 2

typedef struct lpd_db lpd_db;
typedef struct lpd_session lpd_session;
typedef struct lpd_engine lpd_engine;
typedef struct lpd_history lpd_history;
typedef struct lpd_players lpd_players;

enum {
    LPD_OK = 0,
    LPD_ERR_ARG = -1,
    LPD_ERR_IO = -2,
    LPD_ERR_FORMAT = -3,
    LPD_ERR_CANCELLED = -4,
    LPD_ERR_NOT_FOUND = -5,
    LPD_ERR_STATE = -6
};

/* One row of the Lichess puzzle CSV. */
typedef struct lpd_puzzle_info {
    char id[16];
    char fen[96];
    char moves[512];
    char themes[512];
    char game_url[128];
    char opening_tags[256];
    int32_t rating;
    int32_t rating_deviation;
    int32_t popularity;
    int32_t nb_plays;
    int64_t daily_date; /* unix ms, 0 when absent */
} lpd_puzzle_info;

LPD_API int LPD_CALL lpd_abi_version(void);

/* ---------------------------------------------------------------- database */

LPD_API lpd_db* LPD_CALL lpd_db_create(void);
LPD_API void LPD_CALL lpd_db_destroy(lpd_db* db);

/* Memory-maps and indexes the CSV. Blocking; poll lpd_db_progress from another thread. */
LPD_API int LPD_CALL lpd_db_load(lpd_db* db, const char* path);
/* Progress of the running load / export, 0..1000. Thread-safe. */
LPD_API int LPD_CALL lpd_db_progress(const lpd_db* db);
/* Requests cancellation of the running load / export. Thread-safe. */
LPD_API void LPD_CALL lpd_db_cancel(lpd_db* db);

LPD_API int LPD_CALL lpd_db_count(const lpd_db* db);
LPD_API int LPD_CALL lpd_db_get(const lpd_db* db, int row, lpd_puzzle_info* out);
/* Returns the row of the puzzle with this id, or LPD_ERR_NOT_FOUND. */
LPD_API int LPD_CALL lpd_db_find_id(const lpd_db* db, const char* id);

/* Themes / opening families seen in the file, each with the number of puzzles carrying it. */
LPD_API int LPD_CALL lpd_db_theme_count(const lpd_db* db);
LPD_API int LPD_CALL lpd_db_theme_name(const lpd_db* db, int index, char* buf, int cap);
LPD_API int LPD_CALL lpd_db_theme_puzzles(const lpd_db* db, int index);
LPD_API int LPD_CALL lpd_db_opening_count(const lpd_db* db);
LPD_API int LPD_CALL lpd_db_opening_name(const lpd_db* db, int index, char* buf, int cap);
LPD_API int LPD_CALL lpd_db_opening_puzzles(const lpd_db* db, int index);

/*
 * Rebuilds the filtered set and returns its size.
 *   themes         space separated theme keys, all required; NULL or "" = any
 *   opening        opening family (first OpeningTags entry); NULL or "" = any
 *   exclude_played optional history; puzzles recorded in it are left out
 */
LPD_API int LPD_CALL lpd_db_filter(lpd_db* db, int rating_min, int rating_max, int popularity_min,
                                   int plays_min, const char* themes, const char* opening,
                                   const lpd_history* exclude_played);
LPD_API int LPD_CALL lpd_db_filtered_count(const lpd_db* db);
LPD_API int LPD_CALL lpd_db_filtered_row(const lpd_db* db, int index);
/* A uniformly random row of the filtered set, or LPD_ERR_NOT_FOUND when it is empty. */
LPD_API int LPD_CALL lpd_db_random_row(lpd_db* db);
/*
 * A random row of the filtered set whose rating lies within target +/- spread. When nothing is
 * that close the window is widened until something is found.
 */
LPD_API int LPD_CALL lpd_db_random_row_near(lpd_db* db, int target_rating, int spread);

/* ------------------------------------------------------------------ export */

enum {
    /* Start the game at the position shown to the player instead of one move earlier. */
    LPD_PGN_PLAYER_START = 1
};

/* PGN of a single puzzle with its full solution. */
LPD_API int LPD_CALL lpd_puzzle_pgn(const lpd_puzzle_info* puzzle, int flags, char* buf, int cap);

/*
 * Writes puzzles to a PGN file. rows == NULL exports the filtered set (first max_count
 * entries, max_count <= 0 = all). Blocking; supports lpd_db_progress / lpd_db_cancel.
 * Returns the number of games written or a negative error.
 */
LPD_API int LPD_CALL lpd_db_export_pgn(lpd_db* db, const char* path, const int* rows, int row_count,
                                       int max_count, int flags);
/* Same selection rules, writes the original CSV rows (with header). */
LPD_API int LPD_CALL lpd_db_export_csv(lpd_db* db, const char* path, const int* rows, int row_count,
                                       int max_count);

/* ----------------------------------------------------------------- session */

enum {
    LPD_STATUS_EMPTY = 0,
    LPD_STATUS_IN_PROGRESS = 1,
    LPD_STATUS_SOLVED = 2
};

enum {
    LPD_MOVE_ILLEGAL = 0, /* not a legal move, or not the player's turn to move */
    LPD_MOVE_WRONG = 1,   /* legal but not the solution; position unchanged */
    LPD_MOVE_CORRECT = 2, /* accepted; the opponent's reply has been appended */
    LPD_MOVE_SOLVED = 3   /* accepted and the puzzle is complete */
};

enum {
    LPD_PLY_CAPTURE = 1,
    LPD_PLY_CHECK = 2,
    LPD_PLY_MATE = 4,
    LPD_PLY_PLAYER = 8 /* move made by the solving side */
};

typedef struct lpd_ply_info {
    char san[16];
    char uci[8];
    int32_t from;
    int32_t to;
    int32_t move_number;
    int32_t color; /* 0 white, 1 black */
    int32_t flags; /* LPD_PLY_* */
} lpd_ply_info;

typedef struct lpd_session_state {
    int32_t status;        /* LPD_STATUS_* */
    int32_t player_color;  /* 0 white, 1 black */
    int32_t ply_count;     /* plies revealed so far (>= 1 once loaded) */
    int32_t total_plies;   /* plies in the full solution, including the opponent's first move */
    int32_t view;          /* plies applied in the displayed position, 0..ply_count */
    int32_t side_to_move;  /* in the displayed position */
    int32_t last_from;     /* last move in the displayed position, -1 if none */
    int32_t last_to;
    int32_t check_square;  /* king square when the side to move is in check, else -1 */
    int32_t mistakes;
    int32_t hints;
    int32_t solution_shown;
    int32_t can_move;      /* 1 when the player may move in the displayed position */
} lpd_session_state;

LPD_API lpd_session* LPD_CALL lpd_session_create(void);
LPD_API void LPD_CALL lpd_session_destroy(lpd_session* s);

/* Starts a puzzle: sets up the FEN and plays the opponent's first move. */
LPD_API int LPD_CALL lpd_session_load(lpd_session* s, const lpd_puzzle_info* puzzle);
/* Restarts the current puzzle, keeping the mistake / hint counters. */
LPD_API int LPD_CALL lpd_session_retry(lpd_session* s);

LPD_API int LPD_CALL lpd_session_state_get(const lpd_session* s, lpd_session_state* out);
/* 64 chars, index = square; FEN piece letters, '.' for empty. Displayed position. */
LPD_API int LPD_CALL lpd_session_board(const lpd_session* s, char out[64]);
LPD_API int LPD_CALL lpd_session_fen(const lpd_session* s, char* buf, int cap);
LPD_API int LPD_CALL lpd_session_ply(const lpd_session* s, int index, lpd_ply_info* out);

/* Bitmask of legal destination squares for the piece on `from` (0 when the player cannot move). */
LPD_API uint64_t LPD_CALL lpd_session_legal_targets(const lpd_session* s, int from);
LPD_API int LPD_CALL lpd_session_is_promotion(const lpd_session* s, int from, int to);
/* promotion: 0 or one of 'q','r','b','n'. Returns LPD_MOVE_*. */
LPD_API int LPD_CALL lpd_session_try_move(lpd_session* s, int from, int to, int promotion);

/* Returns the origin square of the next solution move (counts as a hint), or -1. */
LPD_API int LPD_CALL lpd_session_hint(lpd_session* s);
/* Plays the next solution move for the player (and the reply). Returns LPD_MOVE_*. */
LPD_API int LPD_CALL lpd_session_reveal_next(lpd_session* s);
/* Reveals the whole solution. */
LPD_API int LPD_CALL lpd_session_reveal_all(lpd_session* s);

/* Moves the displayed position; clamps to 0..ply_count. Returns the new view. */
LPD_API int LPD_CALL lpd_session_goto(lpd_session* s, int view);

LPD_API int LPD_CALL lpd_session_pgn(const lpd_session* s, int flags, char* buf, int cap);

/* ------------------------------------------------------------------ engine */

typedef struct lpd_engine_info {
    int32_t running;
    int32_t depth;
    int32_t score_cp;  /* from White's point of view */
    int32_t mate;      /* moves to mate, >0 White mates, <0 Black mates, 0 = no mate found */
    int64_t nodes;
    int32_t time_ms;
    int32_t generation; /* increases with every lpd_engine_start */
    char best_uci[8];
    char pv_san[256];
} lpd_engine_info;

LPD_API lpd_engine* LPD_CALL lpd_engine_create(void);
LPD_API void LPD_CALL lpd_engine_destroy(lpd_engine* e);
/* Starts a background search, replacing any running one. Limits <= 0 mean "no limit". */
LPD_API int LPD_CALL lpd_engine_start(lpd_engine* e, const char* fen, int max_depth, int movetime_ms);
LPD_API void LPD_CALL lpd_engine_stop(lpd_engine* e);
/* Latest search information. Thread-safe. */
LPD_API int LPD_CALL lpd_engine_poll(lpd_engine* e, lpd_engine_info* out);

/* ----------------------------------------------------------------- history */

typedef struct lpd_history_entry {
    char id[16];
    int64_t timestamp_ms;
    int32_t rating;
    int32_t solved; /* 1 = solved without mistakes, hints or revealing the solution */
    int32_t mistakes;
    int32_t hints;
    int32_t seconds;
    int32_t player_rating; /* the player's rating after the attempt, 0 when it was not rated */
} lpd_history_entry;

/* Opens (creating if needed) the history file. NULL path = in-memory only. */
LPD_API lpd_history* LPD_CALL lpd_history_open(const char* path);
LPD_API void LPD_CALL lpd_history_close(lpd_history* h);
LPD_API int LPD_CALL lpd_history_add(lpd_history* h, const lpd_history_entry* entry);
LPD_API int LPD_CALL lpd_history_count(const lpd_history* h);
LPD_API int LPD_CALL lpd_history_get(const lpd_history* h, int index, lpd_history_entry* out);
LPD_API int LPD_CALL lpd_history_contains(const lpd_history* h, const char* id);
LPD_API int LPD_CALL lpd_history_clear(lpd_history* h);

/* ----------------------------------------------------------------- ratings */

/* A Glicko-2 rating on the familiar scale (1500 = average, deviation in rating points). */
typedef struct lpd_rating {
    double rating;
    double deviation;
    double volatility;
} lpd_rating;

/* 1500 / 350 / 0.06, the rating of someone who has not played yet. */
LPD_API void LPD_CALL lpd_glicko2_default(lpd_rating* out);
/*
 * Applies one Glicko-2 rating period of `count` games to `player`. scores[i] is 1 for a win,
 * 0 for a loss, 0.5 for a draw against an opponent rated opp_ratings[i] +/- opp_deviations[i].
 */
LPD_API int LPD_CALL lpd_glicko2_update(lpd_rating* player, const double* opp_ratings,
                                        const double* opp_deviations, const double* scores, int count);

/* ----------------------------------------------------------------- players */

typedef struct lpd_player_info {
    int32_t id;
    int32_t games; /* rated puzzles */
    int32_t wins;
    int32_t reserved;
    lpd_rating rating;
    int64_t created_ms;
    char name[64];
} lpd_player_info;

/* Opens (creating if needed) the player list. NULL path = in-memory only. */
LPD_API lpd_players* LPD_CALL lpd_players_open(const char* path);
LPD_API void LPD_CALL lpd_players_close(lpd_players* p);
LPD_API int LPD_CALL lpd_players_count(const lpd_players* p);
LPD_API int LPD_CALL lpd_players_get(const lpd_players* p, int index, lpd_player_info* out);
LPD_API int LPD_CALL lpd_players_find(const lpd_players* p, int id, lpd_player_info* out);
/* Returns the new player's id. LPD_ERR_ARG for an empty name, LPD_ERR_STATE for a duplicate. */
LPD_API int LPD_CALL lpd_players_add(lpd_players* p, const char* name, int64_t created_ms);
LPD_API int LPD_CALL lpd_players_rename(lpd_players* p, int id, const char* name);
LPD_API int LPD_CALL lpd_players_remove(lpd_players* p, int id);
/*
 * Rates one puzzle attempt as a game between the player and the puzzle, saves, and returns the
 * updated player in `out` (optional).
 */
LPD_API int LPD_CALL lpd_players_record(lpd_players* p, int id, double puzzle_rating, double puzzle_deviation,
                                        int solved, lpd_player_info* out);

#ifdef __cplusplus
}
#endif

#endif /* LPD_CORE_H */
