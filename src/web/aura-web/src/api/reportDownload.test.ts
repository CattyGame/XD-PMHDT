import { beforeEach, afterEach, it, expect, vi } from 'vitest'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
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
    .mockResolvedValueOnce(new Response('\ufeffrecordType,caseCode,processingStatus\r\n', { headers: { 'Content-Type': 'text/csv; charset=utf-8' } }))
  const result = await downloadReport('batches', id, 'csv', scoped)
  expect(fetchMock.mock.calls.every(([url]) => String(url).includes(`clinicContextId=${id}`))).toBe(true)
  expect(result.fileName.endsWith('.csv')).toBe(true)
})
it('returns a ZIP only for a batch and requires the backend ZIP media type', async () => {
  fetchMock.mockResolvedValueOnce(json({ ...metadata, format: 'zip' }, 201))
    .mockResolvedValueOnce(new Response('PK\u0003\u0004fixture', { headers: { 'Content-Type': 'application/zip' } }))
  await expect(downloadReport('batches', id, 'zip', context)).resolves.toMatchObject({ fileName: `aura-${id}.zip` })
  await expect(downloadReport('analyses', id, 'zip', context)).rejects.toMatchObject({ code: 'ERR_INVALID_REPORT' })
  fetchMock.mockResolvedValueOnce(json({ ...metadata, format: 'zip' }, 201))
    .mockResolvedValueOnce(new Response('error page', { headers: { 'Content-Type': 'text/html' } }))
  await expect(downloadReport('batches', id, 'zip', context)).rejects.toMatchObject({ code: 'ERR_INVALID_RESPONSE' })
})
it.each([401, 403, 404, 429, 500])('reports creation HTTP %s without starting a download or retrying', async status => {
  fetchMock.mockResolvedValueOnce(json({ error: { code: 'ERR_REPORT_UNAVAILABLE' } }, status))
  await expect(downloadReport('analyses', id, 'pdf', context)).rejects.toMatchObject({ status })
  expect(fetchMock).toHaveBeenCalledTimes(1)
})
it('rejects a response claiming to be a PDF when the bytes do not have a PDF header', async () => {
  fetchMock.mockResolvedValueOnce(json(metadata, 201))
    .mockResolvedValueOnce(new Response('<html>Gateway error</html>', { headers: { 'Content-Type': 'application/pdf' } }))
  await expect(downloadReport('analyses', id, 'pdf', context)).rejects.toMatchObject({ code: 'ERR_INVALID_RESPONSE' })
})
it('rejects another report ID or clinic scope before requesting its bytes', async () => {
  const otherId = '11111111-1111-4111-8111-000000000099'
  fetchMock.mockResolvedValueOnce(json({ ...metadata, downloadUrl: `/api/v1/reports/${otherId}/content` }, 201))
    .mockResolvedValueOnce(json({ ...metadata, downloadUrl: `${metadata.downloadUrl}?clinicContextId=${otherId}` }, 201))
  await expect(downloadReport('analyses', id, 'pdf', context)).rejects.toMatchObject({ code: 'ERR_INVALID_RESPONSE' })
  await expect(downloadReport('analyses', id, 'pdf', { ...context, clinicContextId: id })).rejects.toMatchObject({ code: 'ERR_INVALID_RESPONSE' })
  expect(fetchMock.mock.calls.every(([, init]) => init?.method === 'POST')).toBe(true)
})
it.each([200, 404, 500])('uses the case-specific POST and protected GET over MSW (content HTTP %s)', async status => {
  vi.unstubAllGlobals()
  const caseId = '11111111-1111-4111-8111-000000000055'
  const casePath = `/api/v1/analyses/${caseId}/reports`
  const contentPath = `/api/v1/reports/${id}/content`
  const assertHeaders = (request: Request) => {
    expect(request.headers.get('Authorization')).toBe('Bearer new-bearer')
    expect(request.cache).toBe('no-store')
  }
  const server = setupServer(
    http.post(new URL(casePath, window.location.origin).href, async ({ request }) => {
      assertHeaders(request)
      expect(await request.json()).toEqual({ format: 'pdf' })
      return HttpResponse.json(metadata, { status: 201 })
    }),
    http.get(new URL(contentPath, window.location.origin).href, ({ request }) => {
      assertHeaders(request)
      return status === 200 ? new HttpResponse('%PDF-mock-for-case-55', { headers: { 'Content-Type': 'application/pdf' } })
        : HttpResponse.json({ error: { code: status === 404 ? 'ERR_NOT_FOUND' : 'ERR_DEPENDENCY' } }, { status })
    }),
  )
  server.listen({ onUnhandledRequest: 'error' })
  const interceptedFetch = globalThis.fetch
  vi.stubGlobal('fetch', (input: string, init: RequestInit) => interceptedFetch(new URL(input, window.location.origin), init))
  try {
    const download = downloadReport('analyses', caseId, 'pdf', context)
    if (status === 200) expect(await (await download).blob.text()).toBe('%PDF-mock-for-case-55')
    else await expect(download).rejects.toMatchObject({ status })
  } finally {
    vi.unstubAllGlobals()
    server.close()
  }
})
