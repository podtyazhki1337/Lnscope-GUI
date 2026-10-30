"""Validate review staging integrity without promoting masks to training labels."""
import argparse
import json
from collections import Counter
from pathlib import Path

import cv2
import numpy as np


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("path", type=Path)
    args = parser.parse_args()
    root = args.path.resolve()
    manifest = json.loads((root / "manifest.json").read_text())
    seen = set()
    masks = 0
    for frame in manifest["frames"]:
        key = (frame["source_sha256"], frame["frame"])
        assert key not in seen, f"Duplicate frame: {key}"
        seen.add(key)
        image = cv2.imread(str(root / frame["image"]))
        assert image is not None, frame["image"]
        assert not frame["training_eligible"]
        for obj in frame["objects"]:
            mask = cv2.imread(str(root / obj["mask"]), 0)
            assert mask is not None and mask.shape == image.shape[:2], obj["mask"]
            assert set(np.unique(mask)) <= {0, 255}, obj["mask"]
            assert not obj["training_eligible"]
            assert obj["kind"] in manifest["classes"], obj["kind"]
            masks += 1
    report = {"frames": len(seen), "masks": masks,
              "splits": dict(Counter(f["split"] for f in manifest["frames"])),
              "status": "integrity_passed_not_annotation_approval"}
    (root / "validation.json").write_text(json.dumps(report, indent=2))
    print(json.dumps(report))


if __name__ == "__main__":
    main()
