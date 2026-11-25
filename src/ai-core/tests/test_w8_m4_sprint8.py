"""
Sprint 8 Verification Suite for Module M4 (W8-M4-01 / W8-M4-02 / W8-M4-03).

Covers:
1. TC-W8-M4-01 (SCRUM-423): Verifiable artifact registry, SHA-256 fail-closed checks,
   Git hygiene (no models/datasets/Iowa/credentials tracked), license-clean repro ZIP pack,
   and RuntimeManifestRegistry rollout/rollback alignment (NFR-16, NFR-21, NFR-23).
2. TC-W8-M4-02 (SCRUM-424): Separation of functional V2 handoff vs. scientific validation,
   exact cohort/hash/annotationSource pinning, honest RTM status, no synthetic expert labels,
   and V2 boundaries across UI/PDF renderers (FR-3, FR-4, NFR-15, NFR-22, NFR-23).
3. TC-W8-M4-03 (SCRUM-425): Live measured vs. unavailable AVR pair via measure_avr_prediction,
   3-minute speech transcript length and terminology alignment, strict AnalysisResponse 1.3.0
   validation, and negative controls rejecting clinical ICDR claims or avr=0 substitution.
"""

from __future__ import annotations

import copy
import hashlib
import json
import zipfile
from pathlib import Path

import pytest

from app.schemas.analysis import AnalysisResponse
from app.services.runtime_registry import RuntimeManifestRegistry
from scripts import manage_w8_artifact_registry as w8_reg
from scripts import verify_w8_defense_script as w8_def
from scripts import verify_w8_v2_acceptance as w8_acc

REPO_ROOT = Path(__file__).resolve().parents[3]
W8_REGISTRY_PATH = REPO_ROOT / "docs" / "benchmarks" / "w8_artifact_registry_v1.0.json"
W8_ACCEPTANCE_PATH = REPO_ROOT / "docs" / "benchmarks" / "w8_v2_acceptance_summary.json"
W8_DEFENSE_DOC_PATH = REPO_ROOT / "docs" / "ai" / "w8_m4_3min_defense_script_and_cases.md"


# ==============================================================================
# TC-W8-M4-01 (SCRUM-423): Artifact registry, SHA-256 fail-closed & clean repro ZIP
# ==============================================================================


def test_w8_m4_01_registry_hygiene_repro_pack_and_runtime_rollout(tmp_path: Path):
    # 1. Positive controls FIRST
    result = w8_reg.verify_w8_registry(W8_REGISTRY_PATH, root=REPO_ROOT, require_local_weights=False)
    assert result["ok"] is True, f"W8 artifact registry verification failed: {result['errors']}"
    assert result["gitHygieneClean"] is True
    assert w8_reg.audit_git_hygiene(REPO_ROOT) == []

    # Build a clean metadata/config reproducibility ZIP (zero Iowa / credentials / raw images)
    pack_zip = tmp_path / "aura_m4_repro_pack.zip"
    pack_summary = w8_reg.build_reproducibility_pack(pack_zip, registry_path=W8_REGISTRY_PATH, root=REPO_ROOT)
    assert pack_zip.is_file()
    assert pack_summary["iowaDatasetIncluded"] is False

    with zipfile.ZipFile(pack_zip, "r") as zf:
        names = zf.namelist()
        assert "PACK_MANIFEST.json" in names
        assert not any("iowa" in n.lower() for n in names)
        manifest = json.loads(zf.read("PACK_MANIFEST.json").decode("utf-8"))
        assert manifest["iowaDatasetIncluded"] is False
        assert manifest["credentialsIncluded"] is False
        for item in manifest["files"]:
            actual = hashlib.sha256(zf.read(item["path"])).hexdigest()
            assert actual == item["sha256Raw"], f"ZIP digest mismatch for {item['path']}"

    # Verify runtime manifest catalog IDs align with RuntimeManifestRegistry atomic switch + rollback
    reg_data = json.loads(W8_REGISTRY_PATH.read_text(encoding="utf-8"))
    runtime_registry = RuntimeManifestRegistry()
    active_v1 = runtime_registry.get_active_snapshot()
    reg_manifests = {m["artifactManifestId"]: m for m in reg_data["runtimeManifestCatalog"]["manifests"]}
    assert active_v1.artifact_manifest_id in reg_manifests
    target = reg_manifests["11111111-1111-4111-8111-111111110405"]
    activated_v2 = runtime_registry.activate_manifest(
        {
            "expectedVersion": active_v1.version,
            "artifactManifestId": target["artifactManifestId"],
            "thresholdConfigSha256": target["thresholdConfigSha256"],
            "reason": "W8-M4-01 positive control atomic rollout test",
        }
    )
    assert activated_v2.version == 2
    rolled_back_v3 = runtime_registry.rollback_manifest(
        expected_version=2,
        reason="W8-M4-01 positive control rollback verification",
    )
    assert rolled_back_v3.artifact_manifest_id == active_v1.artifact_manifest_id


