/// <reference types="node" />
import { webcrypto } from 'node:crypto'
import { beforeEach, afterEach, describe, it, expect, vi } from 'vitest'
const mocks = vi.hoisted(() => ({ auth: vi.fn(), mock: false }))
vi.mock('../firebase/config', () => ({ getFirebaseAuth: mocks.auth }))
vi.mock('./client', () => ({ get isMockEnabled() { return mocks.mock } }))
import { createUploadAttempt } from './analysisUpload'
import { createClinicItemUploadAttempt, sendItemUploadAttempt, resumeItemUpload, loadUploadInventory, type UploadInventoryItem } from './itemUpload'
import type { BackendContext } from './backendContext'

const batchId = '11111111-1111-4111-8111-000000000001'
const context: BackendContext = {
  accountId: '11111111-1111-4111-8111-000000000002', patientId: '11111111-1111-4111-8111-000000000003',
  personalTenantId: '11111111-1111-4111-8111-000000000004', firebaseUid: 'user-a', roles: ['PATIENT'], displayName: 'A', profileVersion: 1,
}
const user = { uid: context.firebaseUid, getIdToken: vi.fn() }, auth: { currentUser: typeof user | null } = { currentUser: user }
const fetchMock = vi.fn<typeof fetch>()
const json = (x: unknown, status = 200) => new Response(JSON.stringify(x), { status, headers: { 'Content-Type': 'application/json' } })
const image = (i: number) => new File([`pixels-${i}`], `retina-${i}.png`, { type: 'image/png' })
const validUuid = (x: string) => /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(x)
let items: UploadInventoryItem[], active: number, peak: number, finalized: boolean
const snapshot = () => ({ batchId, createdAt: '2026-11-04T00:00:00Z', expiresAt: '2026-11-05T00:00:00Z', statusUrl: `/api/v1/batches/${batchId}`, items })
const receipt = () => ({ batchId, createdAt: snapshot().createdAt, statusUrl: snapshot().statusUrl,
  items: items.map(x => ({ analysisId: x.itemId, fileName: x.fileName, processingStatus: x.uploadStatus === 'REJECTED' ? 'REJECTED' : 'QUEUED',
    ...(x.uploadStatus === 'REJECTED' ? { error: { code: 'ERR_VALIDATION', message: 'Invalid image', retryable: false } } : {}) })) })

async function server(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const path = String(input).split('?')[0]
  const headers = new Headers(init?.headers)
  expect(headers.get('Authorization')).toBe('Bearer user-token')
  if (init?.method !== 'GET') expect(validUuid(headers.get('Idempotency-Key') ?? '')).toBe(true)
  if (init?.method === 'POST' && path.endsWith('/analysis-upload-batches')) {
    items = JSON.parse(String(init.body)).items.map((x: UploadInventoryItem) => ({ ...x, uploadStatus: 'DECLARED' }))
    return json(snapshot(), 201)
  }
  if (init?.method === 'PUT') {
    active++; peak = Math.max(active, peak)
    const body = init.body as FormData
    expect([...body.keys()]).toEqual(['image'])
    expect((body.get('image') as File).size).toBeLessThanOrEqual(10 * 1024 * 1024)
    const itemId = path.split('/').at(-1)!
    expect(headers.get('Idempotency-Key')).toBe(itemId)
    await new Promise(resolve => setTimeout(resolve, 2))
    items = items.map(x => x.itemId === itemId ? { ...x, uploadStatus: 'STAGED' } : x)
    active--
    return json(items.find(x => x.itemId === itemId))
  }
  if (path.endsWith('/finalize')) { finalized = true; return json(receipt(), 202) }
  return json({ ...snapshot(), ...(finalized ? { finalizedResponse: receipt() } : {}) })
}
beforeEach(() => {
  vi.resetAllMocks(); items = []; active = peak = 0; finalized = false
  auth.currentUser = user; user.getIdToken.mockResolvedValue('user-token'); mocks.auth.mockReturnValue(auth); mocks.mock = false
  vi.stubGlobal('crypto', webcrypto); vi.stubGlobal('fetch', fetchMock); fetchMock.mockImplementation(server)
})
afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks() })

