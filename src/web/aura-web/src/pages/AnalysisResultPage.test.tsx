import { act, cleanup, fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AnalysisDetail } from '../api/analysisResult'
import type { BackendContext } from '../api/backendContext'
import { BackendSessionContext } from '../auth/backendSession'

const mocks = vi.hoisted(() => ({ get: vi.fn() }))
vi.mock('../api/analysisResult', async (actual) => ({
  ...await actual<typeof import('../api/analysisResult')>(), getAnalysisResult: mocks.get,
}))
import AnalysisResultPage from './AnalysisResultPage'

const analysisId = '50000000-0000-4000-8000-000000000005'
const context: BackendContext = {
  firebaseUid: 'user-a', accountId: '10000000-0000-4000-8000-000000000001',
  patientId: '20000000-0000-4000-8000-000000000002', personalTenantId: '30000000-0000-4000-8000-000000000003',
  roles: ['PATIENT'], displayName: 'Patient A', profileVersion: 1,
}
const completed: AnalysisDetail = {
  analysisId, batchId: '40000000-0000-4000-8000-000000000004', processingStatus: 'COMPLETED',
  reviewStatus: 'PENDING', updatedAt: '2026-11-14T01:00:00Z', image: null,
  result: {
    version: 1, isMock: false, riskAvailable: false, riskLevel: null, riskScore: null, confidenceScore: null,
    modelVersion: 'frangi-v0.2', configVersion: 'v1.3', thresholdVersion: 'v1',
    disclaimer: 'Chỉ nghiên cứu', limitations: 'Không phải chẩn đoán',
    metrics: { vesselDensity: 0.2, avRatio: null }, indicators: [], annotations: [],
    warnings: [{ code: 'AVR_UNAVAILABLE', message: 'Chưa xác định đủ bộ mạch hợp lệ để tính AVR.' },
      { code: 'RISK_UNAVAILABLE', message: 'Chưa đánh giá nguy cơ.' }],
  },
}
function show() {
  return render(<BackendSessionContext.Provider value={{ state: { status: 'ready', context }, retry: vi.fn(), logout: vi.fn() }}>
    <MemoryRouter initialEntries={[`/analyses/${analysisId}`]}><Routes>
      <Route path="/analyses/:analysisId" element={<AnalysisResultPage />} />
    </Routes></MemoryRouter>
  </BackendSessionContext.Provider>)
}
beforeEach(() => {
  vi.resetAllMocks()
  vi.useFakeTimers()
  mocks.get.mockResolvedValue(completed)
})
afterEach(() => { cleanup(); vi.useRealTimers() })
async function settle() { await act(async () => { await Promise.resolve(); await Promise.resolve() }) }

