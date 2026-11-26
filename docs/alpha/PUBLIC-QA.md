# Alpha 48 accuracy and runtime

This report covers OpenPlanTrace Rust 0.12.60-alpha.48 and native scan schema v43. The comparison build was Rust alpha 47. Five paired rounds on ten approved inputs plus five paired rounds on an additional vector sheet produced 110 scans. The inputs included native vector, scanned raster and mixed PDFs. An independent reviewer checked placement JSON against source-aligned images, performed 385 deep validations and verified 110 source-aligned comparisons. The Rust suite passed 393 tests, with two existing tests ignored. Nine deliberately altered geometry and evidence variants were rejected.

## Verified changes

| Drawing | Alpha 48 result |
| --- | --- |
| Large vector plan | Source geometry verifies the 223-node main route as an exterior. The 39-node detached route remains closed. Placement and all opening geometry reproduce alpha 47 exactly. The two routes still carry review flags. |
| Vector exterior detail | A 857.55-point north wall is ready and connected to the east facade at the authored corner. First-sheet Ready count improved from 11 to 12. All three prior doors and the archived window observations were preserved. The remaining perimeter is open. |
| Eight-sheet raster plan | Ready count improved from 76 to 77 by recovering a measured 208 × 23 native-pixel return. Two measured links connect the partition and door jamb without placing wall material over the doorway. The other seven sheets reproduce alpha 47. |
| Medium vector and mixed versions | Each retains 54 Ready walls, 11 doors, 8 windows and the same closed exterior route. |
| Other controls | The five main JSON exports reproduce alpha 47 exactly on eight inputs, including both medium versions. |

These are local placement checks on specific drawings. The large vector plan still has missed interior walls. Raster and vector drawings can produce open perimeters where evidence remains unresolved. The scanner cannot establish overall wall recall or justify unattended placement from these controls.

## Windows reference runtime

These medians describe the original sealed evaluation on one Windows machine. Elapsed time includes process startup, I/O and host waits.

| Input | Alpha 47 elapsed | Alpha 48 elapsed | Alpha 47 process CPU | Alpha 48 process CPU |
| --- | ---: | ---: | ---: | ---: |
| Large vector | 3.18 s | 3.31 s | 3.17 s | 3.08 s |
| Medium vector | 0.56 s | 0.91 s | 0.55 s | 0.55 s |
| Medium mixed | 0.53 s | 0.73 s | 0.52 s | 0.48 s |
| Eight-sheet raster | 0.91 s | 1.11 s | 0.91 s | 0.88 s |

A synthetic 506,959,100-byte PDF with 33 sheets passed all five full runs at a median of 2.22 seconds and 972.8 MiB peak working set. Fifteen selected-page scans and five controlled rejections also passed. The synthetic sheets exercise PDF resources and page routing with light geometry. Dense real plans can take longer.

## Windows distribution

The Windows executable was rebuilt from the exact sealed Rust source snapshot with remapped file paths. The download includes dependency notices and five authored BSD-2-Clause synthetic PDFs. The private evaluation drawings, screenshots and scan artifacts stay local. The synthetic examples exercise narrow vector-paint cases and cannot reproduce the private-corpus accuracy measurements.

See [build provenance](BUILD-PROVENANCE.json) and [distribution parity](PUBLIC-PARITY-SUMMARY.json) for the executable checksum and reproducible scope of the comparison.
