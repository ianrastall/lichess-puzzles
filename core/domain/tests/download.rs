//! Download, resume and update checks against a local HTTP server.

use std::io::{BufRead, BufReader, Write};
use std::net::{TcpListener, TcpStream};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use lichess_puzzles_core::source::{DownloadOutcome, SOURCE_FILE, Source, UpdateCheck};

/// Serves one file with an ETag, honouring `Range` with `If-Range`. Each response can be cut
/// short after a number of body bytes to simulate a dropped connection.
#[derive(Clone)]
struct Server {
    url: String,
    state: Arc<Mutex<ServerState>>,
    requests: Arc<AtomicUsize>,
}

struct ServerState {
    body: Vec<u8>,
    etag: String,
    /// Body bytes to send before closing, per upcoming GET response.
    cut_after: Vec<usize>,
    ranges: Vec<Option<String>>,
}

impl Server {
    fn start(body: Vec<u8>, etag: &str) -> Server {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let url = format!("http://{}/lichess_db_puzzle.csv.zst", listener.local_addr().unwrap());
        let state =
            Arc::new(Mutex::new(ServerState { body, etag: etag.into(), cut_after: Vec::new(), ranges: Vec::new() }));
        let server = Server { url, state, requests: Arc::new(AtomicUsize::new(0)) };
        let handler = server.clone();
        std::thread::spawn(move || {
            for stream in listener.incoming().flatten() {
                handler.requests.fetch_add(1, Ordering::SeqCst);
                let _ = handler.handle(stream);
            }
        });
        server
    }

    fn handle(&self, stream: TcpStream) -> std::io::Result<()> {
        let mut reader = BufReader::new(stream.try_clone()?);
        let mut request_line = String::new();
        reader.read_line(&mut request_line)?;
        let (mut range, mut if_range) = (None, None);
        loop {
            let mut line = String::new();
            reader.read_line(&mut line)?;
            let line = line.trim_end();
            if line.is_empty() {
                break;
            }
            let (name, value) = line.split_once(':').unwrap();
            match name.to_ascii_lowercase().as_str() {
                "range" => range = Some(value.trim().to_owned()),
                "if-range" => if_range = Some(value.trim().to_owned()),
                _ => {}
            }
        }
        let mut state = self.state.lock().unwrap();
        let head = request_line.starts_with("HEAD");
        let total = state.body.len();
        let start = match (&range, &if_range) {
            (Some(r), Some(tag)) if *tag == state.etag => {
                r.strip_prefix("bytes=").and_then(|r| r.strip_suffix('-')).and_then(|n| n.parse().ok())
            }
            _ => None,
        };
        let mut out = stream;
        let mut header = match start {
            Some(start) => format!(
                "HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {start}-{}/{total}\r\nContent-Length: {}\r\n",
                total - 1,
                total - start
            ),
            None => format!("HTTP/1.1 200 OK\r\nContent-Length: {total}\r\n"),
        };
        header.push_str(&format!(
            "ETag: {}\r\nLast-Modified: Fri, 02 Oct 2026 08:51:45 GMT\r\nConnection: close\r\n\r\n",
            state.etag
        ));
        out.write_all(header.as_bytes())?;
        if head {
            return Ok(());
        }
        state.ranges.push(range);
        let start = start.unwrap_or(0);
        let remaining = total - start;
        let cut = if state.cut_after.is_empty() { remaining } else { state.cut_after.remove(0).min(remaining) };
        out.write_all(&state.body[start..start + cut])?;
        out.flush()
    }
}

fn body(len: usize) -> Vec<u8> {
    (0..len).map(|i| (i * 7 % 251) as u8).collect()
}

