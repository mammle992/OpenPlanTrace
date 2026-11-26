# OpenPlanTrace Rust alpha 48

Experimental Windows x64 release. The scanner handles native vector, raster and mixed PDF pages using a single Rust entry point. In this release, it places two more verified wall runs on the test drawings, connects their supported ends and identifies the existing main perimeter on a large vector drawing as exterior. The medium controls preserve 54 Ready walls, 11 doors, 8 windows and one closed exterior on each PDF version.

The source evaluation used 110 scans in five paired rounds, 385 deep checks and source-aligned comparison images. A synthetic 507 MB, 33-sheet check passed separately. The large vector plan still has missing interior walls. Review every proposed placement and any incomplete perimeter against the original plan.

The Windows download includes the scanner, dependency notices, public QA and five authored synthetic PDFs. The same PDFs are also available as a separate self-test ZIP. Use SHA256SUMS.txt to verify the downloads.

Read the [public QA report](https://github.com/mammle992/OpenPlanTrace/blob/v0.12.60-alpha.48/docs/alpha/PUBLIC-QA.md) for counts, performance and distribution parity.
