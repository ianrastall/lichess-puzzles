// Log of attempted puzzles, persisted as a small CSV file.
#pragma once

#include "../include/lpd_core.h"

#include <string>
#include <unordered_set>
#include <vector>

struct lpd_history {
    std::wstring path;
    std::vector<lpd_history_entry> entries;
    std::unordered_set<std::string> ids;
};
