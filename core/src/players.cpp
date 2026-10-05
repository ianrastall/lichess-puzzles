// Glicko-2 ratings and the list of players, persisted as a small CSV file.
#include "puzzle.hpp"

#include <algorithm>
#include <charconv>
#include <cmath>
#include <cstdio>
#include <cstdlib>

namespace {

constexpr double kScale = 173.7178;  // Glicko-2 internal scale
constexpr double kTau = 0.5;         // constrains how fast volatility changes
constexpr double kPi = 3.14159265358979323846;
constexpr double kDefaultRating = 1500, kDefaultDeviation = 350, kDefaultVolatility = 0.06;
// Keep ratings responsive after many games and bounded for brand-new players.
constexpr double kMinDeviation = 45, kMaxDeviation = 350;

const char kHeader[] = "Id,Name,Rating,Deviation,Volatility,Games,Wins,Created\n";

double g(double phi) { return 1.0 / std::sqrt(1.0 + 3.0 * phi * phi / (kPi * kPi)); }

// Mark Glickman, "Example of the Glicko-2 system" (2012), steps 2-8.
bool glicko2(lpd_rating& player, const double* opp_ratings, const double* opp_deviations, const double* scores,
             int count) {
    if (!(player.deviation > 0) || !(player.volatility > 0)) return false;
    const double mu = (player.rating - kDefaultRating) / kScale;
    const double phi = player.deviation / kScale;
    const double sigma = player.volatility;

    double v_inv = 0, delta_sum = 0;
    for (int i = 0; i < count; ++i) {
        double mu_j = (opp_ratings[i] - kDefaultRating) / kScale;
        double gj = g(std::max(opp_deviations[i], 0.0) / kScale);
        double e = 1.0 / (1.0 + std::exp(-gj * (mu - mu_j)));
        v_inv += gj * gj * e * (1.0 - e);
        delta_sum += gj * (scores[i] - e);
    }
    if (count <= 0 || v_inv <= 0) {
        // No games in the period: only the uncertainty grows.
        player.deviation = std::min(std::sqrt(phi * phi + sigma * sigma) * kScale, kMaxDeviation);
        return true;
    }
    const double v = 1.0 / v_inv;
    const double delta = v * delta_sum;

    // Step 5: new volatility by the Illinois variant of regula falsi.
    const double a = std::log(sigma * sigma);
    auto f = [&](double x) {
        double ex = std::exp(x);
        double d = phi * phi + v + ex;
        return ex * (delta * delta - phi * phi - v - ex) / (2.0 * d * d) - (x - a) / (kTau * kTau);
    };
    double A = a, B;
    if (delta * delta > phi * phi + v) {
        B = std::log(delta * delta - phi * phi - v);
    } else {
        double k = 1;
        while (f(a - k * kTau) < 0) ++k;
        B = a - k * kTau;
    }
    double fA = f(A), fB = f(B);
    for (int i = 0; i < 100 && std::fabs(B - A) > 1e-6; ++i) {
        double C = A + (A - B) * fA / (fB - fA);
        double fC = f(C);
        if (fC * fB <= 0) {
            A = B;
            fA = fB;
        } else {
            fA /= 2;
        }
        B = C;
        fB = fC;
    }
    const double new_sigma = std::exp(A / 2);

    const double phi_star = std::sqrt(phi * phi + new_sigma * new_sigma);
    const double new_phi = 1.0 / std::sqrt(1.0 / (phi_star * phi_star) + v_inv);
    const double new_mu = mu + new_phi * new_phi * delta_sum;

    player.rating = new_mu * kScale + kDefaultRating;
    player.deviation = new_phi * kScale;
    player.volatility = new_sigma;
    return std::isfinite(player.rating) && std::isfinite(player.deviation) && std::isfinite(player.volatility);
}

// Names end up in a CSV row and in file-free UI labels: no separators or control characters.
std::string clean_name(const char* name) {
    std::string s = name ? name : "";
    for (char& c : s)
        if (c == ',' || static_cast<unsigned char>(c) < 0x20) c = ' ';
    size_t first = s.find_first_not_of(' ');
    if (first == std::string::npos) return {};
    s = s.substr(first, s.find_last_not_of(' ') - first + 1);
    size_t limit = sizeof(lpd_player_info{}.name) - 1;
    if (s.size() > limit) {
        s.resize(limit);
        // Do not cut a UTF-8 sequence in half.
        while (!s.empty() && (static_cast<unsigned char>(s.back()) & 0xC0) == 0x80) s.pop_back();
        if (!s.empty() && (static_cast<unsigned char>(s.back()) & 0x80)) s.pop_back();
    }
    return s;
}

} // namespace

