//! Convert reviewed correction datasets and crop manifests into reusable label rules.
mod corrections;
mod crops;
pub(crate) mod layers;

use serde_json::{Value, json};
use std::{
    collections::HashSet,
    fs,
    io::{self, BufRead, BufReader, Read, Write},
    path::{Path, PathBuf},
    process::ExitCode,
};

use openplantrace_rs::object_labels::{CATEGORIES, KINDS, Profile, SCHEMA};
pub(crate) fn load_objects(paths: &[PathBuf]) -> Result<Vec<Profile>, String> {
    paths
        .iter()
        .map(|p| {
            super::validate::read_json(p)
                .and_then(Profile::parse)
                .map_err(|e| format!("{}: {e}", p.display()))
        })
        .collect()
}
const SOURCES: &[&str] = &[
    "Unknown",
    "TextPrimitive",
    "CadSymbol",
    "ClosedGeometry",
    "CompositeLinework",
    "WallComponentIsland",
];

fn cleaned(s: &str) -> Option<&str> {
    Some(s.trim()).filter(|x| !x.is_empty())
}
fn label(v: &Value) -> Option<&str> {
    v.as_str().and_then(cleaned)
}
fn canon<'a>(s: &str, names: &'a [&str]) -> Option<&'a str> {
    names.iter().copied().find(|v| s.eq_ignore_ascii_case(v))
}
fn unique<'a>(values: impl IntoIterator<Item = &'a str>, insensitive: bool) -> Vec<&'a str> {
    let mut seen = HashSet::new();
    values
        .into_iter()
        .filter(|s| {
            seen.insert(if insensitive {
                s.to_lowercase()
            } else {
                s.to_string()
            })
        })
        .collect()
}
fn labels(v: &Value, insensitive: bool) -> Vec<&str> {
    unique(
        v.as_array().into_iter().flatten().filter_map(label),
        insensitive,
    )
}
fn tag_pattern<'a>(tags: impl IntoIterator<Item = &'a str>) -> Option<String> {
    let mut family = None;
    let mut separated = true;
    for tag in tags {
        let tag = tag.trim();
        let (prefix, has_separator) = if let Some((prefix, _)) = tag.split_once(['-', '_']) {
            if prefix.is_empty() || !prefix.chars().all(char::is_alphabetic) {
                continue;
            }
            (prefix, true)
        } else {
            let end = tag.find(|c: char| !c.is_alphabetic()).unwrap_or(tag.len());
            if end == 0 || !tag[end..].chars().any(char::is_numeric) {
                continue;
            }
            (&tag[..end], false)
        };
        let prefix = prefix.to_uppercase();
        if family.as_ref().is_some_and(|old| old != &prefix) {
            return None;
        }
        family = Some(prefix);
        separated &= has_separator;
    }
    family.map(|s| format!("{s}{}", if separated { "-*" } else { "*" }))
}
fn plural(n: usize) -> &'static str {
    if n == 1 { "" } else { "s" }
}
fn rounded(n: f64) -> f64 {
    (n.clamp(0., 1.) * 100.).round() / 100.
}
fn rule(mut v: Value) -> Value {
    v.as_object_mut()
        .expect("rule object")
        .retain(|_, v| !v.is_null());
    v
}
#[derive(Default)]
struct Options {
    input: PathBuf,
    output: Option<PathBuf>,
    name: Option<String>,
    version: Option<String>,
    compact: bool,
    confirmed_only: bool,
    corrected_only: bool,
    crop_only: bool,
    classified_only: bool,
    negatives: bool,
    max_rules: Option<usize>,
}
impl Options {
    fn parse(args: Vec<String>, crops: bool) -> Result<Self, String> {
        let mut options = Self::default();
        let mut args = args.into_iter();
        let mut input = None;
        while let Some(arg) = args.next() {
            let mut next = || {
                args.next()
                    .ok_or_else(|| format!("missing value for {arg}"))
            };
            match arg.as_str() {
                "--json" => options.output = Some(next()?.into()),
                "--name" => options.name = cleaned(&next()?).map(str::to_owned),
                "--version" => options.version = cleaned(&next()?).map(str::to_owned),
                "--compact-json" => options.compact = true,
                "--confirmed-only" if !crops => options.confirmed_only = true,
                "--corrected-only" if !crops => options.corrected_only = true,
                "--crop-only" if crops => options.crop_only = true,
                "--classified-only" if crops => options.classified_only = true,
                "--include-hard-negatives" if crops => options.negatives = true,
                "--max-rules" if crops => {
                    options.max_rules = Some(
                        next()?
                            .parse::<i32>()
                            .ok()
                            .filter(|n| *n > 0)
                            .ok_or("--max-rules must be a positive integer")?
                            as usize,
                    )
                }
                _ if arg.starts_with('-') => return Err(format!("unknown option: {arg}")),
                _ if input.is_none() => input = Some(arg.into()),
                _ => return Err(format!("unexpected argument: {arg}")),
            }
        }
        options.input = input.ok_or("missing input path")?;
        if options.crop_only && options.classified_only
            || options.confirmed_only && options.corrected_only
        {
            return Err("the two inclusion filters cannot be combined".into());
        }
        Ok(options)
    }
}

