import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import InteractiveImageViewer from './InteractiveImageViewer'

afterEach(cleanup)

describe('SCRUM-354: InteractiveImageViewer', () => {
  it('zoom, xoay, reset, bật tắt overlay và chế độ song song', () => {
    render(<InteractiveImageViewer originalUrl="https://example.org/retina.png"
      vesselUrl="https://example.org/vessel.png" vesselKind="mask" />)
    fireEvent.click(screen.getByRole('button', { name: 'Phóng to' }))
    expect(screen.getByLabelText('Tỷ lệ phóng to').textContent).toBe('125%')
    fireEvent.click(screen.getByRole('button', { name: 'Xoay 90°' }))
    fireEvent.change(screen.getByRole('slider', { name: 'Độ trong suốt lớp phủ' }), { target: { value: '30' } })
    expect((screen.getByRole('slider') as HTMLInputElement).value).toBe('30')
    fireEvent.click(screen.getByLabelText('So sánh song song'))
    expect(screen.getByText('Ảnh gốc')).toBeTruthy()
    expect(screen.getByText('Ảnh mạch máu (mask)')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Đặt lại' }))
    expect(screen.getByLabelText('Tỷ lệ phóng to').textContent).toBe('100%')
  })

  it('khóa công cụ overlay khi API chưa cung cấp mask', () => {
    render(<InteractiveImageViewer originalUrl="https://example.org/retina.png" vesselUrl={null} vesselKind={null} />)
    expect((screen.getByLabelText('Lớp phủ mạch máu') as HTMLInputElement).disabled).toBe(true)
    expect(screen.getByText(/API chưa trả mask\/overlay/)).toBeTruthy()
  })
})
