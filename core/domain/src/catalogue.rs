//! Display names, groups and descriptions for puzzle themes, and display names for opening tags.
//!
//! The Lichess CSV uses theme keys such as `attackingF2F7`. Groups follow the workbench layout;
//! descriptions are written for this app. A key missing from the table falls into `Other` with
//! the key as its name.

pub struct ThemeInfo {
    pub key: &'static str,
    pub group: &'static str,
    pub name: &'static str,
    pub description: &'static str,
}

const fn theme(key: &'static str, group: &'static str, name: &'static str, description: &'static str) -> ThemeInfo {
    ThemeInfo { key, group, name, description }
}

pub const OTHER_GROUP: &str = "Other";

/// Groups in display order.
pub const GROUPS: &[&str] = &[
    "Goal",
    "Length",
    "Phase",
    "Motif",
    "Advanced",
    "Mate in",
    "Mating pattern",
    "Special move",
    "Origin",
    OTHER_GROUP,
];

/// Known themes in display order.
pub const THEMES: &[ThemeInfo] = &[
    theme("equality", "Goal", "Equality", "Hold a lost or difficult position to a draw or a balanced game."),
    theme("advantage", "Goal", "Advantage", "Win a clear advantage, about two to six pawns."),
    theme("crushing", "Goal", "Crushing", "Win a decisive advantage, more than six pawns."),
    theme("mate", "Goal", "Checkmate", "Finish the game by checkmate."),
    theme("oneMove", "Length", "One move", "The solution is a single move."),
    theme("short", "Length", "Short", "The solution is two moves."),
    theme("long", "Length", "Long", "The solution is three moves."),
    theme("veryLong", "Length", "Very long", "The solution is four moves or more."),
    theme("opening", "Phase", "Opening", "The position arises in the opening."),
    theme("middlegame", "Phase", "Middlegame", "The position arises in the middlegame."),
    theme("endgame", "Phase", "Endgame", "The position arises in the endgame."),
    theme("rookEndgame", "Phase", "Rook endgame", "An endgame with only rooks and pawns."),
    theme("bishopEndgame", "Phase", "Bishop endgame", "An endgame with only bishops and pawns."),
    theme("knightEndgame", "Phase", "Knight endgame", "An endgame with only knights and pawns."),
    theme("pawnEndgame", "Phase", "Pawn endgame", "An endgame with only kings and pawns."),
    theme("queenEndgame", "Phase", "Queen endgame", "An endgame with only queens and pawns."),
    theme("queenRookEndgame", "Phase", "Queen and rook endgame", "An endgame with only queens, rooks and pawns."),
    theme(
        "advancedPawn",
        "Motif",
        "Advanced pawn",
        "A pawn far up the board, often close to promoting, decides the position.",
    ),
    theme(
        "attackingF2F7",
        "Motif",
        "Attacking f2 or f7",
        "An attack on the f2 or f7 pawn, the square next to the uncastled king.",
    ),
    theme(
        "capturingDefender",
        "Motif",
        "Capturing the defender",
        "Remove a piece that defends another, then win the undefended piece.",
    ),
    theme(
        "discoveredAttack",
        "Motif",
        "Discovered attack",
        "Move a piece out of the way to uncover an attack by the piece behind it.",
    ),
    theme(
        "discoveredCheck",
        "Motif",
        "Discovered check",
        "Move a piece out of the way to uncover a check by the piece behind it.",
    ),
    theme("doubleCheck", "Motif", "Double check", "Give check with two pieces at once, so the king has to move."),
    theme("exposedKing", "Motif", "Exposed king", "Attack a king that has few defenders around it."),
    theme("fork", "Motif", "Fork", "Attack two or more pieces with one move."),
    theme("hangingPiece", "Motif", "Hanging piece", "Win a piece that is undefended or not defended enough."),
    theme("kingsideAttack", "Motif", "Kingside attack", "Attack a king castled on the kingside."),
    theme("pin", "Motif", "Pin", "Use a piece that cannot move without exposing a more valuable piece behind it."),
    theme("queensideAttack", "Motif", "Queenside attack", "Attack a king castled on the queenside."),
    theme("sacrifice", "Motif", "Sacrifice", "Give up material to gain a greater advantage."),
    theme("skewer", "Motif", "Skewer", "Attack a valuable piece so that it moves and exposes a piece behind it."),
    theme("trappedPiece", "Motif", "Trapped piece", "Win a piece that has no safe square to go to."),
    theme("attraction", "Advanced", "Attraction", "Force a piece onto a square where it can be attacked or exploited."),
    theme("clearance", "Advanced", "Clearance", "Vacate a square, file or diagonal so another piece can use it."),
    theme(
        "collinearMove",
        "Advanced",
        "Collinear move",
        "The key move travels along the same line as an opposing piece's move or attack.",
    ),
    theme(
        "defensiveMove",
        "Advanced",
        "Defensive move",
        "A precise defensive move is needed to avoid losing material or the game.",
    ),
    theme(
        "deflection",
        "Advanced",
        "Deflection",
        "Draw a defending piece away from a square or piece it must protect.",
    ),
    theme("interference", "Advanced", "Interference", "Place a piece between an opposing piece and what it defends."),
    theme("intermezzo", "Advanced", "Intermezzo", "Play a forcing move before the expected reply."),
    theme(
        "quietMove",
        "Advanced",
        "Quiet move",
        "A move without a check or capture that prepares an unstoppable threat.",
    ),
    theme("xRayAttack", "Advanced", "X-ray attack", "A piece attacks or defends a square through an opposing piece."),
    theme("zugzwang", "Advanced", "Zugzwang", "The opponent has only moves that make the position worse."),
    theme("mateIn1", "Mate in", "Mate in 1", "Give checkmate in one move."),
    theme("mateIn2", "Mate in", "Mate in 2", "Give checkmate in two moves."),
    theme("mateIn3", "Mate in", "Mate in 3", "Give checkmate in three moves."),
    theme("mateIn4", "Mate in", "Mate in 4", "Give checkmate in four moves."),
    theme("mateIn5", "Mate in", "Mate in 5 or more", "Give checkmate in five moves or more."),
    theme(
        "anastasiaMate",
        "Mating pattern",
        "Anastasia's mate",
        "A knight and a rook or queen trap the king on the edge, blocked by one of its own pieces.",
    ),
    theme("arabianMate", "Mating pattern", "Arabian mate", "A knight and a rook trap the king in a corner."),
    theme(
        "backRankMate",
        "Mating pattern",
        "Back rank mate",
        "Mate on the home rank of a king hemmed in by its own pieces.",
    ),
    theme(
        "balestraMate",
        "Mating pattern",
        "Balestra mate",
        "A bishop gives mate while a queen covers the king's escape squares.",
    ),
    theme("blindSwineMate", "Mating pattern", "Blind swine mate", "Two rooks on the seventh rank mate the king."),
    theme(
        "bodenMate",
        "Mating pattern",
        "Boden's mate",
        "Two bishops on crossing diagonals mate a king obstructed by its own pieces.",
    ),
    theme(
        "cornerMate",
        "Mating pattern",
        "Corner mate",
        "A rook or queen and another piece mate the king in a corner.",
    ),
    theme(
        "doubleBishopMate",
        "Mating pattern",
        "Double bishop mate",
        "Two bishops on adjacent diagonals mate a king obstructed by its own pieces.",
    ),
    theme(
        "dovetailMate",
        "Mating pattern",
        "Dovetail mate",
        "A queen next to the king mates it, with its retreat squares blocked by its own pieces.",
    ),
    theme(
        "epauletteMate",
        "Mating pattern",
        "Epaulette mate",
        "Mate on the edge, with the king's own pieces on both sides of it.",
    ),
    theme("hookMate", "Mating pattern", "Hook mate", "A rook, knight and pawn combine to mate the king."),
    theme(
        "killBoxMate",
        "Mating pattern",
        "Kill box mate",
        "A rook next to the king and a queen two squares away mate it together.",
    ),
    theme(
        "morphysMate",
        "Mating pattern",
        "Morphy's mate",
        "A bishop mates a cornered king while a rook covers its escape.",
    ),
    theme("operaMate", "Mating pattern", "Opera mate", "A rook mates on the back rank while a bishop defends it."),
    theme(
        "pillsburysMate",
        "Mating pattern",
        "Pillsbury's mate",
        "A rook and bishop mate a king castled on the kingside.",
    ),
    theme("smotheredMate", "Mating pattern", "Smothered mate", "A knight mates a king surrounded by its own pieces."),
    theme(
        "swallowstailMate",
        "Mating pattern",
        "Swallow's tail mate",
        "A queen mates the king from directly in front, with its diagonal retreats blocked.",
    ),
    theme(
        "triangleMate",
        "Mating pattern",
        "Triangle mate",
        "A queen and rook form a triangle around the king to mate it.",
    ),
    theme(
        "vukovicMate",
        "Mating pattern",
        "Vuković mate",
        "A rook and knight mate the king on the edge, the rook protected by another piece.",
    ),
    theme("castling", "Special move", "Castling", "The solution includes castling."),
    theme("enPassant", "Special move", "En passant", "The solution includes an en passant capture."),
    theme("promotion", "Special move", "Promotion", "The solution includes promoting a pawn."),
    theme(
        "underPromotion",
        "Special move",
        "Underpromotion",
        "The solution includes promoting to a knight, bishop or rook.",
    ),
    theme("master", "Origin", "Master game", "From a game played by a titled player."),
    theme("masterVsMaster", "Origin", "Master vs master", "From a game between two titled players."),
    theme("superGM", "Origin", "Super GM game", "From a game played by one of the world's top players."),
];

