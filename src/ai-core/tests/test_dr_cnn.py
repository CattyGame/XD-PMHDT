"""Requires the optional CNN environment; gradients must depend on actual model weights."""
import hashlib
import csv
from pathlib import Path

import numpy as np
import pytest

torch = pytest.importorskip('torch')
torchvision = pytest.importorskip('torchvision')

from app.services.dr_cnn_service import gradient_cam, screen_dr_cnn
from scripts.train_dr_cnn import auc


def test_auc_handles_ties_and_missing_classes():
    assert auc([0, 1], [0.4, 0.4]) == 0.5
    assert auc([0, 1], [0.1, 0.9]) == 1.0
    with pytest.raises(ValueError):
        auc([1, 1], [0.1, 0.9])


def test_gradcam_uses_gradient_and_removes_hook():
    torch.set_num_threads(2)
    torch.manual_seed(1)
    model = torchvision.models.resnet34(weights=None).eval()
    model.fc = torch.nn.Linear(model.fc.in_features, 1)
    with torch.no_grad():
        model.fc.weight.fill_(1)
    tensor = torch.rand(1, 3, 64, 64)
    probability, heatmap = gradient_cam(model, tensor)
    assert 0 <= probability <= 1 and heatmap.shape == (2, 2)
    assert np.isfinite(heatmap).all() and heatmap.max() > 0
    assert len(model.layer4._forward_hooks) == 0
    with torch.no_grad():
        model.fc.weight.zero_()
    _, zero_gradient_heatmap = gradient_cam(model, tensor)
    assert np.count_nonzero(zero_gradient_heatmap) == 0
    assert len(model.layer4._forward_hooks) == 0


def test_checkpoint_integrity_and_actual_resnet_roundtrip(tmp_path):
    torch.set_num_threads(2)
    model = torchvision.models.resnet34(weights=None)
    model.fc = torch.nn.Linear(model.fc.in_features, 1)
    path = tmp_path / 'cnn.pt'
    torch.save({'state_dict': model.state_dict(), 'metadata': {'architecture': 'resnet34', 'task': 'hrf_dr_vs_healthy'}}, path)
    rgb = np.full((256, 320, 3), 80, dtype=np.uint8)
    with pytest.raises(ValueError, match='SHA-256'):
        screen_dr_cnn(rgb, str(path), '0' * 64)
    result = screen_dr_cnn(rgb, str(path), hashlib.sha256(path.read_bytes()).hexdigest())
    assert result['status'] == 'RESEARCH_ONLY' and not result['risk_available']
    assert result['explainability']['method'] == 'GRAD_CAM'
    assert result['explainability']['geometry'] == 'IMAGENET_CENTER_CROP_224'
    assert np.asarray(result['explainability']['heatmap']).shape == (224, 224)


@pytest.mark.parametrize('defect,expected', [('patient', 'Patient overlap'), ('image', 'Duplicate image'), ('category', 'documented HRF')])
def test_training_rejects_bad_manifest_before_loading_pretrained_model(tmp_path, monkeypatch, defect, expected):
    from scripts import train_dr_cnn as training
    monkeypatch.setattr(training, 'ROOT', tmp_path)
    path = tmp_path / 'docs/datasets/hrf/manifest.csv'
    path.parent.mkdir(parents=True)
    rows = [{'patient_id': str(i), 'image_sha256': str(i), 'category': ['dr', 'h', 'g'][i // 15], 'split': 'train'} for i in range(45)]
    if defect == 'patient':
        rows[1]['patient_id'] = rows[0]['patient_id']
    elif defect == 'image':
        rows[1]['image_sha256'] = rows[0]['image_sha256']
    else:
        rows[0]['category'] = 'unknown'
    with path.open('w', newline='', encoding='utf-8') as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)
    with pytest.raises(ValueError, match=expected):
        training.train(tmp_path / 'must-not-exist.pt')
    assert not (tmp_path / 'must-not-exist.pt').exists()
