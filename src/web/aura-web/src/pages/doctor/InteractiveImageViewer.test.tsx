import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import InteractiveImageViewer from './InteractiveImageViewer'

afterEach(cleanup)

describe('SCRUM-354: InteractiveImageViewer', () => {
  it('zoom, xoay, reset, bật tắt overlay và chế độ song song', () => {
    render(<InteractiveImageViewer originalUrl="https://example.org/retina.png"
      layers={{ mask: 'https://example.org/vessel.png' }} />)
    fireEvent.click(screen.getByRole('button', { name: 'Phóng to' }))
    expect(screen.getByLabelText('Tỷ lệ phóng to').textContent).toBe('125%')
    fireEvent.click(screen.getByRole('button', { name: 'Xoay 90°' }))
    fireEvent.change(screen.getByRole('slider', { name: 'Độ trong suốt lớp phủ' }), { target: { value: '30' } })
    expect((screen.getByRole('slider') as HTMLInputElement).value).toBe('30')
    fireEvent.click(screen.getByLabelText('So sánh song song'))
    expect(screen.getByText('Ảnh gốc')).toBeTruthy()
    expect(screen.getByText('Frangi · mask', { selector: 'p' })).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Đặt lại' }))
    expect(screen.getByLabelText('Tỷ lệ phóng to').textContent).toBe('100%')
  })

  it('khóa công cụ overlay khi API chưa cung cấp mask', () => {
    render(<InteractiveImageViewer originalUrl="https://example.org/retina.png" layers={{}} />)
    expect((screen.getByLabelText('Lớp phủ mạch máu') as HTMLInputElement).disabled).toBe(true)
    expect(screen.getByText(/Chưa có lớp ảnh/)).toBeTruthy()
  })

  it('renders the Grad-CAM crop separately from the original even with overlay selected', () => {
    render(<InteractiveImageViewer originalUrl="blob:original" layers={{ overlay: 'blob:frangi', heatmap: 'blob:crop' }} />)
    fireEvent.change(screen.getByRole('combobox', { name: 'Lớp ảnh' }), { target: { value: 'heatmap' } })
    const original = screen.getByRole('img', { name: 'Ảnh võng mạc gốc' })
    const crop = screen.getByRole('img', { name: 'Grad-CAM · ảnh cắt 224 × 224' })
    expect(original.parentElement).not.toBe(crop.parentElement)
    expect((screen.getByRole('slider') as HTMLInputElement).disabled).toBe(true)
  })
})
