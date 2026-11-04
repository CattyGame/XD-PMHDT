import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { BackendContext } from './backendContext'
import {
  DoctorApiError, getDoctorAnalysis, getDoctorFileAccess, getDoctorWorklist,
  submitDoctorReview,
} from './doctorReview'

const mocks = vi.hoisted(() => ({ auth: vi.fn(), mock: false }))
vi.mock('../firebase/config', () => ({ getFirebaseAuth: mocks.auth }))
vi.mock('./client', () => ({ get isMockEnabled() { return mocks.mock } }))

const clinicId = '10000000-0000-4000-8000-000000000001'
const analysisId = '20000000-0000-4000-8000-000000000002'
const patientId = '30000000-0000-4000-8000-000000000003'
const fileId = '40000000-0000-4000-8000-000000000004'
const context: BackendContext = { accountId: '50000000-0000-4000-8000-000000000005',
  firebaseUid: 'doctor-uid', roles: ['DOCTOR'], patientId, personalTenantId: clinicId,
  displayName: 'Bác sĩ', profileVersion: 1 }
const item = { analysisId, batchId: '60000000-0000-4000-8000-000000000006',
  patientId, processingStatus: 'COMPLETED', reviewStatus: 'PENDING', reviewVersion: 2,
  createdAt: '2026-10-30T10:00:00Z', image: { fileId, kind: 'original' },
  result: { riskAvailable: true, riskLevel: 'HIGH', isMock: false, annotations: [], disclaimer: 'AI chỉ hỗ trợ' } }
const user = { uid: 'doctor-uid', getIdToken: vi.fn(async () => 'firebase-token') }
const auth: { currentUser: typeof user | null } = { currentUser: user }
const fetchMock = vi.fn<typeof fetch>()
function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
}
beforeEach(() => {
  vi.clearAllMocks(); mocks.mock = false; auth.currentUser = user
  mocks.auth.mockReturnValue(auth); user.getIdToken.mockResolvedValue('firebase-token')
  vi.stubGlobal('fetch', fetchMock)
})
afterEach(() => vi.unstubAllGlobals())

describe('SCRUM-354: Doctor Review API', () => {
  it('GET worklist dùng Bearer và clinicContextId, chỉ hiển thị dữ liệu thực', async () => {
    fetchMock.mockResolvedValueOnce(json({ items: [item] }))
    const list = await getDoctorWorklist(context, clinicId)
    expect(list).toHaveLength(1)
    expect(list[0]).toMatchObject({ patientId, reviewVersion: 2 })
    expect(fetchMock.mock.calls[0][0]).toBe('/api/v1/doctor/worklist?clinicContextId=' + clinicId)
    expect(fetchMock.mock.calls[0][1]?.headers).toMatchObject({ Authorization: 'Bearer firebase-token' })
    expect(fetchMock.mock.calls[0][1]?.cache).toBe('no-store')
  })

  it('GET chi tiết không chấp nhận analysisId khác ca đã yêu cầu', async () => {
    fetchMock.mockResolvedValueOnce(json({ ...item, analysisId: patientId }))
    await expect(getDoctorAnalysis(context, clinicId, analysisId))
      .rejects.toMatchObject({ code: 'ERR_INVALID_RESPONSE' })
  })

  it('POST review gửi baseVersion và body khớp hợp đồng, HTTP 201 thành công', async () => {
    fetchMock.mockResolvedValueOnce(json({ reviewId: '70000000-0000-4000-8000-000000000007',
      analysisId, version: 3, isApproved: false }, 201))
    const payload = { isApproved: false as const, baseVersion: 2,
      correctedRiskLevel: 'MODERATE' as const, reason: 'Đối chiếu lâm sàng', doctorNotes: 'Theo dõi' }
    const review = await submitDoctorReview(context, clinicId, analysisId, payload)
    expect(review.version).toBe(3)
    expect(fetchMock.mock.calls[0][0]).toBe('/api/v1/doctor/analyses/' + analysisId + '/reviews?clinicContextId=' + clinicId)
    expect(fetchMock.mock.calls[0][1]?.method).toBe('POST')
    expect(JSON.parse(fetchMock.mock.calls[0][1]?.body as string)).toEqual(payload)
  })

  it('HTTP 409 giữ nguyên xung đột để UI chặn ghi đè', async () => {
    fetchMock.mockResolvedValueOnce(json({ error: { code: 'ERR_REVIEW_VERSION_CONFLICT' } }, 409))
    await expect(submitDoctorReview(context, clinicId, analysisId, { isApproved: true, baseVersion: 2 }))
      .rejects.toMatchObject({ status: 409, code: 'ERR_REVIEW_VERSION_CONFLICT' })
  })

  it('File Access kèm clinicContextId và không dùng ảnh không có quyền', async () => {
    fetchMock.mockResolvedValueOnce(json({ url: 'https://storage.example.org/short-lived-file',
      expiresAt: new Date(Date.now() + 60000).toISOString() }))
    const file = await getDoctorFileAccess(context, clinicId, analysisId, fileId)
    expect(file.url).toContain('https://')
    expect(fetchMock.mock.calls[0][0]).toContain('/files/' + fileId + '/access?clinicContextId=' + clinicId)
  })

  it('chặn tài khoản không thuộc DOCTOR ngay tại API client', async () => {
    await expect(getDoctorWorklist({ ...context, roles: ['PATIENT'] }, clinicId))
      .rejects.toMatchObject({ status: 403 })
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('thông báo lỗi rõ ràng khi đang bật mock', async () => {
    mocks.mock = true
    await expect(getDoctorWorklist(context, clinicId)).rejects.toBeInstanceOf(DoctorApiError)
    expect(fetchMock).not.toHaveBeenCalled()
  })
})
