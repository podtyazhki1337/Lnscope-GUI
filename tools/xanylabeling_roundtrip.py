"""Hand native full-frame annotations to X-AnyLabeling for human review and read them back.

export: one folder per video with PNG frames and LabelMe-style JSON polygons, prefilled
        from the classified review (confirmed, pending and propagated frames) or, for
        untouched frames, from the restored crop-reviewed masks.
import: read the edited JSON back, validate it and write a native-frame manifest with
        per-object masks. Only frames flagged `checked` and not `exclude` become trainable.
"""
import argparse
import hashlib
import json
from collections import Counter
from pathlib import Path

import cv2
import numpy as np

ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT / 'datasets/full_frame_annotations_v1'
CLASSIFIED = ROOT / 'datasets/full_frame_classified_v1'
REVIEW = ROOT / 'datasets/xanylabeling_review'
OUTPUT = ROOT / 'datasets/full_frame_human_v1'
CLASSES = ['cell', 'cell_group', 'spheroid']
FLAGS = ['checked', 'exclude']
MIN_CONTOUR_AREA = 20
SIMPLIFY_PX = 1.0


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def write_json(path, data):
    path.write_text(json.dumps(data, indent=1, ensure_ascii=False) + '\n', encoding='utf-8')


def polygons(mask):
    contours, _ = cv2.findContours(mask.astype(np.uint8), cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
    result = []
    for contour in sorted(contours, key=cv2.contourArea, reverse=True):
        if cv2.contourArea(contour) < MIN_CONTOUR_AREA:
            continue
        approx = cv2.approxPolyDP(contour, SIMPLIFY_PX, True).reshape(-1, 2)
        if len(approx) >= 3:
            result.append([[float(x), float(y)] for x, y in approx])
    return result


def video_of(name):
    return name.rsplit('_f', 1)[0]


def cmd_export(overwrite):
    base = {f['name']: f for f in read_json(BASE / 'manifest.json')['frames']}
    classified = {f['name']: f for f in read_json(CLASSIFIED / 'manifest.json')['frames']} \
        if (CLASSIFIED / 'manifest.json').exists() else {}
    if REVIEW.exists() and any(REVIEW.rglob('*.json')) and not overwrite:
        raise SystemExit(f'{REVIEW} already holds review files; pass --overwrite only if nothing was edited.')
    counts = Counter()
    for name, frame in sorted(base.items()):
        folder = REVIEW / video_of(name)
        folder.mkdir(parents=True, exist_ok=True)
        image_name = name + '.png'
        src = BASE / 'images' / frame['split'] / image_name
        (folder / image_name).write_bytes(src.read_bytes())
        if name in classified:
            record, root, origin = classified[name], CLASSIFIED, classified[name].get('review_status', 'classified')
        else:
            record, root, origin = frame, BASE, 'restored_crop_review'
        shapes = []
        for index, obj in enumerate(record['objects'], 1):
            mask = cv2.imread(str(root / obj['mask']), cv2.IMREAD_GRAYSCALE) > 0
            for points in polygons(mask):
                shapes.append(dict(label=obj['class'], points=points, group_id=index, description='',
                                   shape_type='polygon', flags={}, mask=None))
                counts[obj['class']] += 1
        complete = bool(record.get('annotation_complete'))
        write_json(folder / (name + '.json'), dict(
            version='4.3.1', flags={'checked': False, 'exclude': False}, shapes=shapes,
            imagePath=image_name, imageData=None, imageHeight=frame['height'], imageWidth=frame['width'],
            description=f'split={frame["split"]}; prefill={origin}'))
        counts['frames'] += 1
        counts['prefill_previously_confirmed'] += complete
    (REVIEW / 'classes.txt').write_text('\n'.join(CLASSES) + '\n', encoding='utf-8')
    (REVIEW / 'flags.txt').write_text('\n'.join(FLAGS) + '\n', encoding='utf-8')
    print(json.dumps(dict(counts)))


def cmd_import():
    base = {f['name']: f for f in read_json(BASE / 'manifest.json')['frames']}
    frames, counts, problems = [], Counter(), []
    for path in sorted(REVIEW.glob('*/*.json')):
        name = path.stem
        if name not in base:
            problems.append(f'{name}: not in the source manifest')
            continue
        frame, data = base[name], read_json(path)
        shape = (frame['height'], frame['width'])
        if (data.get('imageHeight'), data.get('imageWidth')) != shape:
            problems.append(f'{name}: image size changed')
            continue
        flags = data.get('flags', {})
        checked, exclude = bool(flags.get('checked')), bool(flags.get('exclude'))
        instances = {}
        for i, s in enumerate(data.get('shapes', [])):
            if s.get('label') not in CLASSES:
                problems.append(f'{name}: unknown label {s.get("label")!r}')
                continue
            if s.get('shape_type') not in ('polygon', 'rectangle', 'circle') or len(s.get('points', [])) < 2:
                problems.append(f'{name}: unsupported shape {s.get("shape_type")}')
                continue
            pts = np.array(s['points'], np.float32)
            key = (s['label'], s.get('group_id')) if s.get('group_id') is not None else (s['label'], f'shape{i}')
            mask = instances.setdefault(key, np.zeros(shape, np.uint8))
            if s['shape_type'] == 'circle':
                # X-AnyLabeling stores a circle as centre and one point on the rim.
                radius = int(round(float(np.linalg.norm(pts[1] - pts[0]))))
                cv2.circle(mask, tuple(int(v) for v in np.round(pts[0])), radius, 1, -1)
                continue
            if s['shape_type'] == 'rectangle':
                (x0, y0), (x1, y1) = pts.min(0), pts.max(0)
                pts = np.array([[x0, y0], [x1, y0], [x1, y1], [x0, y1]], np.float32)
            cv2.fillPoly(mask, [np.round(pts).astype(np.int32)], 1)
        # Policy: spheroid is unresolved tissue, so separately labelled cells and groups are cut out of it.
        resolved = np.zeros(shape, bool)
        for (label, _), mask in instances.items():
            if label != 'spheroid':
                resolved |= mask > 0
        for (label, _), mask in instances.items():
            if label == 'spheroid':
                mask[resolved] = 0
        trainable = checked and not exclude
        record = {k: v for k, v in frame.items() if k != 'objects'}
        record.update(objects=[], annotation_complete=checked, training_eligible=trainable,
                      review_status='excluded' if exclude else 'human_checked' if checked else 'unchecked',
                      review_file=path.relative_to(ROOT).as_posix())
        image_path = Path('images') / frame['split'] / (name + '.png')
        (OUTPUT / image_path).parent.mkdir(parents=True, exist_ok=True)
        (OUTPUT / image_path).write_bytes((BASE / image_path).read_bytes())
        for index, ((label, _), mask) in enumerate(instances.items(), 1):
            if not mask.any():
                continue
            mpath = Path('masks') / frame['split'] / f'{name}_id{index}.png'
            (OUTPUT / mpath).parent.mkdir(parents=True, exist_ok=True)
            cv2.imwrite(str(OUTPUT / mpath), mask * 255)
            record['objects'].append(dict(id=index, **{'class': label}, mask=mpath.as_posix(), area=int(mask.sum())))
            if trainable:
                counts[label] += 1
        frames.append(record)
        counts['checked'] += checked
        counts['excluded'] += exclude
        counts['trainable_frames'] += trainable
    summary = dict(counts, frames=len(frames), problems=len(problems))
    write_json(OUTPUT / 'manifest.json', dict(classes={c: i for i, c in enumerate(CLASSES)}, frames=frames,
               status='human_review_xanylabeling', summary=summary,
               review_sha256=hashlib.sha256(b''.join(p.read_bytes() for p in sorted(REVIEW.glob('*/*.json')))).hexdigest()))
    for p in problems[:50]:
        print('PROBLEM', p)
    print(json.dumps(summary))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    e = sub.add_parser('export')
    e.add_argument('--overwrite', action='store_true')
    sub.add_parser('import')
    args = parser.parse_args()
    cmd_export(args.overwrite) if args.command == 'export' else cmd_import()


if __name__ == '__main__':
    main()
