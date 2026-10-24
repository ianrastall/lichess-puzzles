//! A local HTTP server for download tests: one file with an ETag, honouring `Range` with
//! `If-Range`.

#![allow(dead_code)]

use std::io::{BufRead, BufReader, Write};
use std::net::{TcpListener, TcpStream};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};

/// Serves one file with an ETag, honouring `Range` with `If-Range`. Each response can be cut
/// short after a number of body bytes to simulate a dropped connection.
#[derive(Clone)]
pub struct Server {
    pub url: String,
    pub state: Arc<Mutex<ServerState>>,
    pub requests: Arc<AtomicUsize>,
}

pub struct ServerState {
    pub body: Vec<u8>,
    pub etag: String,
    /// Body bytes to send before closing, per upcoming GET response.
    pub cut_after: Vec<usize>,
    pub ranges: Vec<Option<String>>,
}

impl Server {
    pub fn start(body: Vec<u8>, etag: &str) -> Server {
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
