"""Verified server-side AI artifact manifest registry with smoke verification and atomic rollout."""
from __future__ import annotations

import base64
import hashlib
import io
import json
import os
import re
import tempfile
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
    """Execute real end-to-end segmentation (+ integrated artifact check when enabled) against candidate."""
    if os.getenv("AURA_INTEGRATED_RESEARCH_ENABLED", "false").strip().lower() == "true":
        from .integrated_inference import configured_artifacts

        configured_artifacts()
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


def _canonical_fingerprint(payload: Dict[str, Any]) -> str:
    canonical = json.dumps(payload, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()


class RuntimeManifestRegistry:
    """Thread-safe atomic manifest registry supporting persistence, candidate smoke, activation, and rollback."""

    _MAX_IDEMPOTENCY_KEYS = 64

    def __init__(
        self,
        manifest_path: Optional[Path] = None,
        config_dir: Optional[Path] = None,
        state_path: Optional[Path] = None,
    ) -> None:
        base_config_dir = (config_dir or (Path(__file__).resolve().parents[2] / "config")).resolve()
        self._config_dir = base_config_dir
        self._manifest_path = (manifest_path or (base_config_dir / "artifact-manifests.json")).resolve()
        raw_state = str(state_path) if state_path is not None else os.getenv("AURA_RUNTIME_STATE_PATH", "").strip()
        self._state_path: Optional[Path] = Path(raw_state).resolve() if raw_state else None
        self._lock = threading.RLock()
        self._entries: Dict[str, ManifestEntry] = {}
        self._default_manifest_id: str = ""
        self._history: List[ActiveRuntimeSnapshot] = []
        self._idempotency: Dict[str, tuple[str, ActiveRuntimeSnapshot]] = {}
        self._last_commit: Optional[tuple[int, str, ActiveRuntimeSnapshot]] = None
        self._has_persisted_state: bool = False
        self._admin_mutated: bool = False
        self._startup_error: Optional[RuntimeManifestError] = None
        self._current: Optional[ActiveRuntimeSnapshot] = None
        try:
            self._initialize_state()
        except RuntimeManifestError as exc:
            self._startup_error = exc
        except Exception as exc:  # pragma: no cover - defensive fail-closed guard
            self._startup_error = RuntimeManifestError(
                503,
                "ERR_RUNTIME_STATE_CORRUPTED",
                f"Failed to initialize runtime manifest state: {exc}",
            )

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

    def _select_bootstrap_entry(self) -> ManifestEntry:
        bootstrap_id = os.getenv("AURA_BOOTSTRAP_MANIFEST_ID", "").strip()
        if bootstrap_id:
            entry = self._entries.get(bootstrap_id)
            if entry is None:
                raise RuntimeManifestError(
                    503,
                    "SERVICE_NOT_READY",
                    f"Bootstrap manifest ID '{bootstrap_id}' is not in verified server manifest catalog.",
                )
            return entry

        env_config_path = os.getenv("AURA_FRANGI_CONFIG_PATH", "").strip()
        if env_config_path:
            resolved_env = Path(env_config_path).resolve()
            for entry in self._entries.values():
                candidate_path = (self._config_dir / entry.config_file).resolve()
                if candidate_path == resolved_env:
                    return entry
            for entry in self._entries.values():
                if Path(entry.config_file).name == Path(env_config_path).name:
                    return entry
            raise RuntimeManifestError(
                503,
                "SERVICE_NOT_READY",
                "Configured AURA_FRANGI_CONFIG_PATH is outside verified server manifest catalog.",
            )

        return self._entries[self._default_manifest_id]

    def _snapshot_to_record(self, snapshot: ActiveRuntimeSnapshot) -> Dict[str, Any]:
        return {
            "version": snapshot.version,
            "artifactManifestId": snapshot.artifact_manifest_id,
            "thresholdConfigSha256": snapshot.threshold_config_sha256,
            "activatedAt": snapshot.activated_at,
            "reason": snapshot.reason,
        }

    def _record_to_snapshot(self, record: Any) -> ActiveRuntimeSnapshot:
        if not isinstance(record, dict):
            raise RuntimeManifestError(503, "ERR_RUNTIME_STATE_CORRUPTED", "Runtime state snapshot must be a JSON object.")
        version = record.get("version")
        manifest_id = str(record.get("artifactManifestId", "")).strip()
        sha = str(record.get("thresholdConfigSha256", "")).strip().lower()
        activated_at = str(record.get("activatedAt", "")).strip()
        reason = str(record.get("reason", "")).strip() or "Restored verified manifest."
        if not isinstance(version, int) or isinstance(version, bool) or version < 1:
            raise RuntimeManifestError(503, "ERR_RUNTIME_STATE_CORRUPTED", "Persisted runtime version must be a positive integer.")
        if not _SHA256_RE.fullmatch(sha) or not activated_at:
            raise RuntimeManifestError(503, "ERR_RUNTIME_STATE_CORRUPTED", "Persisted runtime snapshot contains invalid hash or timestamp.")
        entry = self._entries.get(manifest_id)
        if entry is None:
            raise RuntimeManifestError(503, "ERR_RUNTIME_STATE_CORRUPTED", f"Persisted manifest '{manifest_id}' missing from catalog.")
        try:
            config = self._resolve_verified_config(entry, sha)
        except RuntimeManifestError as exc:
            raise RuntimeManifestError(503, "ERR_RUNTIME_STATE_CORRUPTED", f"Persisted manifest verification failed: {exc.message}") from exc
        return ActiveRuntimeSnapshot(
            version=version,
            artifact_manifest_id=entry.artifact_manifest_id,
            threshold_config_sha256=entry.threshold_config_sha256,
            activated_at=activated_at,
            config=config,
            reason=reason,
        )

    def _initialize_state(self) -> None:
        default_id, entries = self._load_manifest_catalog()
        self._default_manifest_id = default_id
        self._entries = entries

        if self._state_path is not None and self._state_path.exists():
            try:
                raw = json.loads(self._state_path.read_text(encoding="utf-8"))
            except Exception as exc:
                raise RuntimeManifestError(
                    503,
                    "ERR_RUNTIME_STATE_CORRUPTED",
                    f"Runtime state file is unreadable or invalid JSON: {exc}",
                ) from exc
            if not isinstance(raw, dict) or raw.get("schemaVersion") != "1.0":
                raise RuntimeManifestError(503, "ERR_RUNTIME_STATE_CORRUPTED", "Unsupported runtime state schema.")

            current = self._record_to_snapshot(raw.get("current"))
            raw_history = raw.get("history", [])
            if not isinstance(raw_history, list):
                raise RuntimeManifestError(503, "ERR_RUNTIME_STATE_CORRUPTED", "Runtime state history must be a list.")
            history: List[ActiveRuntimeSnapshot] = []
            prev_ver = 0
            for item in raw_history:
                snap = self._record_to_snapshot(item)
                if snap.version <= prev_ver or snap.version >= current.version:
                    raise RuntimeManifestError(503, "ERR_RUNTIME_STATE_CORRUPTED", "Runtime state history versions are out of order.")
                prev_ver = snap.version
                history.append(snap)

            idem: Dict[str, tuple[str, ActiveRuntimeSnapshot]] = {}
            raw_idem = raw.get("idempotency", {})
            if not isinstance(raw_idem, dict):
                raise RuntimeManifestError(503, "ERR_RUNTIME_STATE_CORRUPTED", "Runtime state idempotency index is invalid.")
            for key, val in raw_idem.items():
                if not isinstance(key, str) or not isinstance(val, dict):
                    raise RuntimeManifestError(503, "ERR_RUNTIME_STATE_CORRUPTED", "Runtime state idempotency entry is invalid.")
                fp = str(val.get("fingerprint", ""))
                if not _SHA256_RE.fullmatch(fp):
                    raise RuntimeManifestError(503, "ERR_RUNTIME_STATE_CORRUPTED", "Runtime state idempotency fingerprint is invalid.")
                idem[key] = (fp, self._record_to_snapshot(val.get("snapshot")))

            last_commit: Optional[tuple[int, str, ActiveRuntimeSnapshot]] = None
            raw_last_commit = raw.get("lastCommit")
            if raw_last_commit is not None:
                if not isinstance(raw_last_commit, dict):
                    raise RuntimeManifestError(503, "ERR_RUNTIME_STATE_CORRUPTED", "Runtime state lastCommit must be an object.")
                lc_ver = raw_last_commit.get("expectedVersion")
                lc_fp = str(raw_last_commit.get("fingerprint", ""))
                if not isinstance(lc_ver, int) or isinstance(lc_ver, bool) or lc_ver < 1 or not _SHA256_RE.fullmatch(lc_fp):
                    raise RuntimeManifestError(503, "ERR_RUNTIME_STATE_CORRUPTED", "Runtime state lastCommit metadata is invalid.")
                last_commit = (lc_ver, lc_fp, self._record_to_snapshot(raw_last_commit.get("snapshot")))

            self._history = history
            self._idempotency = idem
            self._last_commit = last_commit
            self._current = current
            self._has_persisted_state = True
            return

        self._current = self._bootstrap_initial_snapshot()

    def _persist_state_locked(self) -> None:
        if self._state_path is None:
            return
        self._state_path.parent.mkdir(parents=True, exist_ok=True)
        # Keep bounded FIFO of recent idempotency keys
        while len(self._idempotency) > self._MAX_IDEMPOTENCY_KEYS:
            oldest = next(iter(self._idempotency))
            del self._idempotency[oldest]
        payload = {
            "schemaVersion": "1.0",
            "current": self._snapshot_to_record(self._current),
            "history": [self._snapshot_to_record(item) for item in self._history],
            "idempotency": {
                key: {"fingerprint": fp, "snapshot": self._snapshot_to_record(snap)}
                for key, (fp, snap) in self._idempotency.items()
            },
            "lastCommit": (
                {
                    "expectedVersion": self._last_commit[0],
                    "fingerprint": self._last_commit[1],
                    "snapshot": self._snapshot_to_record(self._last_commit[2]),
                }
                if self._last_commit is not None
                else None
            ),
        }
        serialized = json.dumps(payload, indent=2, sort_keys=False) + "\n"
        tmp_path: Optional[Path] = None
        try:
            with tempfile.NamedTemporaryFile(
                mode="w",
                encoding="utf-8",
                dir=str(self._state_path.parent),
                prefix=".runtime-state-",
                suffix=".tmp",
                delete=False,
            ) as tmp_file:
                tmp_path = Path(tmp_file.name)
                tmp_file.write(serialized)
                tmp_file.flush()
                os.fsync(tmp_file.fileno())
            os.replace(str(tmp_path), str(self._state_path))
            self._has_persisted_state = True
        except Exception as exc:
            if tmp_path is not None and tmp_path.exists():
                try:
                    tmp_path.unlink()
                except OSError:
                    pass
            raise RuntimeManifestError(
                503,
                "ERR_RUNTIME_STATE_PERSISTENCE",
                f"Failed to persist runtime config state atomically: {exc}",
            ) from exc

    def _bootstrap_initial_snapshot(self) -> ActiveRuntimeSnapshot:
        entry = self._select_bootstrap_entry()
        config = self._resolve_verified_config(entry, entry.threshold_config_sha256)
        return ActiveRuntimeSnapshot(
            version=1,
            artifact_manifest_id=entry.artifact_manifest_id,
            threshold_config_sha256=entry.threshold_config_sha256,
            activated_at=_utc_now_iso(),
            config=config,
            reason="Initial verified runtime baseline.",
        )

    def _ensure_ready_locked(self) -> None:
        if self._startup_error is not None:
            raise self._startup_error
        if self._current is None:
            raise RuntimeManifestError(503, "SERVICE_NOT_READY", "Runtime manifest state is unavailable.")

    def get_active_snapshot(self) -> ActiveRuntimeSnapshot:
        with self._lock:
            self._ensure_ready_locked()
            # Honor bootstrap policy only before any state persistence or admin mutation occurs.
            if (
                not self._has_persisted_state
                and not self._admin_mutated
                and not self._history
                and self._current.version == 1
                and (os.getenv("AURA_BOOTSTRAP_MANIFEST_ID", "").strip() or os.getenv("AURA_FRANGI_CONFIG_PATH", "").strip())
            ):
                return self._bootstrap_initial_snapshot()
            return self._current

    def get_view(self) -> Dict[str, Any]:
        with self._lock:
            snap = self.get_active_snapshot()
            entry = self._entries[snap.artifact_manifest_id]
            return {
                "artifactManifestId": snap.artifact_manifest_id,
                "thresholdConfigSha256": snap.threshold_config_sha256,
                "version": snap.version,
                "activatedAt": snap.activated_at,
                "modelVersion": entry.model_version,
                "configVersion": entry.config_version,
                "thresholdVersion": entry.threshold_version,
                "riskAvailable": entry.risk_available,
                "canRollback": len(self._history) > 0,
                "historyDepth": len(self._history),
            }

    def list_manifests(self) -> List[ManifestEntry]:
        with self._lock:
            return list(self._entries.values())

    def activate_manifest(
        self,
        payload: Dict[str, Any],
        smoke_runner: Optional[Callable[[FrangiRuntimeConfig], None]] = None,
        idempotency_key: Optional[str] = None,
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

        clean_idem = idempotency_key.strip() if (idempotency_key and idempotency_key.strip()) else None
        req_fp = _canonical_fingerprint(
            {
                "action": "ACTIVATE_MANIFEST",
                "expectedVersion": expected_version,
                "artifactManifestId": manifest_id,
                "thresholdConfigSha256": sha256,
                "reason": reason,
            }
        )

        with self._lock:
            self._ensure_ready_locked()
            if clean_idem and clean_idem in self._idempotency:
                prev_fp, prev_snap = self._idempotency[clean_idem]
                if prev_fp != req_fp:
                    raise RuntimeManifestError(
                        409,
                        "ERR_IDEMPOTENCY_CONFLICT",
                        "Idempotency-Key was already used with a different runtime configuration payload.",
                    )
                return prev_snap

            current = self.get_active_snapshot()
            if (
                self._last_commit is not None
                and expected_version == self._last_commit[0]
                and req_fp == self._last_commit[1]
                and self._last_commit[2].version == current.version
            ):
                return self._last_commit[2]
            entry = self._entries.get(manifest_id)
            if entry is None:
                raise RuntimeManifestError(404, "ERR_MANIFEST_NOT_FOUND", "Unknown artifact manifest ID.")
            if expected_version != current.version:
                raise RuntimeManifestError(
                    409,
                    "ERR_VERSION_CONFLICT",
                    f"Expected version {expected_version} does not match active version {current.version}.",
                )
            candidate = self._resolve_verified_config(entry, sha256)
            runner = smoke_runner or default_smoke_runner
            try:
                runner(candidate)
            except Exception as exc:
                raise RuntimeManifestError(
                    503,
                    "ERR_SMOKE_CHECK_FAILED",
                    f"Candidate manifest smoke verification failed; keeping active version {current.version}: {exc}",
                ) from exc

            next_snapshot = ActiveRuntimeSnapshot(
                version=current.version + 1,
                artifact_manifest_id=entry.artifact_manifest_id,
                threshold_config_sha256=entry.threshold_config_sha256,
                activated_at=_utc_now_iso(),
                config=candidate,
                reason=reason,
            )
            prev_history = list(self._history)
            prev_current = self._current
            prev_idem = dict(self._idempotency)
            prev_last_commit = self._last_commit
            self._history.append(current)
            self._current = next_snapshot
            self._admin_mutated = True
            self._last_commit = (expected_version, req_fp, next_snapshot)
            if clean_idem:
                self._idempotency[clean_idem] = (req_fp, next_snapshot)
            try:
                self._persist_state_locked()
            except Exception:
                self._history = prev_history
                self._current = prev_current
                self._idempotency = prev_idem
                self._last_commit = prev_last_commit
                self._admin_mutated = prev_current is not None and (prev_current.version > 1 or len(prev_history) > 0)
                raise
            return next_snapshot

    def rollback_manifest(
        self,
        expected_version: int,
        reason: str = "Rollback to previous verified manifest.",
        smoke_runner: Optional[Callable[[FrangiRuntimeConfig], None]] = None,
        idempotency_key: Optional[str] = None,
    ) -> ActiveRuntimeSnapshot:
        """Atomically restore the immediately preceding verified manifest snapshot."""
        if not reason or not reason.strip() or len(reason.strip()) > 1000:
            raise RuntimeManifestError(400, "ERR_VALIDATION", "Rollback reason must be between 1 and 1000 characters.")
        clean_reason = reason.strip()
        if not isinstance(expected_version, int) or isinstance(expected_version, bool) or expected_version < 1:
            raise RuntimeManifestError(400, "ERR_VALIDATION", "expectedVersion must be a positive integer.")

        clean_idem = idempotency_key.strip() if (idempotency_key and idempotency_key.strip()) else None
        req_fp = _canonical_fingerprint(
            {
                "action": "ROLLBACK_MANIFEST",
                "expectedVersion": expected_version,
                "reason": clean_reason,
            }
        )

        with self._lock:
            self._ensure_ready_locked()
            if clean_idem and clean_idem in self._idempotency:
                prev_fp, prev_snap = self._idempotency[clean_idem]
                if prev_fp != req_fp:
                    raise RuntimeManifestError(
                        409,
                        "ERR_IDEMPOTENCY_CONFLICT",
                        "Idempotency-Key was already used with a different rollback payload.",
                    )
                return prev_snap

            current = self.get_active_snapshot()
            if (
                self._last_commit is not None
                and expected_version == self._last_commit[0]
                and req_fp == self._last_commit[1]
                and self._last_commit[2].version == current.version
            ):
                return self._last_commit[2]
            if expected_version != current.version:
                raise RuntimeManifestError(
                    409,
                    "ERR_VERSION_CONFLICT",
                    f"Expected version {expected_version} does not match active version {current.version}.",
                )
            if not self._history:
                raise RuntimeManifestError(409, "ERR_NO_PREVIOUS_VERSION", "No previous manifest version to roll back to.")
            target = self._history[-1]
            entry = self._entries[target.artifact_manifest_id]
            candidate = self._resolve_verified_config(entry, target.threshold_config_sha256)
            runner = smoke_runner or default_smoke_runner
            try:
                runner(candidate)
            except Exception as exc:
                raise RuntimeManifestError(
                    503,
                    "ERR_SMOKE_CHECK_FAILED",
                    f"Rollback target smoke verification failed; keeping version {current.version}: {exc}",
                ) from exc

            restored = ActiveRuntimeSnapshot(
                version=current.version + 1,
                artifact_manifest_id=target.artifact_manifest_id,
                threshold_config_sha256=target.threshold_config_sha256,
                activated_at=_utc_now_iso(),
                config=candidate,
                reason=clean_reason,
            )
            prev_history = list(self._history)
            prev_current = self._current
            prev_idem = dict(self._idempotency)
            prev_last_commit = self._last_commit
            self._history.pop()
            self._current = restored
            self._admin_mutated = True
            self._last_commit = (expected_version, req_fp, restored)
            if clean_idem:
                self._idempotency[clean_idem] = (req_fp, restored)
            try:
                self._persist_state_locked()
            except Exception:
                self._history = prev_history
                self._current = prev_current
                self._idempotency = prev_idem
                self._last_commit = prev_last_commit
                self._admin_mutated = prev_current is not None and (prev_current.version > 1 or len(prev_history) > 0)
                raise
            return restored


RUNTIME_REGISTRY = RuntimeManifestRegistry()
