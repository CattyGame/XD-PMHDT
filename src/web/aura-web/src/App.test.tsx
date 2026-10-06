import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import App from './App'

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

describe('AURA connection check', () => {
  it('displays the service after a successful request', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      json: async () => ({
        service: 'AURA.Analysis.Api',
        message: 'Template API is running',
        timestamp: '2026-10-06T10:00:00Z',
      }),
    })

    vi.stubGlobal('fetch', fetchMock)

    render(<App />)

    fireEvent.click(
      screen.getByRole('button', { name: 'Kiểm tra kết nối' }),
    )

    expect(await screen.findByText('AURA.Analysis.Api')).toBeTruthy()
    expect(fetchMock).toHaveBeenCalledWith('/api/v1/platform/ping')
  })

  it('displays an error when the gateway returns 502', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({
        ok: false,
        status: 502,
      }),
    )

    render(<App />)

    fireEvent.click(
      screen.getByRole('button', { name: 'Kiểm tra kết nối' }),
    )

    expect((await screen.findByRole('alert')).textContent).toBe(
      'Không thể kết nối tới hệ thống.',
    )
  })
})