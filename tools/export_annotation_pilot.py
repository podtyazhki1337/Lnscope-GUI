"""Export visually drafted polygons as cropped YOLO segmentation examples."""
import argparse
import hashlib
import json
from pathlib import Path

import cv2
import numpy as np


def main():
    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--annotations", type=Path, default=root / "tools/annotation_pilot.json")
    parser.add_argument("--output", type=Path, default=root / "datasets/extraction_annotation_pilot_v1")
    args = parser.parse_args()
    seed = root / "datasets/extraction_seed_v1"
    output = args.output
    source = json.loads(args.annotations.read_text(encoding="utf-8"))
    manifest = json.loads((seed / "manifest.json").read_text(encoding="utf-8-sig"))
    samples = {Path(s["image"]).name: (v, s) for v in manifest["videos"] for s in v["samples"]}
    output.mkdir(exist_ok=False)
    (output / "overlays").mkdir(parents=True)
    records, tiles = [], []
    for index, annotation in enumerate(source["annotations"], 1):
        video, sample = samples[annotation["image"]]
        split = video["split"]
        assert split in ("train", "val", "test")
        partial = annotation.get("partial_annotation", False) or annotation.get("review_required", False)
        prefix = "review/" if partial else ""
        for folder in (f"{prefix}images/{split}", f"{prefix}labels/{split}"):
            (output / folder).mkdir(parents=True, exist_ok=True)
        path = seed / sample["image"]
        frame = cv2.imread(str(path))
        assert frame is not None
        x0, y0, x1, y1 = annotation["crop"]
        assert 0 <= x0 < x1 <= frame.shape[1] and 0 <= y0 < y1 <= frame.shape[0]
        crop = frame[y0:y1, x0:x1].copy()
        height, width = crop.shape[:2]
        polygons = annotation.get("polygons") or [annotation["polygon"]]
        contours, rows = [], []
        for polygon in polygons:
            points = np.array(polygon, dtype=np.int32) - (x0, y0)
            normalized = points / (width, height)
            assert np.isfinite(normalized).all() and (normalized >= 0).all() and (normalized <= 1).all()
            assert cv2.contourArea(points.astype(np.int32)) > 50
            rows.append("0 " + " ".join(f"{v:.6f}" for v in normalized.flatten()))
            contours.append(points.astype(np.int32))
        name = path.stem + "_" + annotation.get("id", "crop")
        image_relative = f"{prefix}images/{split}/{name}.png"
        assert not (output / image_relative).exists(), "Duplicate annotation output"
        assert cv2.imwrite(str(output / image_relative), crop)
        label = "\n".join(rows) + "\n"
        label_path = output / f"{prefix}labels/{split}" / (name + ".txt")
        label_path.write_text(label, encoding="utf-8")
        for row, points in zip(label_path.read_text().splitlines(), contours, strict=True):
            decoded = np.array([float(v) for v in row.split()[1:]]).reshape(-1, 2)
            assert np.max(np.abs(decoded * (width, height) - points)) < 0.001
        overlay = crop.copy()
        cv2.polylines(overlay, contours, True, (0, 230, 80), 1, cv2.LINE_AA)
        cv2.imwrite(str(output / "overlays" / (name + ".png")), overlay)
        tile = np.full((280, 520, 3), 30, dtype=np.uint8)
        for column, image in enumerate((crop, overlay)):
            scale = min(240 / width, 240 / height)
            resized = cv2.resize(image, (round(width * scale), round(height * scale)), interpolation=cv2.INTER_NEAREST)
            rh, rw = resized.shape[:2]
            top, left = 30 + (240 - rh) // 2, column * 260 + 10 + (240 - rw) // 2
            tile[top:top + rh, left:left + rw] = resized
        tag = "REVIEW" if partial else split
        cv2.putText(tile, f"{index}: {len(polygons)} mask(s), {tag}", (10, 21), cv2.FONT_HERSHEY_SIMPLEX, .5, (230, 230, 230), 1)
        tiles.append(tile)
        records.append({**annotation, "source_image": sample["image"], "source_sha256": hashlib.sha256(path.read_bytes()).hexdigest(), "video": video["source_relative"], "split": split, "output": image_relative, "training_eligible": not partial, "status": source["status"]})
    if len(tiles) % 2:
        tiles.append(np.full_like(tiles[0], 30))
    preview = np.vstack([np.hstack(tiles[i:i+2]) for i in range(0, len(tiles), 2)])
    cv2.imwrite(str(output / "preview.jpg"), preview)
    (output / "annotations.json").write_text(json.dumps({"class_names": [source["class_name"]], "annotations": records, "review_queue": source["review_queue"]}, indent=2), encoding="utf-8")
    (output / "README.md").write_text(source.get("readme", """# Visual annotation pilot

Six cropped images with six manually drafted AI visual polygons, class 0: visible_cell.
YOLO segmentation format: class_id followed by normalized polygon x/y pairs.
Source coordinates, crop rectangles and SHA256 hashes are in annotations.json.
Green contours and unmarked originals appear side by side in preview.jpg.

These are draft annotations, not expert-validated biological ground truth. Outline the
visible cell body, excluding the bright optical halo. Do not infer hidden boundaries.
Only isolated complete cells are included. Dense, occluded and defocused examples
are listed for review, without empty labels that would incorrectly imply background.
E-minus remains eligible; the inspected contact example needs boundary-policy review.

All crops retain their source train split. Repeated frames are not independent cells.
Validation/test images remain unlabelled. No training configuration or trained model
is supplied: this pilot alone cannot establish full-frame detection performance.
The original 232-image dataset is unchanged and is not fully annotated.
Edit tools/annotation_pilot.json to adjust polygons in original image coordinates.
The exporter refuses to overwrite an existing output directory.
"""), encoding="utf-8")
    print(json.dumps({"output": str(output), "images": len(records), "polygons": sum(len(a.get("polygons", [a.get("polygon")])) for a in records), "review_queue": len(source["review_queue"])}))


if __name__ == "__main__":
    main()
