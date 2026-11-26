# Alpha 50 accuracy and runtime

OpenPlanTrace Rust 0.12.62-alpha.50 emits native scan schema v45. The original Windows reference evaluation compared alpha 50 with alpha 49 in five paired rounds on eleven scopes. The 110 completed scans covered native vector, raster and mixed PDFs. Independent QA passed 385 deep checks and 110 source-aligned image comparisons. The scanner rejected 12 altered evidence controls. The Rust suite passed 396 tests, with two existing tests ignored. The Rust viewer passed its release build check.

## Placement checked against the source

| Drawing | Alpha 48 Ready | Alpha 49 Ready | Alpha 50 Ready | Source review |
| --- | ---: | ---: | ---: | --- |
| Large vector plan | 251 | 258 | 262 | Eleven additional wall stocks beside pocket doors since alpha 48. The existing 121 opening observations, 23 archived panes and two exterior routes remain intact. |
| Eight-sheet raster plan | 77 | 79 | 80 | Two horizontal stock courses and a 118 × 17 pixel upright are ready. Source-supported connections add no wall fill across the tested gaps. All seven other sheets are unchanged. |
| Vector plan with masked exterior | 12 | 17 | 18 | Five stepped stock pieces and a visible 101.91-point connector are ready. The placement graph now has one component. The exterior remains open at masked sections, and all three signed doors and three pane observations are preserved. |
| Medium vector and mixed versions | 54 each | 54 each | 54 each | The five canonical geometry exports match alpha 49 on each version. Both have 11 doors, 8 windows and the same closed exterior route. |

Review each proposed wall span and incomplete boundary against the original drawing. The large vector drawing still has missing interior walls, and some sliding-door symbols still need review. The controls do not establish a general recall rate. No learned inference was used in this evaluation.

## Windows reference runtime

These are medians from one Windows machine. Elapsed time includes startup, I/O and host waits. CPU time is measured separately.

| Input | Alpha 49 elapsed | Alpha 50 elapsed | Alpha 49 CPU | Alpha 50 CPU |
| --- | ---: | ---: | ---: | ---: |
| Large vector plan | 3.32 s | 3.91 s | 3.31 s | 3.45 s |
| Medium vector | 0.62 s | 0.86 s | 0.59 s | 0.50 s |
| Medium mixed | 0.50 s | 1.03 s | 0.50 s | 0.50 s |
| Eight-sheet raster | 0.90 s | 1.11 s | 0.88 s | 0.88 s |
| Vector plan with masked exterior, first sheet | 0.16 s | 0.58 s | 0.14 s | 0.14 s |

Median peak working set on the large vector plan was 122.1 MiB. A separate synthetic 506,959,100-byte PDF with 33 sheets passed all five full runs at a median of 1.86 seconds and 972.8 MiB peak working set. Fifteen selected-page scans and five controlled rejection checks also passed. Its light geometry primarily exercises PDF resources and page selection. Dense real plans can take longer.

## Windows distribution

The Windows executable is built from the same frozen Rust production sources with compiler paths remapped. Its package contains dependency notices and five authored BSD-2-Clause synthetic PDFs. The private evaluation drawings, screenshots and scan artifacts stay local. The synthetic examples exercise specific PDF paint and wall-visibility cases.

See [build provenance](BUILD-PROVENANCE.json) and [distribution parity](PUBLIC-PARITY-SUMMARY.json) for the checks run on the published executable.
