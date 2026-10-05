// Helpers shared by the database, session and export code.
#pragma once

#include "../include/lpd_core.h"
#include "chess.hpp"

#include <cstring>
#include <string>
#include <string_view>
#include <vector>

namespace lpd {

template <size_t N>
inline void copy_field(char (&dst)[N], std::string_view src) {
    size_t n = src.size() < N - 1 ? src.size() : N - 1;
    std::memcpy(dst, src.data(), n);
    dst[n] = '\0';
}

inline int copy_out(const std::string& s, char* buf, int cap) {
    if (buf && cap > 0) {
        size_t n = s.size() < static_cast<size_t>(cap - 1) ? s.size() : static_cast<size_t>(cap - 1);
        std::memcpy(buf, s.data(), n);
        buf[n] = '\0';
    }
    return static_cast<int>(s.size());
}

std::vector<std::string_view> split(std::string_view s, char sep);
std::wstring widen(const char* utf8);

// Parses one CSV line (without the line terminator) into a puzzle record.
bool parse_csv_row(std::string_view line, lpd_puzzle_info& out);

struct SolutionPly {
    Move move;
    std::string san;
    std::string uci;
    Position after;
    int move_number = 0;
    Color color = White;
    bool check = false;
    bool mate = false;
};

// Replays the puzzle's move list from its FEN. Fails on a bad FEN or an illegal move.
bool build_solution(const lpd_puzzle_info& puzzle, Position& start, std::vector<SolutionPly>& plies);

std::string puzzle_pgn(const lpd_puzzle_info& puzzle, int flags);

} // namespace lpd
