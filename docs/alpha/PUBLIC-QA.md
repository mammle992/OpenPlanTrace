# Public QA summary: experimental Rust alpha

This report summarizes the sealed evaluation of OpenPlanTrace Rust `0.12.32-alpha.20`, native scan schema `v16`, against preview 19. It reports controlled placement results rather than general scanner accuracy. Private architectural evaluation drawings and identifying evidence remain local; this public summary does not include project names, drawings, screenshots, source paths or scan outputs.

## Public build provenance — verified

The public scanner was rebuilt from the **same 112 frozen production files**, using the same release configuration and features, with compiler/dependency paths remapped and linker debug-path metadata changed for privacy. The executables have different checksums:

| Executable | SHA256 |
|---|---|
| Public privacy-remapped scanner | `6dd19a8e61cfce657cb042793c6b41f9cbece582ca47b4d5a8d9f74f8cd89827` |
| Original sealed evaluation reference | `2853c4d28bf591d2a2210f62ecdaee4eb200937f2394b12d9bbdcf2c06a4ec3f` |

Separate rebuild verification passed **five fresh paired rounds on the same 12 evaluation inputs**: 120 scanner processes and 60 pairs. Five public synthetic samples added 10 fresh processes and five pairs, producing the expected Ready-wall counts **[1,1,0,0,0]** on both executables.

All **325 paired comparisons of the five normal JSON artifacts are byte-for-byte identical**. All five cohort repeats agree, fresh reference exports match the original validated captures, and gzip payloads decode exactly to native JSON. All **390 public-build native/source-aware validations** pass: 360 cohort artifacts and 30 synthetic-sample artifacts. The reference's existing 360-validation proof is reused after confirming that its fresh exports match the original validated files.

The original sealed source identity, QA evidence and metrics remain unchanged. The reference timings below describe the original preview 19/20 evaluation; additional rebuild-parity timing records are separate and are not combined with those metrics. This parity check does not establish accuracy on new drawings.

The public package contains the scanner, third-party notices, documentation and generated synthetic PDFs. Optional trained models, model metadata and evaluator binaries remain local and are omitted. **No learned inference was used for the reported scanner evaluation.**

## Main result

Existing wall-placement controls improve **19/22 → 20/22**. All **32 clear areas**, **9 signed doorway controls**, **162 observed window records** and **71 Door records** are preserved. All **176 previously Ready walls**, their decisions and barriers, **7 material corner polygons** and **43 supported contact/portal links** remain intact.

The tested inputs contain **182 Ready walls** after the update: five additional development spans and one source-approved validation span. This is a conservative readiness gain, not a claim that 20/22 represents overall accuracy or complete wall recall.

| Measure | Preview 19 | Evaluated alpha |
|---|---:|---:|
| Existing placement controls passed | 19/22 | **20/22** |
| Clear-area controls passed | 32/32 | **32/32** |
| Signed doorway controls passed | 9/9 | **9/9** |
| Original glazing controls passed | 4/4 | **4/4** |
| Development Ready walls | 174 | **179** |
| Validation Ready walls | 2 | **3** |
| Total Ready walls | 176 | **182** |

## Development findings

Ten development inputs were compared. Five real insulated-wall spans became Ready: a long exterior wall beside stairs, an internal span above a perpendicular doorway, two exterior segments ending at window frames, and an exterior body containing two batt layers and both narrow finishes. Every new span was inspected over its complete extent against the original drawing. No new furniture strip, parallel duplicate or doorway/window crossing was found.

The exterior target's source controls were fixed before candidate output. Four other gains were investigated after development output and then checked against the originals; those investigations are not new blinded recall targets. The existing control denominators were unchanged.

Two existing placement controls remain missed: an internal lower jamb and an adjoining structural partition. Raster coverage is unchanged by this finish-selection update.

## Validation findings and limits

Two existing validation inputs, covering five PDF pages, were scanned only after the final executable and production sources were frozen. One input gains a real insulated partition, changing two Ready walls to three. The other input retains zero Ready walls. No production tuning followed validation.

For the input with the gain, a separate reviewer annotated the original drawing before seeing that input's scanner output, overlays or target coordinates. The reviewer knew that there was one gain and had received a tile identifier; this prior knowledge was disclosed. Annotation occurred after scanning, and the frozen annotations were then used for the post-pin source audit.

| Independently annotated source samples | Result |
|---|---:|
| Fully covered material samples | **1/34 → 2/34** |
| Material samples still missing full coverage | **32** |
| Clear-area samples with placed material | **0/14** |
| Signed doorway samples with placed material | **0/11** |
| Window/panel samples with placed material | **0/10** |

These are finite source samples, not exhaustive segmentation or a new overall recall denominator. They show one verified gain while also showing that validation placement coverage remains low. The validation inputs had been used for validation in earlier versions; they are not newly unseen projects. Reserved inputs were not opened, rendered, scanned or used for tuning.

