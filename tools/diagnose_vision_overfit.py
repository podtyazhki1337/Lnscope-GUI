"""Train on two training-only examples per task to diagnose memorization."""
import argparse
import json
import shutil
from pathlib import Path

import cv2
import numpy as np
from ultralytics import YOLO


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--epochs", type=int, default=120)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    output = root / "artifacts/vision_overfit_v1"
    output.mkdir(exist_ok=False)
    report, tiles = {}, []
    selections = {
        "cell": ["*21_S*", "*20_S*f000835*"],
        "spheroid": ["*19_Eminus*", "*07_C3*"],
    }
    for task, patterns in selections.items():
        source = root / "datasets/yolo_pilot_v1" / task
        data = output / "diagnostic_data" / task
        for folder in ("images/train", "labels/train"):
            (data / folder).mkdir(parents=True)
        for pattern in patterns:
            paths = list((source / "images/train").glob(pattern))
            assert len(paths) == 1
            path = paths[0]
            shutil.copy2(path, data / "images/train" / path.name)
            shutil.copy2(source / "labels/train" / path.with_suffix(".txt").name,
                         data / "labels/train" / path.with_suffix(".txt").name)
        config = data / "data.yaml"
        config.write_text(f'path: {json.dumps(data.as_posix())}\ntrain: images/train\nval: images/train\nnames:\n  0: {task}\n', encoding="utf-8")
        model = YOLO(str(root / ".cache/yolo26n-seg.pt"))
        model.train(data=str(config), epochs=args.epochs, imgsz=384, batch=2,
                    nbs=2, warmup_epochs=0, optimizer="AdamW", lr0=.001, lrf=.1,
                    freeze=0, patience=0, device=0, workers=0, seed=42,
                    mosaic=0, mixup=0, copy_paste=0, cutmix=0,
                    degrees=0, translate=0, scale=0, shear=0, perspective=0,
                    fliplr=0, flipud=0, hsv_h=0, hsv_s=0, hsv_v=0,
                    amp=False, plots=True, project=str(output), name=task)
        best = YOLO(str(model.trainer.best))
        results = []
        for path in sorted((data / "images/train").glob("*.png")):
            original = cv2.imread(str(path))
            h, w = original.shape[:2]
            values = (data / "labels/train" / path.with_suffix(".txt").name).read_text().split()
            points = np.round(np.array([float(x) for x in values[1:]]).reshape(-1, 2) * (w, h)).astype(np.int32)
            truth = np.zeros((h, w), np.uint8)
            cv2.fillPoly(truth, [points], 1)
            pred = best.predict(original, imgsz=384, device=0, conf=.25, retina_masks=True, verbose=False)[0]
            ious = []
            if pred.masks is not None:
                for mask in pred.masks.data.cpu().numpy():
                    binary = mask > .5
                    assert binary.shape == truth.shape
                    ious.append(float(np.logical_and(binary, truth).sum() / np.logical_or(binary, truth).sum()))
            results.append({"image": path.name, "detections": len(pred.boxes), "best_mask_iou": max(ious, default=0), "confidences": pred.boxes.conf.cpu().tolist()})
            gt = original.copy()
            cv2.polylines(gt, [points], True, (0, 220, 80), 1)
            tile = np.full((300, 960, 3), 30, np.uint8)
            for i, (title, image) in enumerate((("Original", original), ("Annotation", gt), (f"IoU {max(ious, default=0):.2f}", pred.plot()))):
                factor = min(310 / w, 250 / h)
                small = cv2.resize(image, (round(w*factor), round(h*factor)))
                sh, sw = small.shape[:2]
                x, y = i*320+(320-sw)//2, 40+(250-sh)//2
                tile[y:y+sh, x:x+sw] = small
                cv2.putText(tile, task+": "+title, (i*320+5, 25), cv2.FONT_HERSHEY_SIMPLEX, .55, (240,240,240), 1)
            tiles.append(tile)
        report[task] = {"diagnostic_only": True, "evaluation_on_training_images": True,
                        "weights": str(model.trainer.best), "results": results,
                        "pass": all(r["detections"] == 1 and r["best_mask_iou"] >= .8 for r in results)}
        (output / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        cv2.imwrite(str(output / "comparison.jpg"), np.vstack(tiles))


if __name__ == "__main__":
    main()
