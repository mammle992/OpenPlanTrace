mod projection;
use axum::{
    Json, Router,
    body::Body,
    extract::{DefaultBodyLimit, Multipart, Path as UrlPath, Query, Request, State},
    http::{StatusCode, header},
    middleware::{self, Next},
    response::{IntoResponse, Response},
    routing::{get, post},
};
use openplantrace_rs::{
    export, input,
    model::{Options, Scan},
    pipeline,
};
use serde::Deserialize;
use serde_json::{Value, json};
use std::{
    net::{Ipv4Addr, SocketAddrV4},
    path::{Path, PathBuf},
    sync::Arc,
};
use tokio::{fs, io::AsyncReadExt, net::TcpListener, sync::Semaphore};
use tokio_util::io::ReaderStream;

const MIB: u64 = 1 << 20;
#[derive(Clone)]
struct Viewer {
    root: PathBuf,
    authority: String,
    scans: Arc<Semaphore>,
}
type Error = (StatusCode, Json<Value>);
fn error(status: StatusCode, message: impl ToString) -> Error {
    (status, Json(json!({"error":message.to_string()})))
}

async fn local_request(State(state): State<Viewer>, request: Request, next: Next) -> Response {
    let headers = request.headers();
    let host = headers
        .get(header::HOST)
        .and_then(|v| v.to_str().ok())
        .unwrap_or("");
    let local =
        host == state.authority || host == state.authority.replace("127.0.0.1", "localhost");
    let origin_ok = headers
        .get(header::ORIGIN)
        .is_none_or(|v| v.to_str().is_ok_and(|v| v == format!("http://{host}")));
    if !local || !origin_ok {
        return error(
            StatusCode::FORBIDDEN,
            "request must originate from the local viewer",
        )
        .into_response();
    }
    let mut reply = next.run(request).await;
    reply
        .headers_mut()
        .insert(header::CACHE_CONTROL, "no-store".parse().unwrap());
    reply
        .headers_mut()
        .insert(header::X_CONTENT_TYPE_OPTIONS, "nosniff".parse().unwrap());
    reply
}

macro_rules! asset {
    ($file:literal) => {
        include_bytes!(concat!(
            "../../../tools/OpenPlanTrace.Viewer/wwwroot/",
            $file
        ))
        .as_slice()
    };
}
fn static_asset(name: &str) -> Option<(&'static str, &'static [u8])> {
    let item = match name {
        "" | "index.html" => ("text/html; charset=utf-8", asset!("index.html")),
        "app.js" => ("text/javascript; charset=utf-8", asset!("app.js")),
        "styles.css" => ("text/css; charset=utf-8", asset!("styles.css")),
        "samples/annotation-reference-benchmark-draft.json" => (
            "application/json",
            asset!("samples/annotation-reference-benchmark-draft.json"),
        ),
        "samples/annotation-reference-scan.json" => (
            "application/json",
            asset!("samples/annotation-reference-scan.json"),
        ),
        "samples/measurement-qa-scan.json" => (
            "application/json",
            asset!("samples/measurement-qa-scan.json"),
        ),
        "samples/object-correction-scan.json" => (
            "application/json",
            asset!("samples/object-correction-scan.json"),
        ),
        "samples/routing-review-benchmark-draft.json" => (
            "application/json",
            asset!("samples/routing-review-benchmark-draft.json"),
        ),
        "samples/routing-review-scan.json" => (
            "application/json",
            asset!("samples/routing-review-scan.json"),
        ),
        _ => return None,
    };
    Some(item)
}
fn asset_response(name: &str) -> Response {
    if name.is_empty() || name == "index.html" {
        let html = include_str!("../../../tools/OpenPlanTrace.Viewer/wwwroot/index.html").replacen(
            "<html lang=\"en\">",
            "<html lang=\"en\" data-engine=\"rust\">",
            1,
        );
        return axum::response::Html(html).into_response();
    }
    match static_asset(name) {
        Some((kind, content)) => ([(header::CONTENT_TYPE, kind)], content).into_response(),
        None => StatusCode::NOT_FOUND.into_response(),
    }
}

