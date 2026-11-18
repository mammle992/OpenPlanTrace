use serde_json::Value;
use std::{
    io::{BufRead, BufReader, Read, Write},
    net::TcpStream,
    path::Path,
    process::{Child, Command, Stdio},
    time::Duration,
};

struct Server(Child);
impl Drop for Server {
    fn drop(&mut self) {
        let _ = self.0.kill();
        let _ = self.0.wait();
    }
}

fn request(address: &str, method: &str, path: &str, headers: &str, body: &[u8]) -> (u16, String) {
    let mut stream = TcpStream::connect(address).unwrap();
    stream
        .set_read_timeout(Some(Duration::from_secs(15)))
        .unwrap();
    write!(stream,"{method} {path} HTTP/1.1\r\nHost: {address}\r\nConnection: close\r\n{headers}Content-Length: {}\r\n\r\n",body.len()).unwrap();
    stream.write_all(body).unwrap();
    let mut response = String::new();
    stream.read_to_string(&mut response).unwrap();
    let (headers, body) = response.split_once("\r\n\r\n").unwrap();
    let status = headers.split_whitespace().nth(1).unwrap().parse().unwrap();
    (status, body.to_owned())
}

#[test]
fn serves_local_interface_and_returns_source_backed_scan() {
    let fixtures = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../samples/golden");
    let mut server = Server(
        Command::new(env!("CARGO_BIN_EXE_openplantrace-viewer"))
            .args(["--port", "0", "--root"])
            .arg(&fixtures)
            .stdout(Stdio::piped())
            .spawn()
            .unwrap(),
    );
    let mut address = String::new();
    BufReader::new(server.0.stdout.take().unwrap())
        .read_line(&mut address)
        .unwrap();
    let address = address
        .trim()
        .strip_prefix("OpenPlanTrace Viewer at http://")
        .unwrap();
    let (status, html) = request(address, "GET", "/", "", &[]);
    assert_eq!(status, 200);
    assert!(html.contains("data-engine=\"rust\""));
    let (status, body) = request(address, "GET", "/api/health", "", &[]);
    assert_eq!(status, 200);
    assert_eq!(
        serde_json::from_str::<Value>(&body).unwrap()["engine"],
        "rust"
    );
    assert_eq!(
        request(
            address,
            "GET",
            "/api/health",
            "Origin: https://unrelated.invalid\r\n",
            &[]
        )
        .0,
        403
    );
    assert_eq!(
        request(
            address,
            "POST",
            "/api/native-view",
            "Content-Type: application/json\r\n",
            b"{}"
        )
        .0,
        400
    );
    assert_eq!(
        request(
            address,
            "GET",
            "/api/json-file?path=..%2F..%2FREADME.md",
            "",
            &[]
        )
        .0,
        403
    );
    let mut upload = b"--test-boundary\r\nContent-Disposition: form-data; name=\"file\"; filename=\"smoke.dxf\"\r\n\r\n".to_vec();
    upload.extend_from_slice(&std::fs::read(fixtures.join("semantic-smoke.dxf")).unwrap());
    upload.extend_from_slice(b"\r\n--test-boundary--\r\n");
    let (status, body) = request(
        address,
        "POST",
        "/api/scan",
        "Content-Type: multipart/form-data; boundary=test-boundary\r\n",
        &upload,
    );
    assert_eq!(status, 200, "{body}");
    let view: Value = serde_json::from_str(&body).unwrap();
    assert_eq!(view["documentId"], "smoke.dxf");
    assert!(!view["walls"].as_array().unwrap().is_empty());
    assert!(!view["layers"].as_array().unwrap().is_empty());
    let wall_layer = view["layers"]
        .as_array()
        .unwrap()
        .iter()
        .find(|layer| layer["name"] == "A-WALL")
        .unwrap();
    assert_eq!(wall_layer["sourceFormat"], "dxf");
    assert_eq!(wall_layer["likelyCategory"], "Wall");
    assert!(wall_layer["entityCount"].as_u64().unwrap() > 0);
    assert_eq!(view["calibration"]["millimetersPerDrawingUnit"], 1.0);
    let walls = view["walls"].as_array().unwrap();
    let decisions = |status| {
        walls
            .iter()
            .filter(|w| w["placementDecision"]["status"] == status)
            .count()
    };
    assert_eq!(decisions("Ready"), 4);
    assert_eq!(decisions("EvidenceOnly"), 2);
    let door = &view["openings"][0];
    let hosts = door["hostWallIds"].as_array().unwrap();
    assert_eq!(hosts.len(), 2);
    assert!(hosts.iter().all(|id| {
        walls
            .iter()
            .any(|wall| wall["id"] == *id && wall["placementDecision"]["status"] == "EvidenceOnly")
    }));
    assert!(door["placementHostWallIds"].as_array().unwrap().is_empty());
    assert_eq!(
        door["swingObservations"][0]["arcSourceId"],
        "dxf:entity:15:ARC"
    );
    assert!(
        view["walls"]
            .as_array()
            .unwrap()
            .iter()
            .all(|w| w["nativeSolidSpans"].is_array()
                && !w["sourcePrimitiveIds"].as_array().unwrap().is_empty())
    );
    assert!(view["diagnostics"]["messages"].is_array());
}
