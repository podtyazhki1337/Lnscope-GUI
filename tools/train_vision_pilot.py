"""Build isolated pilot datasets and train two offline segmentation models."""
import argparse
import hashlib
import json
import shutil
from collections import Counter
from pathlib import Path


BATCHES = {
    "spheroid": ["spheroid_annotation_pilot_v1", "spheroid_annotation_batch2", "spheroid_annotation_batch3"],
    "cell": ["extraction_annotation_pilot_v1", "cell_annotation_batch2", "cell_annotation_batch3_corrected", "cell_annotation_validation1"],
}


def prepare(root, output):
    output.mkdir(parents=True, exist_ok=False)
    report = {}
    for task, batches in BATCHES.items():
        target = output / task
        records, counts, seen, assignments = [], Counter(), set(), {}
        for batch in batches:
            source = root / "datasets" / batch
            data = json.loads((source / "annotations.json").read_text())
            for a in data["annotations"]:
                if a.get("partial_annotation") or a.get("review_required") or a["output"].startswith("review/"):
                    counts["excluded_images"] += 1
                    continue
                split = a["split"]
                assert assignments.setdefault(a["video"], split) == split
                image = source / a["output"]
                digest = hashlib.sha256(image.read_bytes()).hexdigest()
                assert digest not in seen, "Duplicate crop across batches"
                seen.add(digest)
                label = source / Path(a["output"].replace("images/", "labels/")).with_suffix(".txt")
                for folder in ("images", "labels"):
                    (target / folder / split).mkdir(parents=True, exist_ok=True)
                name = batch + "__" + image.name
                shutil.copy2(image, target / "images" / split / name)
                shutil.copy2(label, target / "labels" / split / Path(name).with_suffix(".txt"))
                counts[split + "_images"] += 1
                counts[split + "_objects"] += len(label.read_text().splitlines())
                records.append({"batch": batch, "source": a, "image": name, "sha256": digest})
        assert counts["train_images"] and counts["val_images"]
        config = f"path: {json.dumps(target.resolve().as_posix())}\ntrain: images/train\nval: images/val\ntest: images/test\nnames:\n  0: {data['class_names'][0]}\n"
        (target / "data.yaml").write_text(config, encoding="utf-8")
        (target / "provenance.json").write_text(json.dumps(records, indent=2), encoding="utf-8")
        report[task] = dict(counts)
    (output / "summary.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prepare-only", action="store_true")
    parser.add_argument("--epochs", type=int, default=120)
    parser.add_argument("--task", choices=list(BATCHES))
    parser.add_argument("--preset", choices=("initial", "small-data"), default="small-data")
    parser.add_argument("--eval-split", choices=("val", "test"), default="val")
    parser.add_argument("--dataset", type=Path)
    parser.add_argument("--runs", type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    dataset = args.dataset.resolve() if args.dataset else root / "datasets/yolo_pilot_v1"
    if not dataset.exists():
        prepare(root, dataset)
    if args.prepare_only:
        return
    import torch
    from ultralytics import YOLO
    assert torch.cuda.is_available(), "CUDA unavailable; diagnose before training"
    runs = args.runs.resolve() if args.runs else root / ("artifacts/vision_pilot_v2" if args.preset == "small-data" else "artifacts/vision_pilot_v1")
    runs.mkdir(parents=True, exist_ok=True)
    weights = root / ".cache/yolo26n-seg.pt"
    report_path = runs / "report.json"
    report = json.loads(report_path.read_text()) if report_path.exists() else {}
    for task in ([args.task] if args.task else BATCHES):
        assert task not in report, "Completed task already exists; preserve its report"
        model = YOLO(str(weights))
        settings = dict(imgsz=512, batch=2, freeze=10, patience=12,
                        degrees=10, translate=.1, scale=.2, amp=True)
        if args.preset == "small-data":
            settings.update(imgsz=384, batch=4, nbs=4, freeze=0, patience=30,
                            warmup_epochs=0, optimizer="AdamW", lr0=.001, lrf=.1,
                            degrees=5, translate=.05, scale=.1, amp=False)
        model.train(data=str(dataset / task / "data.yaml"), epochs=args.epochs,
                    device=0, workers=0, seed=42,
                    project=str(runs), name=task, exist_ok=False,
                    mosaic=0, mixup=0, copy_paste=0, fliplr=.5,
                    hsv_h=0, hsv_s=0, hsv_v=.15, plots=True, **settings)
        best = YOLO(str(model.trainer.best))
        split = args.eval_split
        metrics = best.val(data=str(dataset / task / "data.yaml"), split=split,
                           imgsz=settings["imgsz"], batch=settings["batch"], device=0, workers=0,
                           project=str(runs), name=task + "_" + split, plots=True)
        predictions = best.predict(source=str(dataset / task / "images" / split),
                                   imgsz=settings["imgsz"], device=0, conf=.25, save=True,
                                   project=str(runs), name=task + "_" + split + "_predictions")
        report[task] = {"weights": str(model.trainer.best), "dataset": str(dataset), "split": split, "imgsz": settings["imgsz"],
                        "preset": args.preset, split + "_metrics": metrics.results_dict,
                        split + "_predictions": [{"image": p.path, "detections": len(p.boxes),
                                                   "confidences": p.boxes.conf.cpu().tolist()} for p in predictions]}
        report_path.write_text(json.dumps(report, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