pub fn theme_info(key: &str) -> Option<&'static ThemeInfo> {
    THEMES.iter().find(|t| t.key == key)
}

/// Opening family tag shown as text: `Queens_Pawn_Game` → `Queens Pawn Game`.
pub fn family_name(tag: &str) -> String {
    tag.replace('_', " ")
}

/// Variation tag shown without its family: `French_Defense_Exchange_Variation` → `Exchange Variation`.
pub fn variation_name(tag: &str, family: &str) -> String {
    let name = tag
        .strip_prefix(family)
        .map(|rest| rest.trim_start_matches('_'))
        .filter(|rest| !rest.is_empty())
        .unwrap_or(tag);
    name.replace('_', " ")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn every_theme_has_a_known_group_and_unique_key() {
        let mut keys: Vec<&str> = THEMES.iter().map(|t| t.key).collect();
        keys.sort();
        keys.dedup();
        assert_eq!(keys.len(), THEMES.len());
        assert_eq!(THEMES.len(), 73);
        assert!(THEMES.iter().all(|t| GROUPS.contains(&t.group) && t.group != OTHER_GROUP));
        assert_eq!(theme_info("attackingF2F7").unwrap().name, "Attacking f2 or f7");
        assert!(theme_info("unknownTheme").is_none());
    }

    #[test]
    fn opening_names() {
        assert_eq!(family_name("Queens_Pawn_Game"), "Queens Pawn Game");
        assert_eq!(variation_name("French_Defense_Exchange_Variation", "French_Defense"), "Exchange Variation");
        assert_eq!(variation_name("Sicilian_Defense_Other_variations", "Sicilian_Defense"), "Other variations");
        assert_eq!(variation_name("Odd_Tag", "Other_Family"), "Odd Tag");
    }
}
