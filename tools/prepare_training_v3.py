"""Curate reviewed propagation intervals into isolated cell crops and mass labels."""
import hashlib
import json
from pathlib import Path

import cv2
import numpy as np
from train_vision_pilot import prepare


def main():
    root = Path(__file__).resolve().parents[1]
    output = root / "datasets/yolo_training_v3"
    prepare(root, output)
    # Intervals were visually inspected; no uncertain multicell identities are promoted.
    selections = {
        "isolated_cell_round": (622, 701, {1: "cell"}),
        "isolated_cell_oval": (1192, 1248, {1: "cell"}),
        "free_cell_exit": (629, 661, {1: "cell"}),
        "two_cells_near_mass": (1165, 1205, {1: "spheroid", 2: "cell", 3: "cell"}),
        "deforming_mass_c3": (487, 511, {1: "spheroid"}),
    }
    records, tiles, excluded = [], [], []
    for batch in ("video_annotation_staging_v1", "video_annotation_staging_batch5"):
        source = root / "datasets" / batch
        manifest = json.loads((source / "manifest.json").read_text())
        for frame in manifest["frames"]:
            selected = selections.get(frame["sequence"])
            if selected is None or not selected[0] <= frame["frame"] <= selected[1]:
                continue
            assert frame["split"] == "train", "Do not move validation or test videos into train"
            image = cv2.imread(str(source / frame["image"]))
            masks = {o["id"]: cv2.imread(str(source / o["mask"]), 0) for o in frame["objects"]}
            for object_id, task in selected[2].items():
                if (frame["sequence"] == "isolated_cell_oval" and 1208 <= frame["frame"] <= 1232 or
                    frame["sequence"] == "two_cells_near_mass" and frame["frame"] == 1181 and object_id == 2):
                    excluded.append(dict(sequence=frame["sequence"], frame=frame["frame"], object_id=object_id, reason="Visual review: uncertain defocused or ragged boundary"))
                    continue
                mask = masks[object_id]
                contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
                contours = [c for c in contours if cv2.contourArea(c) >= 25]
                if len(contours) != 1:
                    excluded.append(dict(sequence=frame["sequence"], frame=frame["frame"], object_id=object_id, reason="Empty or fragmented mask"))
                    continue
                contour = cv2.approxPolyDP(contours[0], .7, True)
                x, y, w, h = 0, 0, image.shape[1], image.shape[0]
                if task == "cell":
                    x, y, w, h = cv2.boundingRect(contour)
                    x0, y0 = max(0, x-6), max(0, y-6)
                    x1, y1 = min(image.shape[1], x+w+6), min(image.shape[0], y+h+6)
                    x, y, w, h = x0, y0, x1-x0, y1-y0
                    if any(np.any(other[y:y+h, x:x+w]) for oid, other in masks.items() if oid != object_id):
                        continue
                crop = image[y:y+h, x:x+w]
                polygon = contour.reshape(-1, 2)-[x, y]
                reconstructed = np.zeros((h, w), np.uint8)
                cv2.fillPoly(reconstructed, [polygon.astype(np.int32)], 255)
                actual = mask[y:y+h, x:x+w] > 0
                iou = np.count_nonzero(actual & (reconstructed > 0))/np.count_nonzero(actual | (reconstructed > 0))
                assert iou > .97, iou
                name = f"video_{frame['sequence']}_{frame['frame']:06d}_id{object_id}"
                target = output / task
                cv2.imwrite(str(target / "images/train" / (name+".png")), crop)
                coordinates = polygon / [w, h]
                assert np.all((coordinates >= 0) & (coordinates <= 1))
                (target / "labels/train" / (name+".txt")).write_text("0 " + " ".join(f"{v:.7f}" for v in coordinates.flatten()) + "\n")
                records.append(dict(task=task, name=name, batch=batch, source_video=frame["source_video"],
                    source_sha256=frame["source_sha256"], source_frame=frame["frame"], object_id=object_id,
                    crop=[x, y, w, h], mask_iou=iou, review="AI visual review of selected interval; not expert ground truth",
                    mask_sha256=hashlib.sha256(mask.tobytes()).hexdigest()))
                overlay = crop.copy()
                cv2.polylines(overlay, [polygon.astype(np.int32)], True, (0, 255, 80), 1)
                tile = np.full((180, 220, 3), 30, np.uint8)
                scale = min(210/w, 145/h)
                preview = cv2.resize(overlay, (round(w*scale), round(h*scale)))
                tile[28:28+preview.shape[0], 5:5+preview.shape[1]] = preview
                cv2.putText(tile, f"{len(records)} {task} f{frame['frame']} id{object_id}", (5, 18), 0, .4, (255,255,255), 1)
                tiles.append(tile)
    while len(tiles) % 6:
        tiles.append(np.zeros_like(tiles[0]))
    cv2.imwrite(str(output / "new_labels_review.jpg"), np.vstack([np.hstack(tiles[i:i+6]) for i in range(0, len(tiles), 6)]))
    (output / "video_provenance.json").write_text(json.dumps(records, indent=2))
    (output / "excluded.json").write_text(json.dumps(excluded, indent=2))
    summary = {}
    for task in ("cell", "spheroid"):
        summary[task] = {split: len(list((output/task/"images"/split).glob("*.png"))) for split in ("train", "val", "test")}
    (output / "summary.json").write_text(json.dumps(summary, indent=2))
    print(json.dumps({"new_labels": len(records), "summary": summary}))


if __name__ == "__main__":
    main()