#[derive(Deserialize)]
struct FileQuery {
    path: PathBuf,
}
async fn document(
    State(viewer): State<Viewer>,
    UrlPath(kind): UrlPath<String>,
    Query(query): Query<FileQuery>,
) -> Result<Response, Error> {
    // Resolve symlinks/junctions before checking the viewer's selected document root.
    let path = fs::canonicalize(viewer.root.join(query.path))
        .await
        .map_err(|_| error(StatusCode::NOT_FOUND, "file was not found"))?;
    if !path.starts_with(&viewer.root) {
        return Err(error(StatusCode::FORBIDDEN, "file outside the viewer root"));
    }
    let ext = path
        .extension()
        .and_then(|v| v.to_str())
        .unwrap_or("")
        .to_ascii_lowercase();
    let (mime, limit) = match (kind.as_str(), ext.as_str()) {
        ("pdf-file", "pdf") => ("application/pdf", 250 * MIB),
        ("json-file", "json") => ("application/json", 250 * MIB),
        ("kvemo-crop-manifest", "jsonl") => ("application/x-ndjson", 100 * MIB),
        ("kvemo-crop-image", "png") => ("image/png", 20 * MIB),
        ("kvemo-crop-image", "jpg" | "jpeg") => ("image/jpeg", 20 * MIB),
        ("kvemo-crop-image", "webp") => ("image/webp", 20 * MIB),
        ("pdf-file" | "json-file" | "kvemo-crop-manifest" | "kvemo-crop-image", _) => {
            return Err(error(
                StatusCode::BAD_REQUEST,
                "unsupported file extension for this route",
            ));
        }
        _ => return Err(error(StatusCode::NOT_FOUND, "unknown route")),
    };
    let file = fs::File::open(path)
        .await
        .map_err(|_| error(StatusCode::NOT_FOUND, "file was not found"))?;
    let info = file
        .metadata()
        .await
        .map_err(|e| error(StatusCode::INTERNAL_SERVER_ERROR, e))?;
    if !info.is_file() || info.len() > limit {
        return Err(error(StatusCode::BAD_REQUEST, "unsupported file or size"));
    }
    Ok((
        [(header::CONTENT_TYPE, mime)],
        Body::from_stream(ReaderStream::new(file.take(info.len()))),
    )
        .into_response())
}

async fn scan(State(state): State<Viewer>, mut data: Multipart) -> Result<Json<Value>, Error> {
    let permit = state
        .scans
        .try_acquire_owned()
        .map_err(|_| error(StatusCode::TOO_MANY_REQUESTS, "a scan is already running"))?;
    let mut upload = None;
    while let Some(field) = data
        .next_field()
        .await
        .map_err(|e| error(StatusCode::BAD_REQUEST, e))?
    {
        if field.name() != Some("file") {
            continue;
        }
        if upload.is_some() {
            return Err(error(StatusCode::BAD_REQUEST, "supply one file"));
        }
        let name = field
            .file_name()
            .and_then(|v| Path::new(v).file_name())
            .and_then(|v| v.to_str())
            .ok_or_else(|| error(StatusCode::BAD_REQUEST, "missing filename"))?
            .to_owned();
        let format = input::DrawingFormat::of(Path::new(&name))
            .ok_or_else(|| error(StatusCode::BAD_REQUEST, "supply a PDF or ASCII DXF"))?;
        let bytes = field
            .bytes()
            .await
            .map_err(|e| error(StatusCode::BAD_REQUEST, e))?;
        if bytes.is_empty() || bytes.len() as u64 > 128 * MIB {
            return Err(error(
                StatusCode::BAD_REQUEST,
                "input size outside scanner bounds",
            ));
        }
        upload = Some((name, format, bytes));
    }
    let (name, format, bytes) =
        upload.ok_or_else(|| error(StatusCode::BAD_REQUEST, "missing drawing"))?;
    let result = tokio::task::spawn_blocking(move || {
        let _permit = permit;
        let document = input::load_bytes(format, &bytes)?;
        let scan = pipeline::scan_document(
            document,
            &Options::default(),
            None,
            format == input::DrawingFormat::Pdf,
        )?;
        export::validate(&scan)?;
        Ok::<_, String>(projection::view(&scan, &name))
    })
    .await
    .map_err(|e| error(StatusCode::INTERNAL_SERVER_ERROR, e))?
    .map_err(|e| error(StatusCode::UNPROCESSABLE_ENTITY, e))?;
    Ok(Json(result))
}

