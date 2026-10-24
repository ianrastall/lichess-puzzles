//! Domain core for Lichess Puzzles: downloading the Lichess puzzle database, building the local
//! puzzle store, filtering it and the chess rules used to check solutions.

pub mod build;
pub mod catalogue;
pub mod chess;
pub mod engine;
pub mod export;
pub mod filter;
pub mod players;
pub mod puzzle_id;
pub mod service;
pub mod session;
pub mod source;
pub mod store;

use std::path::PathBuf;

use serde::{Deserialize, Serialize};

/// The side a puzzle's solver plays.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Side {
    White,
    Black,
}

/// Default data folder: `%LOCALAPPDATA%\Jupiter\LichessPuzzles`.
pub fn default_data_dir() -> PathBuf {
    let base = std::env::var_os("LOCALAPPDATA").map(PathBuf::from).unwrap_or_else(|| PathBuf::from("."));
    base.join("Jupiter").join("LichessPuzzles")
}

/// Store file name inside the data folder.
pub const STORE_FILE: &str = "puzzles.store";
