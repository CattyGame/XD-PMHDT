"""Service authentication package for AURA AI Core."""
from .service_jwt import (
    DEFAULT_ALLOWED_CLIENTS,
    DEFAULT_CONFIG_ALLOWED_CLIENTS,
    DEFAULT_CONFIG_SCOPE,
    ServiceAuthConfigError,
    ServiceAuthError,
    ServiceJwtClaims,
    ServiceJwtVerifier,
    load_config_verifier_from_env,
    load_verifier_from_env,
)

__all__ = [
    "DEFAULT_ALLOWED_CLIENTS",
    "DEFAULT_CONFIG_ALLOWED_CLIENTS",
    "DEFAULT_CONFIG_SCOPE",
    "ServiceAuthConfigError",
    "ServiceAuthError",
    "ServiceJwtClaims",
    "ServiceJwtVerifier",
    "load_config_verifier_from_env",
    "load_verifier_from_env",
]