def test_w8_m4_01_negative_controls_fail_closed(tmp_path: Path):
    # 1. Tampered file fails SHA-256 verification closed
    sample = tmp_path / "tampered_config.yaml"
    sample.write_text("threshold: 0.02\n", encoding="utf-8")
    good_sha = w8_reg.sha256_lf(sample)
    assert w8_reg.verify_artifact_file_sha256(sample, good_sha, canonical_lf=True) == good_sha
    sample.write_text("threshold: 0.09\n", encoding="utf-8")
    with pytest.raises(w8_reg.ArtifactVerificationError, match="SHA-256 mismatch"):
        w8_reg.verify_artifact_file_sha256(sample, good_sha, canonical_lf=True)

    # 2. require_local_weights=True against workspace lacking binary weights must fail closed
    fake_root = tmp_path / "clean_checkout"
    for rel in (
        "src/ai-core/config/artifact-manifests.json",
        "src/ai-core/config/ai-frangi.yaml",
        "src/ai-core/config/ai-frangi-v1.3.yaml",
        "src/ai-core/config/ai-frangi-hrf.yaml",
        "src/ai-core/config/dr_screening_weights.json",
        "src/ai-core/app/services/third_party/NOTICE",
        "src/ai-core/app/services/third_party/pvbm/LICENSE",
        "src/ai-core/app/services/third_party/rrwnet/LICENSE",
        "docs/datasets/chase_db1/manifest.csv",
        "docs/datasets/chase_db1/validation_report.json",
        "docs/datasets/hrf/manifest.csv",
        "docs/datasets/hrf/validation_report.json",
        "docs/datasets/inspire_avr/manifest.csv",
        "docs/datasets/inspire_avr/validation_report.json",
        "docs/benchmarks/model_card_v2_registry.json",
        "docs/benchmarks/v2_evaluation_report_s7.json",
    ):
        dst = fake_root / rel
        dst.parent.mkdir(parents=True, exist_ok=True)
        dst.write_bytes((REPO_ROOT / rel).read_bytes())

    strict_missing = w8_reg.verify_w8_registry(W8_REGISTRY_PATH, root=fake_root, require_local_weights=True)
    assert strict_missing["ok"] is False
    assert any("missing required local binary" in err for err in strict_missing["errors"])

    # 3. Attempting to include Iowa files or .pem keys in repro pack must abort without creating ZIP
    bad_extra = tmp_path / "extra_forbidden"
    bad_extra.mkdir()
    (bad_extra / "iowa_gt.txt").write_text("fake", encoding="utf-8")
    refused_zip = tmp_path / "refused.zip"
    with pytest.raises(w8_reg.ArtifactVerificationError, match="Iowa"):
        w8_reg.build_reproducibility_pack(refused_zip, W8_REGISTRY_PATH, root=REPO_ROOT, extra_source_dir=bad_extra)
    assert not refused_zip.exists()

    # 4. Mutating iowaRedistributedInZip=True or closing SCRUM-373 must fail registry check
    tampered_reg = json.loads(W8_REGISTRY_PATH.read_text(encoding="utf-8"))
    tampered_reg["iowaRedistributedInZip"] = True
    tampered_reg["scientificGateScrum373"]["status"] = "PASSED"
    tampered_path = tmp_path / "tampered_w8_registry.json"
    tampered_path.write_text(json.dumps(tampered_reg), encoding="utf-8")
    tampered_result = w8_reg.verify_w8_registry(tampered_path, root=REPO_ROOT, require_local_weights=False)
    assert tampered_result["ok"] is False
    assert any("Iowa" in err for err in tampered_result["errors"])
    assert any("SCRUM-373" in err for err in tampered_result["errors"])


# ==============================================================================
# TC-W8-M4-02 (SCRUM-424): V2 functional handoff vs. scientific validation
# ==============================================================================


def test_w8_m4_02_acceptance_summary_cross_checks_and_ui_pdf_markers():
    errors = w8_acc.verify_v2_acceptance(W8_ACCEPTANCE_PATH, root=REPO_ROOT)
    assert errors == [], f"W8 V2 acceptance verification errors: {errors}"

    summary = json.loads(W8_ACCEPTANCE_PATH.read_text(encoding="utf-8"))
    assert summary["functionalSoftwareHandoff"]["status"] == "VERIFIED_IN_SCOPE_V2"
    assert summary["functionalSoftwareHandoff"]["expertConfirmedGeneratedFromAuto"] is False
    assert summary["scientificValidation"]["clinicalValidation"] is False
    assert summary["scientificValidation"]["icdrGradingSupported"] is False
    assert summary["scientificValidation"]["scientificGateScrum373"]["status"] == "OPEN_NOT_PASSED"


