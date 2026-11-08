import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import DoctorReviewForm from './DoctorReviewForm'

afterEach(cleanup)

const onSubmit = vi.fn(async () => {})
describe('SCRUM-354: DoctorReviewForm', () => {
  it('duyệt kết quả AI với baseVersion gốc', async () => {
    onSubmit.mockClear()
    render(<DoctorReviewForm baseVersion={4} aiRisk="HIGH" onSubmit={onSubmit} />)
    fireEvent.change(screen.getByPlaceholderText('Nhập nhận xét y khoa và khuyến nghị theo đánh giá của bác sĩ'),
      { target: { value: 'Hẹn tái khám' } })
    fireEvent.click(screen.getByRole('button', { name: 'Xác nhận và lưu đánh giá' }))
    await waitFor(() => expect(onSubmit).toHaveBeenCalledWith({ isApproved: true, baseVersion: 4, doctorNotes: 'Hẹn tái khám' }))
  })

  it('yêu cầu lý do khi bác sĩ chỉnh mức nguy cơ', async () => {
    onSubmit.mockClear()
    render(<DoctorReviewForm baseVersion={1} aiRisk={null} onSubmit={onSubmit} />)
    fireEvent.click(screen.getByLabelText('Điều chỉnh mức nguy cơ'))
    fireEvent.click(screen.getByRole('button', { name: 'Xác nhận và lưu đánh giá' }))
    expect(screen.getByRole('alert').textContent).toContain('lý do')
    expect(onSubmit).not.toHaveBeenCalled()
    fireEvent.change(screen.getByLabelText('Lý do điều chỉnh'), { target: { value: '  Kiểm tra lại  ' } })
    fireEvent.change(screen.getByLabelText('Mức nguy cơ sau điều chỉnh'), { target: { value: 'MODERATE' } })
    fireEvent.click(screen.getByRole('button', { name: 'Xác nhận và lưu đánh giá' }))
    await waitFor(() => expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({
      isApproved: false, correctedRiskLevel: 'MODERATE', reason: 'Kiểm tra lại', baseVersion: 1,
    })))
  })

  it('không cho gửi lại ca REVIEWED', () => {
    onSubmit.mockClear()
    render(<DoctorReviewForm baseVersion={2} aiRisk={null} reviewed onSubmit={onSubmit} />)
    expect(screen.getByRole('status').textContent).toContain('đã được đánh giá')
    expect(screen.queryByRole('button', { name: 'Xác nhận và lưu đánh giá' })).toBeNull()
  })
})
