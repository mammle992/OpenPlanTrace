//! Opt-in wall-clock spans for a single CLI scan. Timings never enter scan artifacts.
use serde::Serialize;
use std::{
    cell::RefCell, fs::OpenOptions, io::BufWriter, marker::PhantomData, path::Path, rc::Rc,
    time::Instant,
};

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Timing {
    stage: &'static str,
    parent: Option<usize>,
    page: Option<u32>,
    start_ns: u64,
    end_ns: u64,
}

struct Trace {
    start: Instant,
    spans: Vec<Timing>,
    active: Option<usize>,
}

thread_local! {
    static TRACE: RefCell<Option<Trace>> = const { RefCell::new(None) };
}

impl Trace {
    fn now(&self) -> u64 {
        self.start.elapsed().as_nanos().min(u64::MAX as u128) as u64
    }
}

/// Start and write an explicit local timing sidecar. The single-threaded scan
/// records inclusive spans; subtract direct child durations for exclusive time.
pub fn capture(path: &Path, scan: impl FnOnce() -> Result<(), String>) -> Result<(), String> {
    if path.exists() {
        return Err("stage profile already exists; choose a fresh file".into());
    }
    TRACE.with_borrow_mut(|state| {
        if state.is_some() {
            return Err("a stage profile is already active on this thread".to_string());
        }
        *state = Some(Trace {
            start: Instant::now(),
            spans: Vec::new(),
            active: None,
        });
        Ok(())
    })?;
    struct Reset;
    impl Drop for Reset {
        fn drop(&mut self) {
            TRACE.with_borrow_mut(|state| *state = None);
        }
    }
    let _reset = Reset;
    scan()?;
    let trace = TRACE
        .with_borrow_mut(Option::take)
        .expect("active timing trace");
    #[derive(Serialize)]
    #[serde(rename_all = "camelCase")]
    struct Profile {
        schema_version: &'static str,
        wall_ns: u64,
        spans: Vec<Timing>,
    }
    let profile = Profile {
        schema_version: "openplantrace.rust.stage-profile.v1",
        wall_ns: trace.now(),
        spans: trace.spans,
    };
    let file = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(path)
        .map_err(|e| format!("could not create stage profile: {e}"))?;
    let mut writer = BufWriter::new(file);
    serde_json::to_writer(&mut writer, &profile).map_err(|e| e.to_string())?;
    std::io::Write::flush(&mut writer).map_err(|e| e.to_string())?;
    Ok(())
}

pub struct Span {
    index: Option<usize>,
    thread_bound: PhantomData<Rc<()>>,
}

pub fn scope(stage: &'static str) -> Span {
    Span {
        index: begin(stage, None),
        thread_bound: PhantomData,
    }
}

pub(crate) fn page(stage: &'static str, number: u32) -> Span {
    Span {
        index: begin(stage, Some(number)),
        thread_bound: PhantomData,
    }
}

fn begin(stage: &'static str, page: Option<u32>) -> Option<usize> {
    TRACE.with_borrow_mut(|state| {
        let trace = state.as_mut()?;
        let parent = trace.active;
        let index = trace.spans.len();
        trace.spans.push(Timing {
            stage,
            parent,
            page: page.or_else(|| parent.and_then(|i| trace.spans[i].page)),
            start_ns: trace.now(),
            end_ns: 0,
        });
        trace.active = Some(index);
        Some(index)
    })
}

impl Span {
    pub fn next(&mut self, stage: &'static str) {
        self.end();
        self.index = begin(stage, None);
    }

    fn end(&mut self) {
        if let Some(index) = self.index.take() {
            TRACE.with_borrow_mut(|state| {
                let trace = state.as_mut().expect("active timing trace");
                debug_assert_eq!(trace.active, Some(index));
                trace.spans[index].end_ns = trace.now();
                trace.active = trace.spans[index].parent;
            });
        }
    }
}

impl Drop for Span {
    fn drop(&mut self) {
        self.end();
    }
}
