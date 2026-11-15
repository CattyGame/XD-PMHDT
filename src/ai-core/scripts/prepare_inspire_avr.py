"""Legacy mask-name validation helper; use prepare_avrdb.py for publisher AVRDB100.

The old INSPIRE_AVR directory name was incorrect. Case IDs are not patient IDs.
The nonrecursive 99-case inventory and its split are retained only as historical evidence.
"""
from __future__ import annotations
import hashlib
import shutil
from pathlib import Path
from typing import List, Optional

def sha256_file(p: Path) -> str:
    return hashlib.sha256(p.read_bytes()).hexdigest()


def resolve_and_normalize_mask(cdir: Path, case_id: str, patterns: List[str], canonical_suffix: str) -> Optional[Path]:
    canonical_path = cdir / f"{case_id}{canonical_suffix}"
    candidates = {p for pattern in patterns for p in cdir.glob(pattern)}
    if any(not p.name.startswith(f"{case_id}--") for p in candidates):
        return None
    if len({sha256_file(p) for p in candidates}) > 1:
        raise ValueError(f"Conflicting reference masks: {case_id}")
    if canonical_path.is_file():
        return canonical_path

    for pat in patterns:
        matches = [p for p in cdir.glob(pat) if p.name.startswith(f"{case_id}--")]
        if len(matches) > 1:
            raise ValueError(f"Ambiguous reference masks: {case_id} {pat}")
        if matches:
            src = matches[0]
            shutil.copyfile(src, canonical_path)
            return canonical_path
    return None


if __name__ == "__main__":
    raise SystemExit("Superseded: use prepare_avrdb.py --archive <publisher-AVRDB-v2.zip>; see docs/datasets/avrdb/README.md")
