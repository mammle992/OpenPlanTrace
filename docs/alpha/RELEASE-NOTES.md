# OpenPlanTrace Rust alpha 50

Experimental Windows x64 release for vector, raster and mixed PDFs. Since alpha 48, the scanner places eleven more pocket-wall spans on the large vector control, three more wall spans on the eight-sheet raster control and six more wall spans on the masked vector control. Existing openings and the medium plan's closed exterior are preserved.

Independent alpha 50 QA covered 110 scans, 385 deep checks and 110 source-aligned image comparisons. A separate 507 MB synthetic PDF passed five full scans. The large vector control took 3.91 seconds at the median, up from 3.32 seconds in alpha 49. Review incomplete perimeters, sliding doors and other proposed placements against the original drawing.

The Windows ZIP includes the Rust scanner, five synthetic self-test PDFs and dependency notices. Verify the downloads with SHA256SUMS.txt.

Read the [public QA report](https://github.com/mammle992/OpenPlanTrace/blob/v0.12.62-alpha.50/docs/alpha/PUBLIC-QA.md) for the results, limitations and verification details.
