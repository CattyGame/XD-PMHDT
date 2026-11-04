import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { BackendContext } from './backendContext'
import { getBatchProgress, parseBatchProgress, BatchProgressError } from './batchProgress'

const mocks = vi.hoisted(() => ({ auth: vi.fn(), mock: false }))
vi.mock('../firebase/config', () => ({ getFirebaseAuth: mocks.auth }))
vi.mock('./client', () => ({ get isMockEnabled() { return mocks.mock } }))

const batchId = '40000000-0000-4000-8000-000000000004'
const context: BackendContext = {
  firebaseUid: 'user-a', accountId: '10000000-0000-4000-8000-000000000001',
  patientId: '20000000-0000-4000-8000-000000000002',
  personalTenantId: '30000000-0000-4000-8000-000000000003',
  roles: ['PATIENT'], displayName: 'Patient A', profileVersion: 1,
}
const snapshot = {
  batchId, batchStatus: 'COMPLETED', createdAt: '2026-10-27T03:00:00Z',
  items: [{ analysisId: '50000000-0000-4000-8000-000000000005', fileName: 'fundus.png',
    processingStatus: 'COMPLETED', reviewStatus: 'PENDING', riskLevel: 'MODERATE', job: null, error: null }],
  summary: { total: 1, active: 0, succeeded: 1, failed: 0, duplicate: 0 },
}
const user = { uid: 'user-a', getIdToken: vi.fn(async () => 'firebase-token') }
const auth: { currentUser: typeof user | null } = { currentUser: user }
const fetchMock = vi.fn<typeof fetch>()
function json(value: unknown, status = 200): Response {
  return new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } })
}
beforeEach(() => {
  vi.resetAllMocks()
  auth.currentUser = user
  user.uid = 'user-a'
  mocks.auth.mockReturnValue(auth)
  mocks.mock = false
  user.getIdToken.mockResolvedValue('firebase-token')
  fetchMock.mockResolvedValue(json(snapshot))
  vi.stubGlobal('fetch', fetchMock)
})
afterEach(() => { vi.unstubAllGlobals() })

describe('Analysis batch GET client — SCRUM-332', () => {
  it('keeps admission-rejected items outside the processing denominator', () => {
    const result = parseBatchProgress({ ...snapshot, totalDeclared: 3, admissionRejected: 1, uploadPending: 1 }, batchId)
    expect(result.summary.total).toBe(1)
    expect(result).toMatchObject({ totalDeclared: 3, admissionRejected: 1, uploadPending: 1 })
  })
  it('allows no accepted items during staging or an entirely rejected admission', () => {
    const empty = { ...snapshot, batchStatus: 'IN_PROGRESS', items: [],
      summary: { total: 0, active: 0, succeeded: 0, failed: 0, duplicate: 0 }, totalDeclared: 2, admissionRejected: 0, uploadPending: 2 }
    expect(parseBatchProgress(empty, batchId).items).toEqual([])
    expect(parseBatchProgress({ ...empty, batchStatus: 'FAILED', admissionRejected: 2, uploadPending: 0 }, batchId).summary.total).toBe(0)
  })
  it('rejects a declared count inconsistent with admission plus processing inventory', () => {
    expect(() => parseBatchProgress({ ...snapshot, totalDeclared: 100, admissionRejected: 0, uploadPending: 0 }, batchId)).toThrow(BatchProgressError)
  })
  it('uses only authenticated GET, no POST, and reads a completed batch from server', async () => {
    const result = await getBatchProgress(batchId, context)
    expect(result).toMatchObject({ kind: 'batch', data: { batchStatus: 'COMPLETED' } })
    expect(fetchMock).toHaveBeenCalledTimes(1)
    expect(fetchMock.mock.calls[0][0]).toBe(`/api/v1/batches/${batchId}`)
    expect(fetchMock.mock.calls[0][1]).toMatchObject({ method: 'GET', cache: 'no-store', credentials: 'omit', redirect: 'error' })
    expect(new Headers(fetchMock.mock.calls[0][1]?.headers).get('Authorization')).toBe('Bearer firebase-token')
    expect(new Headers(fetchMock.mock.calls[0][1]?.headers).has('Idempotency-Key')).toBe(false)
    expect(fetchMock.mock.calls[0][1]?.body).toBeUndefined()
  })

  it('does not treat an HTTP 202 receipt as a completed AI result', async () => {
    fetchMock.mockResolvedValue(json({ batchId, statusUrl: `/api/v1/batches/${batchId}` }, 202))
    expect(await getBatchProgress(batchId, context)).toEqual({ kind: 'accepted' })
  })

  it('rejects malformed or foreign batch payloads without showing a result', () => {
    expect(() => parseBatchProgress({ ...snapshot, batchId: context.accountId }, batchId)).toThrow(BatchProgressError)
    expect(() => parseBatchProgress({ ...snapshot, batchStatus: 'RECEIVED' }, batchId)).toThrow(BatchProgressError)
    expect(() => parseBatchProgress({ ...snapshot, batchStatus: 'COMPLETED', summary: { ...snapshot.summary, active: 1 } }, batchId)).toThrow(BatchProgressError)
  })

  it.each([[401, 'ERR_UNAUTHORIZED'], [403, 'ERR_FORBIDDEN'], [404, 'ERR_NOT_FOUND']])(
    'fails closed on HTTP %s (%s)', async (status, code) => {
      fetchMock.mockResolvedValue(json({ error: { code, retryable: false } }, status as number))
      await expect(getBatchProgress(batchId, context)).rejects.toMatchObject({ status, code, retryable: false })
    },
  )

  it('preserves transient GET failures as retryable, without repeating uploads', async () => {
    fetchMock.mockResolvedValue(json({ error: { code: 'ERR_NETWORK', retryable: true } }, 503))
    await expect(getBatchProgress(batchId, context)).rejects.toMatchObject({ status: 503, retryable: true })
    expect(fetchMock).toHaveBeenCalledTimes(1)
    expect(fetchMock.mock.calls[0][1]?.method).toBe('GET')
  })

  it('rejects a switched Firebase session before making a GET', async () => {
    user.uid = 'another-user'
    await expect(getBatchProgress(batchId, context)).rejects.toMatchObject({ status: 401 })
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('honors abort on navigation, including a pending GET', async () => {
    const controller = new AbortController()
    fetchMock.mockImplementation((_url, init) => new Promise<Response>((_resolve, reject) => {
      init?.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')), { once: true })
    }))
    const request = getBatchProgress(batchId, context, controller.signal)
    await Promise.resolve()
    controller.abort()
    await expect(request).rejects.toBeTruthy()
    expect(fetchMock.mock.calls.every((call) => call[1]?.method === 'GET')).toBe(true)
  })
})
