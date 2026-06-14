#![cfg_attr(all(windows, not(debug_assertions)), windows_subsystem = "windows")]

use std::env;
use std::error::Error;
use std::fs;
use std::path::PathBuf;

mod updater;

#[cfg(windows)]
mod helper;

const ELEVATED_HELPER_ARG: &str = "--carton-elevated-helper";
const UPDATE_ARG: &str = "--carton-update";

type AppResult<T> = Result<T, Box<dyn Error + Send + Sync>>;

fn main() {
    let code = match run() {
        Ok(code) => code,
        Err(error) => {
            write_error_log(&error.to_string());
            1
        }
    };

    std::process::exit(code);
}

fn run() -> AppResult<i32> {
    let args: Vec<String> = env::args().skip(1).collect();
    let Some(mode) = args.first() else {
        return Ok(2);
    };

    if mode.eq_ignore_ascii_case(ELEVATED_HELPER_ARG) {
        #[cfg(windows)]
        {
            return helper::run(&args);
        }

        #[cfg(not(windows))]
        {
            return Ok(2);
        }
    }

    if mode.eq_ignore_ascii_case(UPDATE_ARG) {
        return updater::run(&args[1..]);
    }

    Ok(2)
}

fn write_error_log(message: &str) {
    let path = env::temp_dir().join("carton-helper-error.log");
    let _ = fs::write(path, message);
}

fn temp_path(file_name: &str) -> PathBuf {
    env::temp_dir().join(file_name)
}
