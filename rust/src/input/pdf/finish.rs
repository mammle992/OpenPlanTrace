//! Separate finish-area paint history; frozen detection/paint inventories are unchanged.
use super::{Graphics, Point, Rect, Shape, Subpath};
use crate::finish_visibility::PaintHistory;

fn clip_polygon(g: &Graphics) -> Vec<Point> {
    let Some(r) = g.clip else {
        return vec![];
    };
    let mut out = vec![
        Point::new(r.l, r.t),
        Point::new(r.r, r.t),
        Point::new(r.r, r.b),
        Point::new(r.l, r.b),
    ];
    for &(p, n) in &g.clip_planes {
        if out.is_empty() {
            break;
        }
        let mut next = vec![];
        for (&a, &b) in out.iter().zip(out.iter().cycle().skip(1)) {
            let x = (a - p).dot(n);
            let y = (b - p).dot(n);
            let (ia, ib) = (x >= 0., y >= 0.);
            if ia {
                next.push(a);
            }
            if ia != ib {
                next.push(a + (b - a) * (x / (x - y)));
            }
        }
        out = next;
    }
    out
}
fn unknown(history: &mut PaintHistory, order: u64, bounds: Option<Rect>, g: &Graphics) {
    if let Some(b) = bounds.and_then(|r| g.clip.or(g.uncertain_clip).and_then(|c| c.intersect(r))) {
        history.record_bounds(order, Point::new(b.l, b.t), Point::new(b.r, b.b));
    }
}
pub(super) fn observe(
    history: &mut PaintHistory,
    id: &str,
    shape: &Shape,
    sub: &Subpath,
    g: &Graphics,
    order: u64,
) {
    if sub.curves != 0
        || g.finish_clip_unsafe
        || g.masked
        || !g.pen_state_safe
        || !g.undashed_pen
        || !g.stroke
        || g.stroke_alpha != 1.
        || g.white_pen
    {
        return;
    }
    if matches!(
        shape,
        Shape::Line { .. } | Shape::Polyline { filled: false, .. }
    ) {
        history.record_ink(id.to_owned(), order);
    }
}
pub(super) fn paint(
    history: &mut PaintHistory,
    subs: &[Subpath],
    g: &Graphics,
    fill: bool,
    stroke: bool,
    even_odd: bool,
    order: u64,
) {
    if fill && (g.fill_alpha != 0. || !g.pen_state_safe || g.masked) {
        let count = subs.iter().map(|p| p.ps.len()).sum::<usize>();
        if g.masked
            || g.clip.is_none()
            || count > 512
            || subs.iter().any(|p| p.curves != 0)
            || (!g.concave_clips.is_empty() && !subs.iter().all(|p| g.contains_subpath(p, true)))
        {
            unknown(
                history,
                order,
                subs.iter().filter_map(Subpath::bounds).reduce(Rect::union),
                g,
            );
        } else {
            history.record_paths(
                order,
                subs.iter()
                    .filter(|p| p.ps.len() >= 3)
                    .map(|p| p.ps.clone())
                    .collect(),
                even_odd,
                clip_polygon(g),
            );
        }
    }
    if !stroke
        || (g.stroke_alpha == 0. && g.pen_state_safe && !g.masked)
        || !(g.white_pen || !g.stroke || g.masked || !g.pen_state_safe || !g.pen_state_safe)
    {
        return;
    }
    if g.pen_width <= 0. {
        // A PDF hairline has device-dependent positive width, not zero area.
        // Without a target render resolution it cannot prove a clear finish.
        if let Some(bounds) = g.clip.or(g.uncertain_clip) {
            history.record_bounds(
                order,
                Point::new(bounds.l, bounds.t),
                Point::new(bounds.r, bounds.b),
            );
        }
        return;
    }
    let [a, b, c, d, _, _] = g.ctm.0;
    let transform = |v: Point| Point::new(a * v.x + c * v.y, b * v.x + d * v.y);
    let radius = (g.pen_width * 0.5).max(0.00001);
    for path in subs {
        if let [p, q] = path.ps.as_slice()
            && path.curves == 0
            && p != q
            && g.clip.is_some()
            && !g.masked
        {
            let delta = *q - *p;
            let dir = Point::new(d * delta.x - c * delta.y, a * delta.y - b * delta.x).unit();
            let off = transform(Point::new(-dir.y, dir.x) * radius);
            let tip = if g.projecting_cap {
                transform(dir * (radius * (a * d - b * c).signum()))
            } else {
                Point::default()
            };
            let footprint = [
                *p - tip + off,
                *q + tip + off,
                *q + tip - off,
                *p - tip - off,
            ];
            if g.concave_clips.is_empty() || g.contains_footprint(&footprint) {
                history.record_paths(order, vec![footprint.to_vec()], false, clip_polygon(g));
            } else {
                unknown(history, order, Rect::points(&footprint), g);
            }
        } else if let Some(r) = path.bounds() {
            let reach = radius * g.miter_limit.max(2_f64.sqrt());
            let dx = reach * a.hypot(c);
            let dy = reach * b.hypot(d);
            unknown(
                history,
                order,
                Some(Rect {
                    l: r.l - dx,
                    r: r.r + dx,
                    t: r.t - dy,
                    b: r.b + dy,
                }),
                g,
            );
        }
    }
}

// A text run can erase a finish without contributing a source primitive. Glyph
// outlines (including Type3 programs) are not available here, so an uncertain or
// potentially erasing run conservatively invalidates its graphics clip.
pub(super) fn text(history: &mut PaintHistory, g: &Graphics, order: u64) {
    if g.tr >= 4 && g.tr <= 7 {
        if let Some(bounds) = g.clip.or(g.uncertain_clip) {
            history.record_bounds(
                order,
                Point::new(bounds.l, bounds.t),
                Point::new(bounds.r, bounds.b),
            );
        }
    }
    let mode = g.tr.rem_euclid(4);
    let unknown = g.masked || !g.pen_state_safe;
    let fill = matches!(mode, 0 | 2) && (g.fill_alpha != 0. || unknown) && (!g.fill || unknown);
    let stroke = matches!(mode, 1 | 2)
        && (g.stroke_alpha != 0. || unknown)
        && (g.white_pen || !g.stroke || !g.pen_state_safe || unknown);
    if fill || stroke {
        if let Some(bounds) = g.clip.or(g.uncertain_clip) {
            history.record_bounds(
                order,
                Point::new(bounds.l, bounds.t),
                Point::new(bounds.r, bounds.b),
            );
        }
    }
}

pub(super) fn unknown_text(history: &mut PaintHistory, g: &Graphics, order: u64) {
    let mode = g.tr.rem_euclid(4);
    let unknown = g.masked || !g.pen_state_safe;
    if (matches!(mode, 0 | 2) && (g.fill_alpha != 0. || unknown))
        || (matches!(mode, 1 | 2) && (g.stroke_alpha != 0. || unknown))
    {
        if let Some(bounds) = g.clip.or(g.uncertain_clip) {
            history.record_bounds(
                order,
                Point::new(bounds.l, bounds.t),
                Point::new(bounds.r, bounds.b),
            );
        }
    }
}
