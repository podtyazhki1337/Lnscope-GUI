"""Render native SAM proposals and apply explicit, versioned class decisions."""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import shutil

import cv2
import numpy as np

ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT / 'datasets/full_frame_annotations_v1'
PROPOSALS = ROOT / 'datasets/full_frame_sam2_proposals_v1'
COLORS = {'cell': (60, 240, 60), 'cell_group': (255, 210, 40), 'spheroid': (70, 100, 255)}
PROMPTED = ROOT / 'datasets/full_frame_prompted_v1'


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def read_mask(path, shape):
    mask = cv2.imread(str(path), cv2.IMREAD_GRAYSCALE)
    if mask is None or mask.shape != shape:
        raise ValueError(f'Missing or non-native mask: {path}')
    return mask > 0


def fill_enclosed(mask):
    """Fill holes not connected to the image border."""
    count, labels, stats, _ = cv2.connectedComponentsWithStats((~mask).astype(np.uint8), 4)
    h, w = mask.shape
    filled = mask.copy()
    for label in range(1, count):
        x, y, bw, bh, _ = stats[label]
        if x > 0 and y > 0 and x + bw < w and y + bh < h:
            filled[labels == label] = True
    return filled


def draw(image, mask, label, color):
    contours, _ = cv2.findContours(mask.astype(np.uint8), cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    cv2.drawContours(image, contours, -1, color, 1)
    ys, xs = np.where(mask)
    if len(xs):
        x, y = int(np.median(xs)), max(14, int(ys.min()))
        x = min(x, max(0, image.shape[1] - len(label)*7))
        cv2.putText(image, label, (x, y), 0, .4, (0, 0, 0), 3)
        cv2.putText(image, label, (x, y), 0, .4, color, 1)


def render(prefix):
    output = PROPOSALS / 'review'
    output.mkdir(exist_ok=True)
    count = 0
    for path in sorted(PROPOSALS.glob(prefix + '*/proposals.json')):
        data = read_json(path)
        original = cv2.imread(str(BASE / data['image']))
        tiles = []
        for obj in data['objects']:
            mask = read_mask(path.parent / f"{obj['id']:03d}.png", original.shape[:2])
            x, y, w, h = obj['bbox']
            x0, y0 = max(0, x-6), max(0, y-6)
            x1, y1 = min(original.shape[1], x+w+7), min(original.shape[0], y+h+7)
            crop = original[y0:y1, x0:x1].copy()
            draw(crop, mask[y0:y1, x0:x1], '', (70, 255, 70))
            scale = min(192/crop.shape[1], 150/crop.shape[0])
            crop = cv2.resize(crop, (round(crop.shape[1]*scale), round(crop.shape[0]*scale)))
            tile = np.full((180, 200, 3), 28, np.uint8)
            tile[25:25+crop.shape[0], 4:4+crop.shape[1]] = crop
            label = f"{obj['id']}: {obj.get('suggested_class') or '?'}"
            cv2.putText(tile, label, (4, 17), 0, .4, (255, 255, 255), 1)
            tiles.append(tile)
        for start in range(0, len(tiles), 24):
            page = tiles[start:start+24]
            while len(page) % 6:
                page.append(np.zeros_like(tiles[0]))
            cv2.imwrite(str(output/f'{path.parent.name}_{start//24+1}.jpg'),
                        np.vstack([np.hstack(page[i:i+6]) for i in range(0, len(page), 6)]))
        count += 1
    print(f'Rendered {count} frames; crops are review aids, masks remain native.')


def apply(decisions_path, output):
    decisions = read_json(decisions_path)
    manifest = read_json(BASE/'manifest.json')
    frames = {f['name']: f for f in manifest['frames']}
    result = []
    counts = Counter()
    output.mkdir(parents=True, exist_ok=True)
    for name, decision in decisions['frames'].items():
        original = frames[name]
        folder = PROPOSALS/name
        proposals = read_json(folder/'proposals.json')
        if proposals['source_sha256'] != original['source_video_sha256'] or proposals['source_frame'] != original['frame']:
            raise ValueError(f'Source mismatch: {name}')
        shape = (original['height'], original['width'])
        masks, provenance = {}, {}
        for obj in decision['objects']:
            key = obj['key']
            if key in masks or obj['class'] not in COLORS:
                raise ValueError(f'Duplicate key or invalid class: {name}/{key}')
            if obj['source'] == 'base':
                source = next(o for o in original['objects'] if o['id'] == obj['id'])
                path = BASE/source['mask']
            elif obj['source'] == 'sam2':
                if obj['id'] not in {o['id'] for o in proposals['objects']}:
                    raise ValueError(f'Unknown proposal: {name}/{obj["id"]}')
                path = folder/f"{obj['id']:03d}.png"
            elif obj['source'] == 'prompted':
                path = PROMPTED/name/f'{key}.png'
                prompt_record = read_json(PROMPTED/name/f'{key}.json')
                if prompt_record['prompt'] != obj['prompt']:
                    raise ValueError(f'Stale prompted mask: {name}/{key}')
            else:
                raise ValueError(f'Unknown mask source: {obj["source"]}')
            masks[key] = read_mask(path, shape)
            provenance[key] = dict(source=str(path.relative_to(ROOT)),
                sha256=hashlib.sha256(path.read_bytes()).hexdigest(), decision=obj)
        final = {}
        for obj in decision['objects']:
            mask = masks[obj['key']].copy()
            for key in obj.get('subtract', []):
                mask &= ~masks[key]
            if obj.get('largest_component'):
                count, labels, stats, _ = cv2.connectedComponentsWithStats(mask.astype(np.uint8), 8)
                if count > 1:
                    mask = labels == (1 + int(np.argmax(stats[1:, cv2.CC_STAT_AREA])))
            if not mask.any():
                raise ValueError(f'Empty mask: {name}/{obj["key"]}')
            final[obj['key']] = mask
        # A reviewed frame must account for every candidate, including rejected ones.
        selected = {o['id'] for o in decision['objects'] if o['source'] == 'sam2'}
        rejected = {int(i) for i in decision.get('rejected', {})}
        complete = decision.get('annotation_complete', False)
        if selected & rejected:
            raise ValueError(f'Selected and rejected candidate: {name}')
        if complete and (selected | rejected != {o['id'] for o in proposals['objects']} or decision.get('issues')):
            raise ValueError(f'Incomplete review cannot become training eligible: {name}')
        record = {k: v for k, v in original.items() if k != 'objects'}
        record.update(objects=[], annotation_complete=complete, training_eligible=complete,
                      review_status='complete' if complete else 'corrections_required', review_notes=decision)
        image_path = Path('images')/original['split']/(name+'.png')
        (output/image_path).parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(BASE/image_path, output/image_path)
        image = cv2.imread(str(output/image_path))
        for index, obj in enumerate(decision['objects'], 1):
            mask = final[obj['key']]
            path = Path('masks')/original['split']/f'{name}_id{index}.png'
            (output/path).parent.mkdir(parents=True, exist_ok=True)
            cv2.imwrite(str(output/path), mask.astype(np.uint8)*255)
            record['objects'].append(dict(id=index, **{'class': obj['class']}, mask=path.as_posix(),
                area=int(mask.sum()), provenance=[provenance[obj['key']]]))
            counts[obj['class']] += 1
            draw(image, mask, f"{index} {obj['class']}", COLORS[obj['class']])
        (output/'review').mkdir(exist_ok=True)
        cv2.imwrite(str(output/'review'/f'{name}.png'), image)
        result.append(record)
    summary = dict(reviewed_frames=len(result), complete_frames=sum(r['annotation_complete'] for r in result),
                   objects=dict(counts), total_source_frames=len(frames))
    (output/'manifest.json').write_text(json.dumps(dict(classes=manifest['classes'], frames=result,
        status='explicit_full_frame_class_review', summary=summary), indent=2), encoding='utf-8')
    print(json.dumps(summary))


def prompt_masks(decisions_path, only=''):
    import torch
    from sam2.build_sam import build_sam2
    from sam2.sam2_image_predictor import SAM2ImagePredictor

    predictor = SAM2ImagePredictor(build_sam2('configs/sam2.1/sam2.1_hiera_t.yaml',
        str(ROOT/'.cache/sam2.1_hiera_tiny.pt'), device='cuda', apply_postprocessing=False))
    frames = {f['name']: f for f in read_json(BASE/'manifest.json')['frames']}
    with torch.inference_mode(), torch.autocast('cuda', dtype=torch.bfloat16):
        for name, decision in read_json(decisions_path)['frames'].items():
            objects = [o for o in decision['objects'] if o['source'] == 'prompted']
            if not objects or not name.startswith(only):
                continue
            if all((PROMPTED/name/f'{o["key"]}.json').exists() and (PROMPTED/name/f'{o["key"]}.png').exists()
                   and read_json(PROMPTED/name/f'{o["key"]}.json')['prompt'] == o['prompt'] for o in objects):
                continue
            image = cv2.imread(str(BASE/'images'/frames[name]['split']/(name+'.png')))
            predictor.set_image(cv2.cvtColor(image, cv2.COLOR_BGR2RGB))
            folder = PROMPTED/name
            folder.mkdir(parents=True, exist_ok=True)
            for obj in objects:
                prompt = obj['prompt']
                record = folder/f'{obj["key"]}.json'
                # Reuse a mask whose prompt is unchanged so confirmed masks are not regenerated.
                if record.exists() and (folder/f'{obj["key"]}.png').exists() and read_json(record)['prompt'] == prompt:
                    continue
                masks, scores, _ = predictor.predict(
                    point_coords=np.array(prompt['points'], dtype=np.float32),
                    point_labels=np.array(prompt['labels'], dtype=np.int32),
                    box=np.array(prompt['box'], dtype=np.float32), multimask_output=False)
                mask = fill_enclosed(masks[0] > 0)
                if mask.shape != image.shape[:2] or not mask.any():
                    raise ValueError(f'Invalid prompted mask: {name}/{obj["key"]}')
                cv2.imwrite(str(folder/f'{obj["key"]}.png'), mask.astype(np.uint8)*255)
                (folder/f'{obj["key"]}.json').write_text(json.dumps(dict(prompt=prompt,
                    predicted_iou=float(scores[0]), status='requires_visual_review'), indent=2), encoding='utf-8')
                print(f'{name}/{obj["key"]}: {int(mask.sum())} pixels', flush=True)


def native_pages(output, prefix):
    """Tile final overlays without resizing any source pixels."""
    frames = [f for f in read_json(output/'manifest.json')['frames'] if f['name'].startswith(prefix)]
    for start in range(0, len(frames), 4):
        tiles = []
        for frame in frames[start:start+4]:
            overlay = cv2.imread(str(output/'review'/f'{frame["name"]}.png'))
            tile = np.full((overlay.shape[0]+26, overlay.shape[1], 3), 24, np.uint8)
            tile[26:] = overlay
            cv2.putText(tile, frame['name'], (6, 18), 0, .45, (255, 255, 255), 1)
            tiles.append(tile)
        while len(tiles) < 4:
            tiles.append(np.zeros_like(tiles[0]))
        page = np.vstack([np.hstack(tiles[:2]), np.hstack(tiles[2:])])
        path = output/'review'/f'native_{prefix}_{start//4+1:03d}.jpg'
        cv2.imwrite(str(path), page, [cv2.IMWRITE_JPEG_QUALITY, 95])
    print(f'Rendered {len(frames)} final overlays at native pixel scale.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--render', help='Render candidate sheets for a frame-name prefix.')
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('--prompt', action='store_true', help='Generate explicitly prompted repair masks for review.')
    parser.add_argument('--only', default='', help='Limit --prompt to frame names with this prefix.')
    parser.add_argument('--pages', help='Tile final native-size overlays matching this frame-name prefix.')
    parser.add_argument('--decisions', type=Path, default=ROOT/'tools/full_frame_class_review.json')
    parser.add_argument('--output', type=Path, default=ROOT/'datasets/full_frame_classified_v1')
    args = parser.parse_args()
    if args.render is not None:
        render(args.render)
    if args.prompt:
        prompt_masks(args.decisions, args.only)
    if args.apply:
        apply(args.decisions, args.output)
    if args.pages is not None:
        native_pages(args.output, args.pages)


if __name__ == '__main__':
    main()
