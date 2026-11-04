import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { BackendContext } from './backendContext'
import {
  AnalysisResultError, getAnalysisResult, getPrivateFileAccess, getProtectedContent, parseAnalysis,
} from './analysisResult'

const mocks = vi.hoisted(() => ({ auth: vi.fn(), mock: false }))
vi.mock('../firebase/config', () => ({ getFirebaseAuth: mocks.auth }))
vi.mock('./client', () => ({ get isMockEnabled() { return mocks.mock } }))

const analysisId = '50000000-0000-4000-8000-000000000005'
const batchId = '40000000-0000-4000-8000-000000000004'
const fileId = '60000000-0000-4000-8000-000000000006'
const maskId = '70000000-0000-4000-8000-000000000007'
const context: BackendContext = {
  firebaseUid: 'user-a', accountId: '10000000-0000-4000-8000-000000000001',
  patientId: '20000000-0000-4000-8000-000000000002',
  personalTenantId: '30000000-0000-4000-8000-000000000003',
  roles: ['PATIENT'], displayName: 'Patient A', profileVersion: 1,
}
const completed = {
  analysisId, batchId, processingStatus: 'COMPLETED', reviewStatus: 'PENDING',
  updatedAt: '2026-10-28T10:00:00Z', image: { fileId, kind: 'original' },
  result: {
    version: 2, isMock: false, riskAvailable: false,
    riskLevel: null, riskScore: null, confidenceScore: null,
    modelVersion: 'frangi-v0.2', configVersion: 's3-config',
    thresholdVersion: 'threshold-v1', disclaimer: 'Chỉ nghiên cứu',
    limitations: 'Không phải chẩn đoán', metrics: { vesselDensity: 0.31 },
    indicators: [], annotations: [{ fileId: maskId, kind: 'mask' }],
    warnings: [{ code: 'RISK_UNAVAILABLE', message: 'Chưa đánh giá nguy cơ' }],
  },
}
const user = { uid: 'user-a', getIdToken: vi.fn(async () => 'firebase-token') }
const auth: { currentUser: typeof user | null } = { currentUser: user }
const fetchMock = vi.fn<typeof fetch>()
function json(value: unknown, status = 200): Response {
  return new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } })
}
beforeEach(() => {
  vi.resetAllMocks()
  mocks.mock = false
  user.uid = 'user-a'
  auth.currentUser = user
  mocks.auth.mockReturnValue(auth)
  user.getIdToken.mockResolvedValue('firebase-token')
  fetchMock.mockResolvedValue(json(completed))
  vi.stubGlobal('fetch', fetchMock)
})
afterEach(() => { vi.unstubAllGlobals() })

