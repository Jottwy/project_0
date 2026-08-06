//! P0-3: exclusive lock on the world save, held for the life of the process.
//!
//! `./saves/world_{seed}.json` (`game_loop::resolve_save_path`) is derived ONLY from the seed —
//! no identity, no role. Nothing stopped two host-mode backends with the same seed (a stale
//! process left running, a careless relaunch with the default seed) from autosaving over each
//! other every ~3 minutes with no error. The lock makes that a fatal refusal-to-start instead of
//! a silent, irreversible loss.
//!
//! OS-level (`flock` on Unix, `LockFileEx` on Windows, via `fd-lock`), not a sentinel file: the
//! kernel releases the lock when this process's handles close — crash, `kill -9`, or a normal
//! exit all release it. A lock file whose mere EXISTENCE meant "locked" would strand the world
//! forever after any crash; this cannot.
//!
//! The lock lives on `<save_path>.lock`, a sibling of the save file, never the save file itself
//! — `save::SaveFile::save_to` already owns `<path>.tmp` + rename for the save; locking that
//! path too would tangle two unrelated mechanisms for no benefit.

use std::fs::{File, OpenOptions};
use std::io::Write;
use std::path::{Path, PathBuf};

use fd_lock::{RwLock, RwLockWriteGuard};
use log::error;

fn lock_path_for(save_path: &Path) -> PathBuf {
    let mut p = save_path.as_os_str().to_owned();
    p.push(".lock");
    PathBuf::from(p)
}

/// Opens (creating if needed) the lock file for `save_path`. Does not itself acquire the lock —
/// see `try_acquire`/`acquire_or_exit`. Split out so a caller can hold the `RwLock` for the
/// whole process lifetime as an ordinary local variable (its guard borrows from it).
pub fn open_for_locking(save_path: &Path) -> std::io::Result<RwLock<File>> {
    let lock_path = lock_path_for(save_path);
    if let Some(parent) = lock_path.parent() {
        if !parent.as_os_str().is_empty() {
            std::fs::create_dir_all(parent)?;
        }
    }
    let file = OpenOptions::new()
        .create(true)
        .truncate(false)
        .write(true)
        .read(true)
        .open(&lock_path)?;
    Ok(RwLock::new(file))
}

/// Attempts the exclusive lock. On success, stamps this process's pid into the file (truncate +
/// write) so a SECOND process that fails to acquire can still read who holds it — best-effort,
/// never fails the acquisition itself. Pure `Result`, no process exit: kept separate from
/// `acquire_or_exit` so the "does a second attempt on the same path fail" behavior is testable
/// without killing the test process.
pub fn try_acquire(lock: &mut RwLock<File>) -> std::io::Result<RwLockWriteGuard<'_, File>> {
    let mut guard = lock.try_write()?;
    guard.set_len(0)?;
    write!(guard, "{}", std::process::id())?;
    guard.flush()?;
    Ok(guard)
}

/// P0-3: acquire the lock or terminate the process. NEVER degrades silently and NEVER falls
/// back to an alternate path — the whole point is that a second writer must be a loud refusal,
/// not a quiet corruption three minutes later.
pub fn acquire_or_exit<'a>(
    lock: &'a mut RwLock<File>,
    save_path: &Path,
) -> RwLockWriteGuard<'a, File> {
    match try_acquire(lock) {
        Ok(guard) => guard,
        Err(e) => {
            let lock_path = lock_path_for(save_path);
            let held_by = std::fs::read_to_string(&lock_path)
                .ok()
                .map(|s| s.trim().to_string())
                .filter(|s| !s.is_empty())
                .unwrap_or_else(|| "unknown".into());
            eprintln!(
                "FATAL: cannot acquire exclusive lock on {} ({e}). Another backend (pid={held_by}) \
                 already owns this world's save at {} — refusing to start a second writer. Close \
                 it first, or use a different WORLD_SEED/SAVE_PATH.",
                lock_path.display(),
                save_path.display()
            );
            error!(
                "P0-3: world lock held by pid={held_by} at {}; exiting",
                lock_path.display()
            );
            std::process::exit(1);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn scratch_lock_path(name: &str) -> PathBuf {
        let mut p = std::env::temp_dir();
        p.push(format!(
            "backrooms_p0_3_lock_test_{name}_{}",
            std::process::id()
        ));
        p
    }

    #[test]
    fn a_second_lock_attempt_on_the_same_path_fails() {
        let save_path = scratch_lock_path("second_attempt");
        let _ = std::fs::remove_file(lock_path_for(&save_path));

        let mut first = open_for_locking(&save_path).expect("first open must succeed");
        let _first_guard = try_acquire(&mut first).expect("first lock must succeed");

        let mut second = open_for_locking(&save_path).expect("second open must succeed");
        let result = try_acquire(&mut second);
        assert!(
            result.is_err(),
            "a second process must never acquire a lock already held"
        );

        drop(_first_guard);
        let _ = std::fs::remove_file(lock_path_for(&save_path));
    }

    #[test]
    fn releasing_the_first_lock_lets_a_second_attempt_succeed() {
        // Contrapartida del test anterior: el candado NO se queda pegado tras liberarse — si
        // esto fallara, un reinicio limpio del mismo backend jamás podría re-arrancar.
        let save_path = scratch_lock_path("release_then_reacquire");
        let _ = std::fs::remove_file(lock_path_for(&save_path));

        let mut first = open_for_locking(&save_path).expect("first open must succeed");
        {
            let _guard = try_acquire(&mut first).expect("first lock must succeed");
        } // guard dropped here — releases the lock
        drop(first); // close the handle too, for good measure

        let mut second = open_for_locking(&save_path).expect("second open must succeed");
        let guard = try_acquire(&mut second);
        assert!(
            guard.is_ok(),
            "a released lock must be re-acquirable, got: {guard:?}"
        );

        drop(guard);
        let _ = std::fs::remove_file(lock_path_for(&save_path));
    }

    #[test]
    fn a_successful_lock_stamps_its_own_pid_into_the_file() {
        let save_path = scratch_lock_path("pid_stamp");
        let _ = std::fs::remove_file(lock_path_for(&save_path));

        let mut lock = open_for_locking(&save_path).expect("open must succeed");
        let guard = try_acquire(&mut lock).expect("lock must succeed");
        drop(guard);
        drop(lock);

        let contents = std::fs::read_to_string(lock_path_for(&save_path)).unwrap();
        assert_eq!(
            contents,
            std::process::id().to_string(),
            "the lock file must hold exactly this process's pid, for the next process's \
             diagnostic message"
        );

        let _ = std::fs::remove_file(lock_path_for(&save_path));
    }
}
