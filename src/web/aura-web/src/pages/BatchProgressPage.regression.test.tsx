import { cleanup, render, screen, waitFor, within } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterAll, afterEach, beforeAll, expect, it, vi } from 'vitest'
import type { BackendContext } from '../api/backendContext'
import { BackendSessionContext } from '../auth/backendSession'
import BatchProgressPage from './BatchProgressPage'

const context: BackendContext = {
  firebaseUid: 'batch-test', roles: ['PATIENT'], displayName: 'Synthetic profile', profileVersion: 1,
  accountId: '10000000-0000-4000-8000-000000000001',
  patientId: '10000000-0000-4000-8000-000000000002',
  personalTenantId: '10000000-0000-4000-8000-000000000003',
}
const batchId = '10000000-0000-4000-8000-000000000004'
const endpoint = `/api/v1/batches/${batchId}`
const server = setupServer()
vi.mock('../firebase/config', () => ({
  getFirebaseAuth: () => ({ currentUser: user }),
}))
vi.mock('../api/client', () => ({ isMockEnabled: false }))
const user = { uid: 'batch-test', getIdToken: async () => 'synthetic-bearer' }

function openBatch() {
  return render(
    <BackendSessionContext.Provider value={{ state: { status: 'ready', context }, logout: vi.fn(), retry: vi.fn() }}>
      <MemoryRouter initialEntries={[`/batches/${batchId}`]}>
        <Routes><Route path="/batches/:batchId" element={<BatchProgressPage />} /></Routes>
      </MemoryRouter>
    </BackendSessionContext.Provider>,
  )
}

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }))
afterEach(() => { cleanup(); server.resetHandlers(); vi.restoreAllMocks() })
afterAll(() => server.close())

function snapshot(completed: number) {
  const failed = completed === 100 ? 1 : 0
  return {
    batchId, createdAt: '2026-11-04T00:00:00Z', batchStatus: failed ? 'PARTIAL_FAILED' : 'IN_PROGRESS',
    summary: { total: 100, active: 100 - completed, succeeded: completed - failed, failed, duplicate: 0 },
    items: Array.from({ length: 100 }, (_, index) => ({
      analysisId: `10000000-0000-4000-8001-${String(index + 1).padStart(12, '0')}`,
      fileName: `synthetic-${index}.png`,
      processingStatus: index < completed - failed ? 'COMPLETED'
        : index === 99 && failed ? 'FAILED' : index % 2 ? 'PROCESSING' : 'PENDING',
      reviewStatus: 'PENDING', riskLevel: null,
      error: index === 99 && failed ? { code: 'ERR_NETWORK', message: 'Lỗi tổng hợp', retryable: false } : null,
    })),
  }
}

it.each([0, 68, 100])('restores %i/100 and per-item outcomes from the URL with a GET', async completed => {
  server.use(http.get(`*${endpoint}`, ({ request }) => {
    expect(request.cache).toBe('no-store')
    expect(request.headers.get('Authorization')).toBe('Bearer synthetic-bearer')
    return HttpResponse.json(snapshot(completed))
  }))
  openBatch()
  const progress = await screen.findByRole('progressbar', { name: 'Tiến độ xử lý batch' })
  expect(progress.getAttribute('value')).toBe(String(completed))
  const statusOf = (index: number) => within(screen.getByText(`synthetic-${index}.png`, { exact: true }).closest('tr')!)
  if (completed === 0) {
    expect(statusOf(0).getByText('Đang tiếp nhận')).toBeTruthy()
    expect(statusOf(1).getByText('Đang phân tích')).toBeTruthy()
    expect(statusOf(1).queryByRole('button', { name: /thử lại/i })).toBeNull()
  }
  if (completed > 0) expect(statusOf(0).getByText(/Đã hoàn tất AI/)).toBeTruthy()
  if (completed === 100) {
    expect(statusOf(99).getByText('Phân tích thất bại')).toBeTruthy()
    expect(screen.getByText(/đã dừng polling/)).toBeTruthy()
  }
})

it.each([401, 403, 404, 429, 500, 503])('shows the protected batch error for HTTP %i', async status => {
  server.use(http.get(`*${endpoint}`, () => HttpResponse.json({ error: { code: 'ERR_NETWORK' } }, { status })))
  openBatch()
  const alert = await screen.findByRole('alert')
  const messages: Record<number, RegExp> = {
    401: /Phiên đăng nhập/, 403: /không có quyền/, 404: /Không tìm thấy batch/,
    429: /giới hạn lượt đọc|Chưa tải được trạng thái/,
  }
  expect(alert.textContent).toMatch(messages[status] ?? /Chưa tải được trạng thái/)
  expect(screen.queryByRole('progressbar')).toBeNull()
})

it('reports a network timeout when the request deadline aborts a pending read', async () => {
  const deadline = new AbortController()
  vi.spyOn(AbortSignal, 'timeout').mockReturnValue(deadline.signal)
  let requested = false
  server.use(http.get(`*${endpoint}`, async ({ request }) => {
    requested = true
    await new Promise<void>(resolve => request.signal.addEventListener('abort', () => resolve(), { once: true }))
    return HttpResponse.error()
  }))
  const view = openBatch()
  await waitFor(() => expect(requested).toBe(true))
  deadline.abort()
  expect((await screen.findByRole('alert')).textContent).toContain('quá thời gian chờ')
  view.unmount()
})
