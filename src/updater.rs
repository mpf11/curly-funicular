// Once-per-day background check against GitHub releases. Spawned as a detached
// thread from main so network I/O never touches the message loop. Replacement
// takes effect on the next launch — no in-process restart.

use std::fs;
use std::path::PathBuf;
use std::time::{Duration, SystemTime};

const CHECK_INTERVAL_SECS: u64 = 24 * 60 * 60;

pub fn spawn_background_check() {
    std::thread::spawn(|| {
        if !should_check() {
            return;
        }
        let _ = try_update();
        let _ = write_stamp();
    });
}

fn stamp_path() -> Option<PathBuf> {
    let base = std::env::var_os("LOCALAPPDATA")?;
    let dir = PathBuf::from(base).join("WheelSwitcher");
    fs::create_dir_all(&dir).ok()?;
    Some(dir.join("last_update_check"))
}

fn should_check() -> bool {
    let Some(p) = stamp_path() else { return false };
    let Ok(meta) = fs::metadata(&p) else { return true };
    let Ok(mtime) = meta.modified() else { return true };
    SystemTime::now()
        .duration_since(mtime)
        .unwrap_or_default()
        > Duration::from_secs(CHECK_INTERVAL_SECS)
}

fn write_stamp() -> std::io::Result<()> {
    if let Some(p) = stamp_path() {
        fs::write(p, b"")?;
    }
    Ok(())
}

fn try_update() -> Result<(), Box<dyn std::error::Error>> {
    self_update::backends::github::Update::configure()
        .repo_owner("mpf11")
        .repo_name("wheel_switcher")
        .bin_name("wheel_switcher")
        .target("x86_64-pc-windows-msvc")
        .current_version(env!("CARGO_PKG_VERSION"))
        .no_confirm(true)
        .show_download_progress(false)
        .show_output(false)
        .build()?
        .update()?;
    Ok(())
}
