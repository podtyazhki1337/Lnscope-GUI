"""Render test predictions beside ground truth without hardware access."""
import argparse
import json
from pathlib import Path

import cv2
import numpy as np
from ultralytics import YOLO


def main():
    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runs", type=Path, default=root / "artifacts/vision_pilot_v1")
    args = parser.parse_args()
    runs = args.runs
    report = json.loads((runs / "report.json").read_text())
    tiles = []
    for task, result in report.items():
        task_tiles = []
        split = result.get("split", "test")
        model = YOLO(result["weights"])
        dataset = Path(result.get("dataset", str(root / "datasets/yolo_pilot_v1"))) / task
        for path in sorted((dataset / "images" / split).glob("*.png")):
            original = cv2.imread(str(path))
            height, width = original.shape[:2]
            truth = original.copy()
            labels = (dataset / "labels" / split / path.with_suffix(".txt").name).read_text()
            for line in labels.splitlines():
                xy = np.array([float(v) for v in line.split()[1:]]).reshape(-1, 2)
                points = np.round(xy * (width, height)).astype(np.int32)
                cv2.polylines(truth, [points], True, (0, 220, 80), 1)
            prediction = model.predict(original, imgsz=result.get("imgsz",512), device=0, conf=.25, verbose=False)[0]
            rendered = prediction.plot()
            tile = np.full((300, 960, 3), 30, dtype=np.uint8)
            for i, (title, source) in enumerate((("Original", original), ("Ground truth", truth), (f"Prediction: {len(prediction.boxes)}", rendered))):
                scale = min(310 / width, 250 / height)
                resized = cv2.resize(source, (round(width * scale), round(height * scale)))
                h, w = resized.shape[:2]
                y, x = 40 + (250 - h) // 2, 320 * i + (320 - w) // 2
                tile[y:y+h, x:x+w] = resized
                cv2.putText(tile, f"{task}: {title}", (320*i+7, 24), cv2.FONT_HERSHEY_SIMPLEX, .55, (240, 240, 240), 1)
            tiles.append(tile)
            task_tiles.append(tile)
        cv2.imwrite(str(runs / (task + "_" + split + "_comparison.jpg")), np.vstack(task_tiles))
    cv2.imwrite(str(runs / "comparison.jpg"), np.vstack(tiles))


if __name__ == "__main__":
    main()
