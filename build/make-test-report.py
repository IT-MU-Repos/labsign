#!/usr/bin/env python3
"""Write a small test report PDF that LabSign can sign (it contains the SignHere! marker).

    python3 make-test-report.py [out.pdf]

Standard library only. Upload it as a sample's report in Mule to try Sign reports end to end.
"""

import sys
from datetime import date


def build(path: str) -> None:
    lines = [
        ("Test laboratory report", 18, 760),
        (f"Issued {date.today():%Y-%m-%d} - for testing LabSign only", 11, 735),
        ("Sample TEST-0001    pH 6.5    EC 0.35 mS/cm    P (Bray) 25 mg/kg", 11, 690),
        ("Authorised by:", 11, 200),
        ("SignHere!", 11, 180),
    ]
    text = "".join(f"BT /F1 {size} Tf 72 {y} Td ({line}) Tj ET\n" for line, size, y in lines)
    stream = text.encode("latin-1")
    objects = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        b"/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        b"<< /Length %d >>\nstream\n" % len(stream) + stream + b"endstream",
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
    ]
    out = bytearray(b"%PDF-1.4\n")
    offsets = []
    for number, body in enumerate(objects, start=1):
        offsets.append(len(out))
        out += b"%d 0 obj\n" % number + body + b"\nendobj\n"
    xref = len(out)
    out += b"xref\n0 %d\n0000000000 65535 f \n" % (len(objects) + 1)
    out += b"".join(b"%010d 00000 n \n" % offset for offset in offsets)
    out += b"trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (len(objects) + 1, xref)
    with open(path, "wb") as handle:
        handle.write(out)


if __name__ == "__main__":
    target = sys.argv[1] if len(sys.argv) > 1 else "test-report.pdf"
    build(target)
    print(f"Wrote {target}")
