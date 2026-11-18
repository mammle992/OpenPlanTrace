use std::{
    fs,
    path::{Path, PathBuf},
};

#[cfg(feature = "onnx")]
pub fn run(args: Vec<String>) -> Result<(), String> {
    use openplantrace_rs::visual::source_review::{ModelBundle, Regions, review_pdf, sha256};
    if args.len() != 7 || args[1] != "--bundle" || args[3] != "--regions" || args[5] != "--out" {
        return Err("usage: object-review INPUT.pdf --bundle MODEL_DIRECTORY --regions REGIONS.json --out NEW_DIRECTORY".into());
    }
    let input = Path::new(&args[0]);
    if !input
        .extension()
        .is_some_and(|e| e.eq_ignore_ascii_case("pdf"))
    {
        return Err("source-pixel object review requires PDF input".into());
    }
    let bytes = bounded_source(input)?;
    let bundle = ModelBundle::load(Path::new(&args[2]))?;
    if fs::metadata(&args[4]).map_err(|e| e.to_string())?.len() > 4 << 20 {
        return Err("source region metadata exceeds4MiB".into());
    }
    let region_bytes = fs::read(&args[4]).map_err(|e| e.to_string())?;
    let regions: Regions = serde_json::from_slice(&region_bytes).map_err(|e| e.to_string())?;
    if regions.schema != "openplantrace.object-review-regions.v1"
        || regions.source_sha256 != sha256(&bytes)
    {
        return Err("source-region schema or input PDF hash mismatch".into());
    }
    openplantrace_rs::export::publish_directory(Path::new(&args[6]), |stage| {
        let report = review_pdf(
            &bytes,
            &bundle,
            &regions.regions,
            "provided-source-regions",
            stage,
        )?;
        fs::write(
            stage.join("object-review.json"),
            serde_json::to_vec_pretty(&report).map_err(|e| e.to_string())?,
        )
        .map_err(|e| e.to_string())?;
        println!(
            "Reviewed {} original PDF regions; {} withheld source crops. Results are advisory.",
            report.predictions.len(),
            report.warnings.len()
        );
        Ok(())
    })
}
#[cfg(not(feature = "onnx"))]
pub fn run(_args: Vec<String>) -> Result<(), String> {
    Err("object review requires an ONNX-enabled Rust build".into())
}

pub fn bounded_source(path: &Path) -> Result<Vec<u8>, String> {
    use std::io::Read;
    let file = fs::File::open(path).map_err(|e| e.to_string())?;
    let mut bytes = Vec::new();
    file.take((128 << 20) + 1)
        .read_to_end(&mut bytes)
        .map_err(|e| e.to_string())?;
    if bytes.len() > 128 << 20 {
        return Err("PDF source exceeds128MiB".into());
    }
    Ok(bytes)
}

pub struct ScannerReview {
    #[cfg(feature = "onnx")]
    bundle: openplantrace_rs::visual::source_review::ModelBundle,
    input: PathBuf,
}
impl ScannerReview {
    pub fn load(bundle: &Path, input: &Path) -> Result<Self, String> {
        #[cfg(not(feature = "onnx"))]
        {
            let _ = (bundle, input);
            return Err("object review requires an ONNX-enabled Rust build".into());
        }
        #[cfg(feature = "onnx")]
        {
            if !input
                .extension()
                .is_some_and(|e| e.eq_ignore_ascii_case("pdf"))
            {
                return Err("source-pixel object review requires PDF input".into());
            }
            Ok(Self {
                bundle: openplantrace_rs::visual::source_review::ModelBundle::load(bundle)?,
                input: input.to_owned(),
            })
        }
    }
    pub fn write(&self, scan: &openplantrace_rs::model::Scan, stage: &Path) -> Result<(), String> {
        #[cfg(feature = "onnx")]
        {
            use openplantrace_rs::visual::{
                source_proposals::propose,
                source_review::{review_pdf, sha256},
            };
            let bytes = bounded_source(&self.input)?;
            let proposals = propose(scan, 200)?;
            let regions: Vec<_> = proposals
                .proposals
                .iter()
                .map(|p| p.region.clone())
                .collect();
            // Review proposals carry their own finite source witnesses. They do
            // not edit objects, hosted openings or physical placement decisions.
            let proposal_report = serde_json::json!({
                "schema": "openplantrace.source-object-proposals.v1",
                "sourceSha256": sha256(&bytes),
                "scanSchemaVersion": scan.schema_version,
                "advisoryOnly": true,
                "automaticPlacementAllowed": false,
                "proposalSet": proposals,
            });
            fs::write(
                stage.join("object-proposals.json"),
                serde_json::to_vec_pretty(&proposal_report).map_err(|e| e.to_string())?,
            )
            .map_err(|e| e.to_string())?;
            let mut report = review_pdf(
                &bytes,
                &self.bundle,
                &regions,
                "native-source-evidence-proposals",
                stage,
            )?;
            if proposals.truncated {
                report.warnings.push(format!(
                    "Object review emitted {} of {} source-evidence proposals; {} omitted by the {}-region limit.",
                    proposals.counts.emitted,
                    proposals.counts.total_before_limit,
                    proposals.counts.omitted,
                    proposals.limit,
                ));
            }
            fs::write(
                stage.join("object-review.json"),
                serde_json::to_vec_pretty(&report).map_err(|e| e.to_string())?,
            )
            .map_err(|e| e.to_string())?;
        }
        #[cfg(not(feature = "onnx"))]
        {
            let _ = (scan, stage, &self.input);
        }
        Ok(())
    }
}