async fn native_view(State(state): State<Viewer>, request: Request) -> Result<Json<Value>, Error> {
    let permit = state
        .scans
        .try_acquire_owned()
        .map_err(|_| error(StatusCode::TOO_MANY_REQUESTS, "a scan is already running"))?;
    let bytes = axum::body::to_bytes(request.into_body(), 512 * MIB as usize)
        .await
        .map_err(|_| {
            error(
                StatusCode::PAYLOAD_TOO_LARGE,
                "scan exceeds viewer input limit",
            )
        })?;
    tokio::task::spawn_blocking(move || {
        let _permit = permit;
        let scan: Scan = match serde_json::from_slice(&bytes) {
            Ok(scan) => scan,
            Err(e) => {
                let encoded: Value = serde_json::from_slice(&bytes)
                    .map_err(|_| error(StatusCode::BAD_REQUEST, e))?;
                let raw = openplantrace_rs::compact::decode(&encoded)
                    .map_err(|e| error(StatusCode::BAD_REQUEST, e))?;
                if encoded["schemaVersion"] == openplantrace_rs::compact::REFERENCE {
                    return Ok(Json(raw));
                }
                serde_json::from_value(raw).map_err(|e| error(StatusCode::BAD_REQUEST, e))?
            }
        };
        export::validate(&scan).map_err(|e| error(StatusCode::UNPROCESSABLE_ENTITY, e))?;
        Ok(Json(projection::view(&scan, "OpenPlanTrace scan")))
    })
    .await
    .map_err(|e| error(StatusCode::INTERNAL_SERVER_ERROR, e))?
}

fn routes(state: Viewer) -> Router {
    Router::new()
        .route("/", get(|| async { asset_response("") }))
        .route(
            "/api/health",
            get(|| async { Json(json!({"status":"ready","engine":"rust"})) }),
        )
        .route(
            "/api/scan",
            post(scan).layer(DefaultBodyLimit::max(129 * MIB as usize)),
        )
        .route("/api/native-view", post(native_view))
        .route("/api/{kind}", get(document))
        .route(
            "/{*asset}",
            get(|UrlPath(name): UrlPath<String>| async move { asset_response(&name) }),
        )
        .layer(middleware::from_fn_with_state(state.clone(), local_request))
        .with_state(state)
}
#[tokio::main]
async fn main() -> Result<(), String> {
    let mut root = std::env::current_dir().map_err(|e| e.to_string())?;
    let mut port = 5077u16;
    let mut args = std::env::args_os().skip(1);
    while let Some(arg) = args.next() {
        match arg.to_str() {
            Some("--root") => root = args.next().ok_or("missing viewer root")?.into(),
            Some("--port") => {
                port = args
                    .next()
                    .and_then(|v| v.to_str()?.parse().ok())
                    .ok_or("invalid viewer port")?
            }
            Some("-h" | "--help") => {
                println!(
                    "openplantrace-viewer [--root DIRECTORY] [--port PORT]\nServes the local review interface and scans on 127.0.0.1. Port 0 selects an available port."
                );
                return Ok(());
            }
            _ => return Err(format!("unknown argument: {}", arg.to_string_lossy())),
        }
    }
    let root = fs::canonicalize(root).await.map_err(|e| e.to_string())?;
    if !root.is_dir() {
        return Err("viewer root must be a directory".into());
    }
    let listener = TcpListener::bind(SocketAddrV4::new(Ipv4Addr::LOCALHOST, port))
        .await
        .map_err(|e| e.to_string())?;
    let authority = listener
        .local_addr()
        .map_err(|e| e.to_string())?
        .to_string();
    println!("OpenPlanTrace Viewer at http://{authority}");
    axum::serve(
        listener,
        routes(Viewer {
            root,
            authority,
            scans: Arc::new(Semaphore::new(1)),
        }),
    )
    .await
    .map_err(|e| e.to_string())
}
