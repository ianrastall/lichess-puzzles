//! Downloading the Lichess puzzle database: resumable transfer and update checks.
//!
//! The compressed file is written to `<dir>/lichess_db_puzzle.csv.zst.part` and moved into place
//! once complete. `download.json` records the completed file's `ETag`, `Last-Modified` and length,
//! so an update check needs only a `HEAD` request. The partial file has its own record
//! (`….part.json`), so an interrupted transfer resumes with a `Range` request guarded by
//! `If-Range`, and the completed file stays current until the new one replaces it.

use std::fs::{self, File, OpenOptions};
use std::io::{Read, Seek, SeekFrom, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

use serde::{Deserialize, Serialize};

pub const PUZZLE_URL: &str = "https://database.lichess.org/lichess_db_puzzle.csv.zst";
pub const SOURCE_FILE: &str = "lichess_db_puzzle.csv.zst";
const PART_SUFFIX: &str = ".part";
const RECORD_FILE: &str = "download.json";
const MAX_RETRIES: u32 = 5;
const PROGRESS_INTERVAL: Duration = Duration::from_millis(250);

/// What the server reports about the file.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct RemoteVersion {
    pub etag: Option<String>,
    pub last_modified: Option<String>,
    pub length: Option<u64>,
}

impl RemoteVersion {
    fn from_headers(headers: &ureq::http::HeaderMap, length: Option<u64>) -> Self {
        let text = |name: &str| headers.get(name).and_then(|v| v.to_str().ok()).map(str::to_owned);
        RemoteVersion { etag: text("etag"), last_modified: text("last-modified"), length }
    }

    /// The validator sent with `If-Range`: a strong ETag if there is one, otherwise the date.
    fn validator(&self) -> Option<&str> {
        match &self.etag {
            Some(etag) if !etag.starts_with("W/") => Some(etag),
            _ => self.last_modified.as_deref(),
        }
    }

