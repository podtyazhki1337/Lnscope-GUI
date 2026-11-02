"""Portable ONNX segmentation and conservative image-space identities; no motor access."""
import base64
from pathlib import Path
import time

import cv2
import numpy as np


def box_iou(a, b):
    x0, y0 = max(a[0], b[0]), max(a[1], b[1])
    x1, y1 = min(a[2], b[2]), min(a[3], b[3])
    intersection = max(0, x1-x0)*max(0, y1-y0)
    return intersection/max(1, (a[2]-a[0])*(a[3]-a[1])+(b[2]-b[0])*(b[3]-b[1])-intersection)


class Identities:
    def __init__(self):
        self.items = {}
        self.next_id = 1
        self.last_time = None

    def update(self, detections, timestamp):
        if self.last_time is not None and (timestamp <= self.last_time or timestamp-self.last_time > 3):
            self.items.clear()
        self.last_time = timestamp
        candidates = []
        for number, item in self.items.items():
            for index, detection in enumerate(detections):
                if item['class'] != detection['class']:
                    continue
                score = box_iou(item['bbox'], detection['bbox'])
                if score >= .2:
                    candidates.append((score, number, index))
        used_ids, used_detections = set(), set()
        for score, number, index in sorted(candidates, reverse=True):
            if number in used_ids or index in used_detections:
                continue
            # Near-equal assignments (crossing, splitting, merging) start a new identity.
            if any((n == number or i == index) and (n, i) != (number, index) and abs(score-s) < .12
                   for s, n, i in candidates):
                continue
            detections[index]['id'] = number
            used_ids.add(number); used_detections.add(index)
        for detection in detections:
            if 'id' not in detection:
                detection['id'] = self.next_id
                self.next_id += 1
            self.items[detection['id']] = dict(detection, seen=timestamp)
        visible = {d['id'] for d in detections}
        lost = [dict(id=n, **{'class': d['class']}, bbox=d['bbox'])
                for n, d in self.items.items() if n not in visible]
        # Lost objects are reported, but never automatically reacquired later.
        self.items = {n: d for n, d in self.items.items() if n in visible}
        return lost


class Model:
    def __init__(self, path, classes):
        import onnxruntime as ort
        ort.disable_telemetry_events()
        options = ort.SessionOptions()
        options.intra_op_num_threads = 4
        options.inter_op_num_threads = 1
        self.session = ort.InferenceSession(str(path), options, providers=['CPUExecutionProvider'])
        self.classes = classes

    def predict(self, image, confidence=.5):
        h, w = image.shape[:2]
        ratio = min(512/w, 512/h)
        rw, rh = round(w*ratio), round(h*ratio)
        left, top = (512-rw)//2, (512-rh)//2
        padded = np.full((512, 512, 3), 114, np.uint8)
        padded[top:top+rh, left:left+rw] = cv2.resize(image, (rw, rh))
        tensor = np.ascontiguousarray(padded[:, :, ::-1].transpose(2, 0, 1)[None], dtype=np.float32)/255
        predictions, proto = self.session.run(None, {'images': tensor})
        rows = predictions[0].T
        nc = len(self.classes)
        labels = rows[:, 4:4+nc].argmax(axis=1)
        scores = rows[np.arange(len(rows)), 4+labels]
        keep = scores >= confidence
        rows, scores, labels = rows[keep], scores[keep], labels[keep]
        boxes = rows[:, :4].copy()
        boxes[:, :2] -= boxes[:, 2:]/2
        selected = []
        for label in range(nc):
            indices = np.where(labels == label)[0]
            selected.extend(indices[np.asarray(cv2.dnn.NMSBoxes(boxes[indices].tolist(), scores[indices].tolist(),
                confidence, .45)).reshape(-1).astype(int)].tolist())
        result = []
        for i in sorted(selected, key=lambda n: -scores[n])[:30]:
            x, y, bw, bh = boxes[i]
            x0, y0 = max(0, (x-left)/ratio), max(0, (y-top)/ratio)
            x1, y1 = min(w, (x+bw-left)/ratio), min(h, (y+bh-top)/ratio)
            if x1 <= x0 or y1 <= y0:
                continue
            logits = (rows[i, 4+nc:] @ proto[0].reshape(32, -1)).reshape(128, 128)
            logits = cv2.resize(logits, (512, 512))[top:top+rh, left:left+rw]
            mask = (cv2.resize(logits, (w, h)) > 0).astype(np.uint8)
            yy, xx = np.ogrid[:h, :w]
            mask *= ((xx >= x0) & (xx < x1) & (yy >= y0) & (yy < y1)).astype(np.uint8)
            contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
            contours = [cv2.approxPolyDP(c, 1, True).reshape(-1, 2).tolist() for c in contours if cv2.contourArea(c) >= 12]
            if not contours:
                continue
            result.append({'class': self.classes[int(labels[i])], 'confidence': float(scores[i]),
                'bbox': [float(x0), float(y0), float(x1), float(y1)], 'polygons': contours})
        return result


