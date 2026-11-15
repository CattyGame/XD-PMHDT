import uuid

import pytest
from fastapi import HTTPException

from app import main
from app.schemas.analysis import AnalysisRequest
from app.services.frangi_service import FrangiRuntimeConfig
from scripts.accept_avr_container import soak_payload


def request():
    return AnalysisRequest(**soak_payload(str(uuid.uuid4()),'AQ=='))


def test_soak_payload_reaches_real_frangi_contract_instead_of_422(monkeypatch):
    monkeypatch.setenv('AURA_AI_ENGINE','frangi')
    monkeypatch.setattr(main,'process_frangi_analysis',lambda *args,**kwargs:'REAL_PROCESSOR_REACHED')
    assert main.analyze_retina(request(),FrangiRuntimeConfig())=='REAL_PROCESSOR_REACHED'


def test_research_profile_frangi_shares_the_cnn_slot_and_releases_on_error(monkeypatch):
    monkeypatch.setenv('AURA_AI_ENGINE','frangi')
    monkeypatch.setenv('AURA_AVR_MODEL_PATH','verified-runtime.json')
    monkeypatch.setenv('AURA_AVR_MODEL_SHA256','a'*64)
    called=[]
    def fail(*args,**kwargs):called.append(1);raise RuntimeError('injected failure')
    monkeypatch.setattr(main,'process_frangi_analysis',fail)
    with main.CNN_SLOT:
        with pytest.raises(HTTPException) as error:main.analyze_retina(request(),FrangiRuntimeConfig())
        assert error.value.status_code==429 and error.value.headers['Retry-After']=='1'
        assert not called
    with pytest.raises(HTTPException) as error:main.analyze_retina(request(),FrangiRuntimeConfig())
    assert error.value.status_code==500 and called==[1]
    assert main.CNN_SLOT.acquire(blocking=False)
    main.CNN_SLOT.release()


def test_baseline_without_research_does_not_serialize_frangi(monkeypatch):
    monkeypatch.setenv('AURA_AI_ENGINE','frangi')
    for key in ('AURA_AVR_MODEL_PATH','AURA_AVR_MODEL_SHA256','AURA_DR_CNN_CHECKPOINT','AURA_DR_CNN_SHA256'):
        monkeypatch.delenv(key,raising=False)
    monkeypatch.setattr(main,'process_frangi_analysis',lambda *args,**kwargs:'BASELINE')
    with main.CNN_SLOT:
        assert main.analyze_retina(request(),FrangiRuntimeConfig())=='BASELINE'
