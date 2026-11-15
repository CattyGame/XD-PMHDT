/// <reference types="node" />
import { webcrypto } from 'node:crypto'
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, afterEach, it, expect, vi } from 'vitest'
const m = vi.hoisted(() => ({ auth: vi.fn() }))
vi.mock('../firebase/config', () => ({ getFirebaseAuth: m.auth }))
vi.mock('../api/client', () => ({ isMockEnabled: false }))
import UploadPage from './UploadPage'
import { BackendSessionContext } from '../auth/backendSession'
import type { BackendContext } from '../api/backendContext'
import type { UploadInventoryItem } from '../api/itemUpload'
const batchId = '11111111-1111-4111-8111-000000000001'
let context: BackendContext, items: UploadInventoryItem[], failPut: boolean
const fetchMock = vi.fn<typeof fetch>()
const json = (x: unknown, status = 200) => new Response(JSON.stringify(x), { status })
const snapshot = () => ({ batchId, createdAt: '2026-11-04T00:00:00Z', statusUrl: `/api/v1/batches/${batchId}`, items })
const tree = () => <BackendSessionContext.Provider value={{ state: { status: 'ready', context }, retry: vi.fn(), logout: vi.fn() }}><UploadPage /></BackendSessionContext.Provider>
beforeEach(() => {
  vi.resetAllMocks(); items = []; failPut = false; window.history.replaceState(null, '', '/upload')
  context = { accountId: batchId, patientId: '11111111-1111-4111-8111-000000000002', personalTenantId: '11111111-1111-4111-8111-000000000003',
    firebaseUid: 'user-a', roles: ['PATIENT'], displayName: 'Patient A', profileVersion: 1 }
  m.auth.mockReturnValue({ currentUser: { uid: context.firebaseUid, getIdToken: async () => 'user-token' } })
  vi.stubGlobal('crypto', webcrypto)
  vi.stubGlobal('createImageBitmap', async () => ({ width: 10, height: 10, close: vi.fn() }))
  const RealURL = URL
  vi.stubGlobal('URL', class extends RealURL { static createObjectURL = () => 'blob:preview'; static revokeObjectURL = vi.fn() })
  vi.stubGlobal('fetch', fetchMock)
  fetchMock.mockImplementation(async (url, init) => {
    const path = String(url)
    if (init?.method === 'POST' && path.endsWith('/analysis-upload-batches')) {
      items = JSON.parse(String(init.body)).items.map((x: UploadInventoryItem) => ({ ...x, uploadStatus: 'DECLARED' }))
      return json(snapshot(), 201)
    }
    if (init?.method === 'PUT') {
      if (failPut) throw new TypeError('Offline')
      const id = path.split('/').at(-1)
      items = items.map(x => x.itemId === id ? { ...x, uploadStatus: 'STAGED' } : x)
      return json(items.find(x => x.itemId === id))
    }
    if (path.endsWith('/finalize')) return json({ ...snapshot(), items: items.map(x => ({ analysisId: x.itemId, fileName: x.fileName, processingStatus: 'QUEUED' })) }, 202)
    return json(snapshot())
  })
})
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); window.history.replaceState(null, '', '/upload') })

it('routes the actual UploadPage through manifest, separate item requests and one finalization', async () => {
  render(tree())
  const files = [0, 1, 2].map(i => new File([`pixels-${i}`], `retina-${i}.png`, { type: 'image/png' }))
  fireEvent.change(screen.getByLabelText('Chọn ảnh võng mạc'), { target: { files } })
  await screen.findByText('Đã chọn 3 ảnh:')
  fireEvent.change(screen.getByLabelText('Mắt của retina-1.png'), { target: { value: 'right' } })
  fireEvent.click(screen.getByRole('button', { name: 'Gửi ảnh phân tích' }))
  await screen.findByText('Thông tin tiếp nhận')
  expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'PUT')).toHaveLength(3)
  expect(items.map(item => item.eyeSide)).toEqual(['left', 'right', 'left'])
  expect(fetchMock.mock.calls.filter(([url]) => String(url) === '/api/v1/analyses')).toHaveLength(0)
  expect(new URL(window.location.href).searchParams.get('uploadBatchId')).toBe(batchId)
  expect(localStorage.length).toBe(0)
  expect(sessionStorage.length).toBe(0)
})

