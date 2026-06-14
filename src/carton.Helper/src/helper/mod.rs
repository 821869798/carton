use serde::{Deserialize, Serialize};
use std::collections::VecDeque;
use std::error::Error;
use std::fs;
use std::path::PathBuf;
use std::process::{Child, Command};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use std::os::windows::process::CommandExt;
use windows_sys::Win32::Foundation::CloseHandle;
use windows_sys::Win32::System::Threading::{
    GetExitCodeProcess, OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION,
};

mod http;
mod logs;
mod process;

const REQUEST_FILE_ARG: &str = "--request-file";
const TOKEN_HEADER: &str = "X-Carton-Helper-Token";
const MAX_STARTUP_LOG_LINES: usize = 128;
const MAX_STARTUP_LOG_LINE_LENGTH: usize = 2048;
const CREATE_NO_WINDOW: u32 = 0x08000000;
const STILL_ACTIVE: u32 = 259;

type AppResult<T> = Result<T, Box<dyn Error + Send + Sync>>;

pub fn run(args: &[String]) -> AppResult<i32> {
    let Some(launch) = LaunchOptions::parse(args)? else {
        return Ok(0);
    };

    let runtime = Arc::new(HelperRuntime::new(launch));
    runtime.run()?;
    Ok(0)
}

#[derive(Clone)]
struct LaunchOptions {
    port: u16,
    token: String,
    parent_pid: u32,
}

impl LaunchOptions {
    fn parse(args: &[String]) -> AppResult<Option<Self>> {
        let mut port = 0_u16;
        let mut token = String::new();
        let mut parent_pid = 0_u32;
        let mut request_file_path: Option<PathBuf> = None;

        let mut i = 1;
        while i < args.len() {
            match args[i].as_str() {
                "--port" if i + 1 < args.len() => {
                    port = args[i + 1].parse().unwrap_or(0);
                    i += 1;
                }
                "--token" if i + 1 < args.len() => {
                    token = args[i + 1].clone();
                    i += 1;
                }
                "--parent-pid" if i + 1 < args.len() => {
                    parent_pid = args[i + 1].parse().unwrap_or(0);
                    i += 1;
                }
                REQUEST_FILE_ARG if i + 1 < args.len() => {
                    request_file_path = Some(PathBuf::from(&args[i + 1]));
                    i += 1;
                }
                _ => {}
            }

            i += 1;
        }

        if let Some(path) = request_file_path {
            let payload = match fs::read_to_string(&path) {
                Ok(payload) => payload,
                Err(_) => return Ok(None),
            };
            let _ = fs::remove_file(&path);

            let request: WindowsHelperLaunchRequest = serde_json::from_str(&payload)?;
            if request.port == 0
                || request.token.trim().is_empty()
                || !request_expires_in_future(request.expires_at_utc.as_deref())
            {
                return Ok(None);
            }

            return Ok(Some(Self {
                port: request.port,
                token: request.token,
                parent_pid: request.parent_pid,
            }));
        }

        if port == 0 || token.trim().is_empty() {
            return Ok(None);
        }

        Ok(Some(Self {
            port,
            token,
            parent_pid,
        }))
    }
}

struct HelperRuntime {
    launch: LaunchOptions,
    state: Arc<Mutex<HelperState>>,
    stop_signal_path: PathBuf,
    should_stop: AtomicBool,
}

struct HelperState {
    child: Option<Child>,
    last_known_pid: Option<u32>,
    last_known_exit_code: Option<i32>,
    last_known_error: Option<String>,
    last_api_address: Option<String>,
    last_api_secret: Option<String>,
    log_file: Option<Arc<Mutex<fs::File>>>,
    startup_logs: VecDeque<WindowsHelperStartupLogLine>,
    startup_log_session: u64,
    next_startup_log_sequence: u64,
    capture_startup_logs: bool,
}

impl HelperRuntime {
    fn new(launch: LaunchOptions) -> Self {
        let executable_dir = std::env::current_exe()
            .ok()
            .and_then(|path| path.parent().map(PathBuf::from))
            .unwrap_or_else(|| PathBuf::from("."));

        Self {
            launch,
            state: Arc::new(Mutex::new(HelperState {
                child: None,
                last_known_pid: None,
                last_known_exit_code: None,
                last_known_error: None,
                last_api_address: None,
                last_api_secret: None,
                log_file: None,
                startup_logs: VecDeque::with_capacity(MAX_STARTUP_LOG_LINES),
                startup_log_session: 0,
                next_startup_log_sequence: 1,
                capture_startup_logs: false,
            })),
            stop_signal_path: executable_dir.join(".carton-stop-signal"),
            should_stop: AtomicBool::new(false),
        }
    }

