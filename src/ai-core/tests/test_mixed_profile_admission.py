import uuid
import threading
import time

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


def test_configured_waiter_is_admitted_but_overflow_still_returns_429(monkeypatch):
    monkeypatch.setenv('AURA_AI_ENGINE','frangi')
    monkeypatch.setenv('AURA_INTEGRATED_RESEARCH_ENABLED','false')
    monkeypatch.setenv('AURA_AVR_MODEL_PATH','verified-runtime.json')
    monkeypatch.setenv('AURA_AVR_MODEL_SHA256','a'*64)
    monkeypatch.setenv('AURA_AI_MAX_WAITERS','1')
    monkeypatch.setattr(main,'process_frangi_analysis',lambda *args,**kwargs:'FIFO_PROCESSOR_REACHED')
    results=[];errors=[];done=threading.Event()
    def pending():
        try:results.append(main.analyze_retina(request(),FrangiRuntimeConfig()))
        except HTTPException as ex:errors.append(ex.status_code)
        finally:done.set()
    thread=threading.Thread(target=pending,daemon=True)
    with main.CNN_SLOT:
        thread.start()
        deadline=time.monotonic()+2
        while time.monotonic()<deadline:
            assert not done.is_set(), errors
            with main.CNN_SLOT._lock:
                if len(main.CNN_SLOT._waiters)==1:break
            time.sleep(.001)
        else:pytest.fail('Endpoint did not enter bounded admission queue')
        with pytest.raises(HTTPException) as error:main.analyze_retina(request(),FrangiRuntimeConfig())
        assert error.value.status_code==429 and error.value.headers['Retry-After']=='1'
    assert done.wait(2)
    thread.join(2)
    assert results==['FIFO_PROCESSOR_REACHED'] and errors==[]


@pytest.mark.parametrize('value',['-1','4','invalid'])
def test_invalid_admission_configuration_fails_closed_without_consuming_a_slot(monkeypatch,value):
    monkeypatch.setenv('AURA_AI_MAX_WAITERS',value)
    with pytest.raises(HTTPException) as error:main.acquire_inference_slot()
    assert error.value.status_code==503
    assert main.CNN_SLOT.acquire(False)
    main.CNN_SLOT.release()
