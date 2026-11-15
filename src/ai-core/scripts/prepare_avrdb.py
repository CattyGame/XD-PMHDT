"""Prepare the publisher's AVRDB archive, preserving source bytes and provenance."""
from __future__ import annotations

import argparse
import csv
import hashlib
import io
import json
from pathlib import Path
import re
import zipfile

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[3]
PUBLISHER_SHA256 = "f0af3cc8714e1eaff5d2b5a3e0b77f8c6166a0d18322dd2685c2c6ed325fc230"
DATASET_DOI = "10.17632/3csr652p9y.2"


def digest(data):
    return hashlib.sha256(data).hexdigest()


def write_verified(path: Path, data: bytes):
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists() and path.read_bytes() != data:
        raise ValueError(f"Existing output differs; use a fresh directory: {path}")
    path.write_bytes(data)


def png_bytes(array):
    buffer = io.BytesIO()
    Image.fromarray(array).save(buffer, format="PNG")
    return buffer.getvalue()


def mask_from_jpeg(raw):
    with Image.open(io.BytesIO(raw)) as image:
        return np.mean(np.asarray(image.convert("RGB")), axis=2) < 240


def recover_vector_mask(ai_bytes, rgb, artery: bool):
    import pymupdf

    document = pymupdf.open(stream=ai_bytes, filetype="pdf")
    page = document[0]
    height, width = rgb.shape[:2]
    if tuple(page.rect)[2:] != (float(width), float(height)):
        raise ValueError("Vector annotation geometry does not match the photograph.")
    images = [im for im in page.get_images() if im[2:4] == (width, height)]
    if len(images) != 1:
        raise ValueError("Ambiguous embedded photograph in the publisher annotation.")
    pixmap = pymupdf.Pixmap(document, images[0][0])
    embedded = np.frombuffer(pixmap.samples, np.uint8).reshape(height, width, pixmap.n)[:, :, :3]
    mae = float(np.abs(embedded.astype(np.float32) - rgb).mean())
    if mae > 1:
        raise ValueError("The annotation's photograph does not agree with its case photograph.")
    output = pymupdf.open()
    target = output.new_page(width=width, height=height)
    included = 0
    for drawing in page.get_drawings():
        color = drawing.get("fill")
        if not color or len(color) != 3 or (color[0] > color[2]) != artery:
            continue
        if max(color[0], color[2]) < 2 * color[1]:
            continue
        shape = target.new_shape()
        for item in drawing["items"]:
            if item[0] == "c":
                shape.draw_bezier(*item[1:])
            elif item[0] == "l":
                shape.draw_line(*item[1:])
            elif item[0] == "re":
                shape.draw_rect(item[1])
            elif item[0] == "qu":
                shape.draw_quad(item[1])
            else:
                raise ValueError(f"Unsupported annotation path: {item[0]}")
        shape.finish(fill=(0, 0, 0), color=None, closePath=drawing.get("closePath", False), even_odd=drawing.get("even_odd", False))
        shape.commit()
        included += 1
    if not included:
        raise ValueError("No class-specific vector annotations found.")
    rendered = target.get_pixmap(colorspace=pymupdf.csGRAY, alpha=False)
    mask = np.frombuffer(rendered.samples, np.uint8).reshape(height, width) < 240
    return mask, {"method": "PUBLISHER_SAME_CASE_AI_VECTOR", "paths": included,
                  "embedded_rgb_mae": mae, "pixel_identity_claimed": False}