class SegmentSession:
    def __init__(self):
        folder = Path(__file__).resolve().parent.parent/'Models'
        self.mass = Model(folder/'spheroid.onnx', ['spheroid'])
        self.cells = Model(folder/'cell.onnx', ['cell', 'cell_group'])
        self.tracker = Identities()
        self.tracking = False

    def analyze(self, image, tracking=False, timestamp=None, tiled=True, confidence=.55):
        if not .05 <= confidence <= .95:
            raise ValueError('Confidence must be between 0.05 and 0.95.')
        start = time.perf_counter()
        h, w = image.shape[:2]
        scale = min(1, 640/w, 480/h)
        work = cv2.resize(image, (round(w*scale), round(h*scale))) if scale < 1 else image
        wh, ww = work.shape[:2]
        mass = self.mass.predict(work, confidence)
        cells = []
        tissue = np.zeros((wh, ww), np.uint8)
        for detection in mass:
            cv2.fillPoly(tissue, [np.array(p, np.int32) for p in detection['polygons']], 1)
        # The cell model was trained on local crops. Propose compact optical edges
        # instead of applying it to a whole frame at an incompatible object scale.
        gray = cv2.cvtColor(work, cv2.COLOR_BGR2GRAY)
        candidates = []
        for low, high in ([(20, 50), (10, 30)] if tiled else [(20, 50)]):
            edges = cv2.Canny(gray, low, high)
            edges = cv2.morphologyEx(edges, cv2.MORPH_CLOSE, np.ones((3, 3), np.uint8))
            contours, _ = cv2.findContours(edges, cv2.RETR_LIST, cv2.CHAIN_APPROX_SIMPLE)
            for contour in contours:
                x, y, cw, ch = cv2.boundingRect(contour)
                if min(cw, ch) < 18 or max(cw, ch) > 240 or max(cw/ch, ch/cw) > 3:
                    continue
                if cv2.contourArea(contour) < cw*ch*.25:
                    continue
                box = [max(0, x-5), max(0, y-5), min(ww, x+cw+5), min(wh, y+ch+5)]
                if any(box_iou(box, old) > .65 for old in candidates):
                    continue
                if tissue[box[1]:box[3], box[0]:box[2]].mean() > .65:
                    continue
                candidates.append(box)
        outer = []
        for box in sorted(candidates, key=lambda b: -(b[2]-b[0])*(b[3]-b[1])):
            if any(max(0, min(box[2], b[2])-max(box[0], b[0])) * max(0, min(box[3], b[3])-max(box[1], b[1]))
                   / max(1, (box[2]-box[0])*(box[3]-box[1])) > .8 for b in outer):
                continue
            outer.append(box)
        for x, y, right, bottom in outer[:30]:
            crop = work[y:bottom, x:right]
            for d in self.cells.predict(crop, confidence):
                a, b, c, e = d['bbox']
                d['bbox'] = [a+x, b+y, c+x, e+y]
                d['polygons'] = [[[px+x, py+y] for px, py in p] for p in d['polygons']]
                cells.append(d)
        merged = []
        for d in sorted(cells, key=lambda d: -d['confidence']):
            if not any(box_iou(d['bbox'], other['bbox']) > .35 for other in merged):
                merged.append(d)
        detections = mass + merged[:50]
        for d in detections:
            d['bbox'] = [v/scale for v in d['bbox']]
            d['polygons'] = [[[x/scale, y/scale] for x, y in p] for p in d['polygons']]
        if not tracking or not self.tracking:
            self.tracker.items.clear()
        self.tracking = tracking
        lost = self.tracker.update(detections, timestamp if timestamp is not None else time.monotonic()) if tracking else []
        return dict(objects=detections, lost=lost, width=w, height=h,
                    elapsed_ms=round((time.perf_counter()-start)*1000, 1), tracking=tracking)

    def call(self, method, args):
        if method == 'segment_reset':
            self.tracker.items.clear()
            self.tracker.last_time = None
            self.tracking = False
            return {'reset': True}
        if method != 'segment_frame':
            raise ValueError('Unknown segmentation operation.')
        data = np.frombuffer(base64.b64decode(args['image'], validate=True), dtype=np.uint8)
        frame = cv2.imdecode(data, cv2.IMREAD_COLOR)
        if frame is None:
            raise ValueError('Invalid frame image.')
        return self.analyze(frame, bool(args.get('tracking')), args.get('timestamp'),
                            bool(args.get('tiled', True)), float(args.get('confidence', .55)))
