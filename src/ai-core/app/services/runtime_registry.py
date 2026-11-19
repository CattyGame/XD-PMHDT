"""Verified server-side AI artifact manifest registry with smoke verification and atomic rollout."""
from __future__ import annotations

import base64
import hashlib
import io
import json
import re
import threading
import uuid
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional

import numpy as np
from PIL import Image, ImageDraw

from ..schemas.analysis import AnalysisRequest
from .frangi_service import FrangiConfigError, FrangiRuntimeConfig, load_frangi_config, process_frangi_analysis

_SHA256_RE = re.compile(r"^[a-f0-9]{64}$")
_ALLOWED_CHANGE_KEYS = frozenset({"expectedVersion", "artifactManifestId", "thresholdConfigSha256", "reason"})


class RuntimeManifestError(RuntimeError):
    """Fail-closed domain error for AI manifest activation or rollback."""

    def __init__(self, status_code: int, error_code: str, message: str) -> None:
        super().__init__(message)
        self.status_code = status_code
        self.error_code = error_code
        self.message = message


@dataclass(frozen=True)
class ManifestEntry:
    """Verified server-side manifest descriptor bound to a repository YAML config."""

    artifact_manifest_id: str
    config_file: str
    threshold_config_sha256: str
    model_version: str
    config_version: str
    threshold_version: str
    risk_available: bool
    description: str


@dataclass(frozen=True)
class ActiveRuntimeSnapshot:
    """Immutable pinned configuration snapshot served atomically to requests."""

    version: int
    artifact_manifest_id: str
    threshold_config_sha256: str
    activated_at: str
    config: FrangiRuntimeConfig
    reason: str


def _utc_now_iso() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def _default_smoke_image_b64() -> str:
    image = Image.new("RGB", (128, 128), color=(0, 0, 0))
    draw = ImageDraw.Draw(image)
    draw.ellipse((14, 14, 114, 114), fill=(150, 95, 75))
    draw.line((32, 64, 96, 64), fill=(40, 22, 18), width=3)
    draw.line((64, 30, 64, 98), fill=(35, 20, 16), width=3)
    buf = io.BytesIO()
    image.save(buf, format="PNG")
    return base64.b64encode(buf.getvalue()).decode("ascii")


def default_smoke_runner(candidate: FrangiRuntimeConfig) -> None:
    """Execute one real end-to-end segmentation smoke call against the candidate config."""
    request = AnalysisRequest(
        request_id=str(uuid.uuid4()),
        patient_id=None,
        image_base64=_default_smoke_image_b64(),
    )
    response = process_frangi_analysis(request, config=candidate)
    if response.model_version != candidate.model_version or response.config_version != candidate.config_version:
        raise ValueError("Candidate smoke response drifted from manifest metadata.")
    if response.segmentation is None or not response.segmentation.mask_base64 or not response.segmentation.overlay_base64:
        raise ValueError("Candidate smoke response produced incomplete segmentation output.")


