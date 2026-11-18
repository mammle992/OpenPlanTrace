use super::*;

fn line(x: f64, y: f64, end: f64) -> Value {
    json!({"start":{"x":x,"y":y},"end":{"x":end,"y":y}})
}
fn native() -> Value {
    json!({"schemaVersion":"openplantrace.rust.structure.v2","pages":[{"pageNumber":1,"width":100.,"height":50.,
           "wallRuns":[{"id":"w1","centerLine":line(0.,10.,100.)},{"id":"w2","centerLine":line(100.,30.,0.)}]}]})
}
fn truth() -> Value {
    json!({"schemaVersion":TRUTH_SCHEMA,"completeTruthSet":true,"pages":[{"pageNumber":1,"width":100.,"height":50.}],
           "entries":[{"id":"t1","importance":"Major","pageNumber":1,"centerLine":line(100.,10.,0.)},
                      {"id":"t2","pageNumber":1,"centerLine":line(0.,30.,100.)}]})
}
fn report(t: &Value, s: &Value) -> Value {
    evaluate(&Dataset::read(t).unwrap(), &Structure::read(s).unwrap())
}
#[test]
fn native_reversed_endpoints_complete_and_partial_truth() {
    let mut t = truth();
    let s = native();
    let exact = report(&t, &s);
    assert_eq!(exact["passed"], true);
    assert_eq!(exact["metrics"]["precision"], 1.0);
    assert_eq!(exact["metrics"]["meanEndpointDistance"], 0.);
    assert_eq!(exact["matches"][0]["truthId"], "t1");
    assert!(exact["metrics"].get("solverObjectiveScore").is_none());
    t["completeTruthSet"] = false.into();
    t["entries"].as_array_mut().unwrap().pop();
    let partial = report(&t, &s);
    assert_eq!(partial["passed"], true);
    assert!(partial["metrics"].get("precision").is_none());
    assert_eq!(partial["falsePositives"], json!([]));
}
#[test]
fn signed_not_wall_bounds_and_one_to_one_importance_win() {
    let mut t = truth();
    t["entries"][1]["centerLine"] = line(0., 10., 100.);
    t["entries"].as_array_mut().unwrap().extend([
        json!({"id":"excluded","pageNumber":1,"labelKind":"NotWall","bounds":{"x":0.,"y":29.,"width":75.,"height":2.}}),
        json!({"id":"excluded-line","pageNumber":1,"labelKind":"NotWall","centerLine":line(100.,30.,0.)})
    ]);
    let r = report(&t, &native());
    assert_eq!(r["passed"], false);
    assert_eq!(r["matches"][0]["truthId"], "t1");
    assert_eq!(r["misses"][0]["truthId"], "t2");
    assert_eq!(r["falsePositives"][0]["wallRunId"], "w2");
    assert_eq!(r["notWallViolations"][0]["truthId"], "excluded-line");
    assert_eq!(r["notWallViolations"][1]["overlapRatio"], 0.76);
}
#[test]
fn unverified_native_frame_and_document_fail_their_individual_gates() {
    let mut s = native();
    s["pages"][0].as_object_mut().unwrap().remove("width");
    s["pages"][0].as_object_mut().unwrap().remove("height");
    let mut t = truth();
    t["documentId"] = "other".into();
    let r = report(&t, &s);
    assert_eq!(r["assertions"][0]["passed"], false);
    assert_eq!(r["assertions"][1]["passed"], false);
    assert_eq!(r["metrics"]["matchedWallCount"], 2);
}
#[test]
fn curve_cannot_pass_as_its_chord() {
    let mut s = native();
    s["pages"][0]["wallRuns"][0]["curve"] = json!({"center":{"x":50.,"y":10.},"radius":50.,"startAngle":std::f64::consts::PI,"sweep":std::f64::consts::PI});
    let r = report(&truth(), &s);
    assert_eq!(r["metrics"]["matchedWallCount"], 1);
    assert_eq!(
        r["metrics"]["falsePositiveDrawingLength"],
        round(std::f64::consts::PI * 50.)
    );
}
#[test]
fn truth_validation_rejects_invalid_gates_duplicates_and_coordinates() {
    let mut t = truth();
    t["qualityGate"] = json!({"minPrecision":1.1});
    assert!(validate(&t).is_err());
    let mut t = truth();
    t["entries"][1]["id"] = "t1".into();
    assert!(validate(&t).is_err());
    let mut t = truth();
    t["coordinateOrigin"] = "BottomLeft".into();
    assert!(validate(&t).is_err());
}
