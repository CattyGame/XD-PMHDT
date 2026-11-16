/// <reference types="node" />
import { webcrypto } from 'node:crypto'
import { beforeEach, afterEach, it, expect, vi } from 'vitest'
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
const m = vi.hoisted(() => ({ auth: vi.fn() }))
vi.mock('../firebase/config', () => ({ getFirebaseAuth: m.auth }))
vi.mock('../api/client', () => ({ isMockEnabled: false }))
import ClinicUploadPage from './ClinicUploadPage'
import { BackendSessionContext } from '../auth/backendSession'
import type { BackendContext } from '../api/backendContext'
import type { UploadInventoryItem } from '../api/itemUpload'
const clinicId = '11111111-1111-4111-8111-000000000001'
const patientA = '11111111-1111-4111-8111-000000000002', patientB = '11111111-1111-4111-8111-000000000003'
const batchId = '11111111-1111-4111-8111-000000000004'
const context: BackendContext = { accountId: patientA, patientId: patientA, personalTenantId: patientA, firebaseUid: 'admin-member', roles: ['PATIENT'], displayName: 'A', profileVersion: 1 }
const fetchMock = vi.fn<typeof fetch>()
let items: UploadInventoryItem[]
const json = (x: unknown, status = 200) => new Response(JSON.stringify(x), { status, headers: { 'Content-Type': 'application/json' } })
const snapshot = () => ({ batchId, items, createdAt: '2026-11-04T00:00:00Z', statusUrl: `/api/v1/batches/${batchId}` })
const page = () => render(<MemoryRouter><BackendSessionContext.Provider value={{ state: { status: 'ready', context }, retry: vi.fn(), logout: vi.fn() }}><ClinicUploadPage /></BackendSessionContext.Provider></MemoryRouter>)
beforeEach(() => {
  vi.resetAllMocks(); items = []; window.history.replaceState(null, '', '/clinic-upload')
  m.auth.mockReturnValue({ currentUser: { uid: context.firebaseUid, getIdToken: async () => 'member-bearer' } })
  vi.stubGlobal('crypto', webcrypto); vi.stubGlobal('fetch', fetchMock)
  fetchMock.mockImplementation(async (url, init) => {
    expect(new Headers(init?.headers).get('Authorization')).toBe('Bearer member-bearer')
    const path = String(url).split('?')[0]
    if (path === '/api/v1/clinics') return json({ items: [{ clinicId, clinicName: 'Clinic A', role: 'CLINIC_ADMIN', status: 'ACTIVE' }] })
    if (path.endsWith('/patients')) return json({ clinicId, items: [{ patientId: patientA, fullName: 'Patient A', status: 'ACTIVE' }, { patientId: patientB, fullName: 'Patient B', status: 'ACTIVE' }] })
    if (path.endsWith('/analysis-upload-batches') && init?.method === 'POST') {
      items = JSON.parse(String(init.body)).items.map((x: UploadInventoryItem) => ({ ...x, uploadStatus: 'DECLARED' }))
      return json(snapshot(), 201)
    }
    if (init?.method === 'PUT') {
      const id = path.split('/').at(-1)
      items = items.map(x => x.itemId === id ? { ...x, uploadStatus: 'STAGED' } : x)
      return json(items.find(x => x.itemId === id))
    }
    if (path.endsWith('/finalize')) return json({ ...snapshot(), items: items.map(x => ({ analysisId: x.itemId, fileName: x.fileName, processingStatus: 'QUEUED' })) }, 202)
    return json(snapshot())
  })
})
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); window.history.replaceState(null, '', '/upload') })

it('discovers membership clinics and sends different patient/eye metadata through one real staged protocol', async () => {
  page()
  await screen.findByRole('option', { name: 'Clinic A' })
  fireEvent.change(screen.getByLabelText('Chọn ảnh phòng khám'), { target: { files: [new File(['a'], 'left.png', { type: 'image/png' }), new File(['b'], 'right.png', { type: 'image/png' })] } })
  await screen.findAllByRole('option', { name: 'Patient A' })
  fireEvent.change(screen.getByLabelText('Bệnh nhân ảnh1'), { target: { value: patientA } })
  fireEvent.change(screen.getByLabelText('Bệnh nhân ảnh2'), { target: { value: patientB } })
  fireEvent.change(screen.getByLabelText('Mắt ảnh1'), { target: { value: 'left' } })
  fireEvent.change(screen.getByLabelText('Mắt ảnh2'), { target: { value: 'right' } })
  fireEvent.click(screen.getByRole('button', { name: 'Gửi ảnh phòng khám' }))
  await screen.findByRole('heading', { name: 'Đã tiếp nhận lượt tải phòng khám' })
  expect(items.map(x => [x.patientId, x.eyeSide])).toEqual([[patientA, 'left'], [patientB, 'right']])
  const uploads = fetchMock.mock.calls.filter(([, init]) => init?.method === 'PUT')
  expect(uploads).toHaveLength(2)
  expect(uploads.every(([url, init]) => String(url).includes(`clinicContextId=${clinicId}`) && (init!.body as FormData).getAll('image').length === 1)).toBe(true)
  expect(screen.getByRole('link', { name: 'Theo dõi tiến trình phòng khám' }).getAttribute('href')).toContain(`clinicContextId=${clinicId}`)
  expect(localStorage.length).toBe(0)
})