    fn run(self: Arc<Self>) -> AppResult<()> {
        let _ = fs::remove_file(&self.stop_signal_path);

        let server = tiny_http::Server::http(("127.0.0.1", self.launch.port))?;

        let watchdog_runtime = Arc::clone(&self);
        let watchdog = if self.launch.parent_pid > 0 {
            Some(thread::spawn(move || watchdog_runtime.parent_watchdog()))
        } else {
            None
        };

        let mut receive_error_delay = Duration::from_millis(50);
        while !self.should_stop.load(Ordering::SeqCst) {
            match server.recv_timeout(Duration::from_millis(100)) {
                Ok(Some(request)) => {
                    receive_error_delay = Duration::from_millis(50);
                    let _ = self.handle_request(request);
                }
                Ok(None) => {
                    receive_error_delay = Duration::from_millis(50);
                }
                Err(_) => {
                    thread::sleep(receive_error_delay);
                    let next_delay_ms = (receive_error_delay.as_millis() as u64 * 2).min(1000);
                    receive_error_delay = Duration::from_millis(next_delay_ms);
                }
            }
        }

        let _ = self.stop_sing_box(true, None);
        if let Some(handle) = watchdog {
            let _ = handle.join();
        }

        Ok(())
    }

    fn parent_watchdog(&self) {
        while !self.should_stop.load(Ordering::SeqCst) {
            if self.stop_signal_path.exists() {
                let _ = fs::remove_file(&self.stop_signal_path);
                let _ = self.stop_sing_box(true, None);
            }

            if !is_process_running(self.launch.parent_pid) {
                let _ = self.stop_sing_box(true, None);
                self.should_stop.store(true, Ordering::SeqCst);
                break;
            }

            thread::sleep(Duration::from_millis(500));
        }
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct WindowsHelperLaunchRequest {
    port: u16,
    token: String,
    parent_pid: u32,
    expires_at_utc: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct WindowsHelperStartRequest {
    sing_box_path: String,
    config_path: String,
    working_directory: String,
    log_path: String,
    result_file_path: String,
    api_address: String,
    api_secret: Option<String>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct WindowsHelperActionResponse {
    success: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pid: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    error: Option<String>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct WindowsHelperProcessStatusResponse {
    has_process: bool,
    is_running: bool,
    api_ready: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pid: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    exit_code: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    error: Option<String>,
    startup_log_gap: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    startup_logs: Option<Vec<WindowsHelperStartupLogLine>>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct WindowsHelperStartupLogLine {
    sequence: u64,
    message: String,
}

fn is_process_running(pid: u32) -> bool {
    if pid == 0 {
        return false;
    }

    unsafe {
        let handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
        if handle.is_null() {
            return false;
        }

        let mut exit_code = 0_u32;
        let success = GetExitCodeProcess(handle, &mut exit_code) != 0;
        let _ = CloseHandle(handle);
        success && exit_code == STILL_ACTIVE
    }
}

fn kill_process_tree(pid: u32) {
    if pid == 0 {
        return;
    }

    let _ = Command::new("taskkill.exe")
        .args(["/PID", &pid.to_string(), "/T", "/F"])
        .creation_flags(CREATE_NO_WINDOW)
        .status();
}

fn request_expires_in_future(expires_at_utc: Option<&str>) -> bool {
    let Some(expires_at_utc) = expires_at_utc else {
        return false;
    };

    let Some(expires_at_unix_seconds) = parse_datetime_offset_unix_seconds(expires_at_utc) else {
        return false;
    };

    let now_unix_seconds = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_secs() as i64)
        .unwrap_or(i64::MAX);

    expires_at_unix_seconds > now_unix_seconds
}

fn parse_datetime_offset_unix_seconds(value: &str) -> Option<i64> {
    let bytes = value.as_bytes();
    if bytes.len() < 20
        || bytes.get(4) != Some(&b'-')
        || bytes.get(7) != Some(&b'-')
        || !matches!(bytes.get(10), Some(b'T' | b't' | b' '))
        || bytes.get(13) != Some(&b':')
        || bytes.get(16) != Some(&b':')
    {
        return None;
    }

    let year = parse_decimal(&bytes[0..4])?;
    let month = parse_decimal(&bytes[5..7])?;
    let day = parse_decimal(&bytes[8..10])?;
    let hour = parse_decimal(&bytes[11..13])?;
    let minute = parse_decimal(&bytes[14..16])?;
    let second = parse_decimal(&bytes[17..19])?;

    if !(1..=12).contains(&month)
        || day < 1
        || day > days_in_month(year, month)
        || hour > 23
        || minute > 59
        || second > 60
    {
        return None;
    }

    let mut index = 19;
    if bytes.get(index) == Some(&b'.') {
        index += 1;
        while index < bytes.len() && bytes[index].is_ascii_digit() {
            index += 1;
        }
    }

    let offset_seconds = match bytes.get(index) {
        Some(b'Z' | b'z') if index + 1 == bytes.len() => 0,
        Some(b'+') | Some(b'-') if index + 6 == bytes.len() => {
            if bytes.get(index + 3) != Some(&b':') {
                return None;
            }

            let offset_hour = parse_decimal(&bytes[index + 1..index + 3])?;
            let offset_minute = parse_decimal(&bytes[index + 4..index + 6])?;
            if offset_hour > 23 || offset_minute > 59 {
                return None;
            }

            let sign = if bytes[index] == b'+' { 1 } else { -1 };
            sign * (offset_hour * 3600 + offset_minute * 60)
        }
        _ => return None,
    };

    let days = days_from_civil(year, month, day);
    Some(days * 86_400 + hour * 3600 + minute * 60 + second - offset_seconds)
}

fn parse_decimal(bytes: &[u8]) -> Option<i64> {
    let mut value = 0_i64;
    for byte in bytes {
        if !byte.is_ascii_digit() {
            return None;
        }
        value = value * 10 + i64::from(byte - b'0');
    }
    Some(value)
}

fn days_in_month(year: i64, month: i64) -> i64 {
    match month {
        1 | 3 | 5 | 7 | 8 | 10 | 12 => 31,
        4 | 6 | 9 | 11 => 30,
        2 if is_leap_year(year) => 29,
        2 => 28,
        _ => 0,
    }
}

fn is_leap_year(year: i64) -> bool {
    year % 4 == 0 && (year % 100 != 0 || year % 400 == 0)
}

fn days_from_civil(year: i64, month: i64, day: i64) -> i64 {
    let adjusted_year = year - if month <= 2 { 1 } else { 0 };
    let era = if adjusted_year >= 0 {
        adjusted_year
    } else {
        adjusted_year - 399
    } / 400;
    let year_of_era = adjusted_year - era * 400;
    let month_prime = month + if month > 2 { -3 } else { 9 };
    let day_of_year = (153 * month_prime + 2) / 5 + day - 1;
    let day_of_era = year_of_era * 365 + year_of_era / 4 - year_of_era / 100 + day_of_year;
    era * 146_097 + day_of_era - 719_468
}

#[cfg(test)]
mod tests {
    use super::parse_datetime_offset_unix_seconds;

    #[test]
    fn parses_dotnet_datetime_offset_with_fractional_seconds() {
        assert_eq!(
            parse_datetime_offset_unix_seconds("1970-01-01T00:00:00.0000000+00:00"),
            Some(0)
        );
        assert_eq!(
            parse_datetime_offset_unix_seconds("1970-01-01T01:00:00.1234567+01:00"),
            Some(0)
        );
        assert_eq!(
            parse_datetime_offset_unix_seconds("1970-01-01T00:00:00-01:00"),
            Some(3600)
        );
    }

    #[test]
    fn rejects_invalid_datetime_offsets() {
        assert_eq!(parse_datetime_offset_unix_seconds(""), None);
        assert_eq!(
            parse_datetime_offset_unix_seconds("2026-02-29T00:00:00+00:00"),
            None
        );
        assert_eq!(
            parse_datetime_offset_unix_seconds("2026-01-01T00:00:00"),
            None
        );
    }
}
