#include "history.hpp"
#include "puzzle.hpp"

#include <charconv>
#include <cstdio>

namespace {

const char kHeader[] = "PuzzleId,Timestamp,Rating,Solved,Mistakes,Hints,Seconds\n";

std::string format_entry(const lpd_history_entry& e) {
    char line[160];
    std::snprintf(line, sizeof line, "%s,%lld,%d,%d,%d,%d,%d\n", e.id, static_cast<long long>(e.timestamp_ms),
                  e.rating, e.solved, e.mistakes, e.hints, e.seconds);
    return line;
}

bool parse_entry(std::string_view line, lpd_history_entry& e) {
    while (!line.empty() && (line.back() == '\r' || line.back() == '\n')) line.remove_suffix(1);
    auto f = lpd::split(line, ',');
    if (f.size() < 7 || f[0].empty() || f[0].size() >= sizeof e.id || f[0] == "PuzzleId") return false;
    e = {};
    lpd::copy_field(e.id, f[0]);
    auto num = [](std::string_view s) {
        long long v = 0;
        std::from_chars(s.data(), s.data() + s.size(), v);
        return v;
    };
    e.timestamp_ms = num(f[1]);
    e.rating = static_cast<int32_t>(num(f[2]));
    e.solved = static_cast<int32_t>(num(f[3]));
    e.mistakes = static_cast<int32_t>(num(f[4]));
    e.hints = static_cast<int32_t>(num(f[5]));
    e.seconds = static_cast<int32_t>(num(f[6]));
    return true;
}

} // namespace

extern "C" {

LPD_API lpd_history* LPD_CALL lpd_history_open(const char* path) {
    auto* h = new lpd_history;
    h->path = lpd::widen(path);
    if (h->path.empty()) return h;

    if (FILE* f = _wfopen(h->path.c_str(), L"rb")) {
        char line[512];
        while (std::fgets(line, sizeof line, f)) {
            lpd_history_entry e;
            if (!parse_entry(line, e)) continue;
            h->entries.push_back(e);
            h->ids.insert(e.id);
        }
        std::fclose(f);
    } else if (FILE* created = _wfopen(h->path.c_str(), L"wb")) {
        std::fputs(kHeader, created);
        std::fclose(created);
    }
    return h;
}

LPD_API void LPD_CALL lpd_history_close(lpd_history* h) { delete h; }

LPD_API int LPD_CALL lpd_history_add(lpd_history* h, const lpd_history_entry* entry) {
    if (!h || !entry) return LPD_ERR_ARG;
    lpd_history_entry e = *entry;
    e.id[sizeof e.id - 1] = '\0';
    h->entries.push_back(e);
    h->ids.insert(e.id);
    if (h->path.empty()) return LPD_OK;

    FILE* f = _wfopen(h->path.c_str(), L"ab");
    if (!f) return LPD_ERR_IO;
    std::string line = format_entry(e);
    bool ok = std::fwrite(line.data(), 1, line.size(), f) == line.size();
    std::fclose(f);
    return ok ? LPD_OK : LPD_ERR_IO;
}

LPD_API int LPD_CALL lpd_history_count(const lpd_history* h) { return h ? static_cast<int>(h->entries.size()) : 0; }

LPD_API int LPD_CALL lpd_history_get(const lpd_history* h, int index, lpd_history_entry* out) {
    if (!h || !out || index < 0 || index >= static_cast<int>(h->entries.size())) return LPD_ERR_ARG;
    *out = h->entries[static_cast<size_t>(index)];
    return LPD_OK;
}

LPD_API int LPD_CALL lpd_history_contains(const lpd_history* h, const char* id) {
    return h && id && h->ids.count(id) ? 1 : 0;
}

LPD_API int LPD_CALL lpd_history_clear(lpd_history* h) {
    if (!h) return LPD_ERR_ARG;
    h->entries.clear();
    h->ids.clear();
    if (h->path.empty()) return LPD_OK;
    FILE* f = _wfopen(h->path.c_str(), L"wb");
    if (!f) return LPD_ERR_IO;
    std::fputs(kHeader, f);
    std::fclose(f);
    return LPD_OK;
}

} // extern "C"