it('does not offer fabricated clinics when discovery is denied', async () => {
  fetchMock.mockResolvedValueOnce(json({ error: { code: 'ERR_FORBIDDEN' } }, 403))
  page()
  expect((await screen.findByRole('alert')).textContent).toContain('không được truy cập')
  expect(screen.queryByLabelText('Chọn ảnh phòng khám')).toBeNull()
  expect(fetchMock).toHaveBeenCalledTimes(1)
})

it('renders 100 lazy previews on drop, rejects an overflowing selection, and releases removed and remaining previews', async () => {
  const create = vi.fn((file: File) => `blob:${file.name}`)
  const revoke = vi.fn()
  const RealURL = URL
  vi.stubGlobal('URL', class extends RealURL { static createObjectURL = create; static revokeObjectURL = revoke })
  const view = page()
  await screen.findByRole('option', { name: 'Clinic A' })
  const input = screen.getByLabelText('Chọn ảnh phòng khám')
  const files = Array.from({ length: 101 }, (_, i) => new File(['test'], `retina-${i}.png`, { type: 'image/png' }))
  fireEvent.drop(input, { dataTransfer: { files: files.slice(0, 100) } })
  expect(screen.getAllByAltText(/^Xem trước /)).toHaveLength(100)
  expect(create).toHaveBeenCalledTimes(100)
  expect(screen.getByText('100/100')).toBeTruthy()
  fireEvent.drop(input, { dataTransfer: { files } })
  expect(screen.getByText(/Vượt giới hạn 100 ảnh mỗi batch/)).toBeTruthy()
  expect(screen.queryByAltText('Xem trước retina-100.png')).toBeNull()
  fireEvent.click(screen.getByText('Xóa retina-0.png', { selector: 'button' }))
  expect(revoke).toHaveBeenCalledWith('blob:retina-0.png')
  expect(screen.getAllByAltText(/^Xem trước /)).toHaveLength(99)
  view.unmount()
  expect(revoke).toHaveBeenCalledTimes(100)
})

it('holds one submission, keeps other images after a lost connection and retries only the failed image', async () => {
  const server = fetchMock.getMockImplementation()!
  let failed = false
  fetchMock.mockImplementation(async (url, init) => {
    if (init?.method === 'PUT' && !failed) {
      failed = true
      throw new TypeError('Connection lost before upload')
    }
    return server(url, init)
  })
  const storage = vi.spyOn(Storage.prototype, 'setItem')
  const view = page()
  await screen.findByRole('option', { name: 'Clinic A' })
  await screen.findByLabelText('Chọn ảnh phòng khám')
  const files = [0, 1].map(i => new File([`sample-${i}`], `retina-${i}.png`, { type: 'image/png' }))
  fireEvent.change(screen.getByLabelText('Chọn ảnh phòng khám'), { target: { files } })
  for (const number of [1, 2]) fireEvent.change(screen.getByLabelText(`Mã bệnh nhân ảnh${number}`), { target: { value: patientA } })
  const submit = screen.getByRole('button', { name: 'Gửi ảnh phòng khám' })
  act(() => { submit.click(); submit.click() })
  const retry = await screen.findByRole('button', { name: 'Thử lại retina-0.png' })
  const progress = screen.getByRole('region', { name: 'Tiến độ tiếp nhận ảnh' })
  expect(within(progress).getByText(/Đã tải: 1\/2 ảnh/)).toBeTruthy()
  expect(within(progress).getByText(/Lỗi tải/)).toBeTruthy()
  fireEvent.click(retry)
  await screen.findByRole('heading', { name: 'Đã tiếp nhận lượt tải phòng khám' })
  const uploadNames = fetchMock.mock.calls.filter(([, init]) => init?.method === 'PUT')
    .map(([, init]) => ((init!.body as FormData).get('image') as File).name)
  expect(uploadNames).toEqual(['retina-0.png', 'retina-1.png', 'retina-0.png'])
  expect(fetchMock.mock.calls.filter(([url, init]) => init?.method === 'POST' && String(url).includes('analysis-upload-batches'))).toHaveLength(2)
  expect(storage).not.toHaveBeenCalled()
  expect(screen.queryByRole('img')).toBeNull()
  view.unmount()
})

it('aborts a pending upload on unmount and ignores its late failure', async () => {
  const server = fetchMock.getMockImplementation()!
  let putSignal: AbortSignal | undefined
  fetchMock.mockImplementation((url, init) => {
    if (init?.method !== 'PUT') return server(url, init)
    putSignal = init.signal ?? undefined
    return new Promise((_resolve, reject) => putSignal?.addEventListener('abort', () => reject(putSignal?.reason)))
  })
  const view = page()
  await screen.findByRole('option', { name: 'Clinic A' })
  fireEvent.change(screen.getByLabelText('Chọn ảnh phòng khám'), { target: { files: [new File(['sample'], 'pending.png', { type: 'image/png' })] } })
  fireEvent.change(screen.getByLabelText('Mã bệnh nhân ảnh1'), { target: { value: patientA } })
  fireEvent.click(screen.getByRole('button', { name: 'Gửi ảnh phòng khám' }))
  await screen.findByText(/Đang tải/, { selector: 'li' })
  view.unmount()
  expect(putSignal?.aborted).toBe(true)
  await act(async () => {})
  expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/finalize'))).toBe(false)
})
