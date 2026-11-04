import { beforeEach, afterEach, it, expect, vi } from 'vitest'
const m = vi.hoisted(() => ({ auth: vi.fn() }))
vi.mock('../firebase/config', () => ({ getFirebaseAuth: m.auth }))
vi.mock('./client', () => ({ isMockEnabled: false }))
import { downloadReport } from './reportDownload'
import type { BackendContext } from './backendContext'
const id = '11111111-1111-4111-8111-000000000001'
const context: BackendContext = { accountId: id, patientId: id, personalTenantId: id, firebaseUid: 'user-a', roles: ['PATIENT'], displayName: 'A', profileVersion: 1 }
const fetchMock = vi.fn<typeof fetch>()
const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } })
const metadata = { reportId: id, format: 'pdf', downloadUrl: `/api/v1/reports/${id}/content`, expiresAt: new Date(Date.now() + 60000).toISOString() }
beforeEach(() => {
  vi.resetAllMocks(); m.auth.mockReturnValue({ currentUser: { uid: context.firebaseUid, getIdToken: async () => 'new-bearer' } }); vi.stubGlobal('fetch', fetchMock)
})
afterEach(() => vi.unstubAllGlobals())
it('uses bearer on report creation and content GET without a public URL or browser token query', async () => {
  fetchMock.mockResolvedValueOnce(json(metadata, 201)).mockResolvedValueOnce(new Response('%PDF-probe', { headers: { 'Content-Type': 'application/pdf' } }))
  const result = await downloadReport('analyses', id, 'pdf', context)
  expect(result.blob.type).toBe('application/pdf')
  expect(fetchMock.mock.calls.map(([, options]) => new Headers(options?.headers).get('Authorization'))).toEqual(['Bearer new-bearer', 'Bearer new-bearer'])
  expect(fetchMock.mock.calls[1][0]).toBe(metadata.downloadUrl)
  expect(result.fileName).toBe(`aura-${id}.pdf`)
})
it('does not return bytes after request-time authorization is revoked', async () => {
  fetchMock.mockResolvedValueOnce(json(metadata, 201)).mockResolvedValueOnce(json({ error: { code: 'ERR_FORBIDDEN' } }, 403))
  await expect(downloadReport('analyses', id, 'pdf', context)).rejects.toMatchObject({ status: 403 })
})
it('rejects a signed/external download URL before transmitting bearer credentials to it', async () => {
  fetchMock.mockResolvedValueOnce(json({ ...metadata, downloadUrl: 'https://storage.example/secret.pdf' }, 201))
  await expect(downloadReport('analyses', id, 'pdf', context)).rejects.toMatchObject({ code: 'ERR_INVALID_RESPONSE' })
  expect(fetchMock).toHaveBeenCalledTimes(1)
})
it('checks batch CSV MIME and preserves the clinic selector on authenticated requests', async () => {
  const scoped = { ...context, clinicContextId: id }
  fetchMock.mockResolvedValueOnce(json({ ...metadata, format: 'csv', downloadUrl: `${metadata.downloadUrl}?clinicContextId=${id}` }, 201))
    .mockResolvedValueOnce(new Response('case,status', { headers: { 'Content-Type': 'text/csv; charset=utf-8' } }))
  const result = await downloadReport('batches', id, 'csv', scoped)
  expect(fetchMock.mock.calls.every(([url]) => String(url).includes(`clinicContextId=${id}`))).toBe(true)
  expect(result.fileName.endsWith('.csv')).toBe(true)
})
