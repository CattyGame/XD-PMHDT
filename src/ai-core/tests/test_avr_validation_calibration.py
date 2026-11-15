import sys
import copy
import hashlib
import json
from types import SimpleNamespace

import numpy as np
import pytest

from app.services.avr_rrwnet import rrwnet_labels, verify_vessel_calibration, WEIGHTS_SHA256, AVRDB_MANIFEST_SHA256, CALIBRATION_CASES
from scripts import calibrate_avr_vessel_threshold as calibration


def test_vessel_threshold_changes_support_without_changing_av_class_or_native_size():
    p=np.array([[[.9,.1,.2],[.1,.9,.8]]],np.float32)
    low,probability=rrwnet_labels(p,(4,2),.1)
    high,other=rrwnet_labels(p,(4,2),.5)
    np.testing.assert_array_equal(low[0],[1,1,2,2])
    np.testing.assert_array_equal(high[0],[0,0,2,2])
    np.testing.assert_array_equal(probability,other)
    assert low.shape==(2,4)


def test_output_calibration_does_not_change_author_measurement_seeds_or_repeat_inference(monkeypatch):
    from app.services import avr_rrwnet,avr_geometry,avr_optic_disc
    from app.services.avr_service import analyze_avr_image
    raw=np.full((80,80,3),[.8,.2,.2],np.float32);raw[30:35,50:60,2]=.8
    calls=[];seen=[]
    def infer(rgb,model):calls.append(1);return raw
    monkeypatch.setattr(avr_rrwnet,'rrwnet_probabilities',infer)
    monkeypatch.setattr(avr_optic_disc,'locate_optic_disc',lambda *args:dict(center_x=40,center_y=40,radius_px=5))
    def trace(rgb,mask,probability,disc):
        seen.append(mask.copy());return dict(arteries=[],veins=[],rejected=[])
    monkeypatch.setattr(avr_geometry,'trace_root_calibers',trace)
    model=dict(model_type='RRWNET_RITE',dataset_id='AVRDB',sha256='a'*64,vessel_threshold=.1)
    result,labels,_=analyze_avr_image(np.zeros((80,80,3),np.uint8),model,False)
    assert calls==[1] and np.count_nonzero(labels)==6400 and np.count_nonzero(seen[0])==50
    np.testing.assert_array_equal(seen[0],raw[:,:,2]>=.5)
    assert result['model_metadata']['measurement_vessel_threshold']==.5
    assert result['measurement_available'] is False and result['avr'] is None


@pytest.mark.parametrize('threshold',[0,1,True,float('nan')])
def test_decoder_rejects_invalid_thresholds(threshold):
    with pytest.raises(ValueError):rrwnet_labels(np.zeros((2,3,3),np.float32),(3,2),threshold)


def test_threshold_selection_uses_validation_dice_and_required_support_then_conservative_tie():
    rows=[dict(threshold=.1,vessel_dice=.75,av_accuracy_on_reference_support=.5),
          dict(threshold=.2,vessel_dice=.70,av_accuracy_on_reference_support=.65),
          dict(threshold=.3,vessel_dice=.70,av_accuracy_on_reference_support=.61)]
    assert calibration.select_threshold(rows)['threshold']==.3


def test_probability_cache_reuses_only_valid_identical_inference(tmp_path,monkeypatch):
    monkeypatch.setitem(sys.modules,'torch',SimpleNamespace(get_num_threads=lambda:2))
    calls=[]
    def infer(rgb,model):
        calls.append(1)
        return np.full((*rgb.shape[:2],3),.5,np.float32)
    monkeypatch.setattr(calibration,'rrwnet_probabilities',infer)
    rgb=np.zeros((20,30,3),np.uint8);model={'weights_sha256':'a'*64}
    first,meta=calibration.cached_probabilities(rgb,model,tmp_path)
    second,again=calibration.cached_probabilities(rgb,model,tmp_path)
    assert calls==[1] and not meta['cache_hit'] and again['cache_hit']
    np.testing.assert_array_equal(first,second)
    path=meta['cache_path']
    with np.load(path,allow_pickle=False) as saved:metadata=saved['metadata'].copy()
    np.savez_compressed(path,metadata=metadata,probabilities=np.ones((1,2,3)),inference_seconds=0.)
    with pytest.raises(ValueError,match='Invalid raw probability'):calibration.cached_probabilities(rgb,model,tmp_path)
    assert calls==[1]


def test_calibration_verification_rejects_test_leakage_and_arbitrary_thresholds(tmp_path):
    report=dict(algorithm='rrwnet-vessel-validation-dice-v1',dataset_id='AVRDB',
        base_weights_sha256=WEIGHTS_SHA256,manifest_sha256=AVRDB_MANIFEST_SHA256,
        validation_cases=sorted(CALIBRATION_CASES),test_cases_used=False,iowa_references_used=False,
        threshold_grid=list(calibration.THRESHOLDS),selected_threshold=.1,
        candidates=[dict(threshold=t,vessel_dice=.8 if t==.1 else .7,av_accuracy_on_reference_support=.7)
                    for t in calibration.THRESHOLDS])
    def verify(value,threshold=.1):
        raw=json.dumps(value).encode();(tmp_path/'calibration.json').write_bytes(raw)
        model=dict(calibration_file='calibration.json',calibration_sha256=hashlib.sha256(raw).hexdigest(),
                   manifest_sha256=AVRDB_MANIFEST_SHA256,validation_cases=report['validation_cases'],vessel_threshold=threshold)
        verify_vessel_calibration(model,tmp_path/'runtime.json')
    verify(report)
    for change in ({'test_cases_used':True},{'iowa_references_used':True},{'validation_cases':['IM000001']*15},
                   {'validation_cases':None},{'base_weights_sha256':'0'*64},{'selected_threshold':.2}):
        with pytest.raises(ValueError):verify({**copy.deepcopy(report),**change})
    with pytest.raises(ValueError):verify(report,.2)
    with pytest.raises(ValueError):verify([])


def test_artifact_export_preserves_baseline_and_refuses_existing_destination(tmp_path,monkeypatch):
    baseline=tmp_path/'baseline';baseline.mkdir()
    model=baseline/'runtime.json';model.write_text(json.dumps({'vessel_threshold':.5}))
    for name in ('model.safetensors','lunetv2_odc.onnx','WEIGHTS_LICENSE_CC_BY_NC_4.0.txt'):
        (baseline/name).write_bytes(name.encode())
    report=tmp_path/'calibration.json';report.write_text(json.dumps(dict(selected_threshold=.1,base_model_sha256='base-digest',
        manifest_sha256=AVRDB_MANIFEST_SHA256,validation_cases=sorted(CALIBRATION_CASES))))
    verified=[]
    monkeypatch.setattr(calibration,'load_avr_model',lambda path,digest:verified.append((path,digest)))
    destination=tmp_path/'calibrated'
    digest=calibration.export_artifact(model,'base-digest',report,destination)
    exported=json.loads((destination/'runtime.json').read_bytes())
    assert exported['manifest_sha256']==AVRDB_MANIFEST_SHA256 and exported['vessel_threshold']==.1
    assert exported['calibration_sha256']==hashlib.sha256(report.read_bytes()).hexdigest()
    assert len(verified)==2 and verified[-1][1]==digest
    assert json.loads(model.read_text())['vessel_threshold']==.5
    with pytest.raises(FileExistsError):calibration.export_artifact(model,'base-digest',report,destination)
    with pytest.raises(ValueError,match='different baseline'):calibration.export_artifact(model,'wrong-digest',report,tmp_path/'wrong')
    assert not (tmp_path/'wrong').exists()
