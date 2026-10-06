"""
HealthCheck Schemas for AURA AI Service.
"""
from typing import Optional

try:
    from pydantic import BaseModel, Field
except ImportError:
    # Lightweight fallback for environments before pip install
    from dataclasses import dataclass, field
    class BaseModel:
        def __init__(self, **kwargs):
            for k, v in kwargs.items():
                setattr(self, k, v)
        def dict(self):
            return self.__dict__
    def Field(*args, **kwargs):
        return kwargs.get("default", None)

class HealthResponse(BaseModel):
    status: str = "healthy"
    version: str = "1.0.0"
    service: str = "aura-ai-service"
    device: str = "cpu"
    model_loaded: bool = True
    uptime_seconds: float = 0.0
