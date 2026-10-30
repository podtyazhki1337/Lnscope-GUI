"""Offline video analysis session. This module has no COM or motion dependencies."""
import base64
import csv
import io
import math
from pathlib import Path
import time
import cv2
from tracking import PointTrack


class VideoSession:
    def __init__(self):
        self.capture = None
        self.frame = None
        self.tracks = {}
        self.rows = []
        self.eof = False
        self.elapsed_ms = 0.0
        self.fps = 10.0
        self.frame_index = 0
        self.time_ms = 0.0
        self.path = ""
        self.count = 0
        self.scale = 1.0

    def read(self, track):
        ok, frame = self.capture.read()
        if not ok:
            self.eof = True
            return
        self.frame_index = max(0, int(self.capture.get(cv2.CAP_PROP_POS_FRAMES))-1)
        self.time_ms = max(0.0, self.capture.get(cv2.CAP_PROP_POS_MSEC))
        h, w = frame.shape[:2]
        self.scale = min(1.0, 1280/w, 960/h)
        if self.scale < 1:
            frame = cv2.resize(frame, (round(w*self.scale), round(h*self.scale)), interpolation=cv2.INTER_AREA)
        self.frame = frame
        self.eof = False
        start = time.perf_counter()
        if track:
            gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
            for item in self.tracks.values():
                item.update(gray)
            if self.tracks:
                self.record("frame")
        self.elapsed_ms = (time.perf_counter()-start)*1000

    def record(self, event):
        values = dict(event=event, frame=self.frame_index, time_ms=self.time_ms, scale=self.scale)
        for name in ("tip", "target"):
            item = self.tracks.get(name)
            for key in ("x", "y", "score", "lost"):
                values[f"{name}_{key}"] = getattr(item, key) if item else ""
        tip, target = self.tracks.get("tip"), self.tracks.get("target")
        valid = tip is not None and target is not None and not tip.lost and not target.lost
        values["dx_px"] = target.x-tip.x if valid else ""
        values["dy_px"] = target.y-tip.y if valid else ""
        self.rows.append(values)
        # Bound memory and IPC output for long recordings.
        if len(self.rows) > 10000:
            self.rows.pop(0)

    def result(self):
        ok, encoded = cv2.imencode(".jpg", self.frame, [cv2.IMWRITE_JPEG_QUALITY, 85])
        if not ok:
            raise ValueError("Could not encode the video frame.")
        tip, target = self.tracks.get("tip"), self.tracks.get("target")
        offset = None
        if tip and target and not tip.lost and not target.lost:
            dx, dy = target.x-tip.x, target.y-tip.y
            offset = dict(dx=dx, dy=dy, distance=math.hypot(dx, dy))
        return dict(offset=offset, image=base64.b64encode(encoded).decode("ascii"), width=self.frame.shape[1], height=self.frame.shape[0],
                    frame=self.frame_index, count=self.count, time_ms=self.time_ms, fps=self.fps,
                    scale=self.scale, eof=self.eof, elapsed_ms=self.elapsed_ms,
                    tracks={name: item.result() for name, item in self.tracks.items()},
                    path=self.path, log_rows=len(self.rows))

    def call(self, method, args):
        if method == "vision_open":
            path = Path(args["path"])
            if not path.is_file():
                raise ValueError("Select an existing local video file.")
            capture = cv2.VideoCapture(str(path))
            if not capture.isOpened():
                capture.release()
                raise ValueError("This video could not be opened.")
            if self.capture is not None:
                self.capture.release()
            self.capture, self.path = capture, str(path)
            self.frame = None
            self.tracks.clear()
            self.rows.clear()
            fps = float(capture.get(cv2.CAP_PROP_FPS))
            self.fps = fps if math.isfinite(fps) and fps > 0 else 10.0
            self.count = max(0, int(capture.get(cv2.CAP_PROP_FRAME_COUNT)))
            self.read(False)
            if self.frame is None:
                raise ValueError("The video contains no readable frames.")
            return self.result()
        if self.capture is None or self.frame is None:
            raise ValueError("Open a video first.")
        if method == "vision_next":
            if not self.eof:
                self.read(True)
        elif method == "vision_seek":
            seconds = float(args["seconds"])
            if not math.isfinite(seconds) or seconds < 0:
                raise ValueError("Enter a non-negative video time in seconds.")
            self.tracks.clear()
            if not self.capture.set(cv2.CAP_PROP_POS_MSEC, seconds*1000):
                raise ValueError("The decoder cannot seek in this file.")
            self.read(False)
            if self.eof:
                raise ValueError("No frame at that time; seek to an earlier time.")
            self.record("seek")
        elif method == "vision_mark":
            name = args["name"]
            if name not in ("tip", "target"):
                raise ValueError("Select tip or target.")
            self.tracks[name] = PointTrack(cv2.cvtColor(self.frame, cv2.COLOR_BGR2GRAY), float(args["x"]), float(args["y"]))
            self.record("mark_"+name)
        elif method == "vision_reset":
            self.tracks.clear()
            self.record("reset")
        elif method == "vision_export":
            stream = io.StringIO(newline="")
            if self.rows:
                writer = csv.DictWriter(stream, fieldnames=list(self.rows[0]))
                writer.writeheader()
                writer.writerows(self.rows)
            return {"csv": stream.getvalue(), "rows": len(self.rows)}
        else:
            raise ValueError("Unknown offline video operation.")
        return self.result()
