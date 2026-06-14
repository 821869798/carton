use super::{HelperRuntime, WindowsHelperStartRequest, TOKEN_HEADER};
use serde::Serialize;
use std::collections::HashMap;
use std::error::Error;
use std::sync::atomic::Ordering;
use tiny_http::{Header, Method, Request, Response, StatusCode};

type AppResult<T> = Result<T, Box<dyn Error + Send + Sync>>;

impl HelperRuntime {
    pub(super) fn handle_request(&self, mut request: Request) -> AppResult<()> {
        let authorized = request
            .headers()
            .iter()
            .find(|header| header.field.equiv(TOKEN_HEADER))
            .map(|header| header.value.as_str() == self.launch.token)
            .unwrap_or(false);
        if !authorized {
            return respond_text(request, 401, "unauthorized");
        }

        let method = request.method().clone();
        let (path, query) = parse_target(request.url());
        match path.as_str() {
            "ping" => respond_text(request, 200, &self.launch.token),
            "status" => {
                let after = query
                    .get("afterStartupLogSequence")
                    .and_then(|value| value.parse::<u64>().ok());
                match self.process_status(after) {
                    Ok(status) => respond_json(request, 200, &status),
                    Err(error) => respond_text(request, 500, &error.to_string()),
                }
            }
            "start" => {
                if method != Method::Post {
                    return respond_text(request, 405, "method not allowed");
                }

                let mut body = Vec::new();
                request.as_reader().read_to_end(&mut body)?;
                let Ok(start_request) = serde_json::from_slice::<WindowsHelperStartRequest>(&body)
                else {
                    return respond_text(
                        request,
                        400,
                        r#"{"success":false,"error":"invalid payload"}"#,
                    );
                };

                respond_json(request, 200, &self.start_sing_box(start_request))
            }
            "stop" => {
                let force = query
                    .get("force")
                    .map(|value| value == "1")
                    .unwrap_or(false);
                let pid = query.get("pid").and_then(|value| value.parse::<u32>().ok());
                respond_json(request, 200, &self.stop_sing_box(force, pid))
            }
            "shutdown" => {
                let response = self.stop_sing_box(true, None);
                self.should_stop.store(true, Ordering::SeqCst);
                respond_json(request, 200, &response)
            }
            _ => respond_text(request, 404, "not found"),
        }
    }
}

fn parse_target(target: &str) -> (String, HashMap<String, String>) {
    let (path, query_text) = target.split_once('?').unwrap_or((target, ""));
    let mut query = HashMap::new();
    for part in query_text.split('&').filter(|part| !part.is_empty()) {
        let (name, value) = part.split_once('=').unwrap_or((part, ""));
        query.insert(decode_query_component(name), decode_query_component(value));
    }

    (path.trim_matches('/').to_ascii_lowercase(), query)
}

fn decode_query_component(value: &str) -> String {
    percent_decode(&value.replace('+', " "))
}

fn percent_decode(value: &str) -> String {
    let bytes = value.as_bytes();
    let mut output = Vec::with_capacity(bytes.len());
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == b'%' && i + 2 < bytes.len() {
            if let (Some(high), Some(low)) = (hex_nibble(bytes[i + 1]), hex_nibble(bytes[i + 2])) {
                output.push(high << 4 | low);
                i += 3;
                continue;
            }
        }

        output.push(bytes[i]);
        i += 1;
    }

    String::from_utf8_lossy(&output).into_owned()
}

fn hex_nibble(byte: u8) -> Option<u8> {
    match byte {
        b'0'..=b'9' => Some(byte - b'0'),
        b'a'..=b'f' => Some(byte - b'a' + 10),
        b'A'..=b'F' => Some(byte - b'A' + 10),
        _ => None,
    }
}

fn respond_text(request: Request, status: u16, body: &str) -> AppResult<()> {
    let response = Response::from_string(body.to_string())
        .with_status_code(StatusCode(status))
        .with_header(content_type("text/plain; charset=utf-8")?);
    request.respond(response)?;
    Ok(())
}

fn respond_json<T: Serialize>(request: Request, status: u16, value: &T) -> AppResult<()> {
    let body = serde_json::to_vec(value)?;
    let response = Response::from_data(body)
        .with_status_code(StatusCode(status))
        .with_header(content_type("application/json; charset=utf-8")?);
    request.respond(response)?;
    Ok(())
}

fn content_type(value: &str) -> AppResult<Header> {
    Header::from_bytes("Content-Type".as_bytes(), value.as_bytes())
        .map_err(|_| "invalid content-type header".into())
}

pub(super) fn is_api_ready(api_address: Option<String>, api_secret: Option<String>) -> bool {
    let Some(api_address) = api_address else {
        return false;
    };

    let url = format!("{}/version", api_address.trim_end_matches('/'));
    let mut request = minreq::get(url).with_timeout(1);
    if let Some(secret) = api_secret.filter(|secret| !secret.trim().is_empty()) {
        request = request.with_header("Authorization", format!("Bearer {secret}"));
    }

    match request.send() {
        Ok(response) => matches!(response.status_code, 200..=299 | 401 | 403 | 404 | 405),
        Err(_) => false,
    }
}

#[cfg(test)]
mod tests {
    use super::percent_decode;

    #[test]
    fn decodes_percent_encoded_bytes() {
        assert_eq!(percent_decode("force%3D1"), "force=1");
        assert_eq!(
            percent_decode("%E4%BD%A0"),
            String::from_utf8_lossy(&[0xe4, 0xbd, 0xa0])
        );
    }

    #[test]
    fn leaves_invalid_percent_sequences_without_panicking() {
        assert_eq!(percent_decode("%zz"), "%zz");

        let input = std::str::from_utf8(&[b'%', 0xe4, 0xbd, 0xa0]).unwrap();
        assert_eq!(percent_decode(input), input);
    }
}
