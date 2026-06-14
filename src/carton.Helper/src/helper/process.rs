use super::{
    http, kill_process_tree, HelperRuntime, WindowsHelperActionResponse,
    WindowsHelperProcessStatusResponse, WindowsHelperStartRequest, CREATE_NO_WINDOW,
};
use std::fs::{self, OpenOptions};
use std::io;
use std::os::windows::ffi::OsStrExt;
use std::process::{Command, Stdio};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{Duration, Instant};

use std::os::windows::process::CommandExt;
use windows_sys::Win32::Storage::FileSystem::{
    MoveFileExW, MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH,
};

impl HelperRuntime {
    pub(super) fn start_sing_box(
        &self,
        request: WindowsHelperStartRequest,
    ) -> WindowsHelperActionResponse {
        let startup_log_session = self.reset_startup_logs();

        let response = if !fs::metadata(&request.sing_box_path)
            .map(|metadata| metadata.is_file())
            .unwrap_or(false)
        {
            let response = WindowsHelperActionResponse {
                success: false,
                pid: None,
                error: Some(format!(
                    "sing-box binary not found: {}",
                    request.sing_box_path
                )),
            };
            self.remember_start_failure(&request, &response);
            response
        } else if !fs::metadata(&request.config_path)
            .map(|metadata| metadata.is_file())
            .unwrap_or(false)
        {
            let response = WindowsHelperActionResponse {
                success: false,
                pid: None,
                error: Some(format!("config file not found: {}", request.config_path)),
            };
            self.remember_start_failure(&request, &response);
            response
        } else {
            self.start_sing_box_process(&request, startup_log_session)
        };

        self.write_response_file(&request.result_file_path, &response);
        response
    }

    fn start_sing_box_process(
        &self,
        request: &WindowsHelperStartRequest,
        startup_log_session: u64,
    ) -> WindowsHelperActionResponse {
        let _ = self.stop_sing_box(true, None);

        let log_file = if request.log_path.trim().is_empty() {
            None
        } else {
            if let Some(parent) = std::path::Path::new(&request.log_path).parent() {
                let _ = fs::create_dir_all(parent);
            }
            match OpenOptions::new()
                .create(true)
                .append(true)
                .open(&request.log_path)
            {
                Ok(file) => Some(Arc::new(Mutex::new(file))),
                Err(error) => {
                    return WindowsHelperActionResponse {
                        success: false,
                        pid: None,
                        error: Some(error.to_string()),
                    }
                }
            }
        };

        let mut command = Command::new(&request.sing_box_path);
        command
            .arg("run")
            .arg("-c")
            .arg(&request.config_path)
            .current_dir(&request.working_directory)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .creation_flags(CREATE_NO_WINDOW);

        let mut child = match command.spawn() {
            Ok(child) => child,
            Err(error) => {
                return WindowsHelperActionResponse {
                    success: false,
                    pid: None,
                    error: Some(error.to_string()),
                }
            }
        };

        let pid = child.id();
        if let Some(stdout) = child.stdout.take() {
            self.spawn_log_pump(stdout, log_file.clone(), startup_log_session);
        }
        if let Some(stderr) = child.stderr.take() {
            self.spawn_log_pump(stderr, log_file.clone(), startup_log_session);
        }

        {
            let mut state = self.state.lock().unwrap();
            state.child = Some(child);
            state.log_file = log_file;
            state.startup_log_session = startup_log_session;
            state.last_known_pid = Some(pid);
            state.last_known_exit_code = None;
            state.last_known_error = None;
            state.last_api_address = Some(request.api_address.clone());
            state.last_api_secret = request.api_secret.clone();
            state.capture_startup_logs = true;
        }

        if let Some(exit_code) = self.wait_for_early_exit(Duration::from_millis(300)) {
            let recent_log = self.recent_startup_log_snapshot(10);
            let error =
                recent_log.unwrap_or_else(|| format!("sing-box exited with code {exit_code}"));
            return WindowsHelperActionResponse {
                success: false,
                pid: None,
                error: Some(error),
            };
        }

        WindowsHelperActionResponse {
            success: true,
            pid: Some(pid),
            error: None,
        }
    }

    pub(super) fn stop_sing_box(
        &self,
        force: bool,
        fallback_pid: Option<u32>,
    ) -> WindowsHelperActionResponse {
        let child = {
            let mut state = self.state.lock().unwrap();
            state.capture_startup_logs = false;
            state.log_file = None;
            state.child.take()
        };

        if let Some(mut child) = child {
            let pid = child.id();
            kill_process_tree(pid);
            let deadline = Instant::now() + Duration::from_millis(if force { 1500 } else { 3000 });
            while Instant::now() < deadline {
                match child.try_wait() {
                    Ok(Some(status)) => {
                        let mut state = self.state.lock().unwrap();
                        state.last_known_pid = Some(pid);
                        state.last_known_exit_code = status.code();
                        return WindowsHelperActionResponse {
                            success: true,
                            pid: None,
                            error: None,
                        };
                    }
                    Ok(None) => thread::sleep(Duration::from_millis(50)),
                    Err(error) => {
                        return WindowsHelperActionResponse {
                            success: false,
                            pid: None,
                            error: Some(error.to_string()),
                        }
                    }
                }
            }

            return WindowsHelperActionResponse {
                success: false,
                pid: None,
                error: Some(format!("sing-box process {pid} did not exit after kill")),
            };
        }

        if let Some(pid) = fallback_pid {
            kill_process_tree(pid);
            let mut state = self.state.lock().unwrap();
            state.last_known_pid = Some(pid);
            state.last_known_exit_code = None;
        }

        WindowsHelperActionResponse {
            success: true,
            pid: None,
            error: None,
        }
    }

