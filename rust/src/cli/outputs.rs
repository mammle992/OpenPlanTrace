use openplantrace_rs::{
    export,
    model::{PageScan, Scan},
};
use serde::Serialize;
use std::{
    collections::HashSet,
    fs,
    io::{BufWriter, Write},
    path::{Path, PathBuf},
};

#[derive(Clone, Copy)]
enum Artifact {
    Scan,
    Placement,
    Structure,
    Connections,
    ConnectionsSvg,
    Geographic,
    Local,
    Svg,
    Compact,
    CompactGzip,
}

impl Artifact {
    fn bundle_name(self) -> Option<&'static str> {
        match self {
            Self::Scan => Some("scan.json"),
            Self::Structure => Some("structure.json"),
            Self::Placement => Some("placement.json"),
            Self::Connections => Some("connections.json"),
            Self::Local => Some("plan.local.json"),
            Self::Geographic => Some("plan.geojson"),
            Self::Svg | Self::ConnectionsSvg | Self::Compact | Self::CompactGzip => None,
        }
    }
}

#[derive(Default)]
pub struct Outputs {
    files: Vec<(Artifact, PathBuf)>,
    svg_dir: Option<PathBuf>,
    page: Option<u32>,
    pub compact: bool,
}

impl Outputs {
    pub fn option(&mut self, flag: &str, value: &str) -> Result<bool, String> {
        let kind = match flag {
            "--json" => Artifact::Scan,
            "--placement" => Artifact::Placement,
            "--structure" => Artifact::Structure,
            "--connections" => Artifact::Connections,
            "--connections-svg" => Artifact::ConnectionsSvg,
            "--geojson" => Artifact::Geographic,
            "--local-features" => Artifact::Local,
            "--svg" => Artifact::Svg,
            "--compact-scan" => Artifact::Compact,
            "--compact-scan-gzip" => Artifact::CompactGzip,
            "--svg-dir" if self.svg_dir.is_none() => {
                self.svg_dir = Some(value.into());
                return Ok(true);
            }
            "--page" if self.page.is_none() => {
                self.page = Some(
                    value
                        .parse::<u32>()
                        .ok()
                        .filter(|n| *n > 0)
                        .ok_or("page must be positive")?,
                );
                return Ok(true);
            }
            _ => return Ok(false),
        };
        self.files.push((kind, value.into()));
        Ok(true)
    }

