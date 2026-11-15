import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("registration", Path(__file__).with_name("Register-AuraProfileServiceClient.py"))
registration = importlib.util.module_from_spec(spec)
spec.loader.exec_module(registration)


class ProfileClientRegistrationTests(unittest.TestCase):
    def test_existing_identity_only_registration_can_add_binding_without_rotating_credentials(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            repo, home = root / "repo", root / "private"
            repo.mkdir()
            (repo / "AURA.sln").touch()
            for name in ("issuer", "public", "clients"):
                (home / name).mkdir(parents=True)
            for relative in ("issuer/signing-key.pem", "public/signing-public.pem"):
                (home / relative).write_bytes(b"fixture-key-unchanged")
            legacy = {"aura-identity": ["identity:context"]}
            import base64
            import hashlib
            salt = b"0123456789abcdef"
            secret = "fixture-only-secret"
            digest = hashlib.pbkdf2_hmac("sha256", secret.encode(), salt, 600000, 32)
            encoded = "pbkdf2-sha256$600000$" + base64.b64encode(salt).decode() + "$" + base64.b64encode(digest).decode()
            clients = [{"clientId": name, "principalId": "a1000000-0000-4000-8000-00000000000" + str(i),
                        "allowedTargets": {}, "clientSecretHash": encoded}
                       for i, name in enumerate(("analysis-api", "identity-api", "billing-api"), 1)]
            for client in clients:
                (home / "clients" / (client["clientId"] + ".json")).write_text(json.dumps(client))
            principal = "a8b913b3-1455-568d-a2e0-b4f7271ae375"
            clients.append({"clientId": "profile-api", "principalId": principal,
                            "allowedTargets": legacy, "clientSecretHash": encoded})
            registry = {"schemaVersion": 1, "issuer": "aura-identity-s3-v1", "algorithm": "RS256",
                        "tokenLifetimeSeconds": 300, "clients": clients}
            (home / "issuer/identity-clients.json").write_text(json.dumps(registry))
            credential = {"clientId": "profile-api", "principalId": principal,
                          "clientSecret": secret, "allowedTargets": legacy}
            (home / "clients/profile-api.json").write_text(json.dumps(credential))
            before = {path: path.read_bytes() for path in home.rglob("*") if path.is_file()}
            registration.register(home, repo)
            self.assertTrue(all(path.read_bytes() == data for path, data in before.items()))
            registration.register(home, repo, apply=True)
            actual = registration.read(home / "issuer/identity-clients.json")
            profile = actual["clients"][-1]
            self.assertEqual({"aura-identity": ["identity:context"], "aura-analysis": ["analysis:binding"]}, profile["allowedTargets"])
            self.assertEqual(encoded, profile["clientSecretHash"])
            self.assertEqual(principal, profile["principalId"])
            self.assertEqual(clients[:-1], actual["clients"][:-1])
            updated = registration.read(home / "clients/profile-api.json")
            self.assertEqual(secret, updated["clientSecret"])
            self.assertEqual(profile["allowedTargets"], updated["allowedTargets"])
            for path, data in before.items():
                if path.name not in ("profile-api.json", "identity-clients.json"):
                    self.assertEqual(data, path.read_bytes())
            after = {path: path.read_bytes() for path in home.rglob("*") if path.is_file()}
            registration.register(home, repo, apply=True)
            self.assertTrue(all(path.read_bytes() == data for path, data in after.items()))


if __name__ == "__main__":
    unittest.main()
