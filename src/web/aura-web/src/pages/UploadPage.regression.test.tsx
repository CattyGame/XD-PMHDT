import { webcrypto } from 'node:crypto'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { afterAll, afterEach, beforeAll, beforeEach, expect, it, vi } from 'vitest'
import type { BackendContext } from '../api/backendContext'
import type { UploadInventoryItem } from '../api/itemUpload'
import { BackendSessionContext } from '../auth/backendSession'
import UploadPage from './UploadPage'

const context: BackendContext = {
  firebaseUid: 'upload-test', roles: ['PATIENT'], displayName: 'Synthetic profile', profileVersion: 1,
  accountId: '10000000-0000-4000-8000-000000000001',
  patientId: '10000000-0000-4000-8000-000000000002',
  personalTenantId: '10000000-0000-4000-8000-000000000003',
}
const batchId = '10000000-0000-4000-8000-000000000004'
const endpoint = '/api/v1/analysis-upload-batches'
const server = setupServer()
const user = { uid: 'upload-test', getIdToken: async () => 'synthetic-bearer' }
vi.mock('../firebase/config', () => ({ getFirebaseAuth: () => ({ currentUser: user }) }))
vi.mock('../api/client', () => ({ isMockEnabled: false }))

const releasePreview = vi.fn()
const OriginalUrl = URL
beforeAll(() => server.listen({ onUnhandledRequest: 'error' }))
beforeEach(() => {
  window.history.replaceState(null, '', '/upload')
  releasePreview.mockClear()
  vi.stubGlobal('crypto', webcrypto)
  vi.stubGlobal('URL', class extends OriginalUrl {
    static createObjectURL = (blob: File) => `blob:${blob.name}`
    static revokeObjectURL = releasePreview
  })
  vi.stubGlobal('createImageBitmap', async () => ({ width: 8, height: 8, close: vi.fn() }))
})
afterEach(() => {
  cleanup()
  server.resetHandlers()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
  window.history.replaceState(null, '', '/')
})
afterAll(() => server.close())

it.each([1, 100])('previews and stages %i images through the upload component', async count => {
  const append = vi.spyOn(FormData.prototype, 'append')
  const methods: string[] = []
  const staged = new Set<string>()
  let active = 0
  let peak = 0
  let items: UploadInventoryItem[] = []
  const receipt = { batchId, statusUrl: `/api/v1/batches/${batchId}`, createdAt: '2026-11-04T00:00:00Z' }
  const inventory = () => ({ ...receipt, items, status: 'UPLOADING' })
  server.use(
    http.post(`*${endpoint}`, async ({ request }) => {
      methods.push('manifest')
      expect(request.cache).toBe('no-store')
      const body = await request.json() as { items: UploadInventoryItem[] }
      expect(body.items).toHaveLength(count)
      expect(body.items[0]).toMatchObject({ patientId: context.patientId, fileName: 'one.jpg' })
      items = body.items.map(item => ({ ...item, uploadStatus: 'DECLARED' }))
      return HttpResponse.json(inventory(), { status: 201 })
    }),
    http.put(`*${endpoint}/${batchId}/items/:itemId`, async ({ request, params }) => {
      methods.push('image')
      expect(request.headers.get('Idempotency-Key')).toBe(params.itemId)
      const item = items.find(value => value.itemId === params.itemId)!
      active++
      peak = Math.max(peak, active)
      try {
        expect(request.headers.get('Content-Type')).toMatch(/^multipart\/form-data; boundary=/)
        const multipart = await request.text()
        expect(multipart.match(/name="image"/g)).toHaveLength(1)
        expect(multipart).toContain(item.fileName.endsWith('.jpg') ? 'image/jpeg' : 'image/png')
        expect(staged.has(item.itemId)).toBe(false)
        staged.add(item.itemId)
        const saved = { ...item, uploadStatus: 'STAGED' } as const
        items = items.map(value => value.itemId === saved.itemId ? saved : value)
        return HttpResponse.json(saved)
      } finally { active-- }
    }),
    http.get(`*${endpoint}/${batchId}`, () => {
      methods.push('inventory')
      return HttpResponse.json(inventory())
    }),
    http.post(`*${endpoint}/${batchId}/finalize`, () => {
      methods.push('finalize')
      return HttpResponse.json({
        ...receipt,
        items: items.map(item => ({ analysisId: item.itemId, fileName: item.fileName, processingStatus: 'QUEUED' })),
      }, { status: 202 })
    }),
  )
  const view = render(
    <BackendSessionContext.Provider value={{ state: { status: 'ready', context: { ...context } }, logout: vi.fn(), retry: vi.fn() }}>
      <UploadPage />
    </BackendSessionContext.Provider>,
  )
  expect(screen.getByRole('heading', { name: /Upload ảnh võng mạc/ })).toBeTruthy()
  const input = screen.getByLabelText('Chọn ảnh võng mạc')
  const jpeg = new File(['synthetic'], 'one.jpg', { type: 'image/jpeg' })
  if (count === 1) {
    fireEvent.change(input, { target: { files: [new File(['invalid'], 'bad.gif', { type: 'image/gif' })] } })
    expect((await screen.findByRole('alert')).textContent).toMatch(/chỉ hỗ trợ.*jpg và png/i)
    const oversized = new File([new Uint8Array(10 * 1024 * 1024 + 1)], 'large.png', { type: 'image/png' })
    fireEvent.change(input, { target: { files: [oversized] } })
    expect((await screen.findByRole('alert')).textContent).toMatch(/vượt quá 10 MB/)
    expect(screen.queryByRole('img')).toBeNull()
    fireEvent.change(input, { target: { files: [jpeg] } })
  } else {
    const pngs = Array.from({ length: count - 1 }, (_, index) =>
      new File([`synthetic-${index}`], `image-${index}.png`, { type: 'image/png' }))
    fireEvent.drop(input, { dataTransfer: { files: [jpeg, ...pngs] } })
  }
  await screen.findByText(`Đã chọn ${count} ảnh:`)
  expect(screen.getByAltText('Xem trước one.jpg')).toBeTruthy()
  expect(screen.getAllByAltText(/^Xem trước/)).toHaveLength(count)
  fireEvent.click(screen.getByRole('button', { name: 'Gửi ảnh phân tích' }))
  await waitFor(() => expect(screen.getByText('Thông tin tiếp nhận', { exact: true })).toBeTruthy(), {
    container: view.container,
    // Ignore row-by-row progress mutations while waiting for the final receipt.
    mutationObserverOptions: { childList: true },
    interval: 150,
    timeout: 35_000,
  })
  expect(methods).toEqual(['manifest', ...Array<string>(count).fill('image'), 'inventory', 'finalize'])
  expect(staged.size).toBe(count)
  expect(append.mock.calls.filter(([key, file]) => key === 'image' && file instanceof File)
    .map(([, file]) => (file as File).name).sort()).toEqual(items.map(item => item.fileName).sort())
  expect(peak).toBeLessThanOrEqual(4)
  view.unmount()
  expect(releasePreview.mock.calls).toContainEqual(['blob:one.jpg'])
  expect(new Set(releasePreview.mock.calls.map(([url]) => url)).size).toBe(count)
}, 45_000)
