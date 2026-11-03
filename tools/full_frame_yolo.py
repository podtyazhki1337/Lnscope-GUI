"""Full-frame 3-class YOLO segmentation: export, train, and model prefill for human review.

Classes: 0 cell, 1 cell_group, 2 spheroid. Frames are native 640x480 images, as in raw video.
Only frames marked checked (and not excluded) in X-AnyLabeling are exported.
"""
import argparse
import hashlib
import json
import shutil
from collections import Counter
from pathlib import Path

import cv2
import numpy as np

ROOT = Path(__file__).resolve().parents[1]
HUMAN = ROOT / 'datasets/full_frame_human_v1'
REVIEW = ROOT / 'datasets/xanylabeling_review'
PRETRAINED = ROOT / '.cache/yolo26n-seg.pt'
CLASSES = ['cell', 'cell_group', 'spheroid']
MIN_AREA = 20
PREFILL_MANIFEST = REVIEW / 'prefill_manifest.json'
MAX_COVERED = 0.3


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def write_json(path, data):
    path.write_text(json.dumps(data, indent=1, ensure_ascii=False) + '\n', encoding='utf-8')


def contours(mask):
    found, _ = cv2.findContours(mask.astype(np.uint8), cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
    return [cv2.approxPolyDP(c, 1.0, True).reshape(-1, 2) for c in found
            if cv2.contourArea(c) >= MIN_AREA and len(c) >= 3]


def cmd_export(output, bootstrap_val):
    frames = [f for f in read_json(HUMAN / 'manifest.json')['frames'] if f['training_eligible']]
    if output.exists():
        raise SystemExit(f'{output} exists; choose a new dataset name to keep earlier exports.')
    splits = {f['name']: f['split'] for f in frames}
    if bootstrap_val and not any(s == 'val' for s in splits.values()):
        # No human-checked validation video yet: hold out frames only to pick a checkpoint.
        for name in sorted(splits)[::bootstrap_val]:
            splits[name] = 'val'
    counts = Counter()
    for f in frames:
        split = splits[f['name']]
        h, w = f['height'], f['width']
        (output / 'images' / split).mkdir(parents=True, exist_ok=True)
        (output / 'labels' / split).mkdir(parents=True, exist_ok=True)
        shutil.copy2(HUMAN / 'images' / f['split'] / (f['name'] + '.png'), output / 'images' / split / (f['name'] + '.png'))
        lines = []
        for obj in f['objects']:
            mask = cv2.imread(str(HUMAN / obj['mask']), cv2.IMREAD_GRAYSCALE) > 0
            for poly in contours(mask):
                coords = ' '.join(f'{x / w:.6f} {y / h:.6f}' for x, y in poly)
                lines.append(f"{CLASSES.index(obj['class'])} {coords}")
                counts[f"{split}/{obj['class']}"] += 1
        (output / 'labels' / split / (f['name'] + '.txt')).write_text('\n'.join(lines) + ('\n' if lines else ''), encoding='utf-8')
        counts[f'{split}/frames'] += 1
    (output / 'data.yaml').write_text(
        f'path: {output.as_posix()}\ntrain: images/train\nval: images/val\n'
        + ('test: images/test\n' if (output / 'images/test').exists() else '')
        + 'names:\n' + ''.join(f'  {i}: {c}\n' for i, c in enumerate(CLASSES)), encoding='utf-8')
    write_json(output / 'summary.json', dict(counts=dict(counts), bootstrap_val=bool(bootstrap_val and not any(
        f['split'] == 'val' for f in frames)), source=str(HUMAN.relative_to(ROOT))))
    print(json.dumps(dict(counts)))


def cmd_train(data, runs, name, epochs, imgsz):
    from ultralytics import YOLO
    model = YOLO(str(PRETRAINED))
    model.train(data=str(data), project=str(runs), name=name, exist_ok=False, epochs=epochs, imgsz=imgsz,
                batch=4, nbs=4, optimizer='AdamW', lr0=0.001, warmup_epochs=0, patience=40, seed=42,
                mosaic=0.0, mixup=0.0, copy_paste=0.0, fliplr=0.5, flipud=0.5, degrees=10, amp=False,
                workers=0, plots=True, verbose=False)
    print('best:', model.trainer.best)


def shapes_hash(shapes):
    return hashlib.sha256(json.dumps(shapes, sort_keys=True).encode()).hexdigest()


def cmd_prefill(weights, conf, untouched_before):
    """Add model predictions to the prefill of frames the reviewer has not touched."""
    from ultralytics import YOLO
    model = YOLO(str(weights))
    known = read_json(PREFILL_MANIFEST) if PREFILL_MANIFEST.exists() else {}
    replaced, kept = 0, Counter()
    for path in sorted(REVIEW.glob('*/*.json')):
        data = read_json(path)
        flags = data.get('flags', {})
        if flags.get('checked') or flags.get('exclude') or data.get('checked'):
            kept['reviewed'] += 1
            continue
        recorded = known.get(path.stem)
        untouched = shapes_hash(data['shapes']) == recorded if recorded else path.stat().st_mtime <= untouched_before
        if not untouched:
            kept['edited'] += 1
            continue
        result = model.predict(str(path.with_suffix('.png')), conf=conf, imgsz=640, retina_masks=True, verbose=False)[0]
        h, w = data['imageHeight'], data['imageWidth']
        # Keep earlier prefill (crop-reviewed shape and class) and add only model objects it does not cover.
        shapes = [s for s in data['shapes'] if not str(s.get('description', '')).startswith('model ')]
        covered = np.zeros((h, w), np.uint8)
        for s in shapes:
            cv2.fillPoly(covered, [np.round(np.array(s['points'])).astype(np.int32)], 1)
        next_group = 1 + max([s.get('group_id') or 0 for s in shapes] + [0])
        if result.masks is not None:
            for cls, score, mask in zip(result.boxes.cls.tolist(), result.boxes.conf.tolist(),
                                        result.masks.data.cpu().numpy() > 0.5):
                if not mask.any() or (covered[mask] > 0).mean() > MAX_COVERED:
                    continue
                for poly in contours(mask):
                    shapes.append(dict(label=CLASSES[int(cls)], points=[[float(x), float(y)] for x, y in poly],
                                       group_id=next_group, description=f'model {score:.2f}', shape_type='polygon',
                                       flags={}, mask=None))
                next_group += 1
        data['shapes'] = shapes
        data['description'] = f"{data.get('description', '').split('; prefill=')[0]}; prefill=model {Path(weights).parent.parent.name}"
        write_json(path, data)
        known[path.stem] = shapes_hash(shapes)
        replaced += 1
    write_json(PREFILL_MANIFEST, known)
    print(json.dumps(dict(replaced=replaced, **kept)))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    e = sub.add_parser('export')
    e.add_argument('output', type=Path)
    e.add_argument('--bootstrap-val', type=int, default=0, help='Hold out every Nth frame as val if no val video is checked.')
    t = sub.add_parser('train')
    t.add_argument('data', type=Path)
    t.add_argument('--runs', type=Path, default=ROOT / 'artifacts/full_frame_yolo')
    t.add_argument('--name', required=True)
    t.add_argument('--epochs', type=int, default=150)
    t.add_argument('--imgsz', type=int, default=640)
    p = sub.add_parser('prefill')
    p.add_argument('weights', type=Path)
    p.add_argument('--conf', type=float, default=0.35)
    p.add_argument('--untouched-before', type=float, required=True,
                   help='POSIX time of the original export; files modified later are treated as edited.')
    args = parser.parse_args()
    if args.command == 'export':
        cmd_export(args.output, args.bootstrap_val)
    elif args.command == 'train':
        cmd_train(args.data, args.runs, args.name, args.epochs, args.imgsz)
    else:
        cmd_prefill(args.weights, args.conf, args.untouched_before)


if __name__ == '__main__':
    main()
