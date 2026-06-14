use super::{
    HelperRuntime, WindowsHelperStartupLogLine, MAX_STARTUP_LOG_LINES, MAX_STARTUP_LOG_LINE_LENGTH,
};
use std::fs;
use std::io::{BufRead, BufReader, Write};
use std::sync::{Arc, Mutex};
use std::thread;

impl HelperRuntime {
    pub(super) fn spawn_log_pump<T>(
        &self,
        stream: T,
        log_file: Option<Arc<Mutex<fs::File>>>,
        startup_log_session: u64,
    ) where
        T: std::io::Read + Send + 'static,
    {
        let state = Arc::clone(&self.state);
        thread::spawn(move || {
            let mut reader = BufReader::new(stream);
            let mut line_bytes = Vec::new();
            while read_lossy_line(&mut reader, &mut line_bytes) {
                trim_line_ending_bytes(&mut line_bytes);
                let line = String::from_utf8_lossy(&line_bytes);
                let normalized = strip_terminal_decorations(&line).trim().to_string();
                if normalized.is_empty() {
                    continue;
                }

                if let Some(file) = log_file.as_ref() {
                    if let Ok(mut file) = file.lock() {
                        let _ = writeln!(file, "{normalized}");
                        let _ = file.flush();
                    }
                }

                if let Ok(mut runtime_state) = state.lock() {
                    if !runtime_state.capture_startup_logs
                        || runtime_state.startup_log_session != startup_log_session
                    {
                        continue;
                    }

                    let mut message = normalized.clone();
                    if message.len() > MAX_STARTUP_LOG_LINE_LENGTH {
                        message.truncate(MAX_STARTUP_LOG_LINE_LENGTH);
                        message.push_str("...");
                    }

                    let sequence = runtime_state.next_startup_log_sequence;
                    runtime_state.next_startup_log_sequence += 1;
                    runtime_state
                        .startup_logs
                        .push_back(WindowsHelperStartupLogLine { sequence, message });

                    while runtime_state.startup_logs.len() > MAX_STARTUP_LOG_LINES {
                        runtime_state.startup_logs.pop_front();
                    }
                }
            }
        });
    }

    pub(super) fn reset_startup_logs(&self) -> u64 {
        let mut state = self.state.lock().unwrap();
        state.startup_log_session = state.startup_log_session.wrapping_add(1);
        state.startup_logs.clear();
        state.next_startup_log_sequence = 1;
        state.capture_startup_logs = true;
        state.startup_log_session
    }

    pub(super) fn startup_logs_after(
        &self,
        after: u64,
    ) -> (Option<Vec<WindowsHelperStartupLogLine>>, bool) {
        let state = self.state.lock().unwrap();
        if state.startup_logs.is_empty() {
            return (None, false);
        }

        let oldest = state
            .startup_logs
            .front()
            .map(|line| line.sequence)
            .unwrap_or(0);
        let gap = oldest > 1 && after < oldest.saturating_sub(1);
        let logs: Vec<_> = state
            .startup_logs
            .iter()
            .filter(|line| line.sequence > after)
            .cloned()
            .collect();

        (if logs.is_empty() { None } else { Some(logs) }, gap)
    }

    pub(super) fn recent_startup_log_snapshot(&self, max_lines: usize) -> Option<String> {
        let state = self.state.lock().unwrap();
        self.recent_startup_log_snapshot_locked(&state, max_lines)
    }

    pub(super) fn recent_startup_log_snapshot_locked(
        &self,
        state: &super::HelperState,
        max_lines: usize,
    ) -> Option<String> {
        if state.startup_logs.is_empty() {
            return None;
        }

        let skip = state.startup_logs.len().saturating_sub(max_lines.max(1));
        let messages: Vec<_> = state
            .startup_logs
            .iter()
            .skip(skip)
            .map(|line| line.message.as_str())
            .collect();

        Some(messages.join(" | "))
    }
}

fn read_lossy_line<R: BufRead>(reader: &mut R, buffer: &mut Vec<u8>) -> bool {
    buffer.clear();
    loop {
        match reader.read_until(b'\n', buffer) {
            Ok(0) => return !buffer.is_empty(),
            Ok(_) => return true,
            Err(error) if error.kind() == std::io::ErrorKind::Interrupted => continue,
            Err(_) => return false,
        }
    }
}

fn trim_line_ending_bytes(buffer: &mut Vec<u8>) {
    if buffer.last() == Some(&b'\n') {
        buffer.pop();
    }
    if buffer.last() == Some(&b'\r') {
        buffer.pop();
    }
}

fn strip_terminal_decorations(message: &str) -> String {
    let mut output = String::with_capacity(message.len());
    let chars: Vec<char> = message.chars().collect();
    let mut i = 0;
    while i < chars.len() {
        let ch = chars[i];
        if ch == '\u{1b}' && i + 1 < chars.len() && chars[i + 1] == '[' {
            if let Some(end) = find_csi_terminator(&chars, i + 2) {
                i = end + 1;
                continue;
            }
        }

        if ch == '[' && i + 1 < chars.len() && is_csi_parameter_char(chars[i + 1]) {
            if let Some(end) = find_csi_terminator(&chars, i + 1) {
                i = end + 1;
                continue;
            }
        }

        if !ch.is_control() || ch == '\r' || ch == '\n' || ch == '\t' {
            output.push(ch);
        }
        i += 1;
    }

    output
}

fn find_csi_terminator(chars: &[char], start: usize) -> Option<usize> {
    for (index, ch) in chars.iter().enumerate().skip(start) {
        if ch.is_ascii_alphabetic() {
            return Some(index);
        }

        if !is_csi_parameter_char(*ch) {
            return None;
        }
    }

    None
}

fn is_csi_parameter_char(ch: char) -> bool {
    ch.is_ascii_digit() || ch == ';'
}

#[cfg(test)]
mod tests {
    use super::{read_lossy_line, trim_line_ending_bytes};
    use std::io::BufReader;

    #[test]
    fn reads_lines_with_invalid_utf8_without_stopping() {
        let input = b"good\nbad-\xff-line\nnext\n";
        let mut reader = BufReader::new(&input[..]);
        let mut buffer = Vec::new();

        assert!(read_lossy_line(&mut reader, &mut buffer));
        trim_line_ending_bytes(&mut buffer);
        assert_eq!(String::from_utf8_lossy(&buffer), "good");

        assert!(read_lossy_line(&mut reader, &mut buffer));
        trim_line_ending_bytes(&mut buffer);
        assert_eq!(String::from_utf8_lossy(&buffer), "bad-\u{fffd}-line");

        assert!(read_lossy_line(&mut reader, &mut buffer));
        trim_line_ending_bytes(&mut buffer);
        assert_eq!(String::from_utf8_lossy(&buffer), "next");

        assert!(!read_lossy_line(&mut reader, &mut buffer));
    }
}
