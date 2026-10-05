// Puzzle database: a memory-mapped Lichess CSV plus a compact in-memory index used for filtering.
#include "history.hpp"
#include "puzzle.hpp"

#include <algorithm>
#include <atomic>
#include <charconv>
#include <cstdio>
#include <random>
#include <unordered_map>

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

struct lpd_db {
    HANDLE file = INVALID_HANDLE_VALUE;
    HANDLE mapping = nullptr;
    const char* data = nullptr;
    size_t size = 0;

    // Index, one entry per puzzle (structure of arrays keeps it at ~33 bytes per row).
    std::vector<uint64_t> offset;
    std::vector<uint16_t> rating;
    std::vector<int8_t> popularity;
    std::vector<uint32_t> plays;
    std::vector<uint64_t> themes_lo, themes_hi;
    std::vector<uint16_t> opening; // 0 = none, else index + 1 into opening_names

    std::vector<std::string> theme_names, opening_names;
    std::vector<int> theme_counts, opening_counts;
    bool sorted_by_id = true;

    std::vector<uint32_t> filtered;
    std::atomic<int> progress{0};
    std::atomic<bool> cancel{false};
    std::mt19937_64 rng{std::random_device{}()};

    ~lpd_db() { close(); }

    void close() {
        if (data) UnmapViewOfFile(data);
        if (mapping) CloseHandle(mapping);
        if (file != INVALID_HANDLE_VALUE) CloseHandle(file);
        data = nullptr;
        mapping = nullptr;
        file = INVALID_HANDLE_VALUE;
        size = 0;
        offset.clear();
        rating.clear();
        popularity.clear();
        plays.clear();
        themes_lo.clear();
        themes_hi.clear();
        opening.clear();
        theme_names.clear();
        opening_names.clear();
        theme_counts.clear();
        opening_counts.clear();
        filtered.clear();
        sorted_by_id = true;
    }

    int count() const { return static_cast<int>(offset.size()); }

    std::string_view line(int row) const {
        const char* p = data + offset[static_cast<size_t>(row)];
        const char* end = data + size;
        const char* nl = static_cast<const char*>(std::memchr(p, '\n', static_cast<size_t>(end - p)));
        return {p, static_cast<size_t>((nl ? nl : end) - p)};
    }

    std::string_view id(int row) const {
        std::string_view l = line(row);
        return l.substr(0, l.find(','));
    }

    int find(std::string_view puzzle_id) const {
        if (sorted_by_id) {
            int lo = 0, hi = count();
            while (lo < hi) {
                int mid = lo + (hi - lo) / 2;
                if (id(mid) < puzzle_id) lo = mid + 1;
                else hi = mid;
            }
            return lo < count() && id(lo) == puzzle_id ? lo : LPD_ERR_NOT_FOUND;
        }
        for (int row = 0; row < count(); ++row)
            if (id(row) == puzzle_id) return row;
        return LPD_ERR_NOT_FOUND;
    }

    std::vector<uint32_t> selection(const int* rows, int row_count, int max_count) const {
        std::vector<uint32_t> sel;
        if (rows) {
            for (int i = 0; i < row_count; ++i)
                if (rows[i] >= 0 && rows[i] < count()) sel.push_back(static_cast<uint32_t>(rows[i]));
        } else {
            sel = filtered;
        }
        if (max_count > 0 && sel.size() > static_cast<size_t>(max_count)) sel.resize(static_cast<size_t>(max_count));
        return sel;
    }
};

namespace {

using lpd::copy_out;

template <typename T>
T to_number(std::string_view s) {
    T v = 0;
    std::from_chars(s.data(), s.data() + s.size(), v);
    return v;
}

// Interns space separated words that live inside the mapped file.
struct Interner {
    std::unordered_map<std::string_view, int> ids;
    std::vector<std::string>& names;
    std::vector<int>& counts;
    size_t limit;

