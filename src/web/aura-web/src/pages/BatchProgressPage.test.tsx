import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { BackendContext } from '../api/backendContext'
import { BackendSessionContext } from '../auth/backendSession'
import { BatchProgressError, type BatchProgress } from '../api/batchProgress'

const mocks = vi.hoisted(() => ({ get: vi.fn(), push: vi.fn() }))
vi.mock('../firebase/messaging', () => ({ listenForegroundMessages: mocks.push }))
vi.mock('../api/batchProgress', async (actual) => ({
  ...await actual<typeof import('../api/batchProgress')>(), getBatchProgress: mocks.get,
}))
import BatchProgressPage from './BatchProgressPage'

const batchId = '40000000-0000-4000-8000-000000000004'
const context: BackendContext = {
  firebaseUid: 'user-a', accountId: '10000000-0000-4000-8000-000000000001',
  patientId: '20000000-0000-4000-8000-000000000002', personalTenantId: '30000000-0000-4000-8000-000000000003',
  roles: ['PATIENT'], displayName: 'Patient A', profileVersion: 1,
}
const terminal: BatchProgress = {
  batchId, batchStatus: 'COMPLETED', createdAt: '2026-10-27T03:00:00Z',
  items: [{ analysisId: '50000000-0000-4000-8000-000000000005', fileName: 'fundus.png',
    processingStatus: 'COMPLETED', reviewStatus: 'PENDING', riskLevel: 'MODERATE' }],
  summary: { total: 1, active: 0, succeeded: 1, failed: 0, duplicate: 0 },
}
function show(path = `/batches/${batchId}`) {
  return render(<BackendSessionContext.Provider value={{ state: { status: 'ready', context }, retry: vi.fn(), logout: vi.fn() }}>
    <MemoryRouter initialEntries={[path]}><Routes>
      <Route path="/batches" element={<BatchProgressPage />} />
      <Route path="/batches/:batchId" element={<BatchProgressPage />} />
    </Routes></MemoryRouter>
  </BackendSessionContext.Provider>)
}
beforeEach(() => {
  vi.resetAllMocks()
  mocks.get.mockResolvedValue({ kind: 'batch', data: terminal })
  mocks.push.mockResolvedValue(null)
})
afterEach(() => { cleanup(); vi.useRealTimers() })

