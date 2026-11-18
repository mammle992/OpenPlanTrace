pub mod batch;
pub mod batch_compare;
pub mod benchmark;
pub mod kvemo;
pub mod object_review;
pub mod outputs;
pub mod profiles;
pub mod schema;
pub mod validate;
pub mod visual;
pub mod wall_truth;
use openplantrace_rs::{input, model::Shape};
use serde_json::{Value, json};
use std::{
    collections::BTreeMap,
    io::{self, Write},
    path::Path,
    time::Instant,
};

pub fn transform(s: &str) -> Result<[f64; 6], String> {
    let numbers: Vec<_> = s
        .split(',')
        .map(str::parse::<f64>)
        .collect::<Result<_, _>>()
        .map_err(|_| "invalid transform coefficient")?;
    let t: [f64; 6] = numbers
        .try_into()
        .map_err(|_| "expected six transform coefficients")?;
    if !openplantrace_rs::export::valid_transform(&t) {
        return Err("invalid geographic transform".into());
    }
    Ok(t)
}

pub fn print_json(value: &Value, compact: bool) -> Result<(), String> {
    let mut out = io::BufWriter::new(io::stdout().lock());
    if compact {
        serde_json::to_writer(&mut out, value)
    } else {
        serde_json::to_writer_pretty(&mut out, value)
    }
    .map_err(|e| e.to_string())?;
    writeln!(out)
        .and_then(|_| out.flush())
        .map_err(|e| e.to_string())
}

const FILE_FORMATS: &[(&str, &[&str], &str)] = &[
    (
        "PDF",
        &["pdf"],
        "automatic vector, raster and mixed pages; composed tiles and masks",
    ),
    (
        "DXF",
        &["dxf"],
        "ASCII model-space entities and block inserts",
    ),
    ("DWG", &["dwg"], "requires a licensed conversion adapter"),
    (
        "Raster image",
        &["png", "jpg", "jpeg", "bmp", "tif", "tiff", "webp"],
        "requires raster interpretation",
    ),
    ("Vector image", &["svg"], "requires an SVG input adapter"),
    (
        "Clipboard",
        &[],
        "paste content through an application adapter",
    ),
    (
        "Extracted primitives",
        &[],
        "use the Rust library scan_document entry point",
    ),
    ("Unknown", &[], "unrecognized source format"),
];

pub fn formats(args: Vec<String>) -> Result<(), String> {
    let mut json = false;
    let mut compact = false;
    for arg in args {
        match arg.as_str() {
            "--json" => json = true,
            "--compact-json" => compact = true,
            "--help" | "-h" => {
                println!("formats [--json] [--compact-json]");
                return Ok(());
            }
            _ => return Err(format!("unknown formats option: {arg}")),
        }
    }
    let mut records = Vec::new();
    for &(name, extensions, description) in FILE_FORMATS {
        let suffixes: Vec<_> = extensions.iter().map(|e| format!(".{e}")).collect();
        let supported = suffixes.first().is_some_and(|suffix| {
            input::DrawingFormat::of(Path::new(&format!("plan{suffix}"))).is_some()
        });
        let (kind, key, status, loader, license) = match name {
            "PDF" => (
                "Pdf",
                "pdf",
                "Registered",
                Some("lopdf + hayro (native Rust)"),
                "MIT and MIT/Apache-2.0; bundled fonts have separate BSD notices. See THIRD-PARTY-RASTER-NOTICES.md.",
            ),
            "DXF" => (
                "Dxf",
                "dxf",
                "Registered",
                Some("OpenPlanTrace Rust ASCII DXF"),
                "BSD-2-Clause.",
            ),
            "DWG" => (
                "Dwg",
                "dwg",
                "OptionalAdapterRequired",
                None,
                "DWG conversion requires an appropriately licensed adapter.",
            ),
            "Raster image" => (
                "RasterImage",
                "raster",
                "Planned",
                None,
                "Raster decoding and OCR depend on the selected adapter.",
            ),
            "Vector image" => (
                "VectorImage",
                "vector",
                "Planned",
                None,
                "Depends on the selected adapter.",
            ),
            "Clipboard" => (
                "Clipboard",
                "clipboard",
                "KnownButNotRegistered",
                None,
                "Depends on the host application.",
            ),
            "Extracted primitives" => (
                "ExtractedPrimitives",
                "extracted-primitives",
                "KnownButNotRegistered",
                None,
                "BSD-2-Clause.",
            ),
            _ => (
                "Unknown",
                "unknown",
                "Unknown",
                None,
                "Unknown source licensing cannot be assessed.",
            ),
        };
        if json {
            records.push(json!({"kind":kind,"key":key,"displayName":name,"extensions":suffixes,"canLoad":supported,"status":status,
                "registeredLoaderNames":loader.into_iter().collect::<Vec<_>>(),"adapterRequirement":description,"licenseNote":license,"message":description}));
        } else {
            println!(
                "{name} ({})  {}  {description}",
                suffixes.join(", "),
                if supported {
                    "available"
                } else {
                    "unavailable"
                }
            );
        }
    }
    if json {
        print_json(&Value::Array(records), compact)?;
    }
    Ok(())
}

