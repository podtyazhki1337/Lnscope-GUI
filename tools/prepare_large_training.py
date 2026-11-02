"""Export explicitly reviewed video masks, preserving source splits and audit data."""
import argparse
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path
import re
import shutil

import cv2
import numpy as np

from train_vision_pilot import prepare


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def polygon_for(mask):
    contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    if not contours:
        return None
    contour = max(contours, key=cv2.contourArea)
    for epsilon in (.7, .35, 0):
        polygon = cv2.approxPolyDP(contour, epsilon, True).reshape(-1, 2)
        if len(polygon) < 3:
            continue
        rebuilt = np.zeros_like(mask)
        cv2.fillPoly(rebuilt, [polygon], 255)
        iou = np.count_nonzero((mask > 0) & (rebuilt > 0)) / np.count_nonzero((mask > 0) | (rebuilt > 0))
        if iou >= .97:
            return polygon, iou
    return None


def overlaps(a, b):
    x0, y0 = max(a[0], b[0]), max(a[1], b[1])
    x1, y1 = min(a[2], b[2]), min(a[3], b[3])
    intersection = max(0, x1-x0) * max(0, y1-y0)
    smaller = min((a[2]-a[0])*(a[3]-a[1]), (b[2]-b[0])*(b[3]-b[1]))
    return intersection / max(1, smaller)


