"""Audit every native frame and mask, and render indexed review sheets without accepting labels."""
import argparse
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path

import cv2
import numpy as np

from prepare_large_training import polygon_for
from review_full_frame_annotations import COLORS, draw

ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT/'datasets/full_frame_annotations_v1'
CLASSIFIED = ROOT/'datasets/full_frame_classified_v1'
PROPOSALS = ROOT/'datasets/full_frame_sam2_proposals_v1'


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def digest(path):
    sha = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024*1024), b''):
            sha.update(chunk)
    return sha.hexdigest()


def sheets(tiles, folder, columns, per_page):
    folder.mkdir(parents=True, exist_ok=True)
    for start in range(0, len(tiles), per_page):
        page = tiles[start:start+per_page]
        while len(page) % columns:
            page.append(np.zeros_like(tiles[0]))
        canvas = np.vstack([np.hstack(page[i:i+columns]) for i in range(0, len(page), columns)])
        cv2.imwrite(str(folder/f'page_{start//per_page+1:03d}.jpg'), canvas, [cv2.IMWRITE_JPEG_QUALITY, 95])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=ROOT/'datasets/full_frame_audit_v1')
    parser.add_argument('--verify-videos', action='store_true')
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    originals = read(BASE/'manifest.json')['frames']
    classified = {f['name']: f for f in read(CLASSIFIED/'manifest.json')['frames']}
    records, objects, errors, video_checks = [], [], [], []
    frame_tiles, object_tiles = [], []
    splits = defaultdict(set)
    proposal_count, original_mask_count = 0, 0
    for index, original in enumerate(originals, 1):
        name = original['name']
        frame = classified.get(name, original)
        source = CLASSIFIED if name in classified else BASE
        relative_image = Path('images')/frame['split']/(name+'.png')
        image = cv2.imread(str(source/relative_image))
        if image is None or image.shape[:2] != (frame['height'], frame['width']):
            errors.append(dict(frame=name, error='image_shape_or_missing'))
            continue
        shape = image.shape[:2]
        record = dict(index=index, name=name, split=frame['split'], frame=frame['frame'],
                      effective_source=str(source.relative_to(ROOT)), complete=frame['annotation_complete'],
                      flags=[], objects=[], source_video_sha256=frame['source_video_sha256'])
        for key in (frame['date'], frame['source_video_sha256']):
            splits[key].add(frame['split'])
        source_image = ROOT/original['source_image']
        if digest(source_image) != original['source_image_sha256']:
            errors.append(dict(frame=name, error='source_image_hash'))
        source_pixels = cv2.imread(str(source_image))
        if source_pixels is None or not np.array_equal(image, source_pixels):
            errors.append(dict(frame=name, error='source_image_pixels'))
        for obj in original['objects']:
            mask = cv2.imread(str(BASE/obj['mask']), 0)
            if mask is None or mask.shape != shape or np.count_nonzero(mask) != obj['area']:
                errors.append(dict(frame=name, object=obj['id'], error='restored_mask'))
            original_mask_count += 1
        masks = []
        overlay = image.copy()
        for obj in frame['objects']:
            path = source/obj['mask']
            mask = cv2.imread(str(path), 0)
            key = f'{index}:{obj["id"]}'
            if mask is None or mask.shape != shape or not mask.any() or not set(np.unique(mask)) <= {0, 255}:
                errors.append(dict(frame=name, object=key, error='mask_shape_values_or_empty'))
                continue
            if obj['class'] not in COLORS or np.count_nonzero(mask) != obj['area']:
                errors.append(dict(frame=name, object=key, error='class_or_area'))
            count, labels, stats, _ = cv2.connectedComponentsWithStats((mask > 0).astype(np.uint8), 8)
            largest_fraction = int(stats[1:, cv2.CC_STAT_AREA].max()) / int(np.count_nonzero(mask))
            flags = []
            if largest_fraction < .98:
                flags.append('fragmented_mask')
            polygon = polygon_for(mask)
            if polygon is None:
                flags.append('single_polygon_roundtrip_below_0.97')
            ys, xs = np.where(mask)
            x0, y0 = max(0, int(xs.min())-10), max(0, int(ys.min())-10)
            x1, y1 = min(shape[1], int(xs.max())+11), min(shape[0], int(ys.max())+11)
            entry = dict(key=key, frame=name, id=obj['id'], kind=obj['class'], flags=flags,
                         mask=str(path.relative_to(ROOT)), sha256=digest(path), area=int(np.count_nonzero(mask)),
                         largest_component_fraction=largest_fraction, components=count-1,
                         polygon_iou=polygon[1] if polygon else None)
            objects.append(entry)
            record['objects'].append(key)
            masks.append((obj, mask > 0))
            draw(overlay, mask > 0, f'{obj["id"]} {obj["class"]}', COLORS[obj['class']])
            crop = image[y0:y1, x0:x1].copy()
            draw(crop, mask[y0:y1, x0:x1] > 0, '', COLORS[obj['class']])
            scale = min(212/crop.shape[1], 150/crop.shape[0])
            crop = cv2.resize(crop, (round(crop.shape[1]*scale), round(crop.shape[0]*scale)))
            tile = np.full((180, 220, 3), 24, np.uint8)
            tile[25:25+crop.shape[0], 4:4+crop.shape[1]] = crop
            cv2.putText(tile, f'{key} {obj["class"]}', (4, 17), 0, .45, (255,255,255), 1)
            object_tiles.append(tile)
        for i, (a, ma) in enumerate(masks):
            for b, mb in masks[i+1:]:
                intersection = int(np.count_nonzero(ma & mb))
                if intersection/min(int(ma.sum()), int(mb.sum())) > .1:
                    record['flags'].append(dict(overlap=[a['id'], b['id']], pixels=intersection,
                                                classes=[a['class'], b['class']]))
        proposal_path = PROPOSALS/name/'proposals.json'
        proposal = read(proposal_path)
        if (proposal['source_sha256'], proposal['source_frame'], proposal['split']) != (
                frame['source_video_sha256'], frame['frame'], frame['split']):
            errors.append(dict(frame=name, error='proposal_source_identity'))
        for obj in proposal['objects']:
            mask = cv2.imread(str(proposal_path.parent/f'{obj["id"]:03d}.png'), 0)
            if mask is None or mask.shape != shape or np.count_nonzero(mask) != obj['area']:
                errors.append(dict(frame=name, object=obj['id'], error='proposal_mask'))
            proposal_count += 1
        tile = np.full((266, 320, 3), 24, np.uint8)
        tile[26:] = cv2.resize(overlay, (320, 240), interpolation=cv2.INTER_AREA)
        cv2.putText(tile, f'{index} V{name[:2]} f{frame["frame"]} {"complete" if record["complete"] else "pending"}',
                    (4, 18), 0, .45, (255,255,255), 1)
        frame_tiles.append(tile)
        records.append(record)
        if index % 100 == 0:
            print(f'Audited {index}/{len(originals)} frames', flush=True)
    if args.verify_videos:
        videos = {f['source_video']: f['source_video_sha256'] for f in originals}
        for path, expected in videos.items():
            matches = Path(path).is_file() and digest(Path(path)) == expected
            video_checks.append(dict(path=path, sha256=expected, matches=matches))
            if not matches:
                errors.append(dict(video=path, error='video_hash_or_missing'))
    for key, values in splits.items():
        if len(values) > 1:
            errors.append(dict(key=key, error='split_leakage', splits=sorted(values)))
    sheets(frame_tiles, args.output/'frames', 6, 18)
    sheets(object_tiles, args.output/'objects', 6, 24)
    summary = dict(frames=len(records), restored_masks=original_mask_count, effective_masks=len(objects),
                   sam2_proposals=proposal_count, complete_frames=sum(r['complete'] for r in records),
                   source_videos_checked=len(video_checks), hard_errors=len(errors),
                   mask_flags=dict(Counter(flag for obj in objects for flag in obj['flags'])),
                   frame_overlap_flags=sum(bool(r['flags']) for r in records),
                   visual_review='not_performed_by_this_script')
    result = dict(summary=summary, errors=errors, frames=records, objects=objects, videos=video_checks)
    (args.output/'audit.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps(summary), flush=True)


if __name__ == '__main__':
    main()
