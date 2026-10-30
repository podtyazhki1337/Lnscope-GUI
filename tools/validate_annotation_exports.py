"""Validate exported annotation geometry, provenance and review isolation."""
import argparse
import hashlib
import json
from collections import Counter
from pathlib import Path

import cv2
import numpy as np


def cross(p, q, r):
    return (q[0] - p[0]) * (r[1] - p[1]) - (q[1] - p[1]) * (r[0] - p[0])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("datasets", nargs="+", type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    seed = root / "datasets/extraction_seed_v1"
    manifest = json.loads((seed / "manifest.json").read_text(encoding="utf-8-sig"))
    samples = {s["image"]: v["split"] for v in manifest["videos"] for s in v["samples"]}
    for base in args.datasets:
        counts = Counter()
        data = json.loads((base / "annotations.json").read_text())
        for a in data["annotations"]:
            assert samples[a["source_image"]] == a["split"]
            assert hashlib.sha256((seed / a["source_image"]).read_bytes()).hexdigest() == a["source_sha256"]
            decoded_image = cv2.imread(str(base / a["output"]))
            assert decoded_image is not None
            height, width = decoded_image.shape[:2]
            review = a.get("partial_annotation", False) or a.get("review_required", False)
            assert a["output"].startswith("review/") == review
            assert a["training_eligible"] == (not review)
            polygons = a.get("polygons") or [a["polygon"]]
            label_path = base / Path(a["output"].replace("images/", "labels/")).with_suffix(".txt")
            rows = label_path.read_text().splitlines()
            assert len(rows) == len(polygons)
            for polygon, row in zip(polygons, rows, strict=True):
                values = row.split()
                assert values[0] == "0"
                normalized = np.array([float(v) for v in values[1:]]).reshape(-1, 2)
                assert np.isfinite(normalized).all() and (normalized >= 0).all() and (normalized <= 1).all()
                expected = np.array(polygon) - a["crop"][:2]
                assert np.max(np.abs(normalized * (width, height) - expected)) < .001
                for i, p in enumerate(polygon):
                    q = polygon[(i + 1) % len(polygon)]
                    for j in range(i + 2, len(polygon)):
                        if i == 0 and j == len(polygon) - 1:
                            continue
                        r, s = polygon[j], polygon[(j + 1) % len(polygon)]
                        assert not (cross(p, q, r) * cross(p, q, s) < 0 and cross(r, s, p) * cross(r, s, q) < 0), (a["image"], i, j)
                counts["review_masks" if review else a["split"] + "_masks"] += 1
            counts["images"] += 1
        print(json.dumps({"dataset": str(base), **counts}))


if __name__ == "__main__":
    main()