class RuntimeManifestRegistry:
    """Thread-safe atomic manifest registry supporting candidate validation, activation, and rollback."""

    def __init__(
        self,
        manifest_path: Optional[Path] = None,
        config_dir: Optional[Path] = None,
    ) -> None:
        base_config_dir = (config_dir or (Path(__file__).resolve().parents[2] / "config")).resolve()
        self._config_dir = base_config_dir
        self._manifest_path = (manifest_path or (base_config_dir / "artifact-manifests.json")).resolve()
        self._lock = threading.RLock()
        self._entries: Dict[str, ManifestEntry] = {}
        self._history: List[ActiveRuntimeSnapshot] = []
        self._current: ActiveRuntimeSnapshot = self._bootstrap_initial_snapshot()

    def _load_manifest_catalog(self) -> tuple[str, Dict[str, ManifestEntry]]:
        if not self._manifest_path.is_file():
            raise RuntimeManifestError(503, "SERVICE_NOT_READY", "Artifact manifest catalog is missing.")
        raw = json.loads(self._manifest_path.read_text(encoding="utf-8"))
        default_id = str(raw.get("defaultManifestId", "")).strip()
        entries: Dict[str, ManifestEntry] = {}
        for item in raw.get("manifests", []):
            manifest_id = str(item["artifactManifestId"]).strip()
            config_file = str(item["configFile"]).strip()
            sha = str(item["thresholdConfigSha256"]).strip().lower()
            if not _SHA256_RE.fullmatch(sha):
                raise RuntimeManifestError(500, "ERR_MANIFEST_INVALID", f"Invalid hash in manifest {manifest_id}.")
            entries[manifest_id] = ManifestEntry(
                artifact_manifest_id=manifest_id,
                config_file=config_file,
                threshold_config_sha256=sha,
                model_version=str(item["modelVersion"]),
                config_version=str(item["configVersion"]),
                threshold_version=str(item["thresholdVersion"]),
                risk_available=bool(item["riskAvailable"]),
                description=str(item.get("description", "")),
            )
        if not default_id or default_id not in entries:
            raise RuntimeManifestError(503, "SERVICE_NOT_READY", "Default artifact manifest is undefined.")
        return default_id, entries

    def _resolve_verified_config(self, entry: ManifestEntry, expected_sha256: str) -> FrangiRuntimeConfig:
        candidate_path = (self._config_dir / entry.config_file).resolve()
        if not candidate_path.is_relative_to(self._config_dir) or not candidate_path.is_file():
            raise RuntimeManifestError(503, "ERR_MANIFEST_CONFIG_MISSING", "Verified config file is unavailable on server.")
        actual_sha = hashlib.sha256(candidate_path.read_bytes().replace(b"\r\n", b"\n")).hexdigest()
        if actual_sha != entry.threshold_config_sha256 or expected_sha256.lower() != entry.threshold_config_sha256:
            raise RuntimeManifestError(
                409,
                "ERR_HASH_MISMATCH",
                "Config SHA-256 does not match the verified server manifest.",
            )
        try:
            config = load_frangi_config(candidate_path)
        except FrangiConfigError as exc:
            raise RuntimeManifestError(400, "ERR_INVALID_CONFIG", f"Invalid Frangi configuration schema: {exc}") from exc
        if (
            config.model_version != entry.model_version
            or config.config_version != entry.config_version
            or config.threshold_version != entry.threshold_version
        ):
            raise RuntimeManifestError(
                409,
                "ERR_MANIFEST_METADATA_MISMATCH",
                "Loaded config metadata does not match manifest metadata.",
            )
        return config

    def _bootstrap_initial_snapshot(self) -> ActiveRuntimeSnapshot:
        default_id, entries = self._load_manifest_catalog()
        self._entries = entries
        entry = entries[default_id]
        config = self._resolve_verified_config(entry, entry.threshold_config_sha256)
        return ActiveRuntimeSnapshot(
            version=1,
            artifact_manifest_id=entry.artifact_manifest_id,
            threshold_config_sha256=entry.threshold_config_sha256,
            activated_at=_utc_now_iso(),
            config=config,
            reason="Initial verified runtime baseline.",
        )

    def get_active_snapshot(self) -> ActiveRuntimeSnapshot:
        with self._lock:
            return self._current

    def list_manifests(self) -> List[ManifestEntry]:
        with self._lock:
            return list(self._entries.values())

    def activate_manifest(
        self,
        payload: Dict[str, Any],
        smoke_runner: Optional[Callable[[FrangiRuntimeConfig], None]] = None,
    ) -> ActiveRuntimeSnapshot:
        """Validate payload against server registry, run smoke verification, and switch atomically."""
        if not isinstance(payload, dict) or not _ALLOWED_CHANGE_KEYS.issuperset(payload.keys()):
            raise RuntimeManifestError(
                400,
                "ERR_VALIDATION",
                "Payload contains unsupported fields; arbitrary weight paths or URLs are forbidden.",
            )
        manifest_id = str(payload.get("artifactManifestId", "")).strip()
        sha256 = str(payload.get("thresholdConfigSha256", "")).strip().lower()
        reason = str(payload.get("reason", "")).strip()
        expected_version = payload.get("expectedVersion")
        try:
            uuid.UUID(manifest_id)
        except ValueError as exc:
            raise RuntimeManifestError(400, "ERR_VALIDATION", "artifactManifestId must be a valid UUID.") from exc
        if not _SHA256_RE.fullmatch(sha256):
            raise RuntimeManifestError(400, "ERR_VALIDATION", "thresholdConfigSha256 must be 64 lowercase hex chars.")
        if not isinstance(expected_version, int) or isinstance(expected_version, bool) or expected_version < 1:
            raise RuntimeManifestError(400, "ERR_VALIDATION", "expectedVersion must be a positive integer.")
        if not reason or len(reason) > 1000:
            raise RuntimeManifestError(400, "ERR_VALIDATION", "reason must be between 1 and 1000 characters.")

        with self._lock:
            entry = self._entries.get(manifest_id)
            if entry is None:
                raise RuntimeManifestError(404, "ERR_MANIFEST_NOT_FOUND", "Unknown artifact manifest ID.")
            if expected_version != self._current.version:
                raise RuntimeManifestError(
                    409,
                    "ERR_VERSION_CONFLICT",
                    f"Expected version {expected_version} does not match active version {self._current.version}.",
                )
            candidate = self._resolve_verified_config(entry, sha256)
            runner = smoke_runner or default_smoke_runner
            try:
                runner(candidate)
            except Exception as exc:
                raise RuntimeManifestError(
                    503,
                    "ERR_SMOKE_CHECK_FAILED",
                    f"Candidate manifest smoke verification failed; keeping active version {self._current.version}: {exc}",
                ) from exc

            next_snapshot = ActiveRuntimeSnapshot(
                version=self._current.version + 1,
                artifact_manifest_id=entry.artifact_manifest_id,
                threshold_config_sha256=entry.threshold_config_sha256,
                activated_at=_utc_now_iso(),
                config=candidate,
                reason=reason,
            )
            self._history.append(self._current)
            self._current = next_snapshot
            return next_snapshot

    def rollback_manifest(
        self,
        expected_version: int,
        reason: str = "Rollback to previous verified manifest.",
        smoke_runner: Optional[Callable[[FrangiRuntimeConfig], None]] = None,
    ) -> ActiveRuntimeSnapshot:
        """Atomically restore the immediately preceding verified manifest snapshot."""
        if not reason or not reason.strip():
            raise RuntimeManifestError(400, "ERR_VALIDATION", "Rollback reason is required.")
        with self._lock:
            if expected_version != self._current.version:
                raise RuntimeManifestError(
                    409,
                    "ERR_VERSION_CONFLICT",
                    f"Expected version {expected_version} does not match active version {self._current.version}.",
                )
            if not self._history:
                raise RuntimeManifestError(409, "ERR_NO_PREVIOUS_VERSION", "No previous manifest version to roll back to.")
            target = self._history.pop()
            entry = self._entries[target.artifact_manifest_id]
            candidate = self._resolve_verified_config(entry, target.threshold_config_sha256)
            runner = smoke_runner or default_smoke_runner
            try:
                runner(candidate)
            except Exception as exc:
                self._history.append(target)
                raise RuntimeManifestError(
                    503,
                    "ERR_SMOKE_CHECK_FAILED",
                    f"Rollback target smoke verification failed; keeping version {self._current.version}: {exc}",
                ) from exc

            restored = ActiveRuntimeSnapshot(
                version=self._current.version + 1,
                artifact_manifest_id=target.artifact_manifest_id,
                threshold_config_sha256=target.threshold_config_sha256,
                activated_at=_utc_now_iso(),
                config=candidate,
                reason=reason.strip(),
            )
            self._current = restored
            return restored


RUNTIME_REGISTRY = RuntimeManifestRegistry()
