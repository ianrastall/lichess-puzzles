//! Download, resume and update checks against a local HTTP server.

mod support;

use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Duration;

use lichess_puzzles_core::source::{DownloadOutcome, SOURCE_FILE, Source, UpdateCheck};
use support::Server;

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
