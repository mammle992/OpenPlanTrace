//! Native RGB target-centered corpus preparation. No PDF reader or model inference.
use image::{Rgb, RgbImage, imageops};
use openplantrace_symbol_data::{ink_bounds, preprocess_target_rgb};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use std::{
    collections::BTreeMap,
    error::Error,
    fs::{self, File},
    io::{BufWriter, Write},
    path::{Path, PathBuf},
};
type Result<T> = std::result::Result<T, Box<dyn Error>>;
const LABELS: [(&str, &str); 9] = [
    ("other", "Unknown"),
    ("door", "Structural"),
    ("window", "Structural"),
    ("stair", "Stair"),
    ("toilet", "PlumbingFixture"),
    ("sink", "PlumbingFixture"),
    ("shower", "PlumbingFixture"),
    ("bath", "PlumbingFixture"),
    ("furniture", "Furniture"),
];
const SEED: u64 = 20261117;
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Config {
    approved_train: String,
    legacy_real_train: String,
    public_sources: String,
    real_validation: Vec<String>,
    plan_path: String,
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Source {
    id: String,
    project: String,
    domain: String,
    split: String,
    label: usize,
    path: String,
    encoded_sha256: String,
    rgb_sha256: String,
    target: [f64; 4],
    target_policy: String,
}
struct Loaded {
    definition: Source,
    image: RgbImage,
}
#[derive(Clone, Copy, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Aug {
    quarter_turn: u8,
    mirror: bool,
    angle_degrees: f64,
    scale: f64,
    shift_x_fraction: f64,
    shift_y_fraction: f64,
    opacity: f64,
    noise_amplitude: f64,
    seed: u64,
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Planned {
    source_id: String,
    project_group: String,
    domain: String,
    split: String,
    label_index: usize,
    augmentation_id: usize,
    augmentation: Aug,
    procedural_seed: Option<u64>,
}
#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Plan {
    schema: String,
    seed: u64,
    input_manifest_sha256: BTreeMap<String, String>,
    sources: Vec<Source>,
    rows: Vec<Planned>,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Item {
    image_path: String,
    offset: usize,
    label_index: usize,
    source_group: String,
    split: String,
    domain: String,
    project_group: String,
    original_source_id: String,
    source_id: String,
    augmentation_id: usize,
}
struct Rng(u64);
impl Rng {
    fn next(&mut self) -> u64 {
        self.0 = self.0.wrapping_add(0x9e3779b97f4a7c15);
        let mut z = self.0;
        z = (z ^ (z >> 30)).wrapping_mul(0xbf58476d1ce4e5b9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94d049bb133111eb);
        z ^ (z >> 31)
    }
    fn unit(&mut self) -> f64 {
        (self.next() >> 11) as f64 / ((1u64 << 53) as f64)
    }
    fn range(&mut self, a: f64, b: f64) -> f64 {
        a + (b - a) * self.unit()
    }
    fn index(&mut self, n: usize) -> usize {
        (self.next() % n as u64) as usize
    }
}
fn sha(b: &[u8]) -> String {
    Sha256::digest(b)
        .iter()
        .map(|v| format!("{v:02x}"))
        .collect()
}
fn text<'a>(v: &'a Value, key: &str) -> Option<&'a str> {
    v.get(key).and_then(Value::as_str)
}
fn bounds(v: &Value, key: &str) -> Option<[f64; 4]> {
    let b = v.get(key)?.as_array()?;
    if b.len() != 4 {
        return None;
    }
    Some([
        b[0].as_f64()?,
        b[1].as_f64()?,
        b[2].as_f64()?,
        b[3].as_f64()?,
    ])
}
fn read_manifest(path: &str) -> Result<Vec<Value>> {
    let v: Value = serde_json::from_slice(&fs::read(path)?)?;
    v.as_array()
        .cloned()
        .or_else(|| v.get("items").and_then(Value::as_array).cloned())
        .ok_or_else(|| "Expected flat source array".into())
}
fn add_sources(
    path: &str,
    kind: &str,
    out: &mut Vec<Loaded>,
    input_hashes: &mut BTreeMap<String, String>,
) -> Result<()> {
    input_hashes.insert(path.into(), sha(&fs::read(path)?));
    for v in read_manifest(path)? {
        let source_id = text(&v, "originalSourceId")
            .or_else(|| text(&v, "sourceId"))
            .ok_or("Missing original source ID")?
            .to_string();
        let family = text(&v, "sourceFamily")
            .or_else(|| text(&v, "projectGroup"))
            .ok_or("Missing source family")?;
        let real = kind != "public";
        let split = if kind == "validation" {
            "validation"
        } else if kind == "public" {
            text(&v, "split").ok_or("Public split missing")?
        } else {
            "train"
        };
        if !["train", "validation"].contains(&split) {
            return Err("Refusing heldout/test source in preparation".into());
        }
        let project = if real {
            text(&v, "projectGroup")
                .unwrap_or(family)
                .split('/')
                .next()
                .unwrap()
                .to_string()
        } else {
            family.to_string()
        };
        if real && split == "train" && !matches!(project.as_str(), "G01" | "G04" | "G06" | "G09") {
            return Err(format!("Unapproved real training project {project}").into());
        }
        if real && split == "validation" && !matches!(project.as_str(), "G05" | "G08") {
            return Err(format!("Unapproved real validation project {project}").into());
        }
        let category = text(&v, "category")
            .or_else(|| text(&v, "label"))
            .ok_or("Source class missing")?;
        let label = LABELS
            .iter()
            .position(|x| x.0 == category)
            .ok_or("Unknown source class")?;
        let image_path = if real {
            text(&v, "nativeCropPath").ok_or("Real source must provide native RGB crop")?
        } else {
            text(&v, "renderedPath").ok_or("Public source frame missing")?
        };
        if Path::new(image_path).extension().and_then(|x| x.to_str()) != Some("png") {
            return Err("Preparation reads source PNG crops only, never PDFs".into());
        }
        let bytes = fs::read(image_path)?;
        let encoded_hash = sha(&bytes);
        let expected = if real {
            text(&v, "nativeCropSha256")
        } else {
            text(&v, "renderPngSha256").or_else(|| text(&v, "frameFileSha256"))
        };
        if let Some(expected) = expected {
            if encoded_hash != expected {
                return Err(format!("Native/source PNG hash mismatch: {source_id}").into());
            }
        }
        let image = image::load_from_memory(&bytes)?.to_rgb8();
        if real {
            if let Some(expected) = text(&v, "nativeRgbPixelSha256") {
                if sha(image.as_raw()) != expected {
                    return Err(format!("Native RGB hash mismatch: {source_id}").into());
                }
            }
        } else if let Some(expected) = text(&v, "renderSha256") {
            if sha(image::DynamicImage::ImageRgb8(image.clone())
                .to_luma8()
                .as_raw())
                != expected
            {
                return Err(format!("Public source pixel hash mismatch: {source_id}").into());
            }
        }
        let (target, policy) = if !real {
            let b = ink_bounds(&image).unwrap_or((0, 0, image.width(), image.height()));
            (
                [b.0 as f64, b.1 as f64, b.2 as f64, b.3 as f64],
                "Isolated clean public artwork: bounds derived once; same contract thereafter.",
            )
        } else if kind == "legacy" {
            (
                [0., 0., image.width() as f64, image.height() as f64],
                "Legacy LC11 frozen native ROI is the original target; no source PDF or grayscale frame read.",
            )
        } else {
            (
                bounds(&v, "nativeTargetBoxInCrop")
                    .or_else(|| bounds(&v, "targetBoxInNativeCrop"))
                    .ok_or("Explicit native target box required")?,
                "Explicit independently source-reviewed native target; no ink-bbox recentering.",
            )
        };
        preprocess_target_rgb(&image, 64, target, 0.15)?;
        let id = if real && !source_id.starts_with(&project) {
            format!("{project}:{source_id}")
        } else {
            source_id
        };
        out.push(Loaded {
            definition: Source {
                id,
                project,
                domain: if real { "real" } else { "public" }.into(),
                split: split.into(),
                label,
                path: image_path.into(),
                encoded_sha256: encoded_hash,
                rgb_sha256: sha(image.as_raw()),
                target,
                target_policy: policy.into(),
            },
            image,
        });
    }
    Ok(())
}
fn spec(rng: &mut Rng) -> Aug {
    Aug {
        quarter_turn: rng.index(4) as u8,
        mirror: rng.index(2) == 0,
        angle_degrees: rng.range(-4., 4.),
        scale: rng.range(0.90, 1.05),
        shift_x_fraction: rng.range(-0.015, 0.015),
        shift_y_fraction: rng.range(-0.015, 0.015),
        opacity: rng.range(0.85, 1.0),
        noise_amplitude: rng.range(0., 2.),
        seed: rng.next(),
    }
}
fn validation_spec(k: usize) -> Aug {
    Aug {
        quarter_turn: (k % 4) as u8,
        mirror: k >= 4,
        angle_degrees: 0.,
        scale: 1.,
        shift_x_fraction: 0.,
        shift_y_fraction: 0.,
        opacity: 1.,
        noise_amplitude: 0.,
        seed: 0,
    }
}
fn make_plan(sources: &[Loaded], hashes: BTreeMap<String, String>) -> Result<Plan> {
    let mut projects = BTreeMap::new();
    let mut originals = BTreeMap::new();
    for s in sources {
        let d = &s.definition;
        if let Some(old) = projects.insert(d.project.clone(), d.split.clone()) {
            if old != d.split {
                return Err("Project leaks across train/validation".into());
            }
        }
        if let Some(old) = originals.insert(d.id.clone(), d.split.clone()) {
            if old != d.split {
                return Err("Original leaks across train/validation".into());
            }
            return Err(format!("Repeated original ID {}", d.id).into());
        }
    }
    let mut rng = Rng(SEED);
    let mut rows = Vec::new();
    for label in 0..9 {
        let mut domain_counters = BTreeMap::<String, usize>::new();
        let mut original_counters = BTreeMap::<(String, String), usize>::new();
        for j in 0..1024 {
            let domain = if [1, 3].contains(&label) && j >= 922 {
                "procedural"
            } else if j < if [1, 3].contains(&label) { 666 } else { 768 } {
                "real"
            } else {
                "public"
            };
            let augmentation = spec(&mut rng);
            let (source_id, project_group, procedural_seed) = if domain == "procedural" {
                (
                    format!("own-generic-{}-v2", LABELS[label].0),
                    "OPT-procedural-v2".into(),
                    Some(rng.next()),
                )
            } else {
                let mut by_project = BTreeMap::<String, Vec<&Source>>::new();
                for s in sources {
                    let d = &s.definition;
                    if d.split == "train" && d.label == label && d.domain == domain {
                        by_project.entry(d.project.clone()).or_default().push(d);
                    }
                }
                if by_project.is_empty() {
                    return Err(format!("No {domain} source for {}", LABELS[label].0).into());
                }
                let groups: Vec<_> = by_project.keys().cloned().collect();
                let counter = domain_counters.entry(domain.into()).or_default();
                let group = &groups[*counter % groups.len()];
                *counter += 1;
                let entries = &by_project[group];
                let count = original_counters
                    .entry((domain.into(), group.clone()))
                    .or_default();
                let source = entries[*count % entries.len()];
                *count += 1;
                (source.id.clone(), source.project.clone(), None)
            };
            rows.push(Planned {
                source_id,
                project_group,
                domain: domain.into(),
                split: "train".into(),
                label_index: label,
                augmentation_id: j,
                augmentation,
                procedural_seed,
            });
        }
    }
    for s in sources {
        let d = &s.definition;
        if d.split != "validation" {
            continue;
        }
        let count = if d.domain == "public" { 8 } else { 1 };
        for k in 0..count {
            rows.push(Planned {
                source_id: d.id.clone(),
                project_group: d.project.clone(),
                domain: d.domain.clone(),
                split: "validation".into(),
                label_index: d.label,
                augmentation_id: k,
                augmentation: validation_spec(k),
                procedural_seed: None,
            });
        }
    }
    Ok(Plan {
        schema: "openplantrace.target-rgb-sampling-plan.v1".into(),
        seed: SEED,
        input_manifest_sha256: hashes,
        sources: sources.iter().map(|s| s.definition.clone()).collect(),
        rows,
    })
}
fn pixel(image: &RgbImage, x: f64, y: f64, c: usize) -> f64 {
    let n = image.width() as i32;
    if x < 0. || y < 0. || x > (n - 1) as f64 || y > (n - 1) as f64 {
        return 255.;
    }
    let (x0, y0) = (x.floor() as u32, y.floor() as u32);
    let (x1, y1) = ((x0 + 1).min(n as u32 - 1), (y0 + 1).min(n as u32 - 1));
    let (fx, fy) = (x - x0 as f64, y - y0 as f64);
    let p = |x, y| image.get_pixel(x, y)[c] as f64;
    p(x0, y0) * (1. - fx) * (1. - fy)
        + p(x1, y0) * fx * (1. - fy)
        + p(x0, y1) * (1. - fx) * fy
        + p(x1, y1) * fx * fy
}
fn noise(seed: u64, x: u32, y: u32, side: u32) -> f64 {
    let (nx, ny) = ((x * 192 / side) as u64, (y * 192 / side) as u64);
    let mut r =
        Rng(seed ^ nx.wrapping_mul(0x87c37b91114253d5) ^ ny.wrapping_mul(0x4cf5ad432745937f));
    r.unit() + r.unit() + r.unit() - 1.5
}
fn augment(image: &RgbImage, a: Aug) -> RgbImage {
    let n = image.width();
    let center = (n as f64 - 1.) / 2.;
    let (theta_s, theta_c) = (a.quarter_turn as f64 * 90. + a.angle_degrees)
        .to_radians()
        .sin_cos();
    let mut out = RgbImage::from_pixel(n, n, Rgb([255; 3]));
    for y in 0..n {
        for x in 0..n {
            let px = (x as f64 - center - a.shift_x_fraction * n as f64) / a.scale;
            let py = (y as f64 - center - a.shift_y_fraction * n as f64) / a.scale;
            let mut sx = theta_c * px + theta_s * py;
            let sy = -theta_s * px + theta_c * py;
            if a.mirror {
                sx = -sx;
            }
            let epsilon = noise(a.seed, x, y, n) * a.noise_amplitude;
            let mut rgb = [0; 3];
            for c in 0..3 {
                let v = pixel(image, sx + center, sy + center, c);
                rgb[c] = (255. - (255. - v) * a.opacity + epsilon)
                    .round()
                    .clamp(0., 255.) as u8;
            }
            out.put_pixel(x, y, Rgb(rgb));
        }
    }
    out
}
fn line(image: &mut RgbImage, x0: f64, y0: f64, x1: f64, y1: f64, width: f64) {
    let n = ((x1 - x0).abs().max((y1 - y0).abs()) * 2.).ceil() as usize;
    for i in 0..=n.max(1) {
        let t = i as f64 / n.max(1) as f64;
        let (x, y) = (x0 + (x1 - x0) * t, y0 + (y1 - y0) * t);
        let r = width / 2.;
        for yy in (y - r).floor() as i32..=(y + r).ceil() as i32 {
            for xx in (x - r).floor() as i32..=(x + r).ceil() as i32 {
                if (0..256).contains(&xx)
                    && (0..256).contains(&yy)
                    && (xx as f64 - x).powi(2) + (yy as f64 - y).powi(2) <= r * r
                {
                    image.put_pixel(xx as u32, yy as u32, Rgb([25; 3]));
                }
            }
        }
    }
}
fn rect(i: &mut RgbImage, x: f64, y: f64, w: f64, h: f64, p: f64) {
    for (a, b, c, d) in [
        (x, y, x + w, y),
        (x + w, y, x + w, y + h),
        (x + w, y + h, x, y + h),
        (x, y + h, x, y),
    ] {
        line(i, a, b, c, d, p);
    }
}
fn arc(i: &mut RgbImage, cx: f64, cy: f64, r: f64, start: f64, sweep: f64, p: f64) {
    for k in 0..128 {
        let a = start + sweep * k as f64 / 128.;
        let b = start + sweep * (k + 1) as f64 / 128.;
        line(
            i,
            cx + r * a.cos(),
            cy - r * a.sin(),
            cx + r * b.cos(),
            cy - r * b.sin(),
            p,
        );
    }
}
fn procedural(label: usize, seed: u64) -> RgbImage {
    let mut rng = Rng(seed);
    let mut im = RgbImage::from_pixel(256, 256, Rgb([255; 3]));
    let p = rng.range(2., 5.);
    if label == 1 {
        let double = rng.index(3) == 0;
        let w = rng.range(116., 148.);
        let x = (256. - w) / 2.;
        let y = 188.;
        let a = rng.range(55., 105.).to_radians();
        line(&mut im, 16., y, x, y, p * 2.);
        line(&mut im, x + w, y, 240., y, p * 2.);
        for j in [x, x + w] {
            line(&mut im, j, y - 9., j, y + 9., p);
        }
        let leaf = if double { w / 2. } else { w };
        line(&mut im, x, y, x + leaf * a.cos(), y - leaf * a.sin(), p);
        arc(&mut im, x, y, leaf, 0., a, p * 0.7);
        if double {
            line(
                &mut im,
                x + w,
                y,
                x + w - leaf * a.cos(),
                y - leaf * a.sin(),
                p,
            );
            arc(&mut im, x + w, y, leaf, std::f64::consts::PI, -a, p * 0.7);
        }
    } else {
        match rng.index(3) {
            0 => {
                rect(&mut im, 84., 24., 88., 208., p);
                let mut y = 24. + rng.range(12., 20.);
                let step = rng.range(12., 20.);
                while y < 232. {
                    line(&mut im, 84., y, 172., y, p);
                    y += step;
                }
                line(&mut im, 128., 208., 128., 48., p);
                line(&mut im, 128., 48., 117., 66., p);
                line(&mut im, 128., 48., 139., 66., p);
            }
            1 => {
                for (a, b, c, d) in [
                    (48., 24., 116., 24.),
                    (116., 24., 116., 144.),
                    (116., 144., 224., 144.),
                    (224., 144., 224., 216.),
                    (224., 216., 48., 216.),
                    (48., 216., 48., 24.),
                ] {
                    line(&mut im, a, b, c, d, p);
                }
                for k in 1..8 {
                    let y = 24. + k as f64 * 15.;
                    line(&mut im, 48., y, 116., y, p);
                }
                for k in 1..7 {
                    let x = 116. + k as f64 * 15.;
                    line(&mut im, x, 144., x, 216., p);
                }
            }
            _ => {
                rect(&mut im, 36., 36., 184., 184., p);
                line(&mut im, 104., 96., 104., 220., p);
                line(&mut im, 152., 96., 152., 220., p);
                line(&mut im, 104., 96., 152., 96., p);
                for k in 1..9 {
                    let y = 96. + k as f64 * 14.;
                    line(&mut im, 36., y, 104., y, p);
                    line(&mut im, 152., y, 220., y, p);
                }
            }
        }
    }
    im
}
fn run() -> Result<()> {
    let args: Vec<_> = std::env::args().skip(1).collect();
    if args.len()==5&&args[0]=="preprocess" {
        if Path::new(&args[2]).exists(){return Err("Preprocess output already exists; preserve frozen pixels".into());}
        let target:Vec<f64>=args[4].split(',').map(str::parse).collect::<std::result::Result<_,_>>()?;
        let target:[f64;4]=target.try_into().map_err(|_|"Target must be x0,y0,x1,y1")?;
        let input=image::open(&args[1])?.to_rgb8();let side:u32=args[3].parse()?;
        let output=preprocess_target_rgb(&input,side,target,0.15)?;output.save(&args[2])?;
        println!("{}",json!({"side":side,"target":target,"pixelRgbSha256":sha(output.as_raw()),"encodedPngSha256":sha(&fs::read(&args[2])?)}));
        return Ok(());
    }
    if args.len() == 2 && args[0] == "verify" {
        let c: Config = serde_json::from_slice(&fs::read(&args[1])?)?;
        let mut s = Vec::new();
        let mut h = BTreeMap::new();
        add_sources(&c.approved_train, "approved", &mut s, &mut h)?;
        add_sources(&c.legacy_real_train, "legacy", &mut s, &mut h)?;
        add_sources(&c.public_sources, "public", &mut s, &mut h)?;
        for p in &c.real_validation {
            add_sources(p, "validation", &mut s, &mut h)?;
        }
        let plan = make_plan(&s, h)?;
        println!(
            "{}",
            json!({"status":"INPUTS VERIFIED; no dataset/plan written","sources":s.len(),"trainSources":s.iter().filter(|x|x.definition.split=="train").count(),"validationSources":s.iter().filter(|x|x.definition.split=="validation").count(),"plannedTrainRows":plan.rows.iter().filter(|x|x.split=="train").count(),"plannedValidationRows":plan.rows.iter().filter(|x|x.split=="validation").count()})
        );
        return Ok(());
    }
    if args.len() != 3 {
        return Err("Usage: openplantrace-real-data CONFIG.json NEW_OUTPUT_DIRECTORY SIDE64_OR96 | verify CONFIG.json".into());
    }
    let config: Config = serde_json::from_slice(&fs::read(&args[0])?)?;
    let output = PathBuf::from(&args[1]);
    let side: u32 = args[2].parse()?;
    if ![64, 96].contains(&side) || output.exists() {
        return Err("Require side64/96 and a new output directory".into());
    }
    let mut sources = Vec::new();
    let mut hashes = BTreeMap::new();
    add_sources(
        &config.approved_train,
        "approved",
        &mut sources,
        &mut hashes,
    )?;
    add_sources(
        &config.legacy_real_train,
        "legacy",
        &mut sources,
        &mut hashes,
    )?;
    add_sources(&config.public_sources, "public", &mut sources, &mut hashes)?;
    for path in &config.real_validation {
        add_sources(path, "validation", &mut sources, &mut hashes)?;
    }
    let actual_plan = make_plan(&sources, hashes)?;
    let plan_path = PathBuf::from(&config.plan_path);
    let plan = if plan_path.exists() {
        let old: Plan = serde_json::from_slice(&fs::read(&plan_path)?)?;
        if serde_json::to_vec(&old)? != serde_json::to_vec(&actual_plan)? {
            if old.input_manifest_sha256 != actual_plan.input_manifest_sha256 {
                return Err("Frozen input manifest hashes changed".into());
            }
            for (index,(a,b)) in old.sources.iter().zip(&actual_plan.sources).enumerate() {
                if serde_json::to_vec(a)?!=serde_json::to_vec(b)? {eprintln!("SOURCE_MISMATCH {index} old={} new={}",serde_json::to_string(a)?,serde_json::to_string(b)?);break;}
            }
            for (index,(a,b)) in old.rows.iter().zip(&actual_plan.rows).enumerate() {
                if serde_json::to_vec(a)?!=serde_json::to_vec(b)? {eprintln!("ROW_MISMATCH {index} old={} new={}",serde_json::to_string(a)?,serde_json::to_string(b)?);break;}
            }
            return Err(
                "Frozen sampling plan differs from source/configuration; refuse unmatched ablation"
                    .into(),
            );
        }
        old
    } else {
        if let Some(parent) = plan_path.parent() {
            fs::create_dir_all(parent)?;
        }
        fs::write(&plan_path, serde_json::to_vec_pretty(&actual_plan)?)?;
        actual_plan
    };
    let plan_bytes = fs::read(&plan_path)?;
    let source_map: BTreeMap<_, _> = sources
        .iter()
        .map(|s| (s.definition.id.clone(), s))
        .collect();
    let canonical: BTreeMap<_, _> = sources
        .iter()
        .map(|s| {
            Ok((
                s.definition.id.clone(),
                preprocess_target_rgb(&s.image, side, s.definition.target, 0.15)?,
            ))
        })
        .collect::<Result<_>>()?;
    fs::create_dir_all(&output)?;
    let mut shard = BufWriter::new(File::create(output.join("images.rgb"))?);
    let mut ledger = BufWriter::new(File::create(output.join("augmentation-ledger.jsonl"))?);
    let mut digest = Sha256::new();
    let mut items = Vec::new();
    let mut train_sheet = RgbImage::from_pixel(side * 12, side * 18, Rgb([255; 3]));
    let mut seen = [0usize; 9];
    for (index, row) in plan.rows.iter().enumerate() {
        let base = if let Some(seed) = row.procedural_seed {
            let im = procedural(row.label_index, seed);
            let b = ink_bounds(&im).unwrap();
            preprocess_target_rgb(
                &im,
                side,
                [b.0 as f64, b.1 as f64, b.2 as f64, b.3 as f64],
                0.15,
            )?
        } else {
            canonical
                .get(&row.source_id)
                .ok_or("Planned original missing")?
                .clone()
        };
        let image = if row.split == "train" {
            augment(&base, row.augmentation)
        } else if row.domain == "public" {
            augment(&base, row.augmentation)
        } else {
            base
        };
        let offset = index * side as usize * side as usize * 3;
        shard.write_all(image.as_raw())?;
        digest.update(image.as_raw());
        writeln!(ledger, "{}", serde_json::to_string(row)?)?;
        if row.split == "train" && seen[row.label_index] < 24 {
            let k = seen[row.label_index];
            imageops::replace(
                &mut train_sheet,
                &image,
                (k % 12) as i64 * side as i64,
                (row.label_index * 2 + k / 12) as i64 * side as i64,
            );
            seen[row.label_index] += 1;
        }
        items.push(Item {
            image_path: "images.rgb".into(),
            offset,
            label_index: row.label_index,
            source_group: row.source_id.clone(),
            split: row.split.clone(),
            domain: row.domain.clone(),
            project_group: row.project_group.clone(),
            original_source_id: row.source_id.clone(),
            source_id: row.source_id.clone(),
            augmentation_id: row.augmentation_id,
        });
    }
    shard.flush()?;
    ledger.flush()?;
    train_sheet.save(output.join("training-contact-sheet.png"))?;
    let payload_sha: String = digest
        .finalize()
        .iter()
        .map(|v| format!("{v:02x}"))
        .collect();
    let labels: Vec<_> = LABELS
        .iter()
        .map(|(label, category)| json!({"label":label,"category":category}))
        .collect();
    let manifest = json!({"schema":"openplantrace.training-crops.v2","width":side,"height":side,"channels":3,"labels":labels,"items":items,"seed":SEED,"datasetSha256":payload_sha,"samplingPlanSha256":sha(&plan_bytes),"preprocessing":"target-centered-rgb-v1","targetContext":0.15});
    fs::write(
        output.join("manifest.json"),
        serde_json::to_vec_pretty(&manifest)?,
    )?;
    fs::write(
        output.join("source-inventory.json"),
        serde_json::to_vec_pretty(&plan.sources)?,
    )?;
    fs::write(output.join("sampling-plan.json"), &plan_bytes)?;
    let mut counts = BTreeMap::<String, usize>::new();
    for r in &plan.rows {
        *counts
            .entry(format!(
                "{}:{}:{}",
                r.split, r.domain, LABELS[r.label_index].0
            ))
            .or_default() += 1;
    }
    let proof = json!({"sourceOriginals":sources.len(),"sourceOriginalsByDomainSplit":plan.sources.iter().fold(BTreeMap::<String,usize>::new(),|mut m,s|{*m.entry(format!("{}:{}",s.split,s.domain)).or_default()+=1;m}),"side":side,"datasetSha256":payload_sha,"samplingPlanSha256":sha(&plan_bytes),"classDomainCounts":counts,"preprocessing":"Explicit native target; squareceil(maxside*1.30); native RGB context; white outside savedcrop; uniformLanczos3. No realCADinkbbox and no red-channel replication.","augmentation":"Same original IDs and serialized parameters for64/96. Right-angle/mirror, +/-4degrees,scale.90..1.05, shifts+/-1.5percent, RGBpreservingopacity.85..1,paper noise0..2. Publicvalidation8fixed rotations/mirrors; realvalidationcanonical only.","pdfsRead":false,"privacy":"Containsprivate native-derivedRGBpixels; keepcorpus/contactsheet/provenance local; do notpackageprivatepixels.","nativeSourceHashVerified":true,"sourceMapSize":source_map.len()});
    fs::write(
        output.join("preparation-proof.json"),
        serde_json::to_vec_pretty(&proof)?,
    )?;
    println!(
        "FROZEN {} side={} train={} validation={} plan={} images={}",
        output.join("manifest.json").display(),
        side,
        plan.rows.iter().filter(|r| r.split == "train").count(),
        plan.rows.iter().filter(|r| r.split == "validation").count(),
        sha(&plan_bytes),
        payload_sha
    );
    Ok(())
}
fn main() {
    if let Err(error) = run() {
        eprintln!("ERROR: {error}");
        std::process::exit(1);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn identity_augmentation_preserves_distinct_rgb_channels() {
        let input = RgbImage::from_pixel(64, 64, Rgb([210, 40, 90]));
        let output = augment(&input, validation_spec(0));
        assert_eq!(output.get_pixel(32, 32).0, [210, 40, 90]);
        assert_eq!(input.as_raw(), output.as_raw());
    }
    #[test]
    fn frozen_sampling_parameters_roundtrip_bit_exactly() {
        for value in [0.9088041832693049_f64,0.00011478905089656877_f64,-0.0027348401821756206_f64] {
            let bytes=serde_json::to_vec(&value).unwrap();
            let restored:f64=serde_json::from_slice(&bytes).unwrap();
            assert_eq!(value.to_bits(),restored.to_bits());
        }
    }
}