describe('SCRUM-333 — Analysis Result client', () => {
  it('reads only the authorized analysis and preserves version, warnings and no-risk fields', async () => {
    const data = await getAnalysisResult(analysisId, context)
    expect(data).toMatchObject({ analysisId, batchId, processingStatus: 'COMPLETED' })
    expect(data.result).toMatchObject({ version: 2, riskAvailable: false, riskLevel: null,
      riskScore: null, confidenceScore: null, isMock: false })
    expect(data.result?.warnings[0]?.code).toBe('RISK_UNAVAILABLE')
    expect(data.result?.annotations[0]).toEqual({ fileId: maskId, kind: 'mask' })
    expect(fetchMock).toHaveBeenCalledTimes(1)
    expect(fetchMock.mock.calls[0][0]).toBe(`/api/v1/analyses/${analysisId}`)
    expect(fetchMock.mock.calls[0][1]).toMatchObject({ method: 'GET', cache: 'no-store', credentials: 'omit', redirect: 'error' })
    expect(new Headers(fetchMock.mock.calls[0][1]?.headers).get('Authorization')).toBe('Bearer firebase-token')
  })

  it('does not present queued work or HTTP 202 receipts as completed AI results', async () => {
    const pending = parseAnalysis({ ...completed, processingStatus: 'QUEUED', result: null }, analysisId)
    expect(pending.result).toBeNull()
    fetchMock.mockResolvedValueOnce(json({ analysisId, status: 'QUEUED' }, 202))
    await expect(getAnalysisResult(analysisId, context)).rejects.toMatchObject({ code: 'ERR_INVALID_RESPONSE' })
  })

  it('rejects substituted IDs, fabricated risks and fake result fields', () => {
    expect(() => parseAnalysis({ ...completed, analysisId: batchId }, analysisId)).toThrow(AnalysisResultError)
    expect(() => parseAnalysis({ ...completed, result: { ...completed.result, riskScore: 0.8 } }, analysisId)).toThrow(AnalysisResultError)
    expect(() => parseAnalysis({ ...completed, result: { ...completed.result, indicators: ['invented'] } }, analysisId)).toThrow(AnalysisResultError)
    expect(() => parseAnalysis({ ...completed, processingStatus: 'FAILED' }, analysisId)).toThrow(AnalysisResultError)
  })

  it.each([401, 403, 404])('fails closed for HTTP %i', async (status) => {
    fetchMock.mockResolvedValueOnce(json({ error: { code: 'ERR_FORBIDDEN' } }, status))
    await expect(getAnalysisResult(analysisId, context)).rejects.toMatchObject({ status })
  })

  it('rejects a switched account before sending an HTTP request', async () => {
    user.uid = 'other-user'
    await expect(getAnalysisResult(analysisId, context)).rejects.toMatchObject({ status: 401 })
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('never fetches the real API while mock mode is enabled', async () => {
    mocks.mock = true
    await expect(getAnalysisResult(analysisId, context)).rejects.toMatchObject({ code: 'ERR_MOCK_ENABLED' })
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('gets an authenticated same-origin content route without putting the token in its URL', async () => {
    const url = `/api/v1/analyses/${analysisId}/files/${fileId}/content`
    fetchMock.mockResolvedValueOnce(json({ url, expiresAt: new Date(Date.now() + 60_000).toISOString() }))
    const access = await getPrivateFileAccess(analysisId, fileId, context)
    expect(access.url).toBe(url)
    expect(fetchMock.mock.calls[0][0]).toBe(`/api/v1/analyses/${analysisId}/files/${fileId}/access`)
    expect(fetchMock.mock.calls[0][1]?.method).toBe('GET')
    expect(JSON.stringify(access)).not.toContain('firebase-token')
  })

  it('fetches content bytes with a fresh bearer and fails after permission is revoked', async () => {
    const url = `/api/v1/analyses/${analysisId}/files/${fileId}/content`
    fetchMock.mockResolvedValueOnce(new Response(new Uint8Array([1, 2, 3]), { headers: { 'Content-Type': 'image/png' } }))
    const blob = await getProtectedContent(url, context)
    expect(blob.type).toBe('image/png')
    expect(new Headers(fetchMock.mock.calls[0][1]?.headers).get('Authorization')).toBe('Bearer firebase-token')
    fetchMock.mockResolvedValueOnce(json({ error: { code: 'ERR_FORBIDDEN' } }, 403))
    await expect(getProtectedContent(url, context)).rejects.toMatchObject({ status: 403 })
  })

  it('never sends a Firebase bearer to external storage or a substituted file content route', async () => {
    await expect(getProtectedContent('https://outside.example/report', context)).rejects.toMatchObject({ code: 'ERR_INVALID_RESPONSE' })
    expect(fetchMock).not.toHaveBeenCalled()
    fetchMock.mockResolvedValueOnce(json({ url: `/api/v1/analyses/${batchId}/files/${fileId}/content`, expiresAt: new Date(Date.now() + 60_000).toISOString() }))
    await expect(getPrivateFileAccess(analysisId, fileId, context)).rejects.toMatchObject({ code: 'ERR_INVALID_RESPONSE' })
  })

  it('rejects expired, malformed or non-http private file URLs', async () => {
    for (const url of ['javascript:alert(1)', 'not-a-url', 'https://private-storage.example.test/file']) {
      const expiresAt = new Date(Date.now() + (url === 'https://private-storage.example.test/file' ? -1_000 : 60_000)).toISOString()
      fetchMock.mockResolvedValueOnce(json({ url, expiresAt }))
      await expect(getPrivateFileAccess(analysisId, fileId, context)).rejects.toMatchObject({ code: 'ERR_INVALID_RESPONSE' })
    }
  })
})
