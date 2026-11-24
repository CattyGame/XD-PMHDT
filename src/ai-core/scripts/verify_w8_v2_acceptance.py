"""Sprint 8 W8-M4-02 (SCRUM-424): Verify V2 functional acceptance vs. honest scientific report.

Cross-checks SHA-256 digests, separated cohort metrics, annotation sources, RTM status,
absence of synthetic expert_confirmed labels, and V2 disclaimers in UI and PDF renderers.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional

REPO_ROOT = Path(__file__).resolve().parents[3]
DEFAULT_SUMMARY_PATH = REPO_ROOT / "docs" / "benchmarks" / "w8_v2_acceptance_summary.json"

FORBIDDEN_ACCEPTANCE_PHRASES = (
    "scrum-373: pass",
    "scrum-373 closed",
    "bias gate passed",
    "expert_confirmed from auto",
    "icdr grading pass",
    "clinically certified",
)


def sha256_lf(path: Path) -> str:
    return hashlib.sha256(path.read_bytes().replace(b"\r\n", b"\n")).hexdigest()


def verify_v2_acceptance(
    summary_path: Path = DEFAULT_SUMMARY_PATH,
    root: Path = REPO_ROOT,
) -> List[str]:
    """Cross-verify Sprint 8 acceptance JSON, markdown reports, and UI/PDF V2 surfaces."""
    errors: List[str] = []
    if not summary_path.is_file():
        return [f"Missing W8 V2 acceptance summary: {summary_path}"]

    raw_text = summary_path.read_text(encoding="utf-8")
    lowered = raw_text.lower()
    for phrase in FORBIDDEN_ACCEPTANCE_PHRASES:
        if phrase in lowered:
            errors.append(f"Forbidden acceptance phrase found in JSON: {phrase!r}")

    data: Dict[str, Any] = json.loads(raw_text)
    if data.get("taskId") != "W8-M4-02" or data.get("jiraKey") != "SCRUM-424":
        errors.append("Summary must specify taskId=W8-M4-02 and jiraKey=SCRUM-424.")

    # Verify reference artifacts SHA-256 digests
    refs = data.get("referenceArtifacts", {})
    ref_pairs = (
        ("w8ArtifactRegistryPath", "w8ArtifactRegistrySha256Lf"),
        ("modelCardV2RegistryPath", "modelCardV2RegistrySha256Lf"),
        ("v2EvaluationReportS7Path", "v2EvaluationReportS7Sha256Lf"),
        ("latencyRamProfilePath", "latencyRamProfileSha256Lf"),
    )
    loaded_refs: Dict[str, Dict[str, Any]] = {}
    for p_key, h_key in ref_pairs:
        rel = refs.get(p_key, "")
        expected_sha = refs.get(h_key, "")
        abs_p = root / rel
        if not abs_p.is_file():
            errors.append(f"Missing reference artifact {p_key}: {rel}")
            continue
        actual_sha = sha256_lf(abs_p)
        if actual_sha != expected_sha:
            errors.append(f"Reference artifact SHA mismatch for {rel}: expected {expected_sha}, got {actual_sha}")
        loaded_refs[p_key] = json.loads(abs_p.read_text(encoding="utf-8"))

    # Functional vs scientific separation invariants
    func = data.get("functionalSoftwareHandoff", {})
    sci = data.get("scientificValidation", {})
    if func.get("status") != "VERIFIED_IN_SCOPE_V2":
        errors.append("functionalSoftwareHandoff.status must be VERIFIED_IN_SCOPE_V2.")
    if func.get("expertConfirmedGeneratedFromAuto") is not False or sci.get("expertConfirmedGeneratedFromAuto") is not False:
        errors.append("expertConfirmedGeneratedFromAuto must be explicitly false in both functional and scientific blocks.")
    if sci.get("clinicalValidation") is not False or sci.get("icdrGradingSupported") is not False:
        errors.append("scientificValidation must keep clinicalValidation=false and icdrGradingSupported=false.")
    if sci.get("autoCloseScrum373Allowed") is not False:
        errors.append("autoCloseScrum373Allowed must remain false.")

    gate = sci.get("scientificGateScrum373", {})
    if gate.get("status") != "OPEN_NOT_PASSED" or gate.get("thresholdAbsBias") != 0.02:
        errors.append("scientificGateScrum373 must remain OPEN_NOT_PASSED with thresholdAbsBias=0.02.")
    if gate.get("surrogate29CaseBias") != -0.2136 or gate.get("surrogate29CaseMae") != 0.2335:
        errors.append("Baseline INSPIRE-AVR 29-case bias (-0.2136) and MAE (0.2335) must remain pinned honestly.")

    # Cross-check cohorts against v2_evaluation_report_s7.json and model_card_v2_registry.json
    cohorts = sci.get("cohorts", {})
    if set(cohorts.keys()) != {"CHASE_DB1", "HRF45", "AVRDB100", "Iowa40"}:
        errors.append("scientificValidation.cohorts must separate CHASE_DB1, HRF45, AVRDB100, and Iowa40.")
    s7 = loaded_refs.get("v2EvaluationReportS7Path", {}).get("cohorts", {})
    w8_reg = loaded_refs.get("w8ArtifactRegistryPath", {}).get("datasets", {})
    for cid, cinfo in cohorts.items():
        if not cinfo.get("annotationSource"):
            errors.append(f"Cohort {cid} must declare explicit annotationSource.")
        if cinfo.get("expertConfirmedGeneratedFromAuto") is not False:
            errors.append(f"Cohort {cid} must keep expertConfirmedGeneratedFromAuto=false.")
        if cinfo.get("manifestPath"):
            m_abs = root / cinfo["manifestPath"]
            if not m_abs.is_file() or sha256_lf(m_abs) != cinfo.get("manifestSha256Lf"):
                errors.append(f"Cohort {cid} manifest SHA mismatch: {cinfo.get('manifestPath')}")
        if cinfo.get("referenceBenchmarkPath"):
            ref_path = root / cinfo["referenceBenchmarkPath"]
            ref_sha = str(cinfo.get("referenceBenchmarkSha256Lf") or "").strip().lower()
            if len(ref_sha) != 64 or not ref_path.is_file() or sha256_lf(ref_path) != ref_sha:
                errors.append(f"Cohort {cid} referenceBenchmark SHA mismatch: {cinfo.get('referenceBenchmarkPath')}")
        if cid in w8_reg and cinfo.get("manifestSha256Lf") != w8_reg[cid].get("manifestSha256Lf"):
            errors.append(f"Cohort {cid} manifest SHA diverged from w8_artifact_registry_v1.0.json.")
        if cid in s7:
            for k in ("denominator", "measured", "unavailable", "coverage"):
                s_key = "evaluationDenominator" if k == "denominator" else k
                if cinfo.get(s_key) != s7[cid].get(k):
                    errors.append(f"Cohort {cid} {s_key} mismatch with v2_evaluation_report_s7.json.")

    # Verify CHASE/HRF/AVR metrics match S7 report exactly
    if cohorts.get("CHASE_DB1", {}).get("metrics", {}).get("test6MeanDice") != 0.420052:
        errors.append("CHASE_DB1 test6MeanDice must remain pinned at 0.420052.")
    hrf_m = cohorts.get("HRF45", {}).get("metrics", {})
    if hrf_m.get("validationAuc") != 0.888889 or hrf_m.get("testAuc") != 1.0 or hrf_m.get("glaucomaOodExcludedFromAucCount") != 15:
        errors.append("HRF45 metrics or separated glaucoma_ood count drifted from S7 report.")
    if cohorts.get("Iowa40", {}).get("metrics", {}).get("status") != "NOT_TESTED_MISSING_EXTERNAL_DATASET":
        errors.append("Iowa40 status must stay NOT_TESTED_MISSING_EXTERNAL_DATASET.")

    # Verify RTM traceability honesty
    rtm_items = data.get("rtmTraceability", [])
    rtm_by_id = {item.get("id"): item for item in rtm_items if isinstance(item, dict)}
    expected_rtm = {"FR-3", "FR-4", "NFR-15", "NFR-16", "NFR-21", "NFR-22", "NFR-23"}
    if set(rtm_by_id.keys()) != expected_rtm:
        errors.append(f"Expected RTM entries {sorted(expected_rtm)}, got {sorted(rtm_by_id.keys())}.")
    for req_id, item in rtm_by_id.items():
        if item.get("status") != "PARTIAL":
            errors.append(f"RTM requirement {req_id} must preserve honest PARTIAL status without claiming clinical PASS.")

    # Verify V2 UI, client contract, and PDF renderers on disk still show V2 scope boundaries
    ui_rel = func.get("v2ScopeSurfaces", {}).get("webUi", {}).get("file", "")
    ui_path = root / ui_rel
    if not ui_path.is_file():
        errors.append(f"Missing Web UI file: {ui_rel}")
    else:
        ui_txt = ui_path.read_text(encoding="utf-8")
        for needle in ("224×224", "gradcam_geometry", "ICDR", "Chưa có kết quả AVR"):
            if needle not in ui_txt:
                errors.append(f"Web UI {ui_rel} missing V2 scope marker: {needle!r}")

    ui_api_path = root / "src/web/aura-web/src/api/analysisResult.ts"
    if not ui_api_path.is_file():
        errors.append("Missing Web UI API contract file: src/web/aura-web/src/api/analysisResult.ts")
    else:
        api_txt = ui_api_path.read_text(encoding="utf-8")
        for needle in ("IMAGENET_CENTER_CROP_224", "measurement_available", "unavailable_reason"):
            if needle not in api_txt:
                errors.append(f"Web UI contract missing V2 scope marker: {needle!r}")

    pdf_rel = func.get("v2ScopeSurfaces", {}).get("singleCasePdf", {}).get("file", "")
    pdf_path = root / pdf_rel
    if not pdf_path.is_file():
        errors.append(f"Missing PDF renderer file: {pdf_rel}")
    else:
        pdf_txt = pdf_path.read_text(encoding="utf-8")
        for needle in ("RESEARCH OUTPUTS - NOT A CLINICAL DIAGNOSIS", "224x224", "AVR unavailable", "ICDR grading"):
            if needle not in pdf_txt:
                errors.append(f"PDF renderer {pdf_rel} missing V2 scope marker: {needle!r}")

    # Verify markdown reports on disk preserve honest numbers and forbidden-claim guard
    md_checks = (
        ("docs/ai/w8_m4_v2_software_and_scientific_acceptance.md", ("0.420052", "0.888889", "-0.2136", "OPEN_NOT_PASSED", "NOT_TESTED_MISSING_EXTERNAL_DATASET")),
        ("docs/testing/w8_m4_rtm_and_acceptance_verification.md", ("FR-3", "FR-4", "NFR-15", "NFR-22", "NFR-23", "PARTIAL")),
        ("docs/data_cards/model_card_v1.3.md", ("w8_artifact_registry_v1.0.json", "OPEN_NOT_PASSED")),
        ("docs/data_cards/dataset_card.md", ("w8_artifact_registry_v1.0.json", "OPEN_NOT_PASSED")),
    )
    for rel, needles in md_checks:
        p = root / rel
        if not p.is_file():
            errors.append(f"Missing required Sprint 8 document: {rel}")
            continue
        txt = p.read_text(encoding="utf-8")
        low = txt.lower()
        for phrase in FORBIDDEN_ACCEPTANCE_PHRASES:
            if phrase in low:
                errors.append(f"Forbidden acceptance claim {phrase!r} found in {rel}")
        for needle in needles:
            if needle not in txt:
                errors.append(f"Expected fact {needle!r} missing from {rel}")

    return errors


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Verify Sprint 8 W8-M4-02 V2 acceptance and scientific report.")
    parser.add_argument("--verify", action="store_true", help="Run cross-checks across acceptance artifacts.")
    parser.add_argument("--summary", type=Path, default=DEFAULT_SUMMARY_PATH, help="Path to w8_v2_acceptance_summary.json")
    args = parser.parse_args(argv)

    errors = verify_v2_acceptance(summary_path=args.summary, root=REPO_ROOT)
    if errors:
        for err in errors:
            print(f"FAIL: {err}", file=sys.stderr)
        return 1
    print("PASS: W8-M4-02 V2 software handoff and honest scientific validation verified (SCRUM-373=OPEN_NOT_PASSED).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
