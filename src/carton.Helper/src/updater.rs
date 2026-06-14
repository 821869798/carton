use std::error::Error;
use std::fs;
use std::io;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::thread;
use std::time::{Duration, Instant};

#[cfg(unix)]
use std::os::unix::fs::PermissionsExt;

#[cfg(windows)]
use std::os::windows::process::CommandExt;

const DEFAULT_WAIT_SECONDS: u64 = 30;

#[cfg(windows)]
const CREATE_NO_WINDOW: u32 = 0x08000000;
#[cfg(windows)]
const COPY_RETRY_SECONDS: u64 = 15;
#[cfg(windows)]
const ERROR_ACCESS_DENIED: i32 = 5;
#[cfg(windows)]
const ERROR_SHARING_VIOLATION: i32 = 32;
#[cfg(windows)]
const ERROR_LOCK_VIOLATION: i32 = 33;

type AppResult<T> = Result<T, Box<dyn Error + Send + Sync>>;

pub fn run(args: &[String]) -> AppResult<i32> {
    match run_inner(args) {
        Ok(()) => Ok(0),
        Err(error) => {
            let _ = fs::write(
                crate::temp_path("carton-helper-update-error.log"),
                error.to_string(),
            );
            Ok(1)
        }
    }
}

fn run_inner(args: &[String]) -> AppResult<()> {
    let options = UpdaterOptions::parse(args)?;
    if options.show_help {
        print_usage();
        return Ok(());
    }

    let archive_path = options
        .archive_path
        .as_ref()
        .ok_or("Archive path is missing.")?;
    let target_dir = options
        .target_dir
        .as_ref()
        .ok_or("Target directory is missing.")?;

    if !archive_path.is_file() {
        return Err(format!("Archive path does not exist: {}", archive_path.display()).into());
    }

    if !target_dir.is_dir() {
        return Err(format!("Target directory does not exist: {}", target_dir.display()).into());
    }

    wait_for_process_exit(options.process_id, options.wait_seconds);

    let staging_dir =
        std::env::temp_dir().join(format!("carton-update-stage-{}", std::process::id()));
    let _ = fs::remove_dir_all(&staging_dir);
    fs::create_dir_all(&staging_dir)?;

    let result = (|| -> AppResult<()> {
        extract_archive(archive_path, &staging_dir)?;
        let source_root = resolve_archive_root(&staging_dir)?;
        copy_dir(&source_root, target_dir)?;
        Ok(())
    })();

    let _ = fs::remove_dir_all(&staging_dir);
    result?;

    if let Some(restart) = options.restart_executable.as_ref() {
        let exe_path = target_dir.join(restart);
        if exe_path.is_file() {
            let mut command = Command::new(exe_path);
            command.current_dir(target_dir);
            #[cfg(windows)]
            command.creation_flags(CREATE_NO_WINDOW);
            let _ = command.spawn();
        }
    }

    Ok(())
}

fn print_usage() {
    println!(
        "carton-helper --carton-update --pid <pid> --archive <portable.zip|portable.tar.gz> --target <appDir> [--restart carton] [--wait-seconds 30]"
    );
}

#[derive(Default)]
struct UpdaterOptions {
    show_help: bool,
    process_id: Option<u32>,
    archive_path: Option<PathBuf>,
    target_dir: Option<PathBuf>,
    restart_executable: Option<String>,
    wait_seconds: u64,
}

impl UpdaterOptions {
    fn parse(args: &[String]) -> AppResult<Self> {
        let mut options = UpdaterOptions {
            wait_seconds: DEFAULT_WAIT_SECONDS,
            ..Default::default()
        };

        let mut i = 0;
        while i < args.len() {
            match args[i].as_str() {
                "-h" | "--help" => options.show_help = true,
                "--pid" => options.process_id = Some(read_value(args, &mut i, "--pid")?.parse()?),
                "--archive" => {
                    options.archive_path =
                        Some(PathBuf::from(read_value(args, &mut i, "--archive")?))
                }
                "--target" => {
                    options.target_dir = Some(PathBuf::from(read_value(args, &mut i, "--target")?))
                }
                "--restart" => {
                    options.restart_executable = Some(read_value(args, &mut i, "--restart")?)
                }
                "--wait-seconds" => {
                    options.wait_seconds = read_value(args, &mut i, "--wait-seconds")?.parse()?
                }
                _ => {}
            }

            i += 1;
        }

        Ok(options)
    }
}

fn read_value(args: &[String], index: &mut usize, name: &str) -> AppResult<String> {
    if *index + 1 >= args.len() {
        return Err(format!("Missing value for {name}.").into());
    }

    *index += 1;
    Ok(args[*index].clone())
}

fn wait_for_process_exit(process_id: Option<u32>, wait_seconds: u64) {
    let Some(pid) = process_id else {
        return;
    };

    if pid == 0 {
        return;
    }

    let deadline = Instant::now() + Duration::from_secs(wait_seconds.max(1));
    while Instant::now() < deadline {
        if !is_process_running(pid) {
            return;
        }

        thread::sleep(Duration::from_millis(200));
    }

    kill_process_tree(pid);

    let kill_deadline = Instant::now() + Duration::from_secs(5);
    while Instant::now() < kill_deadline {
        if !is_process_running(pid) {
            return;
        }

        thread::sleep(Duration::from_millis(100));
    }
}

#[cfg(windows)]
fn is_process_running(pid: u32) -> bool {
    let output = Command::new("tasklist.exe")
        .args(["/FI", &format!("PID eq {pid}"), "/NH"])
        .creation_flags(CREATE_NO_WINDOW)
        .output();

    output
        .ok()
        .map(|output| String::from_utf8_lossy(&output.stdout).contains(&pid.to_string()))
        .unwrap_or(false)
}