    int get(std::string_view word) {
        auto it = ids.find(word);
        if (it != ids.end()) return it->second;
        if (names.size() >= limit) return -1;
        int id = static_cast<int>(names.size());
        names.emplace_back(word);
        counts.push_back(0);
        ids.emplace(word, id);
        return id;
    }
};

int load(lpd_db& db, const char* path) {
    db.close();
    db.progress = 0;
    db.cancel = false;

    std::wstring wpath = lpd::widen(path);
    db.file = CreateFileW(wpath.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
                          FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
    if (db.file == INVALID_HANDLE_VALUE) return LPD_ERR_IO;
    LARGE_INTEGER sz;
    if (!GetFileSizeEx(db.file, &sz)) return LPD_ERR_IO;
    if (sz.QuadPart == 0) return LPD_ERR_FORMAT;
    db.mapping = CreateFileMappingW(db.file, nullptr, PAGE_READONLY, 0, 0, nullptr);
    if (!db.mapping) return LPD_ERR_IO;
    db.data = static_cast<const char*>(MapViewOfFile(db.mapping, FILE_MAP_READ, 0, 0, 0));
    if (!db.data) return LPD_ERR_IO;
    db.size = static_cast<size_t>(sz.QuadPart);

    // Lichess rows average ~200 bytes.
    size_t estimate = db.size / 150 + 16;
    db.offset.reserve(estimate);
    db.rating.reserve(estimate);
    db.popularity.reserve(estimate);
    db.plays.reserve(estimate);
    db.themes_lo.reserve(estimate);
    db.themes_hi.reserve(estimate);
    db.opening.reserve(estimate);

    Interner themes{{}, db.theme_names, db.theme_counts, 128};
    Interner openings{{}, db.opening_names, db.opening_counts, 65534};

    const char* const begin = db.data;
    const char* const end = db.data + db.size;
    const char* p = begin;
    std::string_view previous_id;

    while (p < end) {
        const char* nl = static_cast<const char*>(std::memchr(p, '\n', static_cast<size_t>(end - p)));
        const char* line_end = nl ? nl : end;
        const char* next = nl ? nl + 1 : end;
        const char* trimmed = line_end;
        if (trimmed > p && trimmed[-1] == '\r') --trimmed;

        std::string_view field[10];
        int nfields = 0;
        for (const char* start = p; nfields < 10;) {
            const char* comma = static_cast<const char*>(std::memchr(start, ',', static_cast<size_t>(trimmed - start)));
            field[nfields++] = {start, static_cast<size_t>((comma ? comma : trimmed) - start)};
            if (!comma) break;
            start = comma + 1;
        }

        if (nfields >= 8 && !field[0].empty() && field[0] != "PuzzleId" && field[0].size() < 16) {
            uint64_t lo = 0, hi = 0;
            for (std::string_view rest = field[7]; !rest.empty();) {
                size_t sp = rest.find(' ');
                std::string_view word = rest.substr(0, sp);
                rest.remove_prefix(sp == std::string_view::npos ? rest.size() : sp + 1);
                if (word.empty()) continue;
                int id = themes.get(word);
                if (id < 0) continue;
                uint64_t& bits = id < 64 ? lo : hi;
                uint64_t bit = 1ull << (id & 63);
                if (!(bits & bit)) ++db.theme_counts[static_cast<size_t>(id)];
                bits |= bit;
            }
            uint16_t opening = 0;
            if (nfields > 9 && !field[9].empty()) {
                int id = openings.get(field[9].substr(0, field[9].find(' ')));
                if (id >= 0) {
                    ++db.opening_counts[static_cast<size_t>(id)];
                    opening = static_cast<uint16_t>(id + 1);
                }
            }

            if (db.sorted_by_id && !previous_id.empty() && !(previous_id < field[0])) db.sorted_by_id = false;
            previous_id = field[0];

            db.offset.push_back(static_cast<uint64_t>(p - begin));
            db.rating.push_back(static_cast<uint16_t>(std::clamp(to_number<int>(field[3]), 0, 65535)));
            db.popularity.push_back(static_cast<int8_t>(std::clamp(to_number<int>(field[5]), -100, 100)));
            db.plays.push_back(to_number<uint32_t>(field[6]));
            db.themes_lo.push_back(lo);
            db.themes_hi.push_back(hi);
            db.opening.push_back(opening);

            if ((db.offset.size() & 0xFFF) == 0) {
                if (db.cancel) {
                    db.close();
                    return LPD_ERR_CANCELLED;
                }
                db.progress = static_cast<int>(static_cast<uint64_t>(p - begin) * 1000 / db.size);
            }
        }
        p = next;
    }

    if (db.offset.empty()) {
        db.close();
        return LPD_ERR_FORMAT;
    }
    db.filtered.resize(db.offset.size());
    for (uint32_t i = 0; i < db.filtered.size(); ++i) db.filtered[i] = i;
    db.progress = 1000;
    return LPD_OK;
}

// Shared driver for the exports: writes each selected row through `emit`.
template <typename Emit>
int export_rows(lpd_db& db, const char* path, const std::vector<uint32_t>& rows, const char* header, Emit emit) {
    db.progress = 0;
    db.cancel = false;
    FILE* f = _wfopen(lpd::widen(path).c_str(), L"wb");
    if (!f) return LPD_ERR_IO;
    std::setvbuf(f, nullptr, _IOFBF, 1 << 20);
    if (header) std::fputs(header, f);

    int written = 0;
    bool ok = true;
    std::string text;
    for (size_t i = 0; i < rows.size() && ok; ++i) {
        if ((i & 0xFF) == 0) {
            if (db.cancel) {
                std::fclose(f);
                return LPD_ERR_CANCELLED;
            }
            db.progress = static_cast<int>(i * 1000 / rows.size());
        }
        text.clear();
        if (!emit(static_cast<int>(rows[i]), text)) continue;
        ok = std::fwrite(text.data(), 1, text.size(), f) == text.size();
        ++written;
    }
    ok = std::fclose(f) == 0 && ok;
    db.progress = 1000;
    return ok ? written : LPD_ERR_IO;
}

} // namespace

extern "C" {

LPD_API int LPD_CALL lpd_abi_version(void) { return LPD_ABI_VERSION; }

LPD_API lpd_db* LPD_CALL lpd_db_create(void) { return new lpd_db; }
LPD_API void LPD_CALL lpd_db_destroy(lpd_db* db) { delete db; }

LPD_API int LPD_CALL lpd_db_load(lpd_db* db, const char* path) {
    if (!db || !path) return LPD_ERR_ARG;
    int rc = load(*db, path);
    if (rc != LPD_OK) db->close();
    return rc;
}

LPD_API int LPD_CALL lpd_db_progress(const lpd_db* db) { return db ? db->progress.load() : 0; }
LPD_API void LPD_CALL lpd_db_cancel(lpd_db* db) {
    if (db) db->cancel = true;
}

LPD_API int LPD_CALL lpd_db_count(const lpd_db* db) { return db ? db->count() : 0; }

LPD_API int LPD_CALL lpd_db_get(const lpd_db* db, int row, lpd_puzzle_info* out) {
    if (!db || !out || row < 0 || row >= db->count()) return LPD_ERR_ARG;
    return lpd::parse_csv_row(db->line(row), *out) ? LPD_OK : LPD_ERR_FORMAT;
}

LPD_API int LPD_CALL lpd_db_find_id(const lpd_db* db, const char* id) {
    if (!db || !id) return LPD_ERR_ARG;
    return db->find(id);
}

LPD_API int LPD_CALL lpd_db_theme_count(const lpd_db* db) { return db ? static_cast<int>(db->theme_names.size()) : 0; }

LPD_API int LPD_CALL lpd_db_theme_name(const lpd_db* db, int index, char* buf, int cap) {
    if (!db || index < 0 || index >= static_cast<int>(db->theme_names.size())) return LPD_ERR_ARG;
    return copy_out(db->theme_names[static_cast<size_t>(index)], buf, cap);
}

LPD_API int LPD_CALL lpd_db_theme_puzzles(const lpd_db* db, int index) {
    if (!db || index < 0 || index >= static_cast<int>(db->theme_counts.size())) return LPD_ERR_ARG;
    return db->theme_counts[static_cast<size_t>(index)];
}

LPD_API int LPD_CALL lpd_db_opening_count(const lpd_db* db) {
    return db ? static_cast<int>(db->opening_names.size()) : 0;
}

LPD_API int LPD_CALL lpd_db_opening_name(const lpd_db* db, int index, char* buf, int cap) {
    if (!db || index < 0 || index >= static_cast<int>(db->opening_names.size())) return LPD_ERR_ARG;
    return copy_out(db->opening_names[static_cast<size_t>(index)], buf, cap);
}

LPD_API int LPD_CALL lpd_db_opening_puzzles(const lpd_db* db, int index) {
    if (!db || index < 0 || index >= static_cast<int>(db->opening_counts.size())) return LPD_ERR_ARG;
    return db->opening_counts[static_cast<size_t>(index)];
}

LPD_API int LPD_CALL lpd_db_filter(lpd_db* db, int rating_min, int rating_max, int popularity_min, int plays_min,
                                   const char* themes, const char* opening, const lpd_history* exclude_played) {
    if (!db) return LPD_ERR_ARG;
    db->filtered.clear();

    uint64_t need_lo = 0, need_hi = 0;
    for (std::string_view word : lpd::split(themes ? themes : "", ' ')) {
        if (word.empty()) continue;
        auto it = std::find(db->theme_names.begin(), db->theme_names.end(), word);
        if (it == db->theme_names.end()) return 0; // unknown theme: nothing can match
        auto id = it - db->theme_names.begin();
        (id < 64 ? need_lo : need_hi) |= 1ull << (id & 63);
    }

    uint16_t need_opening = 0;
    if (opening && *opening) {
        auto it = std::find(db->opening_names.begin(), db->opening_names.end(), std::string_view(opening));
        if (it == db->opening_names.end()) return 0;
        need_opening = static_cast<uint16_t>(it - db->opening_names.begin() + 1);
    }

    std::vector<uint32_t> played;
    if (exclude_played) {
        for (const std::string& id : exclude_played->ids) {
            int row = db->find(id);
            if (row >= 0) played.push_back(static_cast<uint32_t>(row));
        }
        std::sort(played.begin(), played.end());
    }

    const size_t n = db->offset.size();
    for (size_t i = 0; i < n; ++i) {
        int r = db->rating[i];
        if (r < rating_min || r > rating_max) continue;
        if (db->popularity[i] < popularity_min) continue;
        if (plays_min > 0 && db->plays[i] < static_cast<uint32_t>(plays_min)) continue;
        if ((db->themes_lo[i] & need_lo) != need_lo || (db->themes_hi[i] & need_hi) != need_hi) continue;
        if (need_opening && db->opening[i] != need_opening) continue;
        if (!played.empty() && std::binary_search(played.begin(), played.end(), static_cast<uint32_t>(i))) continue;
        db->filtered.push_back(static_cast<uint32_t>(i));
    }
    return static_cast<int>(db->filtered.size());
}

LPD_API int LPD_CALL lpd_db_filtered_count(const lpd_db* db) { return db ? static_cast<int>(db->filtered.size()) : 0; }

LPD_API int LPD_CALL lpd_db_filtered_row(const lpd_db* db, int index) {
    if (!db || index < 0 || index >= static_cast<int>(db->filtered.size())) return LPD_ERR_ARG;
    return static_cast<int>(db->filtered[static_cast<size_t>(index)]);
}

LPD_API int LPD_CALL lpd_db_random_row(lpd_db* db) {
    if (!db) return LPD_ERR_ARG;
    if (db->filtered.empty()) return LPD_ERR_NOT_FOUND;
    std::uniform_int_distribution<size_t> pick(0, db->filtered.size() - 1);
    return static_cast<int>(db->filtered[pick(db->rng)]);
}

LPD_API int LPD_CALL lpd_puzzle_pgn(const lpd_puzzle_info* puzzle, int flags, char* buf, int cap) {
    if (!puzzle) return LPD_ERR_ARG;
    std::string pgn = lpd::puzzle_pgn(*puzzle, flags);
    if (pgn.empty()) return LPD_ERR_FORMAT;
    return copy_out(pgn, buf, cap);
}

LPD_API int LPD_CALL lpd_db_export_pgn(lpd_db* db, const char* path, const int* rows, int row_count, int max_count,
                                       int flags) {
    if (!db || !path) return LPD_ERR_ARG;
    return export_rows(*db, path, db->selection(rows, row_count, max_count), nullptr, [&](int row, std::string& out) {
        lpd_puzzle_info info;
        if (!lpd::parse_csv_row(db->line(row), info)) return false;
        out = lpd::puzzle_pgn(info, flags);
        if (out.empty()) return false;
        out += '\n';
        return true;
    });
}

LPD_API int LPD_CALL lpd_db_export_csv(lpd_db* db, const char* path, const int* rows, int row_count, int max_count) {
    if (!db || !path) return LPD_ERR_ARG;
    static const char kHeader[] =
        "PuzzleId,FEN,Moves,Rating,RatingDeviation,Popularity,NbPlays,Themes,GameUrl,OpeningTags,DailyDate\n";
    return export_rows(*db, path, db->selection(rows, row_count, max_count), kHeader, [&](int row, std::string& out) {
        std::string_view line = db->line(row);
        while (!line.empty() && line.back() == '\r') line.remove_suffix(1);
        out.assign(line);
        out += '\n';
        return true;
    });
}

} // extern "C"