def test_w8_m4_02_negative_controls_reject_forged_gate_or_synthetic_expert_labels(tmp_path: Path):
    base = json.loads(W8_ACCEPTANCE_PATH.read_text(encoding="utf-8"))

    # 1. Forging SCRUM-373 PASS + expertConfirmedGeneratedFromAuto=True + missing annotationSource
    forged = copy.deepcopy(base)
    forged["scientificValidation"]["scientificGateScrum373"]["status"] = "PASS"
    forged["functionalSoftwareHandoff"]["expertConfirmedGeneratedFromAuto"] = True
    forged["scientificValidation"]["cohorts"]["HRF45"]["annotationSource"] = ""
    forged["scientificValidation"]["cohorts"]["CHASE_DB1"]["metrics"]["test6MeanDice"] = 0.99
    forged["scientificValidation"]["cohorts"]["CHASE_DB1"]["referenceBenchmarkSha256Lf"] = "0" * 64
    p_forged = tmp_path / "forged_acceptance.json"
    p_forged.write_text(json.dumps(forged), encoding="utf-8")

    errors = w8_acc.verify_v2_acceptance(p_forged, root=REPO_ROOT)
    assert any("OPEN_NOT_PASSED" in err for err in errors)
    assert any("expertConfirmedGeneratedFromAuto" in err for err in errors)
    assert any("annotationSource" in err for err in errors)
    assert any("test6MeanDice" in err for err in errors)
    assert any("referenceBenchmark SHA mismatch" in err for err in errors)


# ==============================================================================
# TC-W8-M4-03 (SCRUM-425): Live measured vs. unavailable AVR pair & 3-min speech
# ==============================================================================


def test_w8_m4_03_defense_script_and_live_avr_measured_vs_unavailable_pair():
    # 1. Positive control FIRST: live measure_avr_prediction() measured vs. unavailable pair
    live = w8_def.run_live_avr_case_pair()
    measured = live["measured"]
    unavailable = live["unavailable"]

    assert measured["measurement_available"] is True
    assert measured["avr"] == pytest.approx(measured["crae_px"] / measured["crve_px"], abs=1e-6)
    assert measured["risk_available"] is False
    assert measured.get("unavailable_reason") is None

    assert unavailable["measurement_available"] is False
    assert unavailable["avr"] is None
    assert unavailable["crae_px"] is None
    assert unavailable["crve_px"] is None
    assert unavailable["unavailable_reason"] == "INSUFFICIENT_SIX_VESSEL_SETS"
    assert unavailable["risk_available"] is False

    # 2. Verify committed 3-minute defense script & both embedded AnalysisResponse 1.3.0 cases
    assert W8_DEFENSE_DOC_PATH.is_file()
    assert w8_def.verify_defense_doc(W8_DEFENSE_DOC_PATH, root=REPO_ROOT) == []


def test_w8_m4_03_negative_controls_reject_zero_avr_or_false_icdr_claims(tmp_path: Path):
    raw_doc = W8_DEFENSE_DOC_PATH.read_text(encoding="utf-8")

    # 1. Replacing avr: null with avr: 0 in CASE_UNAVAILABLE_JSON violates schema & verifier
    tampered_zero_avr = raw_doc.replace('"avr": null,', '"avr": 0.0,')
    p_zero = tmp_path / "tampered_zero_avr.md"
    p_zero.write_text(tampered_zero_avr, encoding="utf-8")
    zero_errors = w8_def.verify_defense_doc(p_zero, root=REPO_ROOT)
    assert zero_errors

    # 2. Schema directly refuses unavailable AVR with numeric avr
    unavail_payload = w8_def._extract_tagged_json(raw_doc, "CASE_UNAVAILABLE_JSON")
    bad_payload = copy.deepcopy(unavail_payload)
    bad_payload["research"]["avr"]["avr"] = 0.0
    bad_payload["metrics"]["av_ratio"] = 0.0
    with pytest.raises(ValueError):
        AnalysisResponse.model_validate(bad_payload)

    # 3. False clinical claim or dropping crop geometry fails verify_defense_doc
    tampered_claim = raw_doc.replace("IMAGENET_CENTER_CROP_224", "FULL_IMAGE_LESION_MASK") + "\nchẩn đoán ICDR 5 mức\n"
    p_claim = tmp_path / "tampered_claim.md"
    p_claim.write_text(tampered_claim, encoding="utf-8")
    claim_errors = w8_def.verify_defense_doc(p_claim, root=REPO_ROOT)
    assert any("imagenet_center_crop_224" in err.lower() for err in claim_errors)
    assert any("icdr" in err.lower() for err in claim_errors)
