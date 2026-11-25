import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { Link, MemoryRouter, Route, Routes } from 'react-router-dom'
import PatientHistoryPage from './PatientHistoryPage'
import { BackendSessionContext } from '../auth/backendSession'
import type { BackendContext } from '../api/backendContext'

const mocks=vi.hoisted(()=>({auth:vi.fn()}))
vi.mock('../firebase/config',()=>({getFirebaseAuth:mocks.auth}))
vi.mock('../api/client',()=>({isMockEnabled:false}))
const clinic='11111111-1111-4111-8111-700000000001'
const patient='11111111-1111-4111-8111-700000000002'
const other='11111111-1111-4111-8111-700000000003'
const analysis='11111111-1111-4111-8111-700000000004'
const older='11111111-1111-4111-8111-700000000005'
const context:BackendContext={accountId:clinic,patientId:patient,personalTenantId:clinic,firebaseUid:'fixture-uid',roles:['PATIENT'],displayName:'Fixture',profileVersion:1}
const row=(id:string,status='COMPLETED')=>({analysisId:id,batchId:id,processingStatus:status,reviewStatus:'PENDING',createdAt:'2026-11-15T00:00:00Z'})
const json=(value:unknown,status=200)=>new Response(JSON.stringify(value),{status,headers:{'Content-Type':'application/json'}})
const fetchMock=vi.fn<typeof fetch>()
beforeEach(()=>{vi.resetAllMocks();mocks.auth.mockReturnValue({currentUser:{uid:context.firebaseUid,getIdToken:async()=>'fixture-jwt'}});vi.stubGlobal('fetch',fetchMock)})
afterEach(()=>{cleanup();vi.unstubAllGlobals()})
function show(){return render(<BackendSessionContext.Provider value={{state:{status:'ready',context},retry:vi.fn(),logout:vi.fn()}}>
  <MemoryRouter initialEntries={[`/clinics/patients/${patient}/history?clinicContextId=${clinic}`]}>
    <Link to={`/clinics/patients/${other}/history?clinicContextId=${clinic}`}>Đổi bệnh nhân</Link>
    <Routes><Route path="/clinics/patients/:patientId/history" element={<PatientHistoryPage/>}/></Routes>
  </MemoryRouter></BackendSessionContext.Provider>)}

it('loads older pages with a bounded cursor and removes cached rows if authority is revoked',async()=>{
  fetchMock.mockResolvedValueOnce(json({patientId:patient,items:[row(analysis)],nextBeforeId:analysis}))
    .mockResolvedValueOnce(json({patientId:patient,items:[row(older,'FAILED')],nextBeforeId:null}))
    .mockResolvedValueOnce(json({error:{code:'ERR_NOT_FOUND'}},404))
  show();await screen.findByText('Đã hoàn tất AI')
  fireEvent.click(screen.getByRole('button',{name:'Các lượt cũ hơn'}))
  await screen.findByText('Xử lý thất bại')
  expect(fetchMock).toHaveBeenLastCalledWith(expect.stringContaining(`beforeId=${analysis}`),expect.anything())
  expect(screen.queryByText('Đã hoàn tất AI')).toBeNull()
  fireEvent.click(screen.getByRole('button',{name:'Tải lại'}));await screen.findByRole('alert')
  expect(screen.queryByRole('link',{name:'Xem kết quả'})).toBeNull()
})
it('ignores a late history response after changing patient',async()=>{
  let finish:(value:Response)=>void=()=>{}
  fetchMock.mockImplementation(async input=>String(input).includes(other)?json({patientId:other,items:[],nextBeforeId:null}):new Promise<Response>(resolve=>{finish=resolve}))
  show();await waitFor(()=>expect(fetchMock).toHaveBeenCalledOnce())
  fireEvent.click(screen.getByRole('link',{name:'Đổi bệnh nhân'}));await screen.findByText('Chưa có lượt phân tích nào.')
  await act(async()=>finish(json({patientId:patient,items:[row(analysis)],nextBeforeId:null})))
  expect(screen.queryByRole('link',{name:'Xem kết quả'})).toBeNull()
})
it('rejects a history payload identifying a different patient',async()=>{
  fetchMock.mockResolvedValue(json({patientId:other,items:[row(analysis)],nextBeforeId:null}))
  show();await screen.findByRole('alert')
  expect(screen.queryByRole('link',{name:'Xem kết quả'})).toBeNull()
})

it('loads versioned measurements only from the scoped results and preserves unavailable AVR', async () => {
  fetchMock.mockResolvedValueOnce(json({ patientId: patient, items: [row(analysis)], nextBeforeId: null }))
    .mockResolvedValueOnce(json({ ...row(analysis), updatedAt: '2026-11-15T00:00:01Z', reviewVersion: 2, result: {
      version: 3, modelVersion: 'frangi-fixture', configVersion: 'config-fixture', thresholdVersion: 'threshold-fixture',
      isMock: false, riskAvailable: false, riskLevel: null, riskScore: null, confidenceScore: null,
      disclaimer: '', limitations: '', metrics: { vesselDensity: 0.24, avRatio: null, tortuosityIndex: null },
      indicators: [], annotations: [], warnings: [], research: {
        avr: { measurement_available: false, avr: null, crae_px: null, crve_px: null,
          unavailable_reason: 'LOW_QUALITY', model_version: 'avr-fixture', model_sha256: 'a'.repeat(64) },
        dr: { probability_dr: 0.8, model_version: 'dr-fixture', checkpoint_sha256: 'b'.repeat(64),
          gradcam_method: 'GRAD_CAM', gradcam_geometry: 'IMAGENET_CENTER_CROP_224' },
      },
    } }))
  show()
  fireEvent.click(await screen.findByRole('button', { name: 'Hiện chỉ số của trang này' }))
  expect(await screen.findByText('Không đo được (LOW_QUALITY)')).toBeTruthy()
  expect(screen.getByText('24.0%')).toBeTruthy()
  expect(screen.getByText(/Kết quả 3 · Đánh giá 2/)).toBeTruthy()
  expect(fetchMock).toHaveBeenLastCalledWith(`/api/v1/analyses/${analysis}?clinicContextId=${clinic}`, expect.anything())
})