struct lpd_players {
    std::wstring path;
    std::vector<lpd_player_info> list;

    lpd_player_info* find(int id) {
        for (auto& p : list)
            if (p.id == id) return &p;
        return nullptr;
    }

    bool name_taken(const std::string& name, int except_id) const {
        return std::any_of(list.begin(), list.end(),
                           [&](const lpd_player_info& p) { return p.id != except_id && name == p.name; });
    }

    // Writes to a temporary file first so a crash cannot leave a half-written player list.
    int save() const {
        if (path.empty()) return LPD_OK;
        std::wstring tmp = path + L".tmp";
        FILE* f = _wfopen(tmp.c_str(), L"wb");
        if (!f) return LPD_ERR_IO;
        std::fputs(kHeader, f);
        for (const auto& p : list) {
            std::fprintf(f, "%d,%s,%.4f,%.4f,%.6f,%d,%d,%lld\n", p.id, p.name, p.rating.rating, p.rating.deviation,
                         p.rating.volatility, p.games, p.wins, static_cast<long long>(p.created_ms));
        }
        bool ok = std::ferror(f) == 0;
        ok = std::fclose(f) == 0 && ok;
        if (!ok) return LPD_ERR_IO;
        _wremove(path.c_str());
        return _wrename(tmp.c_str(), path.c_str()) == 0 ? LPD_OK : LPD_ERR_IO;
    }
};

