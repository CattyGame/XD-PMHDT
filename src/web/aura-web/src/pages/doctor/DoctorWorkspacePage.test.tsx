import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import DoctorWorkspacePage from './DoctorWorkspacePage'

const clinicId = '10000000-0000-4000-8000-000000000001'
const analysisId = '20000000-0000-4000-8000-000000000002'
const data = {
  analysisId, batchId: '30000000-0000-4000-8000-000000000003',
  patientId: '40000000-0000-4000-8000-000000000004',
  processingStatus: 'COMPLETED', reviewStatus: 'PENDING', reviewVersion: 1,
  createdAt: '2026-11-01T09:00:00Z',
  image: { fileId: '50000000-0000-4000-8000-000000000005', kind: 'original' },
  result: { riskAvailable: true, riskLevel: 'HIGH', isMock: false,
    annotations: [{ fileId: '60000000-0000-4000-8000-000000000006', kind: 'mask' }],
    disclaimer: 'Chỉ hỗ trợ' },
}
const mocks = vi.hoisted(() => ({
  worklist: vi.fn(), detail: vi.fn(), save: vi.fn(), file: vi.fn(), content: vi.fn(),
  context: { accountId: '70000000-0000-4000-8000-000000000007',
    firebaseUid: 'doctor', patientId: '80000000-0000-4000-8000-000000000008',
    personalTenantId: '90000000-0000-4000-8000-000000000009',
    roles: ['DOCTOR'] as const, displayName: 'Bác sĩ', profileVersion: 1 },
}))
vi.mock('../../auth/backendSession', () => ({ useBackendSession: () => ({
  state: { status: 'ready', context: mocks.context },
}) }))
vi.mock('../../api/doctorReview', async (importOriginal) => {
  const original = await importOriginal<typeof import('../../api/doctorReview')>()
  return { ...original, getDoctorWorklist: mocks.worklist, getDoctorAnalysis: mocks.detail,
    submitDoctorReview: mocks.save, getDoctorFileAccess: mocks.file }
})
vi.mock('../../api/analysisResult', async (importOriginal) => ({
  ...await importOriginal<typeof import('../../api/analysisResult')>(), getProtectedContent: mocks.content,
}))
function start() {
  render(<MemoryRouter><DoctorWorkspacePage /></MemoryRouter>)
  fireEvent.change(screen.getByRole('textbox', { name: 'Clinic Context ID' }), { target: { value: clinicId } })
  fireEvent.click(screen.getByRole('button', { name: 'Tải worklist' }))
}
async function openDetail() {
  await screen.findByRole('button', { name: 'Mở chi tiết' })
  fireEvent.click(screen.getByRole('button', { name: 'Mở chi tiết' }))
  await screen.findByRole('heading', { name: 'Kết luận của bác sĩ' })
}
beforeEach(() => {
  vi.clearAllMocks()
  vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:private-doctor-fixture')
  vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {})
  mocks.content.mockResolvedValue(new Blob(['synthetic image'], { type: 'image/png' }))
  mocks.worklist.mockResolvedValue([data])
  mocks.detail.mockResolvedValue(data)
  mocks.file.mockResolvedValue({ url: 'https://storage.example.org/retina.png',
    expiresAt: new Date(Date.now() + 60000).toISOString() })
  mocks.save.mockResolvedValue({ reviewId: 'a0000000-0000-4000-8000-00000000000a', analysisId,
    version: 2, isApproved: true })
})
afterEach(() => { cleanup(); vi.restoreAllMocks() })