describe('SCRUM-332 batch progress component', () => {
  it('opens a camera or web batch by ID and renders the authorized server result', async () => {
    show('/batches')
    fireEvent.change(screen.getByLabelText('Batch ID'), { target: { value: batchId } })
    fireEvent.click(screen.getByRole('button', { name: 'Mở batch' }))
    expect(await screen.findByText('fundus.png')).toBeTruthy()
    expect(screen.getByRole('status').textContent).toContain('Đã hoàn tất AI')
    expect(mocks.get).toHaveBeenCalledWith(batchId, context, expect.any(AbortSignal))
  })

  it('refreshing the browser URL only reads GET, no new receipt or upload', async () => {
    const first = show()
    await screen.findByText('fundus.png')
    first.unmount()
    show()
    await screen.findByText('fundus.png')
    expect(mocks.get).toHaveBeenCalledTimes(2)
  })

  it('terminal state stops polling after the initial GET', async () => {
    vi.useFakeTimers()
    show()
    await act(async () => { await Promise.resolve(); await Promise.resolve() })
    await act(async () => { await vi.advanceTimersByTimeAsync(60000) })
    expect(mocks.get).toHaveBeenCalledTimes(1)
  })

  it('HTTP 202 is never displayed as completed, then polling reads the actual batch', async () => {
    mocks.get.mockResolvedValueOnce({ kind: 'accepted' }).mockResolvedValue({ kind: 'batch', data: terminal })
    vi.useFakeTimers()
    show()
    await act(async () => { await Promise.resolve(); await Promise.resolve() })
    expect(screen.getByText(/Backend đã tiếp nhận batch/)).toBeTruthy()
    expect(screen.queryByText('fundus.png')).toBeNull()
    await act(async () => { await vi.advanceTimersByTimeAsync(3000) })
    expect(screen.getByText('fundus.png')).toBeTruthy()
  })

  it('temporary GET error backs off then recovers without any POST', async () => {
    mocks.get.mockRejectedValueOnce(new BatchProgressError(503, 'ERR_NETWORK', true))
      .mockResolvedValue({ kind: 'batch', data: terminal })
    vi.useFakeTimers()
    show()
    await act(async () => { await Promise.resolve(); await Promise.resolve() })
    expect(screen.getByText(/Mất kết nối hoặc lỗi GET tạm thời/)).toBeTruthy()
    await act(async () => { await vi.advanceTimersByTimeAsync(2000) })
    expect(screen.getByText('fundus.png')).toBeTruthy()
    expect(mocks.get).toHaveBeenCalledTimes(2)
  })

  it.each([[401, 'Phiên đăng nhập'], [403, 'quyền'], [404, 'Không tìm thấy batch']])(
    'shows a safe HTTP %s access message and hides data', async (status, expected) => {
      mocks.get.mockRejectedValueOnce(new BatchProgressError(status as number, 'ERR_FORBIDDEN', false))
      show()
      await waitFor(() => expect(screen.getByRole('alert').textContent).toContain(expected))
      expect(screen.queryByText('fundus.png')).toBeNull()
    },
  )

  it('reconnects with GET when the browser comes back online, with no duplicate request', async () => {
    const running: BatchProgress = {
      ...terminal, batchStatus: 'IN_PROGRESS',
      items: [{ ...terminal.items[0], processingStatus: 'PROCESSING', riskLevel: null }],
      summary: { total: 1, active: 1, succeeded: 0, failed: 0, duplicate: 0 },
    }
    mocks.get.mockResolvedValueOnce({ kind: 'batch', data: running })
      .mockResolvedValue({ kind: 'batch', data: terminal })
    vi.useFakeTimers()
    show()
    await act(async () => { await vi.advanceTimersByTimeAsync(0) })
    act(() => {
      window.dispatchEvent(new Event('online'))
      window.dispatchEvent(new Event('focus'))
      window.dispatchEvent(new Event('online'))
    })
    await act(async () => { await vi.advanceTimersByTimeAsync(2999) })
    expect(mocks.get).toHaveBeenCalledTimes(1)
    await act(async () => { await vi.advanceTimersByTimeAsync(1) })
    expect(mocks.get).toHaveBeenCalledTimes(2)
    expect(screen.getByRole('status').textContent).toContain('Đã hoàn tất AI')
  })

  it('aborts a pending GET when leaving the page', async () => {
    const controllerSignals: AbortSignal[] = []
    mocks.get.mockImplementation((_id, _context, signal: AbortSignal) => {
      controllerSignals.push(signal)
      return new Promise(() => {})
    })
    const page = show()
    expect(controllerSignals).toHaveLength(1)
    page.unmount()
    expect(controllerSignals[0].aborted).toBe(true)
  })

  it('respects Retry-After through focus, reconnect and manual refresh, then caps retries', async () => {
    vi.useFakeTimers()
    mocks.get.mockRejectedValueOnce(new BatchProgressError(429, 'ERR_RATE_LIMIT', true, 10000))
      .mockRejectedValue(new BatchProgressError(503, 'ERR_DEPENDENCY', true))
    show()
    await act(async () => { await vi.advanceTimersByTimeAsync(0) })
    expect(screen.getByRole('alert').textContent).toContain('giới hạn')
    fireEvent.click(screen.getByRole('button', { name: /Tải lại trạng thái/ }))
    act(() => { window.dispatchEvent(new Event('focus')); window.dispatchEvent(new Event('online')) })
    await act(async () => { await vi.advanceTimersByTimeAsync(9999) })
    expect(mocks.get).toHaveBeenCalledTimes(1)
    await act(async () => { await vi.advanceTimersByTimeAsync(1) })
    expect(mocks.get).toHaveBeenCalledTimes(2)
    await act(async () => { await vi.advanceTimersByTimeAsync(300000) })
    expect(mocks.get).toHaveBeenCalledTimes(6)
    expect(screen.getByText(/tạm dừng cập nhật tự động/)).toBeTruthy()
  })

  it('keeps progress authoritative and cleans up the foreground FCM subscription', async () => {
    const detach = vi.fn()
    mocks.push.mockResolvedValue(detach)
    const page = show()
    await screen.findByText('fundus.png')
    const onMessage = mocks.push.mock.calls[0][0]
    act(() => { onMessage({ data: { targetPath: `/batches/${batchId}` } }) })
    expect(mocks.get).toHaveBeenCalledTimes(1)
    page.unmount()
    expect(detach).toHaveBeenCalledOnce()
  })

  it('restores 68 of 100 from its URL and renders case actions only for completed items', async () => {
    const cases = Array.from({ length: 100 }, (_, index) => ({
      ...terminal.items[0], fileName: `sample-${index}.png`,
      analysisId: `50000000-0000-4000-8000-${String(index).padStart(12, '0')}`,
      processingStatus: (index < 67 ? 'COMPLETED' : index === 67 ? 'FAILED' : index < 80 ? 'PROCESSING' : 'QUEUED') as BatchProgress['items'][number]['processingStatus'],
    }))
    const running: BatchProgress = { ...terminal, items: cases, batchStatus: 'IN_PROGRESS',
      summary: { total: 100, active: 32, succeeded: 67, failed: 1, duplicate: 0 } }
    mocks.get.mockResolvedValue({ kind: 'batch', data: running })
    const clinic = '50000000-0000-4000-8000-000000000006'
    const url = `/batches/${batchId}?clinicContextId=${clinic}`
    const first = show(url)
    expect(await screen.findByText(/Đã hoàn thành 68\/100/)).toBeTruthy()
    expect(screen.getAllByRole('button', { name: 'Tải báo cáo PDF' })).toHaveLength(67)
    expect(screen.queryByRole('button', { name: /ZIP|thử lại/i })).toBeNull()
    first.unmount()
    show(url)
    expect(await screen.findByText(/Đã hoàn thành 68\/100/)).toBeTruthy()
    expect(mocks.get).toHaveBeenCalledTimes(2)
    expect(mocks.get).toHaveBeenLastCalledWith(batchId, { ...context, clinicContextId: clinic }, expect.any(AbortSignal))
  })

  it('removes previously visible patient filenames when a poll loses authorization', async () => {
    vi.useFakeTimers()
    const running: BatchProgress = { ...terminal, batchStatus: 'IN_PROGRESS' }
    mocks.get.mockResolvedValueOnce({ kind: 'batch', data: running })
      .mockRejectedValue(new BatchProgressError(403, 'ERR_FORBIDDEN', false))
    show()
    await act(async () => { await vi.advanceTimersByTimeAsync(0) })
    expect(screen.getByText('fundus.png')).toBeTruthy()
    await act(async () => { await vi.advanceTimersByTimeAsync(3000) })
    expect(screen.queryByText('fundus.png')).toBeNull()
    expect(screen.queryByRole('button', { name: 'Tải báo cáo PDF' })).toBeNull()
    await act(async () => { await vi.advanceTimersByTimeAsync(60000) })
    expect(mocks.get).toHaveBeenCalledTimes(2)
  })

  it('keeps unresolved admission read-only and updates the batch through a GET', async () => {
    vi.useFakeTimers()
    const unresolved = { ...terminal, batchStatus: 'IN_PROGRESS', items: [], totalDeclared: 1,
      admissionRejected: 0, uploadPending: 1, summary: { total: 0, active: 0, succeeded: 0, failed: 0, duplicate: 0 } }
    mocks.get.mockResolvedValueOnce({ kind: 'batch', data: unresolved })
    show()
    await act(async () => { await vi.advanceTimersByTimeAsync(0) })
    expect(screen.getByText(/Chưa hoàn tất tải\/đối soát: 1/)).toBeTruthy()
    expect(screen.queryByRole('button', { name: /Tải báo cáo|Tải tất cả|Thử lại ca/ })).toBeNull()
    expect(screen.getByRole('progressbar')).toHaveProperty('value', 0)
    await act(async () => { await vi.advanceTimersByTimeAsync(3000) })
    expect(mocks.get).toHaveBeenCalledTimes(2)
    expect(screen.getByRole('button', { name: 'Tải báo cáo PDF' })).toBeTruthy()
  })

  it('shows a failed case and offers only the terminal batch exports', async () => {
    const failed = { ...terminal, batchStatus: 'FAILED',
      items: [{ ...terminal.items[0], processingStatus: 'FAILED', riskLevel: null }],
      summary: { total: 1, active: 0, succeeded: 0, failed: 1, duplicate: 0 } }
    mocks.get.mockResolvedValue({ kind: 'batch', data: failed })
    show()
    expect(await screen.findByText('Phân tích thất bại', { selector: '.batch-status--failed' })).toBeTruthy()
    expect(screen.queryByRole('button', { name: 'Tải báo cáo PDF' })).toBeNull()
    expect(screen.getByRole('button', { name: 'Tải CSV' })).toBeTruthy()
    expect(screen.getByRole('button', { name: 'Tải tất cả (ZIP)' })).toBeTruthy()
  })
})
it('hiển thị đúng thông báo khi toàn bộ ảnh bị trùng', async () => {
  const duplicateBatch: BatchProgress = {
    ...terminal,
    items: [{
      ...terminal.items[0],
      processingStatus: 'DUPLICATE',
      riskLevel: null,
      existingAnalysisId: '60000000-0000-4000-8000-000000000006',
    }],
    summary: {
      total: 1,
      active: 0,
      succeeded: 0,
      failed: 0,
      duplicate: 1,
    },
  }

  mocks.get.mockResolvedValue({
    kind: 'batch',
    data: duplicateBatch,
  })

  show()

  expect(
    await screen.findByText(
      /Batch đã kết thúc — toàn bộ ảnh trùng, không có phân tích AI mới/
    )
  ).toBeTruthy()

  expect(screen.queryByText('Đã hoàn tất AI')).toBeNull()
})
