"""Reuse bounded model state, never an image's predictions or a failed integrity check."""
import sys
from types import SimpleNamespace

import numpy as np


def test_optic_disc_reuses_one_session_but_runs_each_image(monkeypatch):
    from app.services import avr_optic_disc as od
    calls = []
    class Session:
        def __init__(self, path, *_args, **_kwargs): calls.append(('load', path))
        def get_inputs(self): return [SimpleNamespace(name='image')]
        def run(self, *_args):
            calls.append(('run', None))
            return [np.zeros((1, 4, 512, 512), dtype=np.float32)]
    monkeypatch.setitem(sys.modules, 'onnxruntime', SimpleNamespace(
        SessionOptions=SimpleNamespace, InferenceSession=Session))
    image = np.zeros((16, 16, 3), dtype=np.uint8)
    for path in ['first.onnx', 'first.onnx', 'second.onnx', 'first.onnx']:
        od.locate_optic_disc(image, {'model_type': 'RRWNET_RITE', 'od_weights_path': path,
                                    'od_weights_sha256': od.OD_SHA256})
    assert [value for kind, value in calls if kind == 'load'] == ['first.onnx', 'second.onnx', 'first.onnx']
    assert sum(kind == 'run' for kind, _ in calls) == 4
    od._optic_disc_session.cache_clear()


def test_rrwnet_retains_only_one_verified_model(monkeypatch):
    from app.services import avr_rrwnet as rrw
    from app.services.third_party.rrwnet import model as networks
    import safetensors.torch
    calls = []
    class Network:
        def eval(self): return self
        def to(self, **kwargs): return self
    monkeypatch.setattr(networks, 'RRWNet', Network)
    monkeypatch.setattr(safetensors.torch, 'load_model', lambda net, path, **kw: calls.append(path))
    rrw._rrwnet_network.cache_clear()
    for path in ['one', 'one', 'two', 'one']:
        rrw._rrwnet_network(path, rrw.WEIGHTS_SHA256)
    assert calls == ['one', 'two', 'one']
    assert rrw._rrwnet_network.cache_info().currsize == 1
    rrw._rrwnet_network.cache_clear()