describe('Kết quả AVR chưa khả dụng', () => {
  it('finishes without AVR, explains why and never displays zero or low risk', async () => {
    show(); await settle()
    const avr = screen.getByRole('region', { name: 'Tỷ số động/tĩnh mạch (AVR)' })
    expect(avr.textContent).toContain('Chưa có kết quả AVR')
    expect(avr.textContent).toContain('Chưa xác định đủ bộ mạch hợp lệ')
    expect(avr.textContent).not.toMatch(/AVR:\s*0/)
    expect(screen.queryByRole('region', { name: 'Đánh giá nguy cơ từ API' })).toBeNull()
    expect(screen.getByText('AI đã xử lý xong')).toBeTruthy()
    await act(async () => { await vi.advanceTimersByTimeAsync(360_000) })
    expect(mocks.get).toHaveBeenCalledTimes(1)
    expect(screen.queryByRole('alert')).toBeNull()
  })

  it('displays a measured AVR even when clinical risk is unavailable', async () => {
    mocks.get.mockResolvedValue({ ...completed, result: { ...completed.result!, metrics: { avRatio: 0.672 }, warnings: [] } })
    show(); await settle()
    expect(screen.getByRole('region', { name: 'Tỷ số động/tĩnh mạch (AVR)' }).textContent).toContain('0.672')
    expect(screen.queryByRole('region', { name: 'Đánh giá nguy cơ từ API' })).toBeNull()
  })

  it('shows independent DR output and crop provenance while AVR is unavailable', async () => {
    mocks.get.mockResolvedValue({ ...completed, result: { ...completed.result!, research: {
      avr: { measurement_available: false, avr: null, crae_px: null, crve_px: null,
        unavailable_reason: 'INCOMPLETE_ZONE_B', model_version: 'avr-v8', model_sha256: 'a'.repeat(64) },
      dr: { probability_dr: 0.61, model_version: 'resnet34', checkpoint_sha256: 'b'.repeat(64),
        gradcam_method: 'GRAD_CAM', gradcam_geometry: 'IMAGENET_CENTER_CROP_224' },
    } } })
    show(); await settle()
    const research = screen.getByRole('region', { name: 'Kết quả AI nghiên cứu' })
    expect(research.textContent).toContain('61.0%')
    expect(research.textContent).toContain('resnet34')
    expect(research.textContent).toContain('224×224')
    expect(research.textContent).toContain('INCOMPLETE_ZONE_B')
    expect(screen.queryByRole('region', { name: 'Đánh giá nguy cơ từ API' })).toBeNull()
  })

  it('stops polling when PROCESSING becomes COMPLETED with AVR null', async () => {
    mocks.get.mockResolvedValueOnce({ ...completed, processingStatus: 'PROCESSING', result: null })
    show(); await settle()
    await act(async () => { await vi.advanceTimersByTimeAsync(5_000) })
    expect(screen.getByText('AI đã xử lý xong')).toBeTruthy()
    await act(async () => { await vi.advanceTimersByTimeAsync(360_000) })
    expect(mocks.get).toHaveBeenCalledTimes(2)
  })

  it.each(['PROCESSING', 'QUEUED'])('bounds monitoring of %s and allows a fresh GET', async (status) => {
    mocks.get.mockResolvedValue({ ...completed, processingStatus: status, result: null })
    show(); await settle()
    await act(async () => { await vi.advanceTimersByTimeAsync(300_000) })
    expect(screen.getByRole('alert').textContent).toContain('Tạm dừng theo dõi tự động')
    expect(screen.getByRole('alert').textContent).toContain('không có nghĩa phân tích thất bại')
    const calls = mocks.get.mock.calls.length
    await act(async () => { await vi.advanceTimersByTimeAsync(60_000) })
    expect(mocks.get).toHaveBeenCalledTimes(calls)
    mocks.get.mockResolvedValue(completed)
    fireEvent.click(screen.getByRole('button', { name: 'Thử đọc lại (GET)' })); await settle()
    expect(screen.getByText('AI đã xử lý xong')).toBeTruthy()
    expect(mocks.get).toHaveBeenCalledTimes(calls + 1)
  })

  it('leaves loading on a hanging request and ignores a late response after timeout', async () => {
    let resolve!: (value: AnalysisDetail) => void
    mocks.get.mockReturnValue(new Promise<AnalysisDetail>((done) => { resolve = done }))
    show()
    const signal = mocks.get.mock.calls[0][2] as AbortSignal
    await act(async () => { await vi.advanceTimersByTimeAsync(300_000) })
    expect(signal.aborted).toBe(true)
    expect(screen.queryByText('Đang tải kết quả từ Analysis API...')).toBeNull()
    expect(screen.getByRole('alert')).toBeTruthy()
    await act(async () => { resolve(completed) })
    expect(screen.queryByText('AI đã xử lý xong')).toBeNull()
  })

  it('treats low quality as terminal without an AVR or a guessed risk', async () => {
    mocks.get.mockResolvedValue({ ...completed, processingStatus: 'LOW_QUALITY', result: null })
    show(); await settle()
    expect(screen.getByText(/Ảnh không đạt chất lượng/)).toBeTruthy()
    expect(screen.queryByRole('region', { name: 'Đánh giá nguy cơ từ API' })).toBeNull()
    await act(async () => { await vi.advanceTimersByTimeAsync(360_000) })
    expect(mocks.get).toHaveBeenCalledTimes(1)
  })
})