describe('SCRUM-354: Doctor workspace', () => {
  it('mở worklist qua clinicContextId, xác nhận kết quả và cập nhật ngay trạng thái', async () => {
    start(); await openDetail()
    fireEvent.click(screen.getByRole('button', { name: 'Xác nhận và lưu đánh giá' }))
    await waitFor(() => expect(mocks.save).toHaveBeenCalledWith(
      mocks.context, clinicId, analysisId, expect.objectContaining({ baseVersion: 1, isApproved: true })))
    expect(await screen.findByText(/Ca chụp đã được đánh giá/)).toBeTruthy()
    expect(screen.queryByRole('button', { name: 'Xác nhận và lưu đánh giá' })).toBeNull()
  })

  it('HTTP 409 mở modal, khóa bản cũ, reload kết quả mới nhất thay vì ghi đè', async () => {
    const { DoctorApiError } = await import('../../api/doctorReview')
    mocks.save.mockRejectedValue(new DoctorApiError(409, 'ERR_REVIEW_VERSION_CONFLICT'))
    mocks.detail.mockResolvedValueOnce(data).mockResolvedValueOnce({ ...data, reviewStatus: 'REVIEWED', reviewVersion: 2 })
    start(); await openDetail()
    fireEvent.click(screen.getByRole('button', { name: 'Xác nhận và lưu đánh giá' }))
    const dialog = await screen.findByRole('alertdialog')
    expect(dialog.textContent).toContain('không được lưu')
    expect(mocks.save).toHaveBeenCalledTimes(1)
    fireEvent.click(screen.getByRole('button', { name: 'Tải phiên bản mới nhất' }))
    await waitFor(() => expect(screen.queryByRole('alertdialog')).toBeNull())
    expect(await screen.findByText(/Ca chụp đã được đánh giá/)).toBeTruthy()
    expect(mocks.save).toHaveBeenCalledTimes(1)
  })

  it('lệnh tìm theo mức nguy cơ không hiển thị ca sai bộ lọc', async () => {
    start()
    await screen.findByRole('button', { name: 'Mở chi tiết' })
    fireEvent.change(screen.getByRole('combobox', { name: 'Lọc mức nguy cơ' }), { target: { value: 'LOW' } })
    expect(screen.queryByRole('button', { name: 'Mở chi tiết' })).toBeNull()
  })
})


it('audit: revoked access on review must clear the old patient detail and images', async () => {
  const { DoctorApiError } = await import('../../api/doctorReview')
  mocks.save.mockRejectedValue(new DoctorApiError(403, 'ERR_FORBIDDEN'))
  start(); await openDetail()
  fireEvent.click(screen.getByRole('button', { name: 'Xác nhận và lưu đánh giá' }))
  await screen.findByRole('alert')
  expect(screen.queryByRole('region', { name: 'Chi tiết ca chụp' }) === null).toBe(true)
})

it('audit: a late conflict for case A must not lock newly selected case B', async () => {
  const { DoctorApiError } = await import('../../api/doctorReview')
  const { act } = await import('@testing-library/react')
  const other = { ...data, analysisId: '20000000-0000-4000-8000-00000000000b' }
  mocks.worklist.mockResolvedValue([data, other])
  mocks.detail.mockResolvedValueOnce(data).mockResolvedValueOnce(other)
  let reject: (reason: unknown) => void = () => {}
  mocks.save.mockImplementation(() => new Promise((_resolve, no) => { reject = no }))
  start(); await screen.findAllByRole('button', { name: 'Mở chi tiết' })
  fireEvent.click(screen.getAllByRole('button', { name: 'Mở chi tiết' })[0])
  await screen.findByRole('heading', { name: 'Kết luận của bác sĩ' })
  fireEvent.click(screen.getByRole('button', { name: 'Xác nhận và lưu đánh giá' }))
  await waitFor(() => expect(mocks.save).toHaveBeenCalledOnce())
  fireEvent.click(screen.getAllByRole('button', { name: 'Mở chi tiết' })[1])
  await screen.findByText(other.analysisId)
  await act(async () => reject(new DoctorApiError(409, 'ERR_CONCURRENT_REVIEW')))
  expect(screen.queryByRole('alertdialog') === null).toBe(true)
})

it('fetches protected bytes and revokes object URLs when the selected case changes', async () => {
  start(); await openDetail()
  await waitFor(() => expect(URL.createObjectURL).toHaveBeenCalledTimes(2))
  expect(mocks.content).toHaveBeenCalledWith(expect.any(String),
    { ...mocks.context, clinicContextId: clinicId }, expect.any(AbortSignal))
  fireEvent.click(screen.getByRole('button', { name: 'Làm mới chi tiết' }))
  expect(URL.revokeObjectURL).toHaveBeenCalledTimes(2)
})

it('keeps the current in-memory draft after a transient save failure', async () => {
  const { DoctorApiError } = await import('../../api/doctorReview')
  mocks.save.mockRejectedValue(new DoctorApiError(503, 'ERR_UNAVAILABLE'))
  start(); await openDetail()
  const notes = screen.getByRole('textbox', { name: 'Ghi chú / khuyến nghị điều trị' })
  fireEvent.change(notes, { target: { value: 'Synthetic draft' } })
  fireEvent.click(screen.getByRole('button', { name: 'Xác nhận và lưu đánh giá' }))
  await screen.findByRole('alert')
  expect((notes as HTMLTextAreaElement).value).toBe('Synthetic draft')
  expect(screen.getByRole('region', { name: 'Chi tiết ca chụp' })).toBeTruthy()
})