it('restores server inventory after a new page/context and reselects bytes before resuming', async () => {
  failPut = true
  const view = render(tree())
  const file = new File(['same-pixels'], 'retina.png', { type: 'image/png' })
  fireEvent.change(screen.getByLabelText('Chọn ảnh võng mạc'), { target: { files: [file] } })
  await screen.findByText('Đã chọn 1 ảnh:')
  fireEvent.click(screen.getByRole('button', { name: 'Gửi ảnh phân tích' }))
  await screen.findByRole('alert')
  expect(items[0].uploadStatus).toBe('DECLARED')
  view.unmount(); context = { ...context }; failPut = false
  const before = fetchMock.mock.calls.length
  await act(async () => { render(tree()) })
  await screen.findByRole('button', { name: 'Tiếp tục lượt tải đã lưu' })
  await waitFor(() => expect(String(fetchMock.mock.calls[before][0])).toBe(`/api/v1/analysis-upload-batches/${batchId}`))
  fireEvent.change(screen.getByLabelText('Chọn ảnh võng mạc'), { target: { files: [file] } })
  await screen.findByText('Đã chọn 1 ảnh:')
  fireEvent.click(screen.getByRole('button', { name: 'Tiếp tục lượt tải đã lưu' }))
  await screen.findByText('Thông tin tiếp nhận')
  expect(items[0].uploadStatus).toBe('STAGED')
  expect(fetchMock.mock.calls.slice(before).filter(([url, init]) => init?.method === 'POST' && String(url).endsWith('/analysis-upload-batches'))).toHaveLength(0)
})

it('allows retrying a recoverable image independently of a rejected sibling', async () => {
  const serve = fetchMock.getMockImplementation()!
  let disconnected = false
  fetchMock.mockImplementation(async (url, init) => {
    if (init?.method === 'PUT') {
      const file = (init.body as FormData).get('image') as File
      if (file.name === 'rejected.png') return json({ error: { code: 'ERR_PAYLOAD_TOO_LARGE', retryable: false } }, 413)
      if (!disconnected) {
        disconnected = true
        await new Promise(resolve => setTimeout(resolve, 5))
        throw new TypeError('Single request interrupted')
      }
    }
    return serve(url, init)
  })
  render(tree())
  const files = ['rejected.png', 'recoverable.png'].map(name => new File([name], name, { type: 'image/png' }))
  fireEvent.change(screen.getByLabelText('Chọn ảnh võng mạc'), { target: { files } })
  await screen.findByText('Đã chọn 2 ảnh:')
  fireEvent.click(screen.getByRole('button', { name: 'Gửi ảnh phân tích' }))
  await screen.findByRole('alert')
  expect(screen.queryByRole('button', { name: 'Thử lại rejected.png' })).toBeNull()
  fireEvent.click(screen.getByRole('button', { name: 'Thử lại recoverable.png' }))
  await screen.findByText('Đã tải: 1/2 ảnh')
  await waitFor(() => expect(screen.getByRole('button', { name: 'Thử lại cùng lượt gửi' })).toHaveProperty('disabled', true))
  expect(items.map(item => item.uploadStatus)).toEqual(['DECLARED', 'STAGED'])
  const uploads = fetchMock.mock.calls.filter(([, init]) => init?.method === 'PUT')
    .map(([, init]) => ((init!.body as FormData).get('image') as File).name)
  expect(uploads).toEqual(['rejected.png', 'recoverable.png', 'recoverable.png'])
  expect(fetchMock.mock.calls.some(([url]) => String(url).endsWith('/finalize'))).toBe(false)
})
