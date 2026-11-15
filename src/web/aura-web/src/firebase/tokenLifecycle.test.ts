import { beforeEach, describe, expect, it, vi } from 'vitest'
import { refreshFcmToken } from './tokenLifecycle'

const mocks = vi.hoisted(() => ({
  obtainToken: vi.fn(),
  register: vi.fn(),
}))

vi.mock('./messaging', () => ({
  obtainFcmToken: mocks.obtainToken,
}))

vi.mock('../api/notificationDevices', () => ({
  registerNotificationDevice: mocks.register,
}))

vi.mock('./config', () => ({
  getFirebaseMessaging: vi.fn(),
}))

vi.mock('firebase/messaging', () => ({
  deleteToken: vi.fn(),
}))

beforeEach(() => {
  vi.resetAllMocks()
  mocks.register.mockResolvedValue(undefined)
})

describe('Đồng bộ FCM token với backend', () => {
  it('đồng bộ token thay đổi qua các lần lấy token', async () => {
    mocks.obtainToken
      .mockResolvedValueOnce({
        status: 'ready',
        token: 'synthetic-old-token',
      })
      .mockResolvedValueOnce({
        status: 'ready',
        token: 'synthetic-new-token',
      })

    await refreshFcmToken()
    await refreshFcmToken()

    expect(mocks.register).toHaveBeenNthCalledWith(
      1,
      'synthetic-old-token',
      expect.any(Function),
    )
    expect(mocks.register).toHaveBeenNthCalledWith(
      2,
      'synthetic-new-token',
      expect.any(Function),
    )
  })

  it('đăng ký lại token không đổi để phục hồi registration', async () => {
    mocks.obtainToken.mockResolvedValue({
      status: 'ready',
      token: 'synthetic-same-token',
    })

    await refreshFcmToken()
    await refreshFcmToken()

    expect(mocks.register).toHaveBeenCalledTimes(2)
    expect(mocks.register).toHaveBeenLastCalledWith(
      'synthetic-same-token',
      expect.any(Function),
    )
  })

  it('không đăng ký khi chưa có quyền thông báo', async () => {
    mocks.obtainToken.mockResolvedValue({
      status: 'permission-required',
    })

    await expect(refreshFcmToken()).resolves.toEqual({
      status: 'permission-required',
    })
    expect(mocks.register).not.toHaveBeenCalled()
  })

  it('không báo thành công trước khi backend hoàn tất', async () => {
    mocks.obtainToken.mockResolvedValue({
      status: 'ready',
      token: 'synthetic-token',
    })

    let finishRegistration!: () => void

    mocks.register.mockImplementation(
      () =>
        new Promise<void>((resolve) => {
          finishRegistration = resolve
        }),
    )

    let completed = false
    const pending = refreshFcmToken().then((result) => {
      completed = true
      return result
    })

    await vi.waitFor(() => {
      expect(mocks.register).toHaveBeenCalledTimes(1)
    })

    expect(completed).toBe(false)

    finishRegistration()

    await expect(pending).resolves.toEqual({ status: 'ready' })
  })

  it('backend lỗi thì báo lỗi và lần sau vẫn thử đăng ký lại', async () => {
    mocks.obtainToken.mockResolvedValue({
      status: 'ready',
      token: 'synthetic-retry-token',
    })

    mocks.register
      .mockRejectedValueOnce(new Error('Backend unavailable'))
      .mockResolvedValueOnce(undefined)

    await expect(refreshFcmToken()).rejects.toThrow(
      'Backend unavailable',
    )
    await expect(refreshFcmToken()).resolves.toEqual({
      status: 'ready',
    })

    expect(mocks.register).toHaveBeenCalledTimes(2)
  })
})

it('phiên hết hiệu lực trước khi đăng ký thì không gọi backend', async () => {
  mocks.obtainToken.mockResolvedValue({
    status: 'ready',
    token: 'synthetic-cancelled-token',
  })

  await expect(refreshFcmToken(() => false)).resolves.toEqual({
    status: 'cancelled',
  })

  expect(mocks.register).not.toHaveBeenCalled()
})


it('returns cancelled when session changes while backend is pending', async () => {
  mocks.obtainToken.mockResolvedValue({
    status: 'ready',
    token: 'synthetic-session-change-token',
  })

  let currentSession = true
  let finishRegistration!: () => void

  mocks.register.mockImplementationOnce(
    () => new Promise<void>((resolve) => {
      finishRegistration = resolve
    }),
  )

  const pending = refreshFcmToken(() => currentSession)

  await vi.waitFor(() => {
    expect(mocks.register).toHaveBeenCalledTimes(1)
  })

  currentSession = false
  finishRegistration()

  await expect(pending).resolves.toEqual({
    status: 'cancelled',
  })
})