pub fn inspect(args: Vec<String>) -> Result<(), String> {
    let mut path = None;
    let mut as_json = false;
    let mut compact = false;
    let mut limit = 0;
    let mut args = args.iter().peekable();
    while let Some(arg) = args.next() {
        match arg.as_str() {
            "--json" => as_json = true,
            "--compact-json" => compact = true,
            "--text-samples" => {
                limit = 80;
                if let Some(n) = args.peek().and_then(|s| s.parse::<usize>().ok()) {
                    limit = n;
                    args.next();
                }
            }
            "--help" | "-h" => {
                println!("inspect INPUT [--json] [--compact-json] [--text-samples [N]]");
                return Ok(());
            }
            _ if !arg.starts_with('-') && path.is_none() => path = Some(Path::new(arg)),
            _ => return Err(format!("unexpected inspect argument: {arg}")),
        }
    }
    let path = path.ok_or("inspect requires an input drawing")?;
    let (format, source_kind) =
        match input::DrawingFormat::of(path).ok_or("unsupported drawing format")? {
            input::DrawingFormat::Pdf => ("pdf", "Pdf"),
            input::DrawingFormat::Dxf => ("dxf", "Dxf"),
        };
    let now = Instant::now();
    let document = input::load(path)?;
    let elapsed = now.elapsed().as_secs_f64() * 1000.;
    let mut pages = Vec::new();
    let mut total = 0;
    let mut combined = BTreeMap::<&str, usize>::new();
    let mut combined_layers = BTreeMap::<&str, usize>::new();
    let mut samples = Vec::new();
    for page in &document.pages {
        let mut counts = BTreeMap::<&str, usize>::new();
        let mut layers = BTreeMap::<&str, usize>::new();
        let mut texts = Vec::new();
        for p in &page.primitives {
            let kind = match &p.shape {
                Shape::Line { .. } => "Line",
                Shape::Polyline { .. } => "Polyline",
                Shape::Arc { .. } => "Arc",
                Shape::Text { value, at, height } => {
                    if samples.len() < limit {
                        texts.push((at, &p.id, value, height, &p.layer));
                    }
                    "Text"
                }
                Shape::Insert { .. } => "Insert",
                Shape::Image { .. } => "Image",
                Shape::Dimension { .. } => "Dimension",
            };
            *counts.entry(kind).or_default() += 1;
            *combined.entry(kind).or_default() += 1;
            *layers
                .entry(p.layer.as_deref().unwrap_or("(none)"))
                .or_default() += 1;
        }
        texts.sort_by(|a, b| {
            a.0.y
                .total_cmp(&b.0.y)
                .then(a.0.x.total_cmp(&b.0.x))
                .then(a.1.cmp(b.1))
        });
        for (at, id, value, height, layer) in texts.into_iter().take(limit - samples.len()) {
            samples.push(json!({"pageNumber":page.number,"text":value,"at":at,"sourceId":id,"layer":layer,"fontSize":height}));
        }
        total += page.primitives.len();
        for (layer, count) in &layers {
            *combined_layers.entry(layer).or_default() += count;
        }
        pages.push(json!({"pageNumber":page.number,"width":page.width,"height":page.height,"primitiveCount":page.primitives.len(),"kindCounts":counts,"sourceFormatCounts":{(format):page.primitives.len()},"layerCounts":layers}));
    }
    if as_json {
        let layer_analysis = openplantrace_rs::layers::analyze_with_profile(
            format,
            &[],
            document
                .pages
                .iter()
                .flat_map(|p| p.primitives.iter().map(move |entity| (p.number, entity))),
        );
        return print_json(
            &json!({"schemaVersion":"openplantrace.rust.inspect.v1","inputPath":std::path::absolute(path).map_err(|e|e.to_string())?,
                "fileName":path.file_name().and_then(|n|n.to_str()),"documentId":path.file_name().and_then(|n|n.to_str()),
                "sourceKind":source_kind,"effectiveSourceKind":source_kind,"sourceFormatCounts":{(format):total},"layerCounts":combined_layers,"layerAnalysis":{"layers":layer_analysis},
                "pageCount":pages.len(),"primitiveCount":total,"kindCounts":combined,"loadDurationMilliseconds":elapsed,"pages":pages,"textSamples":samples,"warnings":document.warnings}),
            compact,
        );
    }
    println!(
        "{}: {} pages, {total} primitives",
        path.display(),
        pages.len()
    );
    for (kind, count) in combined {
        println!("  {kind}: {count}");
    }
    for sample in samples {
        println!("  page {}  {}", sample["pageNumber"], sample["text"]);
    }
    for warning in document.warnings {
        println!("  {warning}");
    }
    Ok(())
}
