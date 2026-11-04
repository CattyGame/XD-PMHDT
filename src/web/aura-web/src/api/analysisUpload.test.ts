import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({ auth: vi.fn(), mock: false }))
vi.mock('../firebase/config', () => ({ getFirebaseAuth: mocks.auth }))
vi.mock('./client', () => ({ get isMockEnabled() { return mocks.mock } }))
import { createUploadAttempt, sendUploadAttempt, UploadRequestError } from './analysisUpload'
import type { BackendContext } from './backendContext'

const context: BackendContext = {
  firebaseUid: 'user-a', accountId: '10000000-0000-0000-0000-000000000001', roles: ['PATIENT'],
  patientId: '20000000-0000-0000-0000-000000000002',
  personalTenantId: '30000000-0000-0000-0000-000000000003', displayName: 'Người A', profileVersion: 1,
}
const batchId = '40000000-0000-0000-0000-000000000004'
const analysisId = '50000000-0000-0000-0000-000000000005'
const accepted = {
  batchId, items: [{ analysisId, fileName: 'retina.png', processingStatus: 'QUEUED' }],
  createdAt: '2026-10-24T03:00:00Z', statusUrl: `/api/v1/batches/${batchId}`,
}
const user = { uid: context.firebaseUid, getIdToken: vi.fn() }
const auth: { currentUser: typeof user | null } = { currentUser: user }
const fetchMock = vi.fn<typeof fetch>()
const image = new File(['immutable-image-bytes'], 'retina.png', { type: 'image/png' })
const attempt = () => createUploadAttempt(context, [image], 'left')
function json(payload: unknown, status = 202, headers = {}) {
  return new Response(JSON.stringify(payload), { status, headers: { 'Content-Type': 'application/json', ...headers } })
}
beforeEach(() => {
  vi.resetAllMocks()
  mocks.mock = false
  auth.currentUser = user
  mocks.auth.mockReturnValue(auth)
  user.getIdToken.mockResolvedValue('fresh-token')
  vi.stubGlobal('fetch', fetchMock)
})
afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks() })