/// Visit nonblank JSONL records one at a time, retaining line numbers for diagnostics.
pub(super) fn crop_lines(
    path: &Path,
    mut visit: impl FnMut(usize, Result<Value, String>),
) -> Result<(), String> {
    let mut reader =
        BufReader::new(fs::File::open(path).map_err(|e| format!("{}: {e}", path.display()))?);
    let mut buf = String::new();
    for number in 1.. {
        buf.clear();
        const MAX_LINE: u64 = 16 << 20;
        let size = (&mut reader)
            .take(MAX_LINE + 1)
            .read_line(&mut buf)
            .map_err(|e| e.to_string())? as u64;
        if size == 0 {
            break;
        }
        if size > MAX_LINE {
            return Err(format!("crop record on line {number} exceeds 16 MiB"));
        }
        let line = if number == 1 {
            buf.trim_start_matches('\u{feff}')
        } else {
            &buf
        }
        .trim();
        if line.is_empty() {
            continue;
        }
        visit(
            number,
            serde_json::from_str::<Value>(line)
                .map_err(|e| e.to_string())
                .and_then(|row| {
                    if row["schemaVersion"] == openplantrace_rs::visual::dataset::SCHEMA {
                        super::validate::crops::verify(&row, None)?;
                        Ok(row)
                    } else if label(&row["schemaVersion"]) != Some("openplantrace.kvemo-crops.v2") {
                        Err(format!(
                            "Unexpected schemaVersion '{}'.",
                            label(&row["schemaVersion"]).unwrap_or("(missing)")
                        ))
                    } else {
                        Ok(row)
                    }
                }),
        );
    }
    Ok(())
}

fn output(path: &Path, input: &Path, value: &Value, compact: bool) -> Result<(), String> {
    if let Ok(existing) = path.canonicalize()
        && Some(existing) == input.canonicalize().ok()
    {
        return Err("profile output would replace the input".into());
    }
    let dir = path
        .parent()
        .filter(|p| !p.as_os_str().is_empty())
        .unwrap_or(Path::new("."));
    fs::create_dir_all(dir).map_err(|e| e.to_string())?;
    let (tmp, mut file) = (0..)
        .find_map(|n| {
            let temp = dir.join(format!(
                ".openplantrace-profile-{}-{n}.tmp",
                std::process::id()
            ));
            match fs::File::create_new(&temp) {
                Ok(file) => Some(Ok((temp, file))),
                Err(e) if e.kind() == io::ErrorKind::AlreadyExists => None,
                Err(e) => Some(Err(e)),
            }
        })
        .unwrap()
        .map_err(|e| e.to_string())?;
    let published = (|| {
        let mut writer = io::BufWriter::new(&mut file);
        if compact {
            serde_json::to_writer(&mut writer, value)
        } else {
            serde_json::to_writer_pretty(&mut writer, value)
        }?;
        writer.flush()?;
        drop(writer);
        drop(file);
        fs::rename(&tmp, path)
    })();
    if let Err(e) = published {
        let _ = fs::remove_file(tmp);
        return Err(e.to_string());
    }
    Ok(())
}

pub fn run(cmd: &str, args: Vec<String>) -> Result<ExitCode, String> {
    let crops = cmd.starts_with("kvemo-");
    if args.is_empty()
        || args
            .iter()
            .any(|v| matches!(v.as_str(), "-h" | "--help" | "/?"))
    {
        let filters = if crops {
            "[--crop-only | --classified-only] [--include-hard-negatives] [--max-rules N]"
        } else {
            "[--confirmed-only | --corrected-only]"
        };
        println!(
            "{cmd} INPUT [--json OUTPUT] [--name NAME] [--version VERSION] {filters} [--compact-json]"
        );
        return Ok(if args.is_empty() {
            ExitCode::from(2)
        } else {
            ExitCode::SUCCESS
        });
    }
    let options = Options::parse(args, crops)?;
    let (profile, skipped) = if crops {
        match crops::profile(&options)? {
            Ok(result) => result,
            Err(issues) => {
                eprintln!("Invalid Kvemo crop records:\n{}", issues.join("\n"));
                return Ok(ExitCode::FAILURE);
            }
        }
    } else {
        let data: Value = super::validate::read_json(&options.input)?;
        (corrections::profile(&data, &options)?, 0)
    };
    let n = profile["rules"].as_array().unwrap().len();
    if n == 0 {
        eprintln!("No reusable object label rules were produced.")
    }
    if let Some(path) = &options.output {
        output(path, &options.input, &profile, options.compact)?;
        println!("Object label profile: {}\nRules: {n}", path.display());
        if crops {
            println!("Skipped entries: {skipped}");
        }
    } else {
        super::print_json(&profile, options.compact)?
    }
    Ok(ExitCode::SUCCESS)
}
