# SPDX-License-Identifier: BSD-2-Clause
# Copyright (c) 2026 OpenPlanTrace contributors
"""Rebuild the authored synthetic PDF self-tests using only Python's stdlib.

No source drawings, images, font files, network requests or external files are
read. This helper creates test data; it is not part of the Rust scanner.
"""
from pathlib import Path
import argparse
import hashlib


def stream(data, extra=""):
    if isinstance(data, str):
        data = data.encode("ascii")
    return (b"<< " + extra.encode("ascii") + b" /Length "
            + str(len(data)).encode("ascii") + b" >>\nstream\n"
            + data + b"\nendstream")


def pdf(objects):
    data = bytearray(b"%PDF-1.7\n% public generated QA fixture\n")
    offsets = [0]
    for number, obj in enumerate(objects, 1):
        offsets.append(len(data))
        data.extend(f"{number} 0 obj\n".encode("ascii"))
        data.extend(obj if isinstance(obj, bytes) else obj.encode("ascii"))
        data.extend(b"\nendobj\n")
    position = len(data)
    data.extend(f"xref\n0 {len(offsets)}\n0000000000 65535 f \n".encode("ascii"))
    for offset in offsets[1:]:
        data.extend(f"{offset:010} 00000 n \n".encode("ascii"))
    data.extend((f"trailer\n<< /Size {len(offsets)} /Root 1 0 R >>\n"
                 f"startxref\n{position}\n%%EOF\n").encode("ascii"))
    return bytes(data)


# Authored batt symbol: a small polygonal wave in a 100 by 100 unit cell.
# It follows a mathematical half-cell and its mirrored/repeated counterpart.
CELL = """0.25 G 1 w
0 100 m 13 90 l S 13 90 m 17 80 l S 17 80 m 7 25 l S
7 25 m 12 12 l S 12 12 m 25 0 l S 25 0 m 38 12 l S
38 12 m 43 25 l S 43 25 m 33 80 l S 33 80 m 37 90 l S
37 90 m 50 100 l S 50 100 m 63 90 l S 63 90 m 67 80 l S
67 80 m 57 25 l S 57 25 m 62 12 l S 62 12 m 75 0 l S
75 0 m 88 12 l S 88 12 m 93 25 l S 93 25 m 83 80 l S
83 80 m 87 90 l S 87 90 m 100 100 l S"""
LOWER_POINTS = [(0, 81), (220, 81), (220, 80), (179, 80),
                (179, 60), (178, 60), (178, 80), (22, 80),
                (22, 60), (21, 60), (21, 80), (0, 80)]
SEGMENTS = [f"{a[0]} {a[1]} m {b[0]} {b[1]} l S"
            for a, b in zip(LOWER_POINTS, LOWER_POINTS[1:] + LOWER_POINTS[:1])]
LOWER = " ".join(SEGMENTS)
UPPER = ("20 98 m 180 98 l S 180 98 m 180 120 l S "
         "180 120 m 178 120 l S 178 120 m 178 100 l S "
         "178 100 m 22 100 l S 22 100 m 22 120 l S "
         "22 120 m 20 120 l S 20 120 m 20 98 l S")
PAINT = ("20 95 m 180 95 l S q /Pattern cs /BattA scn 19 81 162 14 re f Q "
         "q /Pattern cs /BattB scn 19 95 162 3 re f Q EMC ")
MAIN = f"/OC /Wall BDC 0 G 0.2 w {LOWER} {UPPER} {PAINT}"
REFERENCES = ("/OC /Furn BDC 0 G 0.2 w "
              + " ".join(f"22 {y} m 178 {y} l S" for y in [80, 81, 95, 98, 100])
              + " EMC ")
CONTEXT = ("/OC /Wall BDC 0 G 0.2 w 100 140 m 180 140 l S "
           "100 142 m 180 142 l S 140 138 m 140 144 l S "
           "160 138 m 160 144 l S EMC /OC /Door BDC 0 G 0.2 w "
           "140 140 m 160 140 l S 140 140 m 140 160 l S "
           "160 140 m 160 151.045695 151.045695 160 140 160 c S EMC ")
BACKGROUND = "q 1 g 80 79.8 15 1.4 re f Q "


def local_faces_case(remote_erase=""):
    remote = " ".join(s for i, s in enumerate(SEGMENTS) if i not in [0, 6])
    local = " ".join(SEGMENTS[i] for i in [0, 6])
    source = ("/OC /Wall BDC 0 G 0.2 w " + remote + " "
              + BACKGROUND + remote_erase + " " + local + " "
              + UPPER + " " + PAINT)
    return source + REFERENCES + CONTEXT


def objects(content):
    # Unused template resources are authored, not imported assets: a 4 by 4 pure
    # white RGB image and a literal gray rectangle transparency form.
    resources = ("<< /Pattern << /BattA 8 0 R /BattB 9 0 R >> "
                 "/Properties << /Wall 5 0 R /Furn 6 0 R /Door 7 0 R >> "
                 "/XObject << /WhiteImage 10 0 R >> /ExtGState << "
                 "/Half << /ca 0.5 /CA 0.5 >> "
                 "/Invisible << /ca 0 /CA 0 >> "
                 "/Masked << /SMask << /S /Luminosity /G 11 0 R >> >> >> >>")
    pattern = ("/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 "
               "/BBox [-1 -1 101 101] /XStep 100 /YStep 100 /Resources << >> ")
    return [
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        ("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 240 200] "
         f"/Contents 4 0 R /Resources {resources} >>"),
        stream(content),
        "<< /Type /OCG /Name (A-WALL) >>",
        "<< /Type /OCG /Name (A-FURN) >>",
        "<< /Type /OCG /Name (A-DOOR) >>",
        stream(CELL, pattern + "/Matrix [0.16 0 0 0.14 20 81]"),
        stream(CELL, pattern + "/Matrix [0.08 0 0 0.03 20 95]"),
        stream(bytes([255] * 48), "/Type /XObject /Subtype /Image /Width 4 "
               "/Height 4 /ColorSpace /DeviceRGB /BitsPerComponent 8"),
        stream("0.5 g 0 0 200 200 re f", "/Type /XObject /Subtype /Form "
               "/BBox [0 0 200 200] /Group << /S /Transparency "
               "/CS /DeviceGray >> /Resources << >>"),
    ]


def generated_pdfs():
    ordinary = MAIN + REFERENCES + CONTEXT
    cases = {
        "01-insulated-wall.pdf": ordinary,
        "02-background-then-local-faces.pdf": local_faces_case(),
        "03-erased-finish.pdf": ordinary + "q 1 g 80 79.8 15 1.19 re f Q ",
        "04-erased-remote-closure.pdf": local_faces_case(
            "q 1 g 219.8 80.2 0.4 0.6 re f Q "),
        "05-compound-paint-erases-closure.pdf": local_faces_case(
            "q 1 g 219.8 80.2 0.4 0.6 re 220 80.2 0.1 0.6 re f Q "),
    }
    return {name: pdf(objects(content)) for name, content in cases.items()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, default=Path(__file__).resolve().parent)
    parser.add_argument("--check", action="store_true", help="Verify existing bytes; write nothing")
    args = parser.parse_args()
    if not args.check:
        args.out.mkdir(parents=True, exist_ok=True)
    for name, data in generated_pdfs().items():
        path = args.out / name
        if args.check:
            if path.read_bytes() != data:
                raise SystemExit(f"Mismatch: {name}")
        else:
            path.write_bytes(data)
        print(hashlib.sha256(data).hexdigest(), name)


if __name__ == "__main__":
    main()
