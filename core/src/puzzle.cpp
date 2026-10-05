#include "puzzle.hpp"

#include <charconv>
#include <cstdio>

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

namespace lpd {

std::vector<std::string_view> split(std::string_view s, char sep) {
    std::vector<std::string_view> parts;
    size_t start = 0;
    while (true) {
        size_t end = s.find(sep, start);
        if (end == std::string_view::npos) {
            parts.push_back(s.substr(start));
            break;
        }
        parts.push_back(s.substr(start, end - start));
        start = end + 1;
    }
    return parts;
}

std::wstring widen(const char* utf8) {
    if (!utf8 || !*utf8) return {};
    int n = MultiByteToWideChar(CP_UTF8, 0, utf8, -1, nullptr, 0);
    if (n <= 0) return {};
    std::wstring w(static_cast<size_t>(n), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, utf8, -1, w.data(), n);
    w.resize(static_cast<size_t>(n - 1));
    return w;
}

namespace {

template <typename T>
T to_number(std::string_view s, T fallback = 0) {
    T v = fallback;
    std::from_chars(s.data(), s.data() + s.size(), v);
    return v;
}

std::vector<std::string_view> split_words(std::string_view s) {
    std::vector<std::string_view> words;
    for (std::string_view w : split(s, ' '))
        if (!w.empty()) words.push_back(w);
    return words;
}

std::string tag(std::string_view name, std::string_view value) {
    std::string s = "[";
    s += name;
    s += " \"";
    for (char c : value) {
        if (c == '"' || c == '\\') s += '\\';
        s += c;
    }
    s += "\"]\n";
    return s;
}

} // namespace

bool parse_csv_row(std::string_view line, lpd_puzzle_info& out) {
    while (!line.empty() && (line.back() == '\r' || line.back() == '\n')) line.remove_suffix(1);
    auto f = split(line, ',');
    if (f.size() < 8 || f[0].empty() || f[0].size() >= sizeof out.id) return false;
    out = {};
    copy_field(out.id, f[0]);
    copy_field(out.fen, f[1]);
    copy_field(out.moves, f[2]);
    out.rating = to_number<int32_t>(f[3]);
    out.rating_deviation = to_number<int32_t>(f[4]);
    out.popularity = to_number<int32_t>(f[5]);
    out.nb_plays = to_number<int32_t>(f[6]);
    copy_field(out.themes, f[7]);
    if (f.size() > 8) copy_field(out.game_url, f[8]);
    if (f.size() > 9) copy_field(out.opening_tags, f[9]);
    if (f.size() > 10) out.daily_date = to_number<int64_t>(f[10]);
    return true;
}

bool build_solution(const lpd_puzzle_info& puzzle, Position& start, std::vector<SolutionPly>& plies) {
    plies.clear();
    if (!start.set_fen(puzzle.fen)) return false;
    Position pos = start;
    for (std::string_view uci : split_words(puzzle.moves)) {
        SolutionPly ply;
        if (!pos.parse_uci(uci, ply.move)) return false;
        ply.san = pos.san(ply.move);
        ply.uci = std::string(uci);
        ply.move_number = pos.fullmove;
        ply.color = pos.stm;
        pos.make(ply.move);
        ply.after = pos;
        ply.check = pos.in_check();
        ply.mate = ply.check && !pos.has_legal_move();
        plies.push_back(std::move(ply));
    }
    // A puzzle needs the opponent's move and at least one move to find.
    return plies.size() >= 2;
}

std::string puzzle_pgn(const lpd_puzzle_info& puzzle, int flags) {
    Position start;
    std::vector<SolutionPly> plies;
    if (!build_solution(puzzle, start, plies)) return {};

    const bool player_start = (flags & LPD_PGN_PLAYER_START) != 0;
    const Color player = plies[1].color;
    const char* player_name = player == White ? "White" : "Black";
    const std::string id = puzzle.id;

    std::string result = "*";
    if (plies.back().mate) result = plies.back().color == White ? "1-0" : "0-1";

    std::string date = "????.??.??";
    if (puzzle.daily_date > 0) {
        // Daily puzzles carry the date they were featured on.
        FILETIME ft;
        ULARGE_INTEGER t;
        t.QuadPart = static_cast<ULONGLONG>(puzzle.daily_date) * 10000ull + 116444736000000000ull;
        ft.dwLowDateTime = t.LowPart;
        ft.dwHighDateTime = t.HighPart;
        SYSTEMTIME st;
        if (FileTimeToSystemTime(&ft, &st)) {
            char buf[16];
            std::snprintf(buf, sizeof buf, "%04d.%02d.%02d", st.wYear, st.wMonth, st.wDay);
            date = buf;
        }
    }

    std::string pgn;
    pgn += tag("Event", "Lichess Puzzle " + id);
    pgn += tag("Site", "https://lichess.org/training/" + id);
    pgn += tag("Date", date);
    pgn += tag("Round", "-");
    pgn += tag("White", player == White ? "Player" : "Opponent");
    pgn += tag("Black", player == Black ? "Player" : "Opponent");
    pgn += tag("Result", result);
    pgn += tag("SetUp", "1");
    pgn += tag("FEN", player_start ? plies[0].after.fen() : std::string(puzzle.fen));
    pgn += tag("PuzzleId", id);
    pgn += tag("PuzzleRating", std::to_string(puzzle.rating));
    pgn += tag("PuzzleRatingDeviation", std::to_string(puzzle.rating_deviation));
    pgn += tag("PuzzlePopularity", std::to_string(puzzle.popularity));
    pgn += tag("PuzzlePlays", std::to_string(puzzle.nb_plays));
    pgn += tag("PuzzleThemes", puzzle.themes);
    if (puzzle.opening_tags[0]) {
        std::string opening = std::string(split_words(puzzle.opening_tags).back());
        for (char& c : opening)
            if (c == '_') c = ' ';
        pgn += tag("Opening", opening);
    }
    if (puzzle.game_url[0]) pgn += tag("Source", puzzle.game_url);
    pgn += tag("Annotator", "Lichess Puzzle Desktop");
    pgn += '\n';

    std::vector<std::string> tokens;
    bool need_number = true;
    auto comment = [&](const std::string& text) {
        tokens.push_back("{");
        for (std::string_view w : split_words(text)) tokens.emplace_back(w);
        tokens.push_back("}");
        need_number = true;
    };

    if (player_start) comment(std::string(player_name) + " to move.");
    for (size_t i = player_start ? 1 : 0; i < plies.size(); ++i) {
        const SolutionPly& ply = plies[i];
        if (ply.color == White) tokens.push_back(std::to_string(ply.move_number) + ".");
        else if (need_number) tokens.push_back(std::to_string(ply.move_number) + "...");
        need_number = false;
        tokens.push_back(ply.san);
        if (i == 0) comment(std::string("Puzzle starts here: ") + player_name + " to move.");
    }
    tokens.push_back(result);

    size_t column = 0;
    for (const std::string& tok : tokens) {
        if (column && column + 1 + tok.size() > 80) {
            pgn += '\n';
            column = 0;
        } else if (column) {
            pgn += ' ';
            ++column;
        }
        pgn += tok;
        column += tok.size();
    }
    pgn += "\n";
    return pgn;
}

} // namespace lpd
