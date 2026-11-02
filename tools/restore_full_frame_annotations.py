"""Restore reviewed crop annotations to native frames without inventing background labels."""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path

import cv2
import numpy as np


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=root/'datasets/full_frame_annotations_v1')
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    decisions = json.loads((root/'tools/large_track_acceptance.json').read_text(encoding='utf-8'))
    manifest = json.loads((root/'datasets/extraction_seed_v1/manifest.json').read_text(encoding='utf-8'))
    videos = {v['source_sha256']: (i, v) for i, v in enumerate(manifest['videos'], 1)}
    frames = {}
    duplicates = 0
    conflicts = []

    def add(video_sha, frame_number, split, image_path, mask, kind, provenance):
        nonlocal duplicates
        key = (video_sha, frame_number)
        index, video = videos[video_sha]
        assert video['split'] == split
        image = cv2.imread(str(image_path))
        assert image is not None and mask.shape == image.shape[:2]
        name = f'{index:02d}_{Path(video["source"]).stem}_f{frame_number:06d}'
        if key not in frames:
            frames[key] = dict(name=name, source_video=video['source'], source_video_sha256=video_sha,
                frame=frame_number, split=split, date=video['date'], width=image.shape[1], height=image.shape[0],
                source_image=str(image_path.relative_to(root)), source_image_sha256=sha(image_path), objects=[],
                annotation_complete=False, training_eligible=False, review_status='full_frame_completeness_review_required')
            target = output/'images'/split/(name+'.png')
            target.parent.mkdir(parents=True, exist_ok=True)
            assert cv2.imwrite(str(target), image)
        record = frames[key]
        for old in record['objects']:
            other = cv2.imread(str(output/old['mask']), 0) > 0
            intersection = np.count_nonzero((mask > 0) & other)
            union = np.count_nonzero((mask > 0) | other)
            if intersection/max(1, union) > .65:
                if old['class'] == kind:
                    old['provenance'].append(provenance)
                    duplicates += 1
                    return
                conflicts.append(dict(frame=name, existing=old['class'], incoming=kind, provenance=provenance))
        mask_path = Path('masks')/split/f'{name}_id{len(record["objects"])+1}.png'
        (output/mask_path).parent.mkdir(parents=True, exist_ok=True)
        assert cv2.imwrite(str(output/mask_path), mask)
        record['objects'].append(dict(id=len(record['objects'])+1, **{'class': kind}, mask=mask_path.as_posix(),
            area=int(np.count_nonzero(mask)), provenance=[provenance]))

    # Exact propagated masks already use native image coordinates; do not resize them.
    for case, accepted in sorted(decisions['cases'].items()):
        folder = root/'artifacts/large_video_annotations'/case
        track = json.loads((folder/'tracks.json').read_text(encoding='utf-8'))
        review = json.loads((root/'datasets/large_track_review'/case/'index.json').read_text(encoding='utf-8'))
        samples = {s['index']: s for s in review['samples']}
        for number in accepted:
            sample = samples[number]
            local = sample['local_frame']
            mask_path = folder/'review_masks'/str(sample['object_id'])/f'{local:05d}.png'
            kind = decisions.get('kind_overrides', {}).get(case, sample['kind'])
            kind = {'cell': 'cell', 'cell_group': 'cell_group', 'unresolved_group': 'spheroid'}[kind]
            add(track['source_sha256'], sample['source_frame'], track['split'], folder/'frames'/f'{local:05d}.jpg',
                cv2.imread(str(mask_path), 0), kind,
                dict(case=case, review_index=number, original_mask=str(mask_path.relative_to(root)), sha256=sha(mask_path)))

    # Earlier manual labels are normalized relative to a crop. Translate them back,
    # retaining the original full image rather than enlarging the crop to frame size.
    base = root/'datasets/yolo_training_large_v2'
    for task in ('cell', 'spheroid'):
        for entry in json.loads((base/task/'provenance.json').read_text(encoding='utf-8')):
            if 'batch' not in entry:
                continue
            a = entry['source']
            path = root/'datasets/extraction_seed_v1'/a['source_image']
            assert path.exists() and sha(path) == a['source_sha256']
            image = cv2.imread(str(path))
            x0, y0, x1, y1 = a['crop']
            label = base/task/'labels'/entry['split']/Path(entry['image']).with_suffix('.txt')
            for line in label.read_text(encoding='utf-8').splitlines():
                values = line.split()
                polygon = (np.array(values[1:], float).reshape(-1, 2)*[x1-x0, y1-y0]+[x0, y0]).round().astype(np.int32)
                mask = np.zeros(image.shape[:2], np.uint8)
                cv2.fillPoly(mask, [polygon], 255)
                add(entry['source_video_sha256'], entry['frame'], entry['split'], path, mask,
                    'cell' if task == 'cell' else 'spheroid', dict(batch=entry['batch'], crop=a['crop'], original_label=str(label.relative_to(root))))

    colors = {'cell': (0, 230, 0), 'cell_group': (255, 160, 0), 'spheroid': (0, 210, 255)}
    records = sorted(frames.values(), key=lambda r: r['name'])
    tiles = []
    (output/'review').mkdir()
    for index, record in enumerate(records, 1):
        record['review_index'] = index
        image = cv2.imread(str(output/'images'/record['split']/(record['name']+'.png')))
        for obj in record['objects']:
            mask = cv2.imread(str(output/obj['mask']), 0)
            contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
            cv2.drawContours(image, contours, -1, colors[obj['class']], 1)
        # These are review overlays, never training images.
        cv2.imwrite(str(output/'review'/(record['name']+'.jpg')), image)
        tile = np.full((210, 256, 3), 25, np.uint8)
        tile[18:210] = cv2.resize(image, (256, 192))
        cv2.putText(tile, f'{index}: {record["name"][:32]}', (3, 13), 0, .31, (255,255,255), 1)
        tiles.append(tile)
    for start in range(0, len(tiles), 16):
        page = tiles[start:start+16]
        while len(page) % 4:
            page.append(np.zeros_like(tiles[0]))
        cv2.imwrite(str(output/'review'/f'page_{start//16+1:03d}.jpg'), np.vstack([np.hstack(page[i:i+4]) for i in range(0,len(page),4)]))
    result = dict(classes={'cell': 0, 'cell_group': 1, 'spheroid': 2}, status='native_frame_review_staging', frames=records)
    (output/'manifest.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    summary = dict(frames=len(records), instances=sum(len(r['objects']) for r in records),
        classes=dict(Counter(o['class'] for r in records for o in r['objects'])),
        duplicate_masks_merged=duplicates, semantic_conflicts=conflicts,
        training_started=False, reason='Transferred masks are reviewed; full-frame annotation completeness still needs review.')
    (output/'summary.json').write_text(json.dumps(summary,indent=2),encoding='utf-8')
    print(json.dumps(summary,indent=2))


if __name__ == '__main__':
    main()