## What the Rust change requires

New insulated finish material needs opposed wall faces, the exact union of certified repeating batt paint, valid pattern alignment, complete narrow finish contours and explicit visibility evidence. Furniture claims, observed openings and overlap with existing Ready material remain vetoes.

The visibility pass protects both the finite band being placed and the actual contour edges supporting it, including remote closure caps. A background drawn before both local wall faces can pass. Later interior erasure, incomplete redraw and erasure of a supporting remote cap must fail. Source witness contours do not add wall material or create connections.

One older synthetic result is explicitly distinguished from a real failure: an unchanged valid batt core remains valid when a separate finish is erased. A different case, where an old full-width body actually includes erased finish, now loses that unsupported full-facing authority. Both behaviors are tested.

Native receipts retain bounded geometry, source identity and draw-order evidence. They are trusted producer evidence, not a serialized replay of the entire PDF paint history. The actual PDF producer is tested separately with adversarial inputs.

## Verification performed

- **Five fresh alternating serial rounds**, totaling **120 scanner processes and 60 pairs** over the same 12 inputs.
- **600 normal JSON exports** forming 120 exactly repeating five-run groups. Raw observations, original material inventories and opening records remain exact.
- **126 source-aligned figures**, including complete crops of all six new spans. All 24 repeated figure groups match exactly. Private figures are not published.
- **360 source-aware native validations**, plus six preflight artifact validations.
- **51 generated public adversarial PDF checks**, covering safe positives, finish erasure, tiny slivers, core preservation, text and Type3 glyphs, clipping, images, unsafe graphics states and strokes, local draw order, remote caps and compound fill unions. The optional complete-redraw case is conservatively withheld.
- **23 native corruption tests rejected**: 13 receipt/provenance mutations and 10 actual-contour mutations.
- **351 Rust tests passed; 2 existing tests ignored.** Native viewer compilation passed, along with 50 archived preview 19 development compatibility checks.

The smallest paint erasures are checked analytically from geometry; screenshots alone cannot establish subpixel visibility. Learned inference was not used for these scan timings, and model automatic acceptance remained disabled.

### Original flags and independent-checker corrections

The original timed capture retains **15 failure flags**, three per round. They concern an exported material-tile list that contains only evidence referenced by Ready decisions. The initial checker incorrectly treated that derived list as an immutable raw inventory, so legitimate new Ready walls caused additive-list failures.

Two independent strict audits establish that every original raw tile remains exact, every old exported tile remains byte-for-byte unchanged, and every added exported tile exactly matches certified native evidence used by a newly Ready wall. No other protected differences were found. After that audit, all **180 protected artifact comparisons** pass. The original failed capture, all flags, helpers, source pins and timings remain retained in the private audit trail; they were not silently replaced. There was no production change, source-truth change, threshold change, rescan or timing change after validation.

A separate correction made before the final pin replaced an overly strict requirement that each individual PDF paint operation cover an entire wall. The independent checker instead proves the complete geometric union of adjacent operations, with gap, pattern-phase, visibility and aperture checks retained. Original checker versions remain archived.

## Runtime cost of the sealed reference update

Timing includes process startup, scanning and normal exports. Other agent builds, scans and rendering were paused. Windows background activity and caches were uncontrolled; these are not cold-cache measurements.

The median of five per-round sums of process wall times increases **7.740 → 9.008 seconds, +16.4%**. This excludes comparison bookkeeping and is not an end-to-end batch latency. The sum of per-input medians is a different statistic: **7.871 → 9.018 seconds, +14.6%**. Summed CPU medians increase 15.9%.

The insulated-wall target increases **0.462 → 1.324 seconds**, or **2.87×**, with peak working set increasing approximately 0.707 MiB. Another development input with four gains increases **0.209 → 0.324 seconds**, with approximately 1.422 MiB additional peak working set. This release adds evidence-supported coverage at a measured runtime cost; it is not a speed improvement.

## Exterior continuity and remaining limitations

No new nonwall contact is accepted. Existing supported links remain intact, while the six added finite wall axes are separate components. Tested graph components increase **139 → 145** and flagged dangling ends **305 → 317**. Closed components and boundary candidates remain zero.

**The tested inputs do not produce a complete house outline.** Unsupported gaps remain flagged rather than snapped shut. Many real wall spans remain under review, including most of the independently sampled validation material. Furniture, openings, duplicates and exterior continuity require review against the original drawing before placement. These results do not establish reliable unattended placement or general accuracy across all PDFs.

This public report preserves the limitations and aggregate findings of the sealed local evaluation. The identified privacy-remapped public build passes the separate parity checks above. These results should not be assumed to certify arbitrary rebuilds or future versions.