#[test]
fn downloads_and_reports_current() {
    let content = body(3_000_000);
    let server = Server::start(content.clone(), "\"v1\"");
    let dir = tempfile::tempdir().unwrap();
    let source = Source::new(dir.path(), server.url.clone());

    assert_eq!(source.check_update().unwrap(), UpdateCheck::NotDownloaded);
    let mut reports = 0;
    let outcome = source.download(&mut |_| reports += 1, &AtomicBool::new(false)).unwrap();
    let DownloadOutcome::Completed { bytes, resumed_from, version } = outcome else { panic!("{outcome:?}") };
    assert_eq!((bytes, resumed_from), (3_000_000, 0));
    assert_eq!(version.etag.as_deref(), Some("\"v1\""));
    assert!(reports >= 1);
    assert_eq!(std::fs::read(dir.path().join(SOURCE_FILE)).unwrap(), content);
    assert!(matches!(source.check_update().unwrap(), UpdateCheck::Current(_)));

    // An interrupted second download leaves the completed file current.
    server.state.lock().unwrap().cut_after = vec![100_000];
    let cancel = AtomicBool::new(false);
    let slow = Source::new(dir.path(), server.url.clone()).with_retry_delay(Duration::from_secs(5));
    std::thread::scope(|scope| {
        scope.spawn(|| {
            std::thread::sleep(Duration::from_millis(500));
            cancel.store(true, Ordering::Relaxed);
        });
        assert_eq!(slow.download(&mut |_| {}, &cancel).unwrap(), DownloadOutcome::Cancelled { downloaded: 100_000 });
    });
    assert!(matches!(source.check_update().unwrap(), UpdateCheck::Current(_)));
    assert_eq!(std::fs::read(dir.path().join(SOURCE_FILE)).unwrap(), content);

    server.state.lock().unwrap().etag = "\"v2\"".into();
    assert!(matches!(source.check_update().unwrap(), UpdateCheck::Available { .. }));
}

#[test]
fn resumes_after_a_dropped_connection() {
    let content = body(2_500_000);
    let server = Server::start(content.clone(), "\"v1\"");
    server.state.lock().unwrap().cut_after = vec![1_000_000];
    let dir = tempfile::tempdir().unwrap();
    let source = Source::new(dir.path(), server.url.clone()).with_retry_delay(Duration::from_millis(10));

    let outcome = source.download(&mut |_| {}, &AtomicBool::new(false)).unwrap();
    assert!(
        matches!(outcome, DownloadOutcome::Completed { bytes: 2_500_000, resumed_from: 1_000_000, .. }),
        "{outcome:?}"
    );
    assert_eq!(std::fs::read(dir.path().join(SOURCE_FILE)).unwrap(), content);
    assert_eq!(server.state.lock().unwrap().ranges, [None, Some("bytes=1000000-".to_owned())]);
}

#[test]
fn restarts_when_the_file_changed_during_an_interruption() {
    let server = Server::start(body(2_000_000), "\"v1\"");
    server.state.lock().unwrap().cut_after = vec![500_000];
    let dir = tempfile::tempdir().unwrap();
    let cancel = AtomicBool::new(false);
    // Cancel during the retry wait after the first, cut-short response.
    let source = Source::new(dir.path(), server.url.clone()).with_retry_delay(Duration::from_secs(5));
    std::thread::scope(|scope| {
        scope.spawn(|| {
            std::thread::sleep(Duration::from_millis(500));
            cancel.store(true, Ordering::Relaxed);
        });
        let outcome = source.download(&mut |_| {}, &cancel).unwrap();
        assert_eq!(outcome, DownloadOutcome::Cancelled { downloaded: 500_000 });
    });

    let replaced = body(1_800_000).into_iter().map(|b| b ^ 0xFF).collect::<Vec<_>>();
    {
        let mut state = server.state.lock().unwrap();
        state.body = replaced.clone();
        state.etag = "\"v2\"".into();
    }
    let outcome = source.download(&mut |_| {}, &AtomicBool::new(false)).unwrap();
    assert!(matches!(outcome, DownloadOutcome::Completed { bytes: 1_800_000, resumed_from: 0, .. }), "{outcome:?}");
    assert_eq!(std::fs::read(dir.path().join(SOURCE_FILE)).unwrap(), replaced);
}
