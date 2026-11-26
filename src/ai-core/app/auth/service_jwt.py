"""
RS256 Service JWT verifier for AURA AI Core (Sprint 3 Contract 1.2.0).

Implements fail-closed verification of service tokens issued by Identity API:
- Algorithm: RS256 (PKCS#1 v1.5 with SHA-256)
- Public key: SPKI PEM (>= 2048-bit RSA; rejects private keys and ambiguous sources)
- Key ID: kid header matched against SHA-256 of SPKI DER ('aura-local-<16hex>')
- Claims: iss ('aura-identity-s3-v1'), aud ('aura-ai'), scope ('ai:analyze'),
  sub (non-zero UUID), client_id ('analysis-worker'), exp, nbf
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import json
import math
import os
import re
import time
import uuid
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, FrozenSet, Optional, Tuple


MAX_TOKEN_LENGTH = 16 * 1024
MIN_RSA_BITS = 2048
DEFAULT_ISSUER = "aura-identity-s3-v1"
DEFAULT_AUDIENCE = "aura-ai"
DEFAULT_REQUIRED_SCOPE = "ai:analyze"
DEFAULT_CONFIG_SCOPE = "ai:config"
DEFAULT_MAX_LIFETIME_SECONDS = 300
DEFAULT_CLOCK_SKEW_SECONDS = 30
DEFAULT_PUBLIC_KEY_PATH = "/run/aura-service-auth/public/signing-public.pem"
DEFAULT_ALLOWED_CLIENTS = frozenset({"analysis-worker"})
DEFAULT_CONFIG_ALLOWED_CLIENTS = frozenset({"analysis-api"})

SCOPE_PATTERN = re.compile(r"^[a-z][a-z0-9-]*:[a-z][a-z0-9-]*$")
CLIENT_ID_PATTERN = re.compile(r"^[a-z][a-z0-9-]{0,63}$")

# DER DigestInfo prefix for SHA-256 in EMSA-PKCS1-v1_5 (RFC 8017 Section 9.2)
SHA256_DIGEST_INFO_PREFIX = bytes.fromhex("3031300d060960864801650304020105000420")
# OID 1.2.840.113549.1.1.1 (rsaEncryption)
RSA_ENCRYPTION_OID_DER = bytes.fromhex("06092a864886f70d010101")


class ServiceAuthConfigError(ValueError):
    """Raised when service authentication configuration or public key material is invalid."""


class ServiceAuthError(Exception):
    """Raised when a bearer token fails authentication (401) or authorization (403)."""

    def __init__(
        self,
        status_code: int,
        error_code: str,
        message: str,
        details: Optional[Dict[str, Any]] = None,
    ) -> None:
        super().__init__(message)
        self.status_code = status_code
        self.error_code = error_code
        self.message = message
        self.details = details


@dataclass(frozen=True)
class ServiceJwtClaims:
    """Verified service JWT identity claims."""
    issuer: str
    audience: str
    subject: str
    client_id: str
    scopes: FrozenSet[str]
    key_id: str
    expires_at: int
    not_before: Optional[int]


def _read_asn1_element(data: bytes, offset: int) -> Tuple[int, bytes, int]:
    """Read a single DER TLV element and return (tag, value_bytes, next_offset)."""
    if offset >= len(data):
        raise ServiceAuthConfigError("Unexpected end of DER data.")
    tag = data[offset]
    offset += 1
    if offset >= len(data):
        raise ServiceAuthConfigError("Truncated DER length.")
    first_len = data[offset]
    offset += 1
    if first_len < 0x80:
        length = first_len
    else:
        num_bytes = first_len & 0x7F
        if num_bytes == 0 or num_bytes > 4 or offset + num_bytes > len(data):
            raise ServiceAuthConfigError("Invalid DER length encoding.")
        length = int.from_bytes(data[offset : offset + num_bytes], "big")
        if length < 0x80:
            raise ServiceAuthConfigError("Non-minimal DER length encoding.")
        offset += num_bytes
    end = offset + length
    if end > len(data):
        raise ServiceAuthConfigError("DER element exceeds buffer.")
    return tag, data[offset:end], end


def _parse_asn1_integer(value_bytes: bytes) -> int:
    if not value_bytes or (value_bytes[0] & 0x80):
        raise ServiceAuthConfigError("Invalid or negative ASN.1 INTEGER.")
    if len(value_bytes) > 1 and value_bytes[0] == 0x00 and not (value_bytes[1] & 0x80):
        raise ServiceAuthConfigError("Non-minimal ASN.1 INTEGER padding.")
    return int.from_bytes(value_bytes, "big")


def parse_spki_rsa_public_key(pem_text: str) -> Tuple[int, int, str, bytes]:
    """
    Parse an X.509 SubjectPublicKeyInfo PEM into (modulus_n, exponent_e, key_id, spki_der).
    Rejects private keys, non-SPKI PEMs, and RSA keys weaker than 2048 bits.
    """
    if not pem_text or not isinstance(pem_text, str):
        raise ServiceAuthConfigError("Public key PEM is empty.")

    stripped = pem_text.strip()
    if "PRIVATE KEY" in stripped:
        raise ServiceAuthConfigError("Private key material is not accepted as a public verification key.")

    begin_marker = "-----BEGIN PUBLIC KEY-----"
    end_marker = "-----END PUBLIC KEY-----"
    if not stripped.startswith(begin_marker) or not stripped.endswith(end_marker):
        raise ServiceAuthConfigError("Public key must be in SPKI PEM format.")

    body = stripped[len(begin_marker) : -len(end_marker)]
    b64_Joined = "".join(line.strip() for line in body.splitlines() if line.strip())
    if not b64_Joined:
        raise ServiceAuthConfigError("Empty SPKI PEM payload.")

    try:
        spki_der = base64.b64decode(b64_Joined, validate=True)
    except Exception as exc:
        raise ServiceAuthConfigError("Malformed Base64 in SPKI PEM.") from exc

    tag, seq_body, next_off = _read_asn1_element(spki_der, 0)
    if tag != 0x30 or next_off != len(spki_der):
        raise ServiceAuthConfigError("Invalid SPKI outer SEQUENCE.")

    alg_tag, alg_body, bit_offset = _read_asn1_element(seq_body, 0)
    if alg_tag != 0x30 or not alg_body.startswith(RSA_ENCRYPTION_OID_DER):
        raise ServiceAuthConfigError("SPKI algorithm is not rsaEncryption.")

    bit_tag, bit_body, end_off = _read_asn1_element(seq_body, bit_offset)
    if bit_tag != 0x03 or end_off != len(seq_body) or len(bit_body) < 2 or bit_body[0] != 0x00:
        raise ServiceAuthConfigError("Invalid SPKI BIT STRING.")

    rsa_der = bit_body[1:]
    r_tag, r_body, r_end = _read_asn1_element(rsa_der, 0)
    if r_tag != 0x30 or r_end != len(rsa_der):
        raise ServiceAuthConfigError("Invalid RSAPublicKey SEQUENCE.")

    n_tag, n_bytes, e_offset = _read_asn1_element(r_body, 0)
    e_tag, e_bytes, r_inner_end = _read_asn1_element(r_body, e_offset)
    if n_tag != 0x02 or e_tag != 0x02 or r_inner_end != len(r_body):
        raise ServiceAuthConfigError("Invalid RSAPublicKey INTEGER fields.")

    modulus_n = _parse_asn1_integer(n_bytes)
    exponent_e = _parse_asn1_integer(e_bytes)

    if modulus_n.bit_length() < MIN_RSA_BITS:
        raise ServiceAuthConfigError(f"RSA public key is weaker than {MIN_RSA_BITS} bits.")
    if exponent_e < 3 or exponent_e % 2 == 0:
        raise ServiceAuthConfigError("Invalid RSA public exponent.")

    key_id = "aura-local-" + hashlib.sha256(spki_der).hexdigest()[:16].lower()
    return modulus_n, exponent_e, key_id, spki_der


def _b64url_decode(segment: str) -> bytes:
    if not segment or any(ch in segment for ch in "+/=\r\n\t "):
        raise ValueError("Invalid base64url characters.")
    rem = len(segment) % 4
    if rem == 1:
        raise ValueError("Invalid base64url length.")
    padded = segment + ("=" * ((4 - rem) % 4))
    return base64.urlsafe_b64decode(padded.encode("ascii"))


def _verify_rs256_pkcs1_v15(
    modulus_n: int,
    exponent_e: int,
    signing_input: bytes,
    signature: bytes,
) -> bool:
    key_bytes = (modulus_n.bit_length() + 7) // 8
    if len(signature) != key_bytes:
        return False
    sig_int = int.from_bytes(signature, "big")
    if sig_int <= 0 or sig_int >= modulus_n:
        return False
    em_int = pow(sig_int, exponent_e, modulus_n)
    em = em_int.to_bytes(key_bytes, "big")

    digest = hashlib.sha256(signing_input).digest()
    t_bytes = SHA256_DIGEST_INFO_PREFIX + digest
    if key_bytes < len(t_bytes) + 11:
        return False
    ps_len = key_bytes - len(t_bytes) - 3
    expected_em = b"\x00\x01" + (b"\xff" * ps_len) + b"\x00" + t_bytes
    return hmac.compare_digest(em, expected_em)


class ServiceJwtVerifier:
    """Stateless RS256 service JWT verifier matching AURA Sprint 3 service-auth rules."""

    def __init__(
        self,
        *,
        issuer: str = DEFAULT_ISSUER,
        audience: str = DEFAULT_AUDIENCE,
        required_scope: str = DEFAULT_REQUIRED_SCOPE,
        public_key_pem: Optional[str] = None,
        public_key_path: Optional[str] = None,
        allowed_clients: FrozenSet[str] = DEFAULT_ALLOWED_CLIENTS,
        clock_skew_seconds: int = DEFAULT_CLOCK_SKEW_SECONDS,
        max_lifetime_seconds: int = DEFAULT_MAX_LIFETIME_SECONDS,
    ) -> None:
        if not issuer or not isinstance(issuer, str) or not issuer.strip():
            raise ServiceAuthConfigError("ServiceAuth issuer must be configured.")
        if not audience or not isinstance(audience, str) or not audience.strip():
            raise ServiceAuthConfigError("ServiceAuth audience must be configured.")
        if not required_scope or not SCOPE_PATTERN.fullmatch(required_scope):
            raise ServiceAuthConfigError("ServiceAuth required_scope must be a two-part scope.")
        if public_key_pem is not None and public_key_path is not None:
            raise ServiceAuthConfigError(
                "Ambiguous public key sources: specify either PublicKeyPath or PublicKeyPem, not both."
            )
        if public_key_pem is None and public_key_path is None:
            raise ServiceAuthConfigError("Missing public key source for ServiceAuth.")

        if public_key_path is not None:
            path_obj = Path(public_key_path)
            if not path_obj.is_file():
                raise ServiceAuthConfigError("Configured ServiceAuth public key file does not exist.")
            raw_bytes = path_obj.read_bytes()
            if not raw_bytes or len(raw_bytes) > 64 * 1024:
                raise ServiceAuthConfigError("Invalid public key file size.")
            try:
                pem_content = raw_bytes.decode("utf-8")
            except UnicodeDecodeError as exc:
                raise ServiceAuthConfigError("Public key file is not valid UTF-8.") from exc
        else:
            pem_content = public_key_pem or ""

        self.issuer = issuer.strip()
        self.audience = audience.strip()
        self.required_scope = required_scope
        self.allowed_clients = allowed_clients
        self.clock_skew_seconds = clock_skew_seconds
        self.max_lifetime_seconds = max_lifetime_seconds
        self._modulus_n, self._exponent_e, self.key_id, _ = parse_spki_rsa_public_key(pem_content)

    def verify_authorization_header(
        self,
        authorization: Optional[str],
        *,
        now: Optional[float] = None,
    ) -> ServiceJwtClaims:
        if not authorization or not isinstance(authorization, str):
            raise ServiceAuthError(
                401,
                "UNAUTHORIZED_SERVICE_TOKEN",
                "Thiếu header Authorization Bearer cho xác thực dịch vụ.",
            )
        parts = authorization.strip().split(" ")
        if len(parts) != 2 or parts[0].lower() != "bearer" or not parts[1]:
            raise ServiceAuthError(
                401,
                "UNAUTHORIZED_SERVICE_TOKEN",
                "Định dạng Authorization header không hợp lệ; yêu cầu 'Bearer <token>'.",
            )
        return self.verify_token(parts[1], now=now)

    def verify_token(
        self,
        token: str,
        *,
        now: Optional[float] = None,
    ) -> ServiceJwtClaims:
        if not token or not isinstance(token, str) or len(token) > MAX_TOKEN_LENGTH:
            raise ServiceAuthError(
                401,
                "UNAUTHORIZED_SERVICE_TOKEN",
                "Token dịch vụ rỗng hoặc vượt kích thước cho phép.",
            )

        segments = token.split(".")
        if len(segments) != 3 or not all(segments):
            raise ServiceAuthError(
                401,
                "UNAUTHORIZED_SERVICE_TOKEN",
                "Cấu trúc JWT không hợp lệ.",
            )

        header_b64, payload_b64, sig_b64 = segments
        try:
            header_bytes = _b64url_decode(header_b64)
            payload_bytes = _b64url_decode(payload_b64)
            sig_bytes = _b64url_decode(sig_b64)
            header = json.loads(header_bytes.decode("utf-8"))
            payload = json.loads(payload_bytes.decode("utf-8"))
        except Exception as exc:
            raise ServiceAuthError(
                401,
                "UNAUTHORIZED_SERVICE_TOKEN",
                "Không thể giải mã JWT header/payload/signature.",
            ) from exc

        if not isinstance(header, dict) or not isinstance(payload, dict):
            raise ServiceAuthError(
                401,
                "UNAUTHORIZED_SERVICE_TOKEN",
                "JWT header hoặc payload không phải JSON object.",
            )

        if header.get("alg") != "RS256":
            raise ServiceAuthError(
                401,
                "UNAUTHORIZED_SERVICE_TOKEN",
                "Thuật toán ký JWT không được phép; chỉ chấp nhận RS256.",
            )

        kid = header.get("kid")
        if not isinstance(kid, str) or not kid or kid != self.key_id:
            raise ServiceAuthError(
                401,
                "UNAUTHORIZED_SERVICE_TOKEN",
                "Mã khóa ký (kid) thiếu hoặc không khớp khóa xác thực hiện hành.",
            )

        signing_input = f"{header_b64}.{payload_b64}".encode("ascii")
        if not _verify_rs256_pkcs1_v15(self._modulus_n, self._exponent_e, signing_input, sig_bytes):
            raise ServiceAuthError(
                401,
                "UNAUTHORIZED_SERVICE_TOKEN",
                "Chữ ký số RS256 của JWT không hợp lệ.",
            )

        # Validate issuer
        iss = payload.get("iss")
        if not isinstance(iss, str) or iss != self.issuer:
            raise ServiceAuthError(
                401,
                "UNAUTHORIZED_SERVICE_TOKEN",
                "Issuer của token không khớp cấu hình dịch vụ.",
            )

        # Validate timestamps (exp required, nbf optional)
        current_ts = time.time() if now is None else float(now)
        exp = payload.get("exp")
        if isinstance(exp, bool) or not isinstance(exp, (int, float)) or not math.isfinite(exp):
            raise ServiceAuthError(
                401,
                "UNAUTHORIZED_SERVICE_TOKEN",
                "Token thiếu trường thời hạn (exp) hợp lệ.",
            )
        if current_ts - self.clock_skew_seconds >= float(exp):
            raise ServiceAuthError(
                401,
                "UNAUTHORIZED_SERVICE_TOKEN",
                "Token dịch vụ đã hết hạn.",
            )

        iat = payload.get("iat")
        if iat is not None:
            if isinstance(iat, bool) or not isinstance(iat, (int, float)) or not math.isfinite(iat):
                raise ServiceAuthError(
                    401,
                    "UNAUTHORIZED_SERVICE_TOKEN",
                    "Trường iat trong token không hợp lệ.",
                )
            if float(exp) <= float(iat) or (float(exp) - float(iat)) > (self.max_lifetime_seconds + self.clock_skew_seconds):
                raise ServiceAuthError(
                    401,
                    "UNAUTHORIZED_SERVICE_TOKEN",
                    "Thời hạn sống của token vượt quá giới hạn cho phép.",
                )

        nbf = payload.get("nbf")
        nbf_int: Optional[int] = None
        if nbf is not None:
            if isinstance(nbf, bool) or not isinstance(nbf, (int, float)) or not math.isfinite(nbf):
                raise ServiceAuthError(
                    401,
                    "UNAUTHORIZED_SERVICE_TOKEN",
                    "Trường nbf trong token không hợp lệ.",
                )
            if float(nbf) > current_ts + self.clock_skew_seconds:
                raise ServiceAuthError(
                    401,
                    "UNAUTHORIZED_SERVICE_TOKEN",
                    "Token dịch vụ chưa tới thời điểm có hiệu lực (nbf).",
                )
            if float(exp) <= float(nbf) or (float(exp) - float(nbf)) > (self.max_lifetime_seconds + self.clock_skew_seconds):
                raise ServiceAuthError(
                    401,
                    "UNAUTHORIZED_SERVICE_TOKEN",
                    "Thời hạn sống của token vượt quá giới hạn cho phép.",
                )
            nbf_int = int(nbf)

        # Validate subject (must be non-zero UUID)
        sub = payload.get("sub")
        if not isinstance(sub, str) or not sub:
            raise ServiceAuthError(
                401,
                "UNAUTHORIZED_SERVICE_TOKEN",
                "Token thiếu định danh chủ thể (sub).",
            )
        try:
            sub_uuid = uuid.UUID(sub)
        except ValueError as exc:
            raise ServiceAuthError(
                401,
                "UNAUTHORIZED_SERVICE_TOKEN",
                "Định danh chủ thể (sub) không phải UUID hợp lệ.",
            ) from exc
        if sub_uuid == uuid.UUID(int=0):
            raise ServiceAuthError(
                401,
                "UNAUTHORIZED_SERVICE_TOKEN",
                "Định danh chủ thể (sub) không được là UUID rỗng.",
            )

        # Validate client_id presence and format
        client_id = payload.get("client_id")
        if not isinstance(client_id, str) or not CLIENT_ID_PATTERN.fullmatch(client_id):
            raise ServiceAuthError(
                401,
                "UNAUTHORIZED_SERVICE_TOKEN",
                "Token thiếu client_id hợp lệ.",
            )

        # Validate audience (403 on wrong target audience; reject multi-audience array)
        aud = payload.get("aud")
        if isinstance(aud, list):
            if len(aud) != 1 or not isinstance(aud[0], str):
                raise ServiceAuthError(
                    401,
                    "UNAUTHORIZED_SERVICE_TOKEN",
                    "Token chứa nhiều audience không được phép.",
                )
            aud_str = aud[0]
        elif isinstance(aud, str) and aud:
            aud_str = aud
        else:
            raise ServiceAuthError(
                401,
                "UNAUTHORIZED_SERVICE_TOKEN",
                "Token thiếu trường audience (aud).",
            )

        if aud_str != self.audience:
            raise ServiceAuthError(
                403,
                "FORBIDDEN_SERVICE_AUDIENCE",
                f"Audience của token không hợp lệ; yêu cầu '{self.audience}'.",
            )

        # Validate caller client_id against allowlist
        if self.allowed_clients and client_id not in self.allowed_clients:
            raise ServiceAuthError(
                403,
                "FORBIDDEN_SERVICE_CLIENT",
                f"Client '{client_id}' không được cấp quyền gọi dịch vụ AI.",
            )

        # Validate scope (403 on missing or wrong scope)
        raw_scope = payload.get("scope")
        scopes: set[str] = set()
        if isinstance(raw_scope, str) and raw_scope.strip():
            for token_scope in raw_scope.strip().split():
                if not SCOPE_PATTERN.fullmatch(token_scope):
                    raise ServiceAuthError(
                        403,
                        "FORBIDDEN_SERVICE_SCOPE",
                        "Scope trong token sai định dạng chuẩn hai thành phần.",
                    )
                scopes.add(token_scope)
        elif isinstance(raw_scope, list) and raw_scope:
            for token_scope in raw_scope:
                if not isinstance(token_scope, str) or not SCOPE_PATTERN.fullmatch(token_scope):
                    raise ServiceAuthError(
                        403,
                        "FORBIDDEN_SERVICE_SCOPE",
                        "Scope trong token sai định dạng chuẩn hai thành phần.",
                    )
                scopes.add(token_scope)
        else:
            raise ServiceAuthError(
                403,
                "FORBIDDEN_SERVICE_SCOPE",
                f"Token thiếu quyền truy cập '{self.required_scope}'.",
            )

        if self.required_scope not in scopes:
            raise ServiceAuthError(
                403,
                "FORBIDDEN_SERVICE_SCOPE",
                f"Token không có quyền '{self.required_scope}'.",
            )

        return ServiceJwtClaims(
            issuer=iss,
            audience=aud_str,
            subject=str(sub_uuid),
            client_id=client_id,
            scopes=frozenset(scopes),
            key_id=kid,
            expires_at=int(exp),
            not_before=nbf_int,
        )


def _first_non_empty_env(*keys: str) -> Optional[str]:
    for key in keys:
        val = os.getenv(key)
        if val and val.strip():
            return val
    return None


def _build_verifier_from_env(
    *,
    default_scope: str,
    scope_env_keys: Tuple[str, ...],
    default_clients: FrozenSet[str],
    clients_env_keys: Tuple[str, ...],
) -> ServiceJwtVerifier:
    issuer = _first_non_empty_env("ServiceAuth__Issuer", "AURA_SERVICE_AUTH_ISSUER") or DEFAULT_ISSUER
    audience = _first_non_empty_env("ServiceAuth__Audience", "AURA_SERVICE_AUTH_AUDIENCE") or DEFAULT_AUDIENCE
    required_scope = _first_non_empty_env(*scope_env_keys) or default_scope

    public_key_path = _first_non_empty_env("ServiceAuth__PublicKeyPath", "AURA_SERVICE_AUTH_PUBLIC_KEY_PATH")
    public_key_pem = _first_non_empty_env("ServiceAuth__PublicKeyPem", "AURA_SERVICE_AUTH_PUBLIC_KEY_PEM")

    if public_key_path is None and public_key_pem is None:
        if os.path.isfile(DEFAULT_PUBLIC_KEY_PATH):
            public_key_path = DEFAULT_PUBLIC_KEY_PATH

    raw_clients = _first_non_empty_env(*clients_env_keys)
    if raw_clients:
        allowed_clients = frozenset(c.strip() for c in raw_clients.split(",") if c.strip())
    else:
        allowed_clients = default_clients

    return ServiceJwtVerifier(
        issuer=issuer,
        audience=audience,
        required_scope=required_scope,
        public_key_pem=public_key_pem,
        public_key_path=public_key_path,
        allowed_clients=allowed_clients,
    )


def load_verifier_from_env() -> ServiceJwtVerifier:
    """Load ServiceJwtVerifier for inference (`ai:analyze`) following AURA conventions."""
    return _build_verifier_from_env(
        default_scope=DEFAULT_REQUIRED_SCOPE,
        scope_env_keys=("ServiceAuth__RequiredScope", "AURA_SERVICE_AUTH_SCOPE"),
        default_clients=DEFAULT_ALLOWED_CLIENTS,
        clients_env_keys=("ServiceAuth__AllowedClientIds", "AURA_SERVICE_AUTH_ALLOWED_CLIENTS"),
    )


def load_config_verifier_from_env() -> ServiceJwtVerifier:
    """Load ServiceJwtVerifier for internal runtime config mutations (`ai:config`)."""
    return _build_verifier_from_env(
        default_scope=DEFAULT_CONFIG_SCOPE,
        scope_env_keys=("ServiceAuth__ConfigRequiredScope", "AURA_SERVICE_AUTH_CONFIG_SCOPE"),
        default_clients=DEFAULT_CONFIG_ALLOWED_CLIENTS,
        clients_env_keys=("ServiceAuth__ConfigAllowedClientIds", "AURA_SERVICE_AUTH_CONFIG_ALLOWED_CLIENTS"),
    )
