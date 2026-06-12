//! Build script: capture build identity (git hash + build timestamp) into
//! compile-time env vars so the running backend can report exactly which build
//! it is. Resolves the "is Host running a stale backend?" ambiguity.

use std::process::Command;
use std::time::{SystemTime, UNIX_EPOCH};

fn main() {
    // Short git hash (best-effort; "unknown" if git is unavailable).
    let git_hash = Command::new("git")
        .args(["rev-parse", "--short", "HEAD"])
        .output()
        .ok()
        .filter(|o| o.status.success())
        .map(|o| String::from_utf8_lossy(&o.stdout).trim().to_string())
        .filter(|s| !s.is_empty())
        .unwrap_or_else(|| "unknown".to_string());
    println!("cargo:rustc-env=BACKROOMS_GIT_HASH={git_hash}");

    // Build timestamp (unix seconds). Stable input → reproducible per build.
    let build_ts = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0);
    println!("cargo:rustc-env=BACKROOMS_BUILD_TIMESTAMP={build_ts}");

    // Rebuild identity when HEAD moves so the hash stays current.
    println!("cargo:rerun-if-changed=../.git/HEAD");
    println!("cargo:rerun-if-changed=build.rs");
}