def prepare(archive: Path, destination: Path, manifest: Path):
    if digest(archive.read_bytes()) != PUBLISHER_SHA256:
        raise ValueError("Archive SHA-256 differs from the publisher's versioned file.")
    records = []
    recoveries = []
    with zipfile.ZipFile(archive) as source:
        originals = sorted(n for n in source.namelist() if re.fullmatch(r"IM\d+\.JPG", Path(n).name, re.IGNORECASE))
        if len(originals) != 100 or len({Path(n).stem for n in originals}) != 100:
            raise ValueError("Expected 100 unique publisher images, including the nested IM000189 case.")
        for index, entry in enumerate(originals):
            case = Path(entry).stem
            prefix = str(Path(entry).parent).replace("\\", "/") + "/"
            candidates = [n for n in source.namelist() if n.startswith(prefix) and "/" not in n[len(prefix):]]
            image_bytes = source.read(entry)
            with Image.open(io.BytesIO(image_bytes)) as image:
                rgb = np.asarray(image.convert("RGB"))
            ai_entry = prefix + case + ".ai"
            ai_bytes = source.read(ai_entry) if ai_entry in source.namelist() else None
            masks = []
            origins = []
            for artery in (True, False):
                suffixes = ("--arteries.jpg", "--artry.jpg", "--artery.jpg", "--atertries.jpg") if artery else ("--veins.jpg", "--vein.jpg", "--veinds.jpg", "--veisn.jpg")
                matching = [n for n in candidates if Path(n).name in {case + suffix for suffix in suffixes}]
                if len(matching) == 1:
                    mask = mask_from_jpeg(source.read(matching[0]))
                    origin = {"method": "PUBLISHER_JPEG", "entry": matching[0], "source_sha256": digest(source.read(matching[0]))}
                elif not matching:
                    if ai_bytes is None:
                        raise ValueError(f"No same-case vector annotation to recover missing mask: {case}")
                    mask, origin = recover_vector_mask(ai_bytes, rgb, artery)
                    origin.update(entry=ai_entry, source_sha256=digest(ai_bytes), case_id=case,
                                  role="artery" if artery else "vein")
                    recoveries.append(origin)
                else:
                    raise ValueError(f"Ambiguous publisher masks for {case}")
                if mask.shape != rgb.shape[:2] or not mask.any():
                    raise ValueError(f"Invalid annotation geometry/content: {case}")
                masks.append(mask)
                origins.append(origin)
            labels = np.zeros(rgb.shape[:2], dtype=np.uint8)
            labels[masks[0]] = 1
            labels[masks[1]] = 2
            labels[masks[0] & masks[1]] = 255
            folder = destination / case
            outputs = {"image": (folder / f"{case}.JPG", image_bytes),
                       "artery": (folder / "arteries.png", png_bytes(masks[0].astype(np.uint8) * 255)),
                       "vein": (folder / "veins.png", png_bytes(masks[1].astype(np.uint8) * 255)),
                       "labels": (folder / "labels.png", png_bytes(labels))}
            for path, data in outputs.values():
                write_verified(path, data)
            record = {"case_id": case, "dataset_id": "AVRDB", "dataset_doi": DATASET_DOI,
                      "split": "pending_group_assignment",
                      "group_id": digest(rgb.tobytes()), "grouping_basis": "decoded_pixel_identity; patient linkage unavailable",
                      "width": rgb.shape[1], "height": rgb.shape[0], "source_entry": entry,
                      "archive_sha256": PUBLISHER_SHA256, "source_ai_sha256": digest(ai_bytes) if ai_bytes is not None else "",
                      "artery_origin": origins[0]["method"], "vein_origin": origins[1]["method"]}
            for key, (path, data) in outputs.items():
                record[f"{key}_path"] = path.resolve().relative_to(ROOT).as_posix()
                record[f"{key}_sha256"] = digest(data)
            records.append(record)
    groups = sorted({row["group_id"] for row in records})
    shuffled = np.random.default_rng(42).permutation(len(groups))
    assignments = {groups[index]: "train" if rank < int(0.7 * len(groups)) else "validation" if rank < int(0.85 * len(groups)) else "test"
                   for rank, index in enumerate(shuffled)}
    for row in records:
        row["split"] = assignments[row["group_id"]]
    manifest.parent.mkdir(parents=True, exist_ok=True)
    with manifest.open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(records[0]))
        writer.writeheader()
        writer.writerows(records)
    report = {"dataset_id": "AVRDB", "doi": DATASET_DOI, "publisher_archive_sha256": PUBLISHER_SHA256,
              "images": len(records), "mask_pairs": len(records), "vector_recoveries": recoveries,
              "split_counts": {s: sum(r["split"] == s for r in records) for s in ("train", "validation", "test")},
              "unique_pixel_groups": len(groups),
              "duplicate_groups": [[r["case_id"] for r in records if r["group_id"] == group] for group in groups
                                   if sum(r["group_id"] == group for r in records) > 1],
              "patient_independence": "NOT_ESTABLISHED; publisher image IDs are not patient IDs",
              "expert_reported_avr_table": "NOT_PRESENT_IN_PUBLISHER_ARCHIVE; do not invent scalar references"}
    manifest.with_suffix(".json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--destination", type=Path, default=ROOT / "datasets/AVRDB")
    parser.add_argument("--manifest", type=Path, default=ROOT / "docs/datasets/avrdb/manifest.csv")
    args = parser.parse_args()
    print(json.dumps(prepare(args.archive, args.destination, args.manifest), indent=2))