describe('Analysis upload contract and immutable attempt', () => {
  it('freezes the key and file order and sends only personal metadata with a user bearer', async () => {
    const files = [image]
    const request = createUploadAttempt(context, files, 'left')
    files.push(new File(['later'], 'later.png'))
    fetchMock.mockResolvedValueOnce(json(accepted))
    const result = await sendUploadAttempt(request)
    expect(result).toMatchObject({ batchId, replayed: false })
    expect(request.images).toEqual([image])
    expect(Object.isFrozen(request)).toBe(true)
    expect(Object.isFrozen(request.images)).toBe(true)
    const [url, options] = fetchMock.mock.calls[0]
    expect(url).toBe('/api/v1/analyses')
    expect(options).toMatchObject({ method: 'POST', cache: 'no-store', credentials: 'omit', redirect: 'error' })
    expect(options?.headers).toEqual({ Authorization: 'Bearer fresh-token', 'Idempotency-Key': request.key })
    const body = options?.body as FormData
    expect(Array.from(body.keys())).toEqual(['patientId', 'modality', 'eyeSide', 'images'])
    expect(body.get('patientId')).toBe(context.patientId)
    expect(body.get('modality')).toBe('FUNDUS')
    expect(body.get('eyeSide')).toBe('left')
    expect(body.get('images')).toBe(image)
    expect(JSON.stringify(result)).not.toContain('fresh-token')
  })

  it('rebuilds the same business payload and key while refreshing the Firebase token on replay', async () => {
    const request = attempt()
    fetchMock.mockResolvedValueOnce(json(accepted)).mockResolvedValueOnce(json(accepted, 202, { 'Idempotent-Replayed': 'true' }))
    await sendUploadAttempt(request)
    user.getIdToken.mockResolvedValue('next-token')
    expect((await sendUploadAttempt(request)).replayed).toBe(true)
    const first = fetchMock.mock.calls[0][1]
    const second = fetchMock.mock.calls[1][1]
    expect(new Headers(first?.headers).get('Idempotency-Key')).toBe(new Headers(second?.headers).get('Idempotency-Key'))
    expect(new Headers(second?.headers).get('Authorization')).toBe('Bearer next-token')
    const firstBody = first?.body as FormData
    const secondBody = second?.body as FormData
    expect(Array.from(firstBody.entries())).toEqual(Array.from(secondBody.entries()))
  })

  it.each([
    { ...context, roles: ['SYSTEM_ADMIN', 'PATIENT'] },
    { ...context, roles: ['DOCTOR'] },
    { ...context, patientId: 'firebase-uid-not-patient-id' },
  ])('rejects an unsupported personal context without HTTP', (value) => {
    expect(() => createUploadAttempt(value as BackendContext, [image], 'left')).toThrow(UploadRequestError)
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('blocks mock mode without retrieving a bearer or sending HTTP', async () => {
    mocks.mock = true
    await expect(sendUploadAttempt(attempt())).rejects.toMatchObject({ code: 'ERR_MOCK_ENABLED', disposition: 'not-sent' })
    expect(user.getIdToken).not.toHaveBeenCalled()
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('requires the attempt owner to remain the authenticated Firebase user', async () => {
    auth.currentUser = { ...user, uid: 'other-user' }
    await expect(sendUploadAttempt(attempt())).rejects.toMatchObject({ status: 401, disposition: 'not-sent' })
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('does not send HTTP after logout while retrieving a token', async () => {
    user.getIdToken.mockImplementation(async () => { auth.currentUser = null; return 'old-token' })
    await expect(sendUploadAttempt(attempt())).rejects.toMatchObject({ code: 'ERR_SESSION_CHANGED', disposition: 'not-sent' })
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('sanitizes token retrieval failures as not sent', async () => {
    user.getIdToken.mockRejectedValue(new Error('private-provider-details'))
    const failure = await sendUploadAttempt(attempt()).catch((e) => e)
    expect(failure).toMatchObject({ code: 'ERR_TOKEN_UNAVAILABLE', disposition: 'not-sent' })
    expect(failure.message).not.toContain('private-provider-details')
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('marks a lost HTTP response as uncertain and never retries automatically', async () => {
    fetchMock.mockRejectedValue(new TypeError('private-network-details'))
    await expect(sendUploadAttempt(attempt())).rejects.toMatchObject({ code: 'ERR_NETWORK', disposition: 'uncertain', canRetry: true })
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it.each([
    [400, 'ERR_VALIDATION'], [401, 'ERR_UNAUTHORIZED'], [403, 'ERR_FORBIDDEN'],
    [404, 'ERR_NOT_FOUND'], [413, 'ERR_PAYLOAD_TOO_LARGE'], [415, 'ERR_UNSUPPORTED_MEDIA_TYPE'],
  ])('recognizes a verified non-retryable rejection %i/%s', async (status, code) => {
    fetchMock.mockResolvedValueOnce(json({ error: { code, retryable: false, correlationId: 'request.123' } }, status))
    await expect(sendUploadAttempt(attempt())).rejects.toMatchObject({ status, code, disposition: 'rejected', canRetry: false, correlationId: 'request.123' })
  })

  it.each([
    [402, 'ERR_INSUFFICIENT_CREDIT'], [409, 'ERR_REQUEST_IN_PROGRESS'],
    [429, 'ERR_TOO_MANY_REQUESTS'], [500, 'ERR_DEPENDENCY'], [502, 'ERR_DEPENDENCY'], [503, 'ERR_DEPENDENCY'],
  ])('retains the attempt for %i/%s instead of permitting a new key', async (status, code) => {
    fetchMock.mockResolvedValueOnce(json({ error: { code, retryable: true } }, status))
    await expect(sendUploadAttempt(attempt())).rejects.toMatchObject({ status, code, disposition: 'uncertain', canRetry: true })
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it('blocks reused-key conflict without silently choosing a different key', async () => {
    fetchMock.mockResolvedValueOnce(json({ error: { code: 'ERR_IDEMPOTENCY_KEY_REUSED', retryable: false } }, 409))
    await expect(sendUploadAttempt(attempt())).rejects.toMatchObject({ disposition: 'conflict', canRetry: false })
  })

  it('does not treat a malformed 400 error as evidence that no work happened', async () => {
    fetchMock.mockResolvedValueOnce(new Response('gateway-html', { status: 400 }))
    await expect(sendUploadAttempt(attempt())).rejects.toMatchObject({ disposition: 'uncertain' })
  })

  it.each([
    null, {}, { ...accepted, batchId: 'not-a-uuid' }, { ...accepted, items: [] },
    { ...accepted, createdAt: 'not-a-date' },
    { ...accepted, statusUrl: 'https://external.example/token-leak' },
    { ...accepted, items: [{ ...accepted.items[0], analysisId: 'not-a-uuid' }] },
    { ...accepted, items: [{ ...accepted.items[0], processingStatus: 'COMPLETED' }] },
    { ...accepted, items: [{ ...accepted.items[0], processingStatus: 'DUPLICATE' }] },
    { ...accepted, items: [{ ...accepted.items[0], error: { code: 'ERR_VALIDATION' } }] },
  ])('treats an invalid 202 body as uncertain rather than successful: %j', async (payload) => {
    fetchMock.mockResolvedValueOnce(json(payload))
    await expect(sendUploadAttempt(attempt())).rejects.toMatchObject({ code: 'ERR_INVALID_RESPONSE', disposition: 'uncertain' })
  })

    it('accepts QUARANTINE receipt for corrupted payload isolation demo', async () => {
    fetchMock.mockResolvedValueOnce(json({ ...accepted, items: [{
      ...accepted.items[0], processingStatus: 'QUARANTINE',
      error: { code: 'ERR_CORRUPTED_PAYLOAD', message: 'Dữ liệu ảnh không hợp lệ', retryable: false },
    }] }))
    const result = await sendUploadAttempt(attempt())
    expect(result.items[0]).toMatchObject({ processingStatus: 'QUARANTINE' })
  })

it('accepts DUPLICATE as a reference without inventing a new AI result', async () => {
    fetchMock.mockResolvedValueOnce(json({ ...accepted, items: [{
      ...accepted.items[0], processingStatus: 'DUPLICATE', existingAnalysisId: context.accountId,
      error: { code: 'ERR_DUPLICATE_REQUEST', message: 'Ảnh trùng', retryable: false },
      riskLevel: 'HIGH',
    }] }))
    const result = await sendUploadAttempt(attempt())
    expect(result.items[0]).toMatchObject({ processingStatus: 'DUPLICATE', existingAnalysisId: context.accountId })
    expect('riskLevel' in result.items[0]).toBe(false)
  })

  it('requires 202 even when another 2xx response has a valid-looking body', async () => {
    fetchMock.mockResolvedValueOnce(json(accepted, 200))
    await expect(sendUploadAttempt(attempt())).rejects.toMatchObject({ status: 200, disposition: 'uncertain' })
  })

  it('drops a response received after the Firebase user changes', async () => {
    fetchMock.mockImplementationOnce(async () => { auth.currentUser = null; return json(accepted) })
    await expect(sendUploadAttempt(attempt())).rejects.toMatchObject({ code: 'ERR_SESSION_CHANGED', disposition: 'uncertain' })
  })

  it('bounds a pending HTTP request with the deadline signal', async () => {
    const deadline = new AbortController()
    vi.spyOn(AbortSignal, 'timeout').mockReturnValue(deadline.signal)
    fetchMock.mockReturnValue(new Promise(() => {}))
    const pending = sendUploadAttempt(attempt())
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1))
    const check = expect(pending).rejects.toMatchObject({ code: 'ERR_TIMEOUT', disposition: 'uncertain' })
    deadline.abort(new DOMException('timeout', 'TimeoutError'))
    await check
  })

  it('bounds token retrieval too and does not send HTTP after its deadline', async () => {
    const deadline = new AbortController()
    vi.spyOn(AbortSignal, 'timeout').mockReturnValue(deadline.signal)
    user.getIdToken.mockReturnValue(new Promise(() => {}))
    const pending = sendUploadAttempt(attempt())
    const check = expect(pending).rejects.toMatchObject({ code: 'ERR_TIMEOUT', disposition: 'not-sent' })
    deadline.abort(new DOMException('timeout', 'TimeoutError'))
    await check
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('ADR-005 D5: sends all batch images under single atomic request with authoritative UUID key', async () => {
    const files = [
      new File(['img0'], 'img0.png', { type: 'image/png' }),
      new File(['img1'], 'img1.png', { type: 'image/png' }),
      new File(['img2'], 'img2.png', { type: 'image/png' }),
    ]
    const batchAttempt = createUploadAttempt(context, files, 'left')
    fetchMock.mockResolvedValueOnce(json({
      batchId,
      items: [
        { analysisId: '50000000-0000-0000-0000-000000000001', fileName: 'img0.png', processingStatus: 'QUEUED' },
        { analysisId: '50000000-0000-0000-0000-000000000002', fileName: 'img1.png', processingStatus: 'QUEUED' },
        { analysisId: '50000000-0000-0000-0000-000000000003', fileName: 'img2.png', processingStatus: 'QUEUED' },
      ],
      createdAt: '2026-10-24T03:00:00Z',
      statusUrl: `/api/v1/batches/${batchId}`,
    }))

    const result = await sendUploadAttempt(batchAttempt)
    expect(fetchMock).toHaveBeenCalledTimes(1)
    expect(result.batchId).toBe(batchId)
    expect(result.items).toHaveLength(3)
    expect(result.items[0].fileName).toBe('img0.png')
    expect(result.items[1].fileName).toBe('img1.png')
    expect(result.items[2].fileName).toBe('img2.png')

    const [, opts] = fetchMock.mock.calls[0]
    const body = opts?.body as FormData
    expect(body.getAll('images')).toHaveLength(3)
    expect(opts?.headers).toMatchObject({ 'Idempotency-Key': batchAttempt.key })
    // Verify Idempotency-Key is a valid canonical RFC-4122 UUID (not UUID-suffix)
    const headers = opts?.headers as Record<string, string> | undefined
    expect(headers?.['Idempotency-Key']).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i)
  })
})
