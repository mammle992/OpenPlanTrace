//! Save the exact native RGB crop, its deterministic object links and optional model evidence.
use super::crop;
use crate::model::Scan;
use flate2::{Compression, Crc, write::ZlibEncoder};
use serde_json::{Value, json};
use std::{
    fs,
    io::{self, BufWriter, Write},
    path::Path,
};

pub const SCHEMA: &str = "openplantrace.rust.kvemo-crops.v1";
pub struct Options {
    pub width: usize,
    pub height: usize,
    pub max_crops: usize,
    pub padding: f64,
}
impl Default for Options {
    fn default() -> Self {
        Self {
            width: 224,
            height: 224,
            max_crops: 200,
            padding: 18.,
        }
    }
}
impl Options {
    pub fn validate(&self) -> Result<(), String> {
        if self.width == 0
            || self.height == 0
            || self.width.saturating_mul(self.height) > 4_194_304
            || !self.padding.is_finite()
            || self.padding < 0.
        {
            return Err("invalid crop dimensions or padding".into());
        }
        Ok(())
    }
}
fn chunk(out: &mut impl Write, kind: &[u8; 4], data: &[u8]) -> io::Result<()> {
    out.write_all(&(data.len() as u32).to_be_bytes())?;
    out.write_all(kind)?;
    out.write_all(data)?;
    let mut crc = Crc::new();
    crc.update(kind);
    crc.update(data);
    out.write_all(&crc.sum().to_be_bytes())
}
fn png(path: &Path, rgb: &[u8], width: usize, height: usize) -> io::Result<()> {
    let mut writer = BufWriter::new(fs::File::create_new(path)?);
    writer.write_all(b"\x89PNG\r\n\x1a\n")?;
    let mut header = [0; 13];
    header[..4].copy_from_slice(&(width as u32).to_be_bytes());
    header[4..8].copy_from_slice(&(height as u32).to_be_bytes());
    header[8] = 8;
    header[9] = 2;
    chunk(&mut writer, b"IHDR", &header)?;
    let mut data = ZlibEncoder::new(Vec::new(), Compression::fast());
    for row in rgb.chunks_exact(width * 3) {
        data.write_all(&[0])?;
        data.write_all(row)?
    }
    chunk(&mut writer, b"IDAT", &data.finish()?)?;
    chunk(&mut writer, b"IEND", &[])?;
    writer.flush()
}
/// The caller supplies a private staging directory and the final directory used for recorded image paths.
pub fn write(
    scan: &Scan,
    input: &Path,
    stage: &Path,
    destination: &Path,
    options: &Options,
) -> Result<(), String> {
    options.validate()?;
    let mut manifest = BufWriter::new(
        fs::File::create_new(stage.join("kvemo-crops.jsonl")).map_err(|e| e.to_string())?,
    );
    let mut attempted = 0;
    for page in &scan.pages {
        if attempted >= options.max_crops {
            break;
        }
        for (object, ids, member) in crop::targets(page)? {
            if attempted >= options.max_crops {
                break;
            }
            attempted += 1;
            let Some(crop) = crop::render(
                page,
                object,
                &member,
                options.width,
                options.height,
                options.padding,
            ) else {
                continue;
            };
            let file = format!("crop-{attempted:06}.png");
            png(&stage.join(&file), &crop.rgb, options.width, options.height)
                .map_err(|e| e.to_string())?;
            let class=scan.visual_classification.as_ref().and_then(|report|{
                let p=report.predictions.iter().find(|p|p.page_number==page.page_number&&p.representative_object_id==object.id)?;
                let top=&p.alternatives[0];
                Some(json!({"label":top.label,"category":top.category,"confidence":top.confidence,"accepted":p.accepted,"alternatives":p.alternatives,
                "modelName":report.model_name,"modelVersion":report.model_version,"modelSha256":report.model_sha256,"inferenceEngine":report.engine}))
            });
            let min = crop.area.min;
            let max = crop.area.max;
            let (low, high) = (member.bounds.min, member.bounds.max);
            let area = (max.x - min.x) * (max.y - min.y);
            let mut row = json!({"schemaVersion":SCHEMA,"engine":"OpenPlanTrace Rust","documentId":input.file_name().and_then(|s|s.to_str()),
                "detectionId":object.id,"detectionKind":if ids.len()>1{"object-group"}else{"object"},"objectIds":ids,"reviewKey":member.signature(), "groupSignature":member.signature(),"candidateKind":member.kind, "symbolName":member.symbol_name,
                "detectedTags":member.tags.iter().map(|t|t.value.as_str()).collect::<Vec<_>>(),"sourceKind":if object.footprint.is_some(){"ClosedGeometry"}else if member.symbol_name.is_some(){"CadSymbol"}else if object.source_ids.len()>1{"CompositeLinework"}else{"ClosedGeometry"},
                "pageNumber":page.page_number,"pageWidth":page.width,"pageHeight":page.height,"coordinateSpace":"page","coordinateOrigin":"top-left","coordinateYAxisDirection":"down",
                "bounds":{"x":low.x,"y":low.y,"width":high.x-low.x,"height":high.y-low.y},"cropBounds":{"x":min.x,"y":min.y,"width":max.x-min.x,"height":max.y-min.y},
                "objectToCropAreaRatio":(high.x-low.x)*(high.y-low.y)/area,"cropToPageAreaRatio":area/(page.width*page.height),
                "category":object.category,"label":object.name,"sourcePrimitiveIds":crop.sources,"sourceEvidence":{"primitiveCount":crop.sources.len()},
                "imageFileName":file,"imagePath":destination.join(&file),"imageWidth":options.width,"imageHeight":options.height,"imageColorSpace":"RGB"});
            row["classification"] = class.unwrap_or(Value::Null);
            serde_json::to_writer(&mut manifest, &row).map_err(|e| e.to_string())?;
            manifest.write_all(b"\n").map_err(|e| e.to_string())?;
        }
    }
    manifest.flush().map_err(|e| e.to_string())
}
