import argparse
import csv
import hashlib
import io
import json
import random
from pathlib import Path

from PIL import Image


def inspect_file(path):
    content = path.read_bytes()

    with Image.open(io.BytesIO(content)) as image:
        image.verify()

    with Image.open(io.BytesIO(content)) as image:
        image.load()
        size = image.size
        mode = image.mode
        values = None

        if path.suffix.lower() == ".png":
            histogram = image.convert("L").histogram()
            values = [i for i, count in enumerate(histogram) if count]

    return {
        "size": size,
        "mode": mode,
        "values": values,
        "sha256": hashlib.sha256(content).hexdigest(),
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-dir", default="datasets/CHASE_DB1/raw")
    parser.add_argument("--output-dir", default="docs/datasets/chase_db1")
    args = parser.parse_args()

    data_dir = Path(args.data_dir)
    output_dir = Path(args.output_dir)

    subjects = [f"{number:02d}" for number in range(1, 15)]
    shuffled = subjects.copy()
    random.Random(42).shuffle(shuffled)

    groups = {
        "train": sorted(shuffled[:8]),
        "validation": sorted(shuffled[8:11]),
        "test": sorted(shuffled[11:]),
    }
    split_by_subject = {
        subject: split
        for split, members in groups.items()
        for subject in members
    }

    rows = []
    hashes = {}

    for subject in subjects:
        for eye in ("L", "R"):
            stem = f"Image_{subject}{eye}"
            names = {
                "image": f"{stem}.jpg",
                "mask_1st": f"{stem}_1stHO.png",
                "mask_2nd": f"{stem}_2ndHO.png",
            }

            row = {
                "subject_id": subject,
                "eye_side": eye,
                "split": split_by_subject[subject],
            }

            for kind, name in names.items():
                path = data_dir / name
                if not path.is_file():
                    raise ValueError(f"Missing file: {path}")

                result = inspect_file(path)

                if result["size"] != (999, 960):
                    raise ValueError(f"Unexpected dimensions: {path}")

                if kind == "image" and result["mode"] != "RGB":
                    raise ValueError(f"Expected RGB image: {path}")

                if kind != "image" and result["values"] != [0, 255]:
                    raise ValueError(f"Invalid or empty binary mask: {path}")

                digest = result["sha256"]
                if digest in hashes:
                    raise ValueError(
                        f"Duplicate file content: {path} and {hashes[digest]}"
                    )
                hashes[digest] = path.as_posix()

                row[kind] = path.as_posix()
                row[f"{kind}_sha256"] = digest

            rows.append(row)

    expected = {
        Path(row[kind]).name
        for row in rows
        for kind in ("image", "mask_1st", "mask_2nd")
    }
    actual = {path.name for path in data_dir.iterdir() if path.is_file()}
    extra_files = sorted(actual - expected)

    report = {
        "dataset": "CHASE_DB1",
        "seed": 42,
        "split_type": "project_defined_subject_split",
        "reference_mask": "1stHO",
        "image_count": len(rows),
        "mask_count": len(rows) * 2,
        "subject_count": len(subjects),
        "dimensions_width_height": [999, 960],
        "decoded_file_count": len(hashes),
        "split_subjects": groups,
        "split_image_counts": {
            split: sum(row["split"] == split for row in rows)
            for split in groups
        },
        "extra_files": extra_files,
        "checks_passed": True,
        "benchmark_performed": False,
    }

    output_dir.mkdir(parents=True, exist_ok=True)

    with (output_dir / "manifest.csv").open(
        "w", encoding="utf-8", newline=""
    ) as file:
        writer = csv.DictWriter(file, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)

    with (output_dir / "validation_report.json").open(
        "w", encoding="utf-8"
    ) as file:
        json.dump(report, file, ensure_ascii=False, indent=2)
        file.write("\n")

    print(json.dumps(report, ensure_ascii=False, indent=2))
    print(f"Saved outputs to: {output_dir}")


if __name__ == "__main__":
    main()