    pub(super) fn process_status(
        &self,
        after_startup_log_sequence: Option<u64>,
    ) -> Result<WindowsHelperProcessStatusResponse, Box<dyn std::error::Error + Send + Sync>> {
        let mut pid = None;
        let mut exit_code = None;
        let mut error = None;
        let mut has_process = false;
        let mut is_running = false;
        let mut api_address = None;
        let mut api_secret = None;

        {
            let mut state = self.state.lock().unwrap();
            if let Some(child) = state.child.as_mut() {
                match child.try_wait()? {
                    Some(status) => {
                        state.last_known_pid = Some(child.id());
                        state.last_known_exit_code = status.code();
                        if state.last_known_error.is_none() {
                            state.last_known_error = self
                                .recent_startup_log_snapshot_locked(&state, 12)
                                .or_else(|| {
                                    Some(format!(
                                        "sing-box exited with code {}",
                                        status.code().unwrap_or(-1)
                                    ))
                                });
                        }
                        state.child = None;
                    }
                    None => {
                        pid = Some(child.id());
                        has_process = true;
                        is_running = true;
                        error = state.last_known_error.clone();
                        api_address = state.last_api_address.clone();
                        api_secret = state.last_api_secret.clone();
                    }
                }
            }

            if !is_running {
                pid = state.last_known_pid;
                exit_code = state.last_known_exit_code;
                error = state.last_known_error.clone();
                has_process = state.last_known_pid.is_some() || state.last_known_error.is_some();
                api_address = state.last_api_address.clone();
                api_secret = state.last_api_secret.clone();
            }
        }

        let api_ready = is_running && http::is_api_ready(api_address, api_secret);
        if api_ready {
            let mut state = self.state.lock().unwrap();
            state.capture_startup_logs = false;
        }

        let (startup_logs, startup_log_gap) = after_startup_log_sequence
            .map(|after| self.startup_logs_after(after))
            .unwrap_or((None, false));

        Ok(WindowsHelperProcessStatusResponse {
            has_process,
            is_running,
            api_ready,
            pid,
            exit_code,
            error,
            startup_log_gap,
            startup_logs,
        })
    }

    fn wait_for_early_exit(&self, timeout: Duration) -> Option<i32> {
        let deadline = Instant::now() + timeout;
        while Instant::now() < deadline {
            let mut state = self.state.lock().unwrap();
            if let Some(child) = state.child.as_mut() {
                if let Ok(Some(status)) = child.try_wait() {
                    let exit_code = status.code().unwrap_or(-1);
                    state.last_known_exit_code = Some(exit_code);
                    state.last_known_error = self
                        .recent_startup_log_snapshot_locked(&state, 10)
                        .or_else(|| Some(format!("sing-box exited with code {exit_code}")));
                    state.child = None;
                    return Some(exit_code);
                }
            }
            drop(state);
            thread::sleep(Duration::from_millis(20));
        }

        None
    }

    fn remember_start_failure(
        &self,
        request: &WindowsHelperStartRequest,
        response: &WindowsHelperActionResponse,
    ) {
        let mut state = self.state.lock().unwrap();
        state.last_api_address = Some(request.api_address.clone());
        state.last_api_secret = request.api_secret.clone();
        state.last_known_pid = None;
        state.last_known_exit_code = None;
        state.last_known_error = response.error.clone();
        state.capture_startup_logs = false;
    }

    fn write_response_file(&self, path: &str, response: &WindowsHelperActionResponse) {
        if path.trim().is_empty() {
            return;
        }

        let result = (|| -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
            if let Some(parent) = std::path::Path::new(path).parent() {
                fs::create_dir_all(parent)?;
            }

            let temp_path = format!("{path}.{}.tmp", std::process::id());
            fs::write(&temp_path, serde_json::to_vec(response)?)?;
            replace_file(std::path::Path::new(&temp_path), std::path::Path::new(path))?;
            Ok(())
        })();
        let _ = result;
    }
}

fn replace_file(source: &std::path::Path, destination: &std::path::Path) -> io::Result<()> {
    let source_wide = path_to_wide(source);
    let destination_wide = path_to_wide(destination);
    let result = unsafe {
        MoveFileExW(
            source_wide.as_ptr(),
            destination_wide.as_ptr(),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
        )
    };
    if result == 0 {
        Err(io::Error::last_os_error())
    } else {
        Ok(())
    }
}

fn path_to_wide(path: &std::path::Path) -> Vec<u16> {
    path.as_os_str().encode_wide().chain(Some(0)).collect()
}