describe('Versioned staged upload against one persisted batch', () => {
  it('uploads100images as one parent batch, one file per request, at most4in flight', async () => {
    const files = Array.from({ length: 100 }, (_, i) => image(i))
    const result = await sendItemUploadAttempt(createUploadAttempt(context, files, 'left'))
    expect(result.batchId).toBe(batchId); expect(result.items).toHaveLength(100)
    expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'PUT')).toHaveLength(100)
    expect(fetchMock.mock.calls.filter(([url, init]) => init?.method === 'POST' && String(url).endsWith('/analysis-upload-batches'))).toHaveLength(1)
    expect(peak).toBe(4)
    expect(items.every(x => /^[a-f0-9]{64}$/.test(x.sha256))).toBe(true)
    expect(new Set(items.map(x => x.itemId)).size).toBe(100)
  })
  it('clamps requested concurrency to6 without exceeding a single-image body', async () => {
    await sendItemUploadAttempt(createUploadAttempt(context, Array.from({ length: 12 }, (_, i) => image(i)), 'left'), { concurrency: 99 })
    expect(peak).toBe(6)
  })
  it('binds one clinic selector while preserving different patient/eye metadata per item', async () => {
    const clinicId = '11111111-1111-4111-8111-000000009999'
    const otherPatient = '11111111-1111-4111-8111-000000008888'
    const attempt = createClinicItemUploadAttempt(context, clinicId, [
      { file: image(0), patientId: context.patientId, eyeSide: 'left' },
      { file: image(1), patientId: otherPatient, eyeSide: 'right' },
    ])
    await sendItemUploadAttempt(attempt)
    expect(items.map(x => [x.patientId, x.eyeSide])).toEqual([[context.patientId, 'left'], [otherPatient, 'right']])
    expect(fetchMock.mock.calls.every(([url]) => String(url).endsWith(`?clinicContextId=${clinicId}`))).toBe(true)
  })
  it('reconciles a lost PUT response and skips its already-staged bytes', async () => {
    let lost = false
    fetchMock.mockImplementation(async (url, init) => {
      const response = await server(url, init)
      if (init?.method === 'PUT' && !lost) { lost = true; throw new TypeError('Response lost after commit') }
      return response
    })
    const result = await sendItemUploadAttempt(createUploadAttempt(context, [image(0)], 'left'))
    expect(result.items).toHaveLength(1)
    expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'PUT')).toHaveLength(1)
    expect(finalized).toBe(true)
  })
  it('waits for siblings, preserves inventory, and retry sends only DECLARED items', async () => {
    let failed = false
    fetchMock.mockImplementation(async (url, init) => {
      if (init?.method === 'PUT' && !failed) { failed = true; throw new TypeError('Connection lost before acceptance') }
      return server(url, init)
    })
    const attempt = createUploadAttempt(context, Array.from({ length: 8 }, (_, i) => image(i)), 'left')
    await expect(sendItemUploadAttempt(attempt)).rejects.toMatchObject({ code: 'ERR_NETWORK' })
    expect(active).toBe(0)
    const stagedIds = new Set(items.filter(x => x.uploadStatus === 'STAGED').map(x => x.itemId))
    const callCount = fetchMock.mock.calls.length
    await sendItemUploadAttempt(attempt)
    const retriedPuts = fetchMock.mock.calls.slice(callCount).filter(([, init]) => init?.method === 'PUT')
    expect(String(fetchMock.mock.calls[callCount][0])).toBe(`/api/v1/analysis-upload-batches/${batchId}`)
    expect(retriedPuts.every(([url]) => !stagedIds.has(String(url).split('/').at(-1)!))).toBe(true)
    expect(finalized).toBe(true)
  })
  it('F5 resume matches reselected bytes by checksum and does not replace accepted files', async () => {
    let first = true
    fetchMock.mockImplementation(async (url, init) => {
      if (init?.method === 'PUT' && first) { first = false; throw new TypeError('Offline') }
      return server(url, init)
    })
    const files = [image(0), image(1), image(2)]
    await expect(sendItemUploadAttempt(createUploadAttempt(context, files, 'left'), { concurrency: 1 })).rejects.toMatchObject({ code: 'ERR_NETWORK' })
    const result = await resumeItemUpload(context, batchId, [files[2], files[0], files[1]])
    expect(result.items).toHaveLength(3); expect(result.batchId).toBe(batchId)
    expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'PUT')).toHaveLength(4)
  })
  it('refuses F5 files with matching names but different bytes', async () => {
    fetchMock.mockImplementation(async (url, init) => init?.method === 'PUT' ? Promise.reject(new TypeError('Offline')) : server(url, init))
    await expect(sendItemUploadAttempt(createUploadAttempt(context, [image(0)], 'left'))).rejects.toMatchObject({ code: 'ERR_NETWORK' })
    const before = fetchMock.mock.calls.length
    await expect(resumeItemUpload(context, batchId, [new File(['different'], 'retina-0.png', { type: 'image/png' })]))
      .rejects.toMatchObject({ code: 'ERR_UPLOAD_FILES_REQUIRED' })
    expect(fetchMock.mock.calls.slice(before).filter(([, init]) => init?.method === 'PUT')).toHaveLength(0)
  })
  it('propagates partial rejected admission without losing the parent batch', async () => {
    fetchMock.mockImplementation(async (url, init) => {
      if (init?.method === 'PUT') {
        const id = String(url).split('/').at(-1)!
        items = items.map(x => x.itemId === id ? { ...x, uploadStatus: 'REJECTED' } : x)
        return json(items.find(x => x.itemId === id))
      }
      return server(url, init)
    })
    const result = await sendItemUploadAttempt(createUploadAttempt(context, [image(0)], 'left'))
    expect(result.items[0]).toMatchObject({ processingStatus: 'REJECTED' })
    expect(result.batchId).toBe(batchId)
  })
  it('validates size and eye-side before sending any manifest or PHI', async () => {
    await expect(sendItemUploadAttempt(createUploadAttempt(context, [new File([new Uint8Array(10 * 1024 * 1024 + 1)], 'big.png', { type: 'image/png' })], 'left')))
      .rejects.toMatchObject({ code: 'ERR_INVALID_UPLOAD_DRAFT' })
    await expect(sendItemUploadAttempt(createUploadAttempt(context, [image(0)], 'corrupted'))).rejects.toMatchObject({ code: 'ERR_INVALID_UPLOAD_DRAFT' })
    expect(fetchMock).not.toHaveBeenCalled()
  })
  it('never permits a different session to resume a batch using cached credentials', async () => {
    auth.currentUser = null
    await expect(loadUploadInventory(context, batchId)).rejects.toMatchObject({ code: 'ERR_SESSION_CHANGED' })
    expect(fetchMock).not.toHaveBeenCalled()
  })
  it('stops an expired inventory before uploading or finalizing anything', async () => {
    fetchMock.mockImplementation(async (url, init) => {
      const response = await server(url, init)
      const value = await response.json()
      return json({ ...value, status: 'EXPIRED' }, response.status)
    })
    await expect(sendItemUploadAttempt(createUploadAttempt(context, [image(0)], 'left')))
      .rejects.toMatchObject({ code: 'ERR_UPLOAD_EXPIRED', status: 410, canRetry: false, disposition: 'rejected' })
    expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'PUT')).toHaveLength(0)
    expect(fetchMock.mock.calls.filter(([url]) => String(url).endsWith('/finalize'))).toHaveLength(0)
  })
})
