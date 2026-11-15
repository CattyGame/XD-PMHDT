"""Fetch the author's pinned research/noncommercial artifact; never download during HTTP inference."""
import argparse
import hashlib
import json
from pathlib import Path
import tempfile
import urllib.request

ROOT = Path(__file__).resolve().parents[3]
REVISION = '9b61e9042e7693f2538e01be9b15c68a3656718f'
WEIGHTS_SHA256 = '32d93fa770f2f1c52359cc30ae524346043443b972b108974dbf524db8104485'
LICENSE_SHA256 = '517acfe79046fca3d989a5a6f8b55b8c3919bb442609bd9ed0e81af6e25deb3a'
OD_REVISION = 'cf1f116d6b9898aa7b2aa3b075e071d3460e3a6e'
OD_SHA256 = 'f72f6c9ea2ade798b80539de77fa610bb6553bb0fd7fcb78d1e841982cae0b5f'


def verified_download(url, target, expected_sha256, maximum_bytes):
    target = Path(target)
    if target.exists():
        with target.open('rb') as stream:
            if hashlib.file_digest(stream, 'sha256').hexdigest() == expected_sha256:
                return
        raise ValueError(f'Existing artifact differs; refusing overwrite: {target.name}')
    target.parent.mkdir(parents=True, exist_ok=True)
    temporary = None
    try:
        digest, length = hashlib.sha256(), 0
        with tempfile.NamedTemporaryFile(dir=target.parent, suffix='.download', delete=False) as output:
            temporary = Path(output.name)
            request = urllib.request.Request(url, headers={'User-Agent': 'AURA-SCRUM373-research'})
            with urllib.request.urlopen(request, timeout=90) as response:
                while chunk := response.read(1024 * 1024):
                    length += len(chunk)
                    if length > maximum_bytes:
                        raise ValueError('Publisher artifact exceeds the pinned size budget.')
                    digest.update(chunk)
                    output.write(chunk)
        if digest.hexdigest() != expected_sha256:
            raise ValueError('Downloaded artifact SHA-256 mismatch.')
        temporary.replace(target)
    finally:
        if temporary is not None and temporary.exists():
            temporary.unlink()


def fetch(destination):
    base = f'https://huggingface.co/j-morano/rrwnet-rite/resolve/{REVISION}/'
    verified_download(base + 'model.safetensors', destination / 'model.safetensors', WEIGHTS_SHA256, 248269468)
    verified_download(base + 'LICENSE', destination / 'WEIGHTS_LICENSE_CC_BY_NC_4.0.txt', LICENSE_SHA256, 20000)
    verified_download(f'https://huggingface.co/JonathanFhima/RLAD-SwinV2-Large/resolve/{OD_REVISION}/pvbm/lunetv2_odc.onnx',
                      destination / 'lunetv2_odc.onnx', OD_SHA256, 124966662)
    descriptor = dict(model_type='RRWNET_RITE', dataset_id='AVRDB', training_dataset_id='RITE',
                      weights_file='model.safetensors', weights_sha256=WEIGHTS_SHA256, model_revision=REVISION,
                      weights_license='CC-BY-NC-4.0', research_only=True,
                      source='https://huggingface.co/j-morano/rrwnet-rite', train_cases=[], validation_cases=[],
                      preprocessing='author-enhancement-v1', iterations=5, max_width=584, vessel_threshold=0.5,
                      optic_disc_method='LUNETV2_ODC_ONNX', od_weights_file='lunetv2_odc.onnx',
                      od_weights_sha256=OD_SHA256, od_model_revision=OD_REVISION,
                      od_source='https://github.com/aim-lab/PVBM', od_reference_status='MODEL_PREDICTION_NOT_EXPERT_ANNOTATION')
    raw = (json.dumps(descriptor, indent=2) + '\n').encode()
    path = destination / 'runtime.json'
    if path.exists() and path.read_bytes() != raw:
        raise ValueError('Existing descriptor differs; use a fresh destination.')
    path.write_bytes(raw)
    return dict(descriptor=str(path.resolve()), sha256=hashlib.sha256(raw).hexdigest(),
                weights_sha256=WEIGHTS_SHA256, weights_license='CC-BY-NC-4.0; research/noncommercial only')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--destination', type=Path, default=ROOT / 'model-artifacts/avr-rrwnet-rite')
    print(json.dumps(fetch(parser.parse_args().destination), indent=2))
