from datetime import datetime, timezone

from fastapi import FastAPI
from pydantic import BaseModel


app = FastAPI(
    title="AURA AI Core",
    version="0.1.0",
)


class HealthResponse(BaseModel):
    status: str
    service: str
    model_loaded: bool


class PingResponse(BaseModel):
    service: str
    message: str
    timestamp: datetime


@app.get("/health", response_model=HealthResponse)
def health():
    return HealthResponse(
        status="ok",
        service="AURA.AiCore",
        model_loaded=False,
    )


@app.get("/api/v1/platform/ping", response_model=PingResponse)
def ping():
    return PingResponse(
        service="AURA.AiCore",
        message="AI Core template is running",
        timestamp=datetime.now(timezone.utc),
    )