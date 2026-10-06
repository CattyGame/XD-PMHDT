"""
Common Enums and Type definitions for AURA AI Service (Module M4).
"""
from enum import Enum

class EyeSide(str, Enum):
    LEFT = "left"
    RIGHT = "right"
    UNKNOWN = "unknown"

class AnalysisMode(str, Enum):
    RETINA_VESSELS = "retina_vessels"
    IRIS_BIOMETRICS = "iris_biometrics"
    HYBRID = "hybrid"

class RiskLevel(str, Enum):
    LOW = "LOW"
    MODERATE = "MODERATE"
    HIGH = "HIGH"

class AnalysisStatus(str, Enum):
    SUCCESS = "SUCCESS"
    WARNING = "WARNING"
    FAILED = "FAILED"
