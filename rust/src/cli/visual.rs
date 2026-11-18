use openplantrace_rs::model::Scan;
use openplantrace_rs::visual::dataset;
use std::{
    collections::BTreeMap,
    path::{Path, PathBuf},
};

#[derive(Default)]
pub struct Arguments(BTreeMap<String, String>);
impl Arguments {
    pub fn take(
        &mut self,
        key: &str,
        args: &mut impl Iterator<Item = String>,
    ) -> Result<bool, String> {
        let alias;
        let key = if let Some(name) = key.strip_prefix("--visual-ai-") {
            alias = format!("--kvemo-{name}");
            alias.as_str()
        } else {
            key
        };
        if !key.starts_with("--kvemo-") {
            return Ok(false);
        }
        let value = match key {
            "--kvemo-channels-last" => String::new(),
            "--kvemo-model"
            | "--kvemo-crop-dir"
            | "--kvemo-labels"
            | "--kvemo-input-name"
            | "--kvemo-output-name"
            | "--kvemo-model-name"
            | "--kvemo-model-version"
            | "--kvemo-input-width"
            | "--kvemo-input-height"
            | "--kvemo-top-k"
            | "--kvemo-max-crops"
            | "--kvemo-crop-padding"
            | "--kvemo-min-confidence"
            | "--kvemo-mean"
            | "--kvemo-std" => args
                .next()
                .ok_or_else(|| format!("missing value for {key}"))?,
            _ => return Err(format!("unsupported visual option {key}")),
        };
        if self.0.insert(key.to_owned(), value).is_some() {
            return Err(format!("repeated option {key}"));
        }
        Ok(true)
    }
    pub fn prepare(self) -> Result<Runner, String> {
        let mut map = self.0;
        let root = map
            .remove("--kvemo-crop-dir")
            .map(std::path::absolute)
            .transpose()
            .map_err(|e| e.to_string())?;
        let model = map.remove("--kvemo-model").map(PathBuf::from);
        let selected = root.is_some() || model.is_some();
        if !selected && !map.is_empty() {
            return Err("Kvemo options require --kvemo-model or --kvemo-crop-dir".into());
        }
        let mut crop = dataset::Options::default();
        for (k, var) in [
            ("--kvemo-input-width", &mut crop.width),
            ("--kvemo-input-height", &mut crop.height),
            ("--kvemo-max-crops", &mut crop.max_crops),
        ] {
            if let Some(v) = map.remove(k) {
                *var = v.parse().map_err(|_| format!("invalid {k}"))?;
            }
        }
        if let Some(v) = map.remove("--kvemo-crop-padding") {
            crop.padding = v.parse().map_err(|_| "invalid --kvemo-crop-padding")?;
        }
        crop.validate()?;
        if model.is_none() && !map.is_empty() {
            return Err("normalization, labels and model options require --kvemo-model".into());
        }
        #[cfg(not(feature = "onnx"))]
        if model.is_some() {
            return Err(
                "this build has no ONNX engine; rebuild with cargo build --release --features onnx"
                    .into(),
            );
        }
        #[cfg(feature = "onnx")]
        let classifier = if let Some(path) = model {
            use openplantrace_rs::visual::classifier::{Classifier, Options};
            let mut o = Options::new(path);
            o.labels = map.remove("--kvemo-labels").map(Into::into);
            o.input_name = map.remove("--kvemo-input-name");
            o.output_name = map.remove("--kvemo-output-name");
            o.name = map.remove("--kvemo-model-name");
            o.version = map.remove("--kvemo-model-version");
            o.channels_first = map.remove("--kvemo-channels-last").is_none();
            o.width = crop.width;
            o.height = crop.height;
            o.max_crops = crop.max_crops;
            o.padding = crop.padding;
            if let Some(v) = map.remove("--kvemo-top-k") {
                o.top_k = v.parse().map_err(|_| "invalid --kvemo-top-k")?;
            }
            if let Some(v) = map.remove("--kvemo-min-confidence") {
                o.min_confidence = v.parse().map_err(|_| "invalid confidence")?
            }
            for (k, var) in [("--kvemo-mean", &mut o.mean), ("--kvemo-std", &mut o.std)] {
                if let Some(v) = map.remove(k) {
                    let vals: Vec<_> = v
                        .split(',')
                        .map(|s| s.trim().parse::<f32>())
                        .collect::<Result<_, _>>()
                        .map_err(|_| format!("invalid {k}"))?;
                    *var = vals
                        .try_into()
                        .map_err(|_| format!("{k} requires three RGB values"))?;
                }
            }
            Some(Classifier::load(o)?)
        } else {
            None
        };
        Ok(Runner {
            root,
            crop,
            #[cfg(feature = "onnx")]
            classifier,
        })
    }
}
pub struct Runner {
    pub root: Option<PathBuf>,
    pub crop: dataset::Options,
    #[cfg(feature = "onnx")]
    classifier: Option<openplantrace_rs::visual::classifier::Classifier>,
}
impl Runner {
    pub fn apply(&self, scan: &mut Scan) -> Result<(), String> {
        #[cfg(feature = "onnx")]
        if let Some(c) = &self.classifier {
            scan.visual_classification = Some(c.classify(scan)?);
        }
        #[cfg(not(feature = "onnx"))]
        let _ = scan;
        Ok(())
    }
    pub fn staged<T>(
        &self,
        bundle: Option<&Path>,
        stage: Option<&Path>,
        write: impl FnOnce(Option<(&Path, &Path)>) -> Result<T, String>,
    ) -> Result<T, String> {
        let Some(dest) = self.root.as_deref() else {
            return write(None);
        };
        let bundle = bundle
            .map(std::path::absolute)
            .transpose()
            .map_err(|e| e.to_string())?;
        let target = if let Some((relative, stage)) = bundle
            .as_ref()
            .and_then(|b| dest.strip_prefix(b).ok())
            .zip(stage)
        {
            if relative.as_os_str().is_empty()
                || !relative
                    .components()
                    .all(|p| matches!(p, std::path::Component::Normal(_)))
            {
                return Err(
                    "crop directory must be distinct from the scan output directory".into(),
                );
            }
            stage.join(relative)
        } else {
            dest.to_owned()
        };
        openplantrace_rs::export::publish_directory(&target, |s| write(Some((s, dest))))
    }
}
