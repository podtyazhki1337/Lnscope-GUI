"""Summarize mask components and render sampled frames for human review."""
import json
from pathlib import Path

import cv2
import numpy as np


def main():
    root = Path(__file__).resolve().parents[1] / "artifacts/sam2_video_pilot_v1"
    for task in ("cell", "spheroid"):
        folder = root / task
        track = json.loads((folder / "track.json").read_text())
        stats, tiles = [], []
        for i, record in enumerate(track["frames"]):
            mask = cv2.imread(str(folder / "review_masks" / f"{i:05d}.png"), 0) > 0
            _, _, components, _ = cv2.connectedComponentsWithStats(mask.astype(np.uint8))
            areas = sorted(components[1:, cv2.CC_STAT_AREA].tolist(), reverse=True)
            significant = sum(int(a >= max(25, .005 * int(mask.sum()))) for a in areas)
            stats.append({"frame": record["source_frame"], "area": int(mask.sum()),
                          "component_areas": areas, "significant_components": significant})
            if i % 4 == 0 or i == len(track["frames"]) - 1:
                frame = cv2.imread(str(folder / "frames" / f"{i:05d}.jpg"))
                frame[mask] = (.55 * frame[mask] + .45 * np.array([40,220,60])).astype(np.uint8)
                frame = cv2.resize(frame, (320,240))
                cv2.putText(frame, str(record["source_frame"]), (10,20), 0, .6, (255,255,255), 1)
                tiles.append(frame)
        while len(tiles) % 4:
            tiles.append(np.zeros_like(tiles[0]))
        cv2.imwrite(str(folder / "inspection.jpg"), np.vstack([np.hstack(tiles[i:i+4]) for i in range(0,len(tiles),4)]))
        (folder / "component_audit.json").write_text(json.dumps(stats,indent=2), encoding="utf-8")
        cap = cv2.VideoCapture(str(folder / "review.mp4"))
        count = 0
        while cap.read()[0]:
            count += 1
        cap.release()
        assert count == len(stats), "Review video frame count mismatch"
        print(json.dumps({"task":task, "video_frames":count, "nonempty_masks":sum(s["area"]>0 for s in stats),
                          "significant_multi_component_frames":sum(s["significant_components"]>1 for s in stats)}))


if __name__ == "__main__":
    main()