def main():
    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=root/'datasets/yolo_training_large_v1')
    args = parser.parse_args()
    output = args.output.resolve()
    decisions = json.loads((root/'tools/large_track_acceptance.json').read_text())
    source_manifest = json.loads((root/'datasets/extraction_seed_v1/manifest.json').read_text())
    videos = {v['source_relative'].replace('\\', '/'): v for v in source_manifest['videos']}
    by_hash = {v['source_sha256']: v for v in videos.values()}
    tracks_root = root/'artifacts/large_video_annotations'
    review_root = root/'datasets/large_track_review'
    cases = {p.parent.name for p in tracks_root.glob('*/tracks.json')}
    assert cases == set(decisions['cases']), 'Every completed case must have an explicit review decision.'
    prepare(root, output)
    records = {task: json.loads((output/task/'provenance.json').read_text()) for task in ('cell', 'spheroid')}
    baseline_frames = set()
    seen = defaultdict(list)
    rejected = []
    for task, entries in records.items():
        for entry in entries:
            source = entry['source']
            video = videos[source['video'].replace('\\', '/')]
            assert source['split'] == video['split']
            frame = int(re.search(r'_f(\d+)', source['image'])[1])
            entry.update(source_video=video['source'], source_video_sha256=video['source_sha256'],
                         date=video['date'], frame=frame, split=source['split'],
                         review_status='Earlier explicit AI visual annotation; not expert ground truth')
            baseline_frames.add((task, video['source_sha256'], frame))

    def reject(case, sample, reason):
        rejected.append(dict(case=case, index=sample['index'], reason=reason))

    for case in sorted(cases):
        folder = tracks_root/case
        track = json.loads((folder/'tracks.json').read_text())
        index = json.loads((review_root/case/'index.json').read_text())
        samples = {s['index']: s for s in index['samples']}
        selected = decisions['cases'][case]
        assert len(selected) == len(set(selected)) and set(selected) <= set(samples)
        video = by_hash[track['source_sha256']]
        assert track['split'] == index['split'] == video['split']
        for number in selected:
            sample = samples[number]
            local = sample['local_frame']
            frame = track['frames'][local]
            assert frame['source_frame'] == sample['source_frame']
            kind = decisions.get('kind_overrides', {}).get(case, sample['kind'])
            assert kind in ('cell', 'cell_group', 'unresolved_group'), kind
            task = 'spheroid' if kind == 'unresolved_group' else 'cell'
            key = (task, video['source_sha256'])
            if (*key, frame['source_frame']) in baseline_frames:
                reject(case, sample, 'baseline_already_labels_this_frame'); continue
            mask_path = folder/'review_masks'/str(sample['object_id'])/f'{local:05d}.png'
            image_path = folder/'frames'/f'{local:05d}.jpg'
            mask = cv2.imread(str(mask_path), 0)
            image = cv2.imread(str(image_path))
            assert mask is not None and image is not None and mask.shape == image.shape[:2]
            assert set(np.unique(mask)) <= {0, 255}
            ys, xs = np.nonzero(mask)
            if len(xs) < 25:
                reject(case, sample, 'empty_or_tiny_mask'); continue
            bounds = [int(xs.min()), int(ys.min()), int(xs.max())+1, int(ys.max())+1]
            if any(abs(frame['source_frame']-f) < 8 and overlaps(bounds, box) > .5 for f, box in seen[key]):
                reject(case, sample, 'overlapping_object_within_eight_source_frames'); continue
            h, w = mask.shape
            crop = [0, 0, w, h] if task == 'spheroid' else [max(0,bounds[0]-4), max(0,bounds[1]-4), min(w,bounds[2]+4), min(h,bounds[3]+4)]
            x0, y0, x1, y1 = crop
            # Sparse full-frame cell labels would incorrectly teach unlabelled cells as background.
            # Export only the visually reviewed local crop; reject known neighboring masks.
            conflict = False
            if task == 'cell':
                for obj in frame['objects']:
                    if obj['id'] == sample['object_id']:
                        continue
                    other = cv2.imread(str(folder/'review_masks'/str(obj['id'])/f'{local:05d}.png'), 0)
                    if np.count_nonzero(other[y0:y1, x0:x1]) > 5:
                        conflict = True; break
            if conflict:
                reject(case, sample, 'another_tracked_object_intersects_crop'); continue
            cropped_mask = mask[y0:y1, x0:x1]
            result = polygon_for(cropped_mask)
            if result is None:
                reject(case, sample, 'polygon_roundtrip_iou_below_0.97'); continue
            polygon, iou = result
            height, width = cropped_mask.shape
            normalized = polygon / np.array([width, height])
            assert np.isfinite(normalized).all() and normalized.min() >= 0 and normalized.max() <= 1
            class_id = 1 if kind == 'cell_group' else 0
            split = video['split']
            stem = f'{case}__f{frame["source_frame"]:06d}_id{sample["object_id"]}'
            for subdir in ('images', 'labels', 'masks'):
                (output/task/subdir/split).mkdir(parents=True, exist_ok=True)
            target = output/task/'images'/split/(stem+'.png')
            assert cv2.imwrite(str(target), image[y0:y1, x0:x1])
            assert cv2.imwrite(str(output/task/'masks'/split/(stem+'.png')), cropped_mask)
            label = output/task/'labels'/split/(stem+'.txt')
            label.write_text(str(class_id)+' '+' '.join(f'{v:.8f}' for v in normalized.flatten())+'\n')
            records[task].append(dict(image=target.name, sha256=digest(target), label_sha256=digest(label),
                source_video=video['source'], source_video_sha256=video['source_sha256'], date=video['date'],
                frame=frame['source_frame'], decoder_time_ms=frame['decoder_time_ms'], split=split,
                case=case, review_index=number, object_id=sample['object_id'], kind=kind, crop=crop,
                source_mask=str(mask_path.relative_to(root)), source_mask_sha256=digest(mask_path),
                source_frame_sha256=digest(image_path), polygon_mask_iou=iou,
                review_status='Explicit AI visual review; not expert ground truth',
                scope='mass_only_full_frame' if task == 'spheroid' else 'reviewed_cell_crop_not_exhaustive_full_frame'))
            seen[key].append((frame['source_frame'], bounds))

    negatives_path = root/'tools/large_negative_acceptance.json'
    negatives = json.loads(negatives_path.read_text())['samples']
    for sample in negatives:
        folder = tracks_root/sample['case']
        track = json.loads((folder/'tracks.json').read_text())
        video = by_hash[sample['source_sha256']]
        local = sample['local_frame']
        assert track['source_sha256'] == sample['source_sha256']
        assert track['split'] == sample['split'] == video['split']
        assert track['frames'][local]['source_frame'] == sample['source_frame']
        image_path = folder/'frames'/f'{local:05d}.jpg'
        image = cv2.imread(str(image_path))
        x0, y0, x1, y1 = sample['crop']
        assert 0 <= x0 < x1 <= image.shape[1] and 0 <= y0 < y1 <= image.shape[0]
        for task in records:
            split = video['split']
            stem = f'negative_{sample["index"]:03d}_{sample["case"]}'
            target = output/task/'images'/split/(stem+'.png')
            assert cv2.imwrite(str(target), image[y0:y1, x0:x1])
            (output/task/'labels'/split/(stem+'.txt')).write_text('')
            records[task].append(dict(image=target.name, sha256=digest(target), split=split,
                source_video=video['source'], source_video_sha256=video['source_sha256'], date=video['date'],
                frame=sample['source_frame'], crop=sample['crop'], negative=True,
                source_frame_sha256=digest(image_path), review_index=sample['index'],
                review_status='Explicit AI visual review: no visible cell, group, or spheroid'))

    summary = {'status': 'Reviewed training candidate; expert review still recommended', 'tasks': {}}
    assignments, date_assignments = {}, {}
    for task, entries in records.items():
        counts = defaultdict(Counter)
        image_hashes = set()
        for entry in entries:
            split = entry['split']
            assert assignments.setdefault(entry['source_video_sha256'], split) == split, 'Video leakage'
            assert date_assignments.setdefault(entry['date'], split) == split, 'Acquisition-date leakage'
            assert entry['sha256'] not in image_hashes, 'Duplicate image in a task'
            image_hashes.add(entry['sha256'])
            counts[split]['images'] += 1
            if entry.get('negative'):
                counts[split]['negative_images'] += 1
            label = output/task/'labels'/split/Path(entry['image']).with_suffix('.txt')
            for line in label.read_text().splitlines():
                values = line.split()
                assert len(values) >= 7 and len(values) % 2 == 1
                class_id = int(values[0])
                assert class_id in ([0, 1] if task == 'cell' else [0])
                points = np.array(values[1:], dtype=float)
                assert np.isfinite(points).all() and points.min() >= 0 and points.max() <= 1
                counts[split]['objects'] += 1
                counts[split]['cell_group' if class_id == 1 else ('visible_cell' if task == 'cell' else 'spheroid_mass')] += 1
        assert all(counts[s]['images'] for s in ('train', 'val', 'test'))
        names = '  0: visible_cell\n  1: cell_group\n' if task == 'cell' else '  0: spheroid_mass\n'
        (output/task/'data.yaml').write_text(f'path: {json.dumps((output/task).as_posix())}\ntrain: images/train\nval: images/val\ntest: images/test\nnames:\n'+names)
        (output/task/'provenance.json').write_text(json.dumps(entries, indent=2))
        summary['tasks'][task] = dict(splits=dict(counts), images=len(entries),
            unique_source_frames=len({(r['source_video_sha256'], r['frame']) for r in entries}),
            unique_source_videos=len({r['source_video_sha256'] for r in entries}))
    summary.update(reviewed_cases=len(cases), accepted_review_masks=sum(map(len, decisions['cases'].values())),
        exclusions=dict(Counter(r['reason'] for r in rejected)),
        unique_source_frames=len({(r['source_video_sha256'], r['frame']) for es in records.values() for r in es}),
        unique_source_videos=len(assignments), splits_by_date=date_assignments,
        review_decisions_sha256=digest(root/'tools/large_track_acceptance.json'))
    (output/'summary.json').write_text(json.dumps(summary, indent=2))
    (output/'exclusions.json').write_text(json.dumps(rejected, indent=2))
    shutil.copy2(root/'tools/large_track_acceptance.json', output/'review_decisions.json')
    shutil.copy2(negatives_path, output/'negative_review_decisions.json')
    print(json.dumps(summary, indent=2))


if __name__ == '__main__':
    main()