    /// Same file when a shared validator matches; a length difference always counts as a change.
    pub fn same_file(&self, other: &RemoteVersion) -> bool {
        if self.length.is_some() && other.length.is_some() && self.length != other.length {
            return false;
        }
        match (&self.etag, &other.etag) {
            (Some(a), Some(b)) => a == b,
            _ => self.last_modified.is_some() && self.last_modified == other.last_modified,
        }
    }
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct DownloadRecord {
    pub url: String,
    pub version: RemoteVersion,
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Progress {
    pub downloaded: u64,
    pub total: Option<u64>,
    /// Bytes per second over this request.
    pub rate: f64,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum DownloadOutcome {
    Completed {
        bytes: u64,
        resumed_from: u64,
        version: RemoteVersion,
    },
    /// The partial file is kept, and the next download resumes from it.
    Cancelled {
        downloaded: u64,
    },
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum UpdateCheck {
    NotDownloaded,
    Current(RemoteVersion),
    Available { local: RemoteVersion, remote: RemoteVersion },
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SourceError {
    Http(u16),
    Network(String),
    Io(String),
    /// The body ended before the length the server announced.
    Truncated {
        expected: u64,
        received: u64,
    },
}

impl std::fmt::Display for SourceError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            SourceError::Http(status) => write!(f, "server returned HTTP {status}"),
            SourceError::Network(message) => write!(f, "network error: {message}"),
            SourceError::Io(message) => write!(f, "file error: {message}"),
            SourceError::Truncated { expected, received } => {
                write!(f, "download ended at {received} of {expected} bytes")
            }
        }
    }
}

impl std::error::Error for SourceError {}

fn io_error(error: std::io::Error) -> SourceError {
    SourceError::Io(error.to_string())
}

pub struct Source {
    dir: PathBuf,
    url: String,
    agent: ureq::Agent,
    retry_delay: Duration,
}

impl Source {
    pub fn new(dir: impl Into<PathBuf>, url: impl Into<String>) -> Self {
        let agent = ureq::Agent::config_builder()
            .http_status_as_error(false)
            .timeout_connect(Some(Duration::from_secs(30)))
            .timeout_recv_response(Some(Duration::from_secs(60)))
            .timeout_per_read(Some(Duration::from_secs(30)))
            .user_agent(format!("JupiterLichessPuzzles/{}", env!("CARGO_PKG_VERSION")))
            .build()
            .new_agent();
        Source { dir: dir.into(), url: url.into(), agent, retry_delay: Duration::from_secs(2) }
    }

    /// First wait between retries; later waits double. Tests shorten it.
    pub fn with_retry_delay(mut self, delay: Duration) -> Self {
        self.retry_delay = delay;
        self
    }

    pub fn file_path(&self) -> PathBuf {
        self.dir.join(SOURCE_FILE)
    }

    fn part_path(&self) -> PathBuf {
        self.dir.join(format!("{SOURCE_FILE}{PART_SUFFIX}"))
    }

    fn record_path(&self) -> PathBuf {
        self.dir.join(RECORD_FILE)
    }

    fn part_record_path(&self) -> PathBuf {
        self.dir.join(format!("{SOURCE_FILE}{PART_SUFFIX}.json"))
    }

    /// The completed download's record.
    pub fn record(&self) -> Option<DownloadRecord> {
        read_record(&self.record_path())
    }

    fn discard_part(&self) {
        let _ = fs::remove_file(self.part_path());
        let _ = fs::remove_file(self.part_record_path());
    }

    /// The downloaded file's version, if a complete download from this URL is present.
    pub fn local_version(&self) -> Option<RemoteVersion> {
        let record = self.record()?;
        (record.url == self.url && self.file_path().is_file()).then_some(record.version)
    }

    pub fn remote_version(&self) -> Result<RemoteVersion, SourceError> {
        let response = self.agent.head(&self.url).call().map_err(|e| SourceError::Network(e.to_string()))?;
        if !response.status().is_success() {
            return Err(SourceError::Http(response.status().as_u16()));
        }
        let length = header_u64(response.headers(), "content-length");
        Ok(RemoteVersion::from_headers(response.headers(), length))
    }

    pub fn check_update(&self) -> Result<UpdateCheck, SourceError> {
        let remote = self.remote_version()?;
        Ok(match self.local_version() {
            None => UpdateCheck::NotDownloaded,
            Some(local) if local.same_file(&remote) => UpdateCheck::Current(remote),
            Some(local) => UpdateCheck::Available { local, remote },
        })
    }

    /// Downloads the file, resuming a matching partial download. Network failures are retried
    /// with resumption up to five times. `cancel` is checked between reads.
    pub fn download(
        &self,
        progress: &mut dyn FnMut(Progress),
        cancel: &AtomicBool,
    ) -> Result<DownloadOutcome, SourceError> {
        fs::create_dir_all(&self.dir).map_err(io_error)?;
        let mut failures = 0;
        loop {
            match self.attempt(progress, cancel) {
                Ok(outcome) => return Ok(outcome),
                Err(Attempt::Fatal(error)) => return Err(error),
                Err(Attempt::Retry(error)) => {
                    failures += 1;
                    if failures > MAX_RETRIES {
                        return Err(error);
                    }
                    let delay = self.retry_delay * 2u32.pow(failures - 1);
                    if !wait(delay, cancel) {
                        return Ok(DownloadOutcome::Cancelled { downloaded: file_len(&self.part_path()) });
                    }
                }
            }
        }
    }

    fn attempt(&self, progress: &mut dyn FnMut(Progress), cancel: &AtomicBool) -> Result<DownloadOutcome, Attempt> {
        let part = self.part_path();
        let previous = read_record(&self.part_record_path()).filter(|r| r.url == self.url);
        let validator = previous.as_ref().and_then(|r| r.version.validator().map(str::to_owned));
        let have = match &validator {
            Some(_) => file_len(&part),
            None => 0,
        };

        let mut request = self.agent.get(&self.url);
        if let (true, Some(validator)) = (have > 0, &validator) {
            request = request.header("Range", format!("bytes={have}-")).header("If-Range", validator);
        }
        let response = request.call().map_err(|e| Attempt::Retry(SourceError::Network(e.to_string())))?;
        let status = response.status().as_u16();
        let headers = response.headers().clone();

        let (start, version) = match status {
            206 => {
                let range = headers.get("content-range").and_then(|v| v.to_str().ok()).and_then(parse_content_range);
                match range {
                    Some((first, total)) if first == have => {
                        let mut version = previous.map(|r| r.version).unwrap_or_default();
                        version.length = total.or(version.length);
                        (have, version)
                    }
                    _ => {
                        // The server answered a different range; start again from the beginning.
                        self.discard_part();
                        return Err(Attempt::Retry(SourceError::Http(206)));
                    }
                }
            }
            200 => (0, RemoteVersion::from_headers(&headers, header_u64(&headers, "content-length"))),
            416 => {
                self.discard_part();
                return Err(Attempt::Retry(SourceError::Http(416)));
            }
            429 | 500..=599 => return Err(Attempt::Retry(SourceError::Http(status))),
            _ => return Err(Attempt::Fatal(SourceError::Http(status))),
        };

        let record = DownloadRecord { url: self.url.clone(), version: version.clone() };
        write_record(&self.part_record_path(), &record).map_err(Attempt::Fatal)?;
        let mut file = OpenOptions::new().create(true).write(true).truncate(false).open(&part).map_err(fatal_io)?;
        file.set_len(start).map_err(fatal_io)?;
        file.seek(SeekFrom::Start(start)).map_err(fatal_io)?;

        let mut body = response.into_body().into_reader();
        let mut buffer = vec![0u8; 1 << 20];
        let mut downloaded = start;
        let started = Instant::now();
        let mut last_report: Option<Instant> = None;
        loop {
            if cancel.load(Ordering::Relaxed) {
                file.flush().map_err(fatal_io)?;
                return Ok(DownloadOutcome::Cancelled { downloaded });
            }
            let read = match body.read(&mut buffer) {
                Ok(0) => break,
                Ok(read) => read,
                Err(error) if error.kind() == std::io::ErrorKind::Interrupted => continue,
                Err(error) => {
                    file.flush().map_err(fatal_io)?;
                    return Err(Attempt::Retry(SourceError::Network(error.to_string())));
                }
            };
            file.write_all(&buffer[..read]).map_err(fatal_io)?;
            downloaded += read as u64;
            if last_report.is_none_or(|t| t.elapsed() >= PROGRESS_INTERVAL) {
                last_report = Some(Instant::now());
                progress(Progress { downloaded, total: version.length, rate: rate(downloaded - start, started) });
            }
        }
        file.sync_all().map_err(fatal_io)?;
        drop(file);
        progress(Progress { downloaded, total: version.length, rate: rate(downloaded - start, started) });

        if let Some(expected) = version.length
            && downloaded != expected
        {
            return Err(Attempt::Retry(SourceError::Truncated { expected, received: downloaded }));
        }
        fs::rename(&part, self.file_path()).map_err(fatal_io)?;
        let version = RemoteVersion { length: Some(downloaded), ..version };
        write_record(&self.record_path(), &DownloadRecord { url: self.url.clone(), version: version.clone() })
            .map_err(Attempt::Fatal)?;
        let _ = fs::remove_file(self.part_record_path());
        Ok(DownloadOutcome::Completed { bytes: downloaded, resumed_from: start, version })
    }
}

enum Attempt {
    Retry(SourceError),
    Fatal(SourceError),
}

fn fatal_io(error: std::io::Error) -> Attempt {
    Attempt::Fatal(io_error(error))
}

fn read_record(path: &Path) -> Option<DownloadRecord> {
    serde_json::from_str(&fs::read_to_string(path).ok()?).ok()
}

fn write_record(path: &Path, record: &DownloadRecord) -> Result<(), SourceError> {
    let mut temp = path.as_os_str().to_owned();
    temp.push(".tmp");
    fs::write(&temp, serde_json::to_vec_pretty(record).expect("record serializes")).map_err(io_error)?;
    fs::rename(&temp, path).map_err(io_error)
}

fn file_len(path: &Path) -> u64 {
    File::open(path).and_then(|f| f.metadata()).map(|m| m.len()).unwrap_or(0)
}

fn header_u64(headers: &ureq::http::HeaderMap, name: &str) -> Option<u64> {
    headers.get(name)?.to_str().ok()?.trim().parse().ok()
}

/// `bytes 100-199/1000` → (100, Some(1000)); `bytes 100-199/*` → (100, None).
fn parse_content_range(value: &str) -> Option<(u64, Option<u64>)> {
    let rest = value.trim().strip_prefix("bytes ")?;
    let (range, total) = rest.split_once('/')?;
    let (first, _) = range.split_once('-')?;
    Some((first.trim().parse().ok()?, total.trim().parse().ok()))
}

fn rate(bytes: u64, since: Instant) -> f64 {
    let seconds = since.elapsed().as_secs_f64();
    if seconds > 0.0 { bytes as f64 / seconds } else { 0.0 }
}

/// Sleeps for `delay`, returning false early if cancelled.
fn wait(delay: Duration, cancel: &AtomicBool) -> bool {
    let until = Instant::now() + delay;
    while Instant::now() < until {
        if cancel.load(Ordering::Relaxed) {
            return false;
        }
        std::thread::sleep(Duration::from_millis(50).min(until - Instant::now()));
    }
    !cancel.load(Ordering::Relaxed)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn content_range() {
        assert_eq!(parse_content_range("bytes 100-199/1000"), Some((100, Some(1000))));
        assert_eq!(parse_content_range("bytes 5-9/*"), Some((5, None)));
        assert_eq!(parse_content_range("items 1-2/3"), None);
    }

    #[test]
    fn versions_compare_by_validator_and_length() {
        let a = RemoteVersion { etag: Some("\"x\"".into()), last_modified: Some("d1".into()), length: Some(10) };
        assert!(a.same_file(&a.clone()));
        assert!(!a.same_file(&RemoteVersion { etag: Some("\"y\"".into()), ..a.clone() }));
        assert!(!a.same_file(&RemoteVersion { length: Some(11), ..a.clone() }));
        let dated = RemoteVersion { etag: None, ..a.clone() };
        assert!(dated.same_file(&a));
        assert_eq!(RemoteVersion { etag: Some("W/\"x\"".into()), ..a.clone() }.validator(), Some("d1"));
    }
}
