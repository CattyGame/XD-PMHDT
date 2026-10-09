"""
Sync OpenAPI Contracts Script for AURA AI Service (Module M4 - SCRUM-61).
Generates and synchronizes `contracts/ai_service_openapi.json` directly from
canonical `contracts/ai_service_openapi.yaml` so that YAML and JSON are always
100% derived from the single source of truth.

Usage:
    python scripts/sync_openapi_contracts.py          # Regenerates JSON from YAML
    python scripts/sync_openapi_contracts.py --check  # Verifies sync status
"""
import argparse
import json
import os
import sys
import yaml


def get_repo_paths():
    script_dir = os.path.dirname(os.path.abspath(__file__))
    # script is in src/ai-core/scripts
    repo_root = os.path.dirname(os.path.dirname(os.path.dirname(script_dir)))
    yaml_path = os.path.join(repo_root, "contracts", "ai_service_openapi.yaml")
    json_path = os.path.join(repo_root, "contracts", "ai_service_openapi.json")
    return repo_root, yaml_path, json_path


def load_yaml_contract(yaml_path: str) -> dict:
    if not os.path.exists(yaml_path):
        raise FileNotFoundError(f"OpenAPI YAML contract not found at {yaml_path}")
    with open(yaml_path, "r", encoding="utf-8") as f:
        data = yaml.safe_load(f)
    return data


def validate_contract_structure(data: dict):
    assert data.get("openapi", "").startswith("3.0"), "OpenAPI version must be 3.0.x"
    assert "info" in data, "Contract must contain 'info'"
    assert "paths" in data, "Contract must contain 'paths'"
    assert "components" in data and "schemas" in data["components"], "Contract must contain component schemas"

    required_paths = ["/health", "/api/v1/platform/ping", "/api/v1/analyze"]
    for path in required_paths:
        assert path in data["paths"], f"Missing required path {path} in OpenAPI contract"

    analyze_responses = data["paths"]["/api/v1/analyze"]["post"]["responses"]
    for code in ["200", "400", "422", "500"]:
        assert code in analyze_responses, f"Missing HTTP {code} in /api/v1/analyze responses"

    required_schemas = [
        "HealthResponse",
        "PingResponse",
        "EyeSide",
        "AnalysisMode",
        "RiskLevel",
        "AnalysisRequest",
        "ImageInfo",
        "VesselMetrics",
        "RiskAssessment",
        "SegmentationResult",
        "AnalysisResponse",
        "ErrorResponse",
    ]
    for s in required_schemas:
        assert s in data["components"]["schemas"], f"Missing required schema {s} in OpenAPI contract"


def sync_contracts(check_mode: bool = False) -> bool:
    repo_root, yaml_path, json_path = get_repo_paths()
    data = load_yaml_contract(yaml_path)
    validate_contract_structure(data)

    new_json_str = json.dumps(data, indent=2, ensure_ascii=False) + "\n"

    if check_mode:
        if not os.path.exists(json_path):
            print(f"FAIL: {json_path} does not exist.")
            return False
        with open(json_path, "r", encoding="utf-8") as f:
            existing_json_str = f.read()
        if existing_json_str == new_json_str:
            print("OK: JSON and YAML contracts are 100% in sync.")
            return True
        else:
            print("MISMATCH: JSON contract differs from YAML contract source.")
            return False
    else:
        with open(json_path, "w", encoding="utf-8") as f:
            f.write(new_json_str)
        print(f"SUCCESS: Successfully synchronized {json_path} from {yaml_path}")
        return True


if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description="Sync OpenAPI YAML and JSON contracts")
    parser.add_argument("--check", action="store_true", help="Check if JSON is synchronized without writing")
    args = parser.parse_args()

    success = sync_contracts(check_mode=args.check)
    sys.exit(0 if success else 1)