#[cfg(unix)]
fn is_process_running(pid: u32) -> bool {
    Command::new("kill")
        .args(["-0", &pid.to_string()])
        .status()
        .map(|status| status.success())
        .unwrap_or(false)
}

#[cfg(windows)]
fn kill_process_tree(pid: u32) {
    let _ = Command::new("taskkill.exe")
        .args(["/PID", &pid.to_string(), "/T", "/F"])
        .creation_flags(CREATE_NO_WINDOW)
        .status();
}

#[cfg(unix)]
fn kill_process_tree(pid: u32) {
    let mut processes = collect_descendant_processes(pid);
    processes.push(pid);

    for process_id in processes.iter().rev() {
        signal_process(*process_id, "-TERM");
    }

    thread::sleep(Duration::from_millis(500));

    for process_id in processes.iter().rev() {
        signal_process(*process_id, "-KILL");
    }
}

#[cfg(unix)]
fn signal_process(pid: u32, signal: &str) {
    let _ = Command::new("kill")
        .args([signal, &pid.to_string()])
        .status();
}

#[cfg(unix)]
fn collect_descendant_processes(root_pid: u32) -> Vec<u32> {
    let output = Command::new("ps").args(["-eo", "pid=,ppid="]).output();
    let Ok(output) = output else {
        return Vec::new();
    };

    let process_pairs: Vec<(u32, u32)> = String::from_utf8_lossy(&output.stdout)
        .lines()
        .filter_map(|line| {
            let mut parts = line.split_whitespace();
            let pid = parts.next()?.parse::<u32>().ok()?;
            let parent_pid = parts.next()?.parse::<u32>().ok()?;
            Some((pid, parent_pid))
        })
        .collect();

    let mut descendants = Vec::new();
    collect_descendants(root_pid, &process_pairs, &mut descendants);
    descendants
}

#[cfg(unix)]
fn collect_descendants(parent_pid: u32, process_pairs: &[(u32, u32)], descendants: &mut Vec<u32>) {
    for (pid, ppid) in process_pairs {
        if *ppid == parent_pid {
            descendants.push(*pid);
            collect_descendants(*pid, process_pairs, descendants);
        }
    }
}

#[cfg(windows)]
fn extract_archive(archive_path: &Path, staging_dir: &Path) -> AppResult<()> {
    let file = fs::File::open(archive_path)?;
    let mut archive = zip::ZipArchive::new(file)?;

    for i in 0..archive.len() {
        let mut entry = archive.by_index(i)?;
        let Some(enclosed_name) = entry.enclosed_name() else {
            continue;
        };

        let destination = staging_dir.join(enclosed_name);
        if entry.is_dir() {
            fs::create_dir_all(&destination)?;
            continue;
        }

        if let Some(parent) = destination.parent() {
            fs::create_dir_all(parent)?;
        }

        let mut output = fs::File::create(destination)?;
        io::copy(&mut entry, &mut output)?;
    }

    Ok(())
}

#[cfg(unix)]
fn extract_archive(archive_path: &Path, staging_dir: &Path) -> AppResult<()> {
    let file = fs::File::open(archive_path)?;
    let gzip = flate2::read::GzDecoder::new(file);
    let mut archive = tar::Archive::new(gzip);
    archive.unpack(staging_dir)?;
    Ok(())
}

fn resolve_archive_root(staging_dir: &Path) -> AppResult<PathBuf> {
    let entries = fs::read_dir(staging_dir)?.collect::<Result<Vec<_>, io::Error>>()?;
    if entries.len() == 1 {
        let path = entries[0].path();
        if path.is_dir() {
            return Ok(path);
        }
    }

    Ok(staging_dir.to_path_buf())
}

fn copy_dir(source: &Path, target: &Path) -> AppResult<()> {
    fs::create_dir_all(target)?;

    for entry in fs::read_dir(source)? {
        let entry = entry?;
        let source_path = entry.path();
        let target_path = target.join(entry.file_name());
        let metadata = entry.metadata()?;

        if metadata.is_dir() {
            copy_dir(&source_path, &target_path)?;
        } else if metadata.is_file() {
            if let Some(parent) = target_path.parent() {
                fs::create_dir_all(parent)?;
            }

            copy_file(&source_path, &target_path)?;
            copy_permissions(&metadata, &target_path);
        }
    }

    Ok(())
}

#[cfg(windows)]
fn copy_file(source: &Path, target: &Path) -> AppResult<()> {
    let deadline = Instant::now() + Duration::from_secs(COPY_RETRY_SECONDS);
    loop {
        match fs::copy(source, target) {
            Ok(_) => return Ok(()),
            Err(error) if is_retryable_copy_error(&error) && Instant::now() < deadline => {
                thread::sleep(Duration::from_millis(150));
            }
            Err(error) => {
                return Err(format!(
                    "Failed to copy '{}' to '{}': {error}",
                    source.display(),
                    target.display()
                )
                .into())
            }
        }
    }
}

#[cfg(windows)]
fn is_retryable_copy_error(error: &io::Error) -> bool {
    matches!(
        error.raw_os_error(),
        Some(ERROR_ACCESS_DENIED | ERROR_SHARING_VIOLATION | ERROR_LOCK_VIOLATION)
    )
}

#[cfg(not(windows))]
fn copy_file(source: &Path, target: &Path) -> AppResult<()> {
    fs::copy(source, target)?;
    Ok(())
}

#[cfg(unix)]
fn copy_permissions(metadata: &fs::Metadata, target_path: &Path) {
    let mode = metadata.permissions().mode();
    let _ = fs::set_permissions(target_path, fs::Permissions::from_mode(mode));
}

#[cfg(not(unix))]
fn copy_permissions(_metadata: &fs::Metadata, _target_path: &Path) {}