extern "C" {

LPD_API void LPD_CALL lpd_glicko2_default(lpd_rating* out) {
    if (out) *out = {kDefaultRating, kDefaultDeviation, kDefaultVolatility};
}

LPD_API int LPD_CALL lpd_glicko2_update(lpd_rating* player, const double* opp_ratings, const double* opp_deviations,
                                        const double* scores, int count) {
    if (!player || count < 0 || (count > 0 && (!opp_ratings || !opp_deviations || !scores))) return LPD_ERR_ARG;
    lpd_rating next = *player;
    if (!glicko2(next, opp_ratings, opp_deviations, scores, count)) return LPD_ERR_ARG;
    *player = next;
    return LPD_OK;
}

LPD_API lpd_players* LPD_CALL lpd_players_open(const char* path) {
    auto* p = new lpd_players;
    p->path = lpd::widen(path);
    if (p->path.empty()) return p;

    FILE* f = _wfopen(p->path.c_str(), L"rb");
    if (!f) return p;
    char line[512];
    while (std::fgets(line, sizeof line, f)) {
        std::string_view row = line;
        while (!row.empty() && (row.back() == '\r' || row.back() == '\n')) row.remove_suffix(1);
        auto fields = lpd::split(row, ',');
        if (fields.size() < 8 || fields[0] == "Id") continue;
        auto integer = [](std::string_view s) {
            long long v = 0;
            std::from_chars(s.data(), s.data() + s.size(), v);
            return v;
        };
        auto real = [](std::string_view s, double fallback) {
            double v = fallback;
            std::from_chars(s.data(), s.data() + s.size(), v);
            return std::isfinite(v) ? v : fallback;
        };
        lpd_player_info info{};
        info.id = static_cast<int32_t>(integer(fields[0]));
        lpd::copy_field(info.name, fields[1]);
        info.rating.rating = real(fields[2], kDefaultRating);
        info.rating.deviation = std::clamp(real(fields[3], kDefaultDeviation), kMinDeviation, kMaxDeviation);
        info.rating.volatility = real(fields[4], kDefaultVolatility);
        if (!(info.rating.volatility > 0)) info.rating.volatility = kDefaultVolatility;
        info.games = static_cast<int32_t>(integer(fields[5]));
        info.wins = static_cast<int32_t>(integer(fields[6]));
        info.created_ms = integer(fields[7]);
        if (info.id > 0 && info.name[0] && !p->find(info.id)) p->list.push_back(info);
    }
    std::fclose(f);
    return p;
}

LPD_API void LPD_CALL lpd_players_close(lpd_players* p) { delete p; }

LPD_API int LPD_CALL lpd_players_count(const lpd_players* p) { return p ? static_cast<int>(p->list.size()) : 0; }

LPD_API int LPD_CALL lpd_players_get(const lpd_players* p, int index, lpd_player_info* out) {
    if (!p || !out || index < 0 || index >= static_cast<int>(p->list.size())) return LPD_ERR_ARG;
    *out = p->list[static_cast<size_t>(index)];
    return LPD_OK;
}

LPD_API int LPD_CALL lpd_players_find(const lpd_players* p, int id, lpd_player_info* out) {
    if (!p || !out) return LPD_ERR_ARG;
    const lpd_player_info* found = const_cast<lpd_players*>(p)->find(id);
    if (!found) return LPD_ERR_NOT_FOUND;
    *out = *found;
    return LPD_OK;
}

LPD_API int LPD_CALL lpd_players_add(lpd_players* p, const char* name, int64_t created_ms) {
    if (!p) return LPD_ERR_ARG;
    std::string clean = clean_name(name);
    if (clean.empty()) return LPD_ERR_ARG;
    if (p->name_taken(clean, 0)) return LPD_ERR_STATE;

    lpd_player_info info{};
    for (const auto& other : p->list) info.id = std::max(info.id, other.id);
    ++info.id;
    lpd::copy_field(info.name, clean);
    lpd_glicko2_default(&info.rating);
    info.created_ms = created_ms;
    p->list.push_back(info);
    int rc = p->save();
    return rc == LPD_OK ? info.id : rc;
}

LPD_API int LPD_CALL lpd_players_rename(lpd_players* p, int id, const char* name) {
    if (!p) return LPD_ERR_ARG;
    lpd_player_info* player = p->find(id);
    if (!player) return LPD_ERR_NOT_FOUND;
    std::string clean = clean_name(name);
    if (clean.empty()) return LPD_ERR_ARG;
    if (p->name_taken(clean, id)) return LPD_ERR_STATE;
    lpd::copy_field(player->name, clean);
    return p->save();
}

LPD_API int LPD_CALL lpd_players_remove(lpd_players* p, int id) {
    if (!p) return LPD_ERR_ARG;
    auto it = std::find_if(p->list.begin(), p->list.end(), [id](const lpd_player_info& x) { return x.id == id; });
    if (it == p->list.end()) return LPD_ERR_NOT_FOUND;
    p->list.erase(it);
    return p->save();
}

LPD_API int LPD_CALL lpd_players_record(lpd_players* p, int id, double puzzle_rating, double puzzle_deviation,
                                        int solved, lpd_player_info* out) {
    if (!p) return LPD_ERR_ARG;
    lpd_player_info* player = p->find(id);
    if (!player) return LPD_ERR_NOT_FOUND;

    lpd_rating next = player->rating;
    const double score = solved ? 1.0 : 0.0;
    if (!glicko2(next, &puzzle_rating, &puzzle_deviation, &score, 1)) return LPD_ERR_ARG;
    next.deviation = std::clamp(next.deviation, kMinDeviation, kMaxDeviation);
    player->rating = next;
    ++player->games;
    if (solved) ++player->wins;
    if (out) *out = *player;
    return p->save();
}

} // extern "C"