    pub fn write_with_extra(
        &self,
        scan: &Scan,
        bundle: Option<&Path>,
        geo: Option<[f64; 6]>,
        overlays: bool,
        extra: impl FnOnce(Option<&Path>) -> Result<(), String>,
    ) -> Result<(), String> {
        if self.files.is_empty() && self.svg_dir.is_none() && self.page.is_none() {
            return match bundle {
                Some(path) => export::write_bundle_with_extra(scan, path, geo, overlays, |stage| {
                    extra(Some(stage))
                }),
                None => {
                    export::validate(scan)?;
                    extra(None)
                }
            };
        }
        let selected = match self.page {
            Some(n) => scan
                .pages
                .iter()
                .find(|p| p.page_number == n)
                .ok_or_else(|| format!("page {n} was not found"))?,
            None => scan.pages.first().ok_or("empty drawing")?,
        };
        let mut files: Vec<_> = self
            .files
            .iter()
            .map(|(kind, path)| (*kind, path.clone(), selected))
            .collect();
        if let Some(dir) = &self.svg_dir {
            files.extend(scan.pages.iter().map(|p| {
                (
                    Artifact::Svg,
                    dir.join(format!("page-{}.svg", p.page_number)),
                    p,
                )
            }));
        }
        let mut destinations = HashSet::new();
        for (kind, path, _) in &files {
            let id = std::path::absolute(path).map_err(|e| e.to_string())?;
            #[cfg(windows)]
            let id = id.as_os_str().to_ascii_lowercase();
            if path.exists() || !destinations.insert(id) {
                return Err(format!(
                    "output already exists or was requested twice: {}",
                    path.display()
                ));
            }
            if matches!(kind, Artifact::Geographic) && geo.is_none() {
                return Err("--geojson requires a known --geo-transform; use --local-features for drawing coordinates".into());
            }
        }
        let root = bundle
            .map(std::path::absolute)
            .transpose()
            .map_err(|e| e.to_string())?;
        let mut created = Vec::new();
        let write_files = |stage: Option<&Path>| {
            for (kind, path, p) in &files {
                let absolute = std::path::absolute(path).map_err(|e| e.to_string())?;
                let dest =
                    match root.as_deref().zip(stage).and_then(|(r, s)| {
                        absolute.strip_prefix(r).ok().map(|relative| (relative, s))
                    }) {
                        Some((relative, s))
                            if relative
                                .components()
                                .all(|c| matches!(c, std::path::Component::Normal(_))) =>
                        {
                            let standard_connection_svg = if scan
                                .pages
                                .first()
                                .is_some_and(|first| first.page_number == p.page_number)
                            {
                                "connections.svg".to_owned()
                            } else {
                                format!("page-{}-connections.svg", p.page_number)
                            };
                            let same_bundle_file =
                                kind.bundle_name().is_some_and(|n| relative == Path::new(n))
                                    || (matches!(kind, Artifact::ConnectionsSvg)
                                        && relative == Path::new(&standard_connection_svg));
                            if same_bundle_file && s.join(relative).is_file() {
                                continue;
                            }
                            s.join(relative)
                        }
                        _ => absolute,
                    };
                let path = &dest;
                if let Some(parent) = path.parent().filter(|p| !p.as_os_str().is_empty()) {
                    fs::create_dir_all(parent).map_err(|e| e.to_string())?;
                }
                let file =
                    fs::File::create_new(path).map_err(|e| format!("{}: {e}", path.display()))?;
                created.push(path.clone());
                self.artifact(*kind, BufWriter::new(file), scan, p, geo)?;
            }
            extra(stage)
        };
        let result = match bundle {
            Some(path) => export::write_bundle_with_extra(scan, path, geo, overlays, |stage| {
                write_files(Some(stage))
            }),
            None => {
                export::validate(scan)?;
                write_files(None)
            }
        };
        if result.is_err() {
            for path in created {
                let _ = fs::remove_file(path);
            }
        }
        result
    }

    fn artifact(
        &self,
        kind: Artifact,
        mut out: BufWriter<fs::File>,
        scan: &Scan,
        page: &PageScan,
        geo: Option<[f64; 6]>,
    ) -> Result<(), String> {
        let json =
            |out: &mut BufWriter<fs::File>, v: &serde_json::Value| write_json(out, v, self.compact);
        match kind {
            Artifact::Scan => write_json(&mut out, scan, self.compact)?,
            Artifact::Placement => json(&mut out, &export::placement(scan))?,
            Artifact::Structure => json(&mut out, &export::structure(scan))?,
            Artifact::Connections => json(&mut out, &export::connections(scan))?,
            Artifact::ConnectionsSvg => out
                .write_all(export::connections_svg(page).as_bytes())
                .map_err(|e| e.to_string())?,
            Artifact::Local => json(&mut out, &export::spatial(scan, None)?)?,
            Artifact::Geographic => json(&mut out, &export::spatial(scan, geo)?)?,
            Artifact::Svg => out
                .write_all(export::svg(page, true).as_bytes())
                .map_err(|e| e.to_string())?,
            Artifact::Compact => {
                write_json(&mut out, &openplantrace_rs::compact::encode(scan)?, true)?
            }
            Artifact::CompactGzip => {
                let value = openplantrace_rs::compact::encode(scan)?;
                let encoder = flate2::write::GzEncoder::new(&mut out, flate2::Compression::fast());
                let mut writer = BufWriter::new(encoder);
                write_json(&mut writer, &value, true)?;
                writer
                    .into_inner()
                    .map_err(|e| e.to_string())?
                    .finish()
                    .map_err(|e| e.to_string())?;
            }
        }
        out.flush().map_err(|e| e.to_string())
    }
}
fn write_json(out: &mut impl Write, v: &impl Serialize, compact: bool) -> Result<(), String> {
    if compact {
        serde_json::to_writer(out, v)
    } else {
        serde_json::to_writer_pretty(out, v)
    }
    .map_err(|e| e.to_string())
}
