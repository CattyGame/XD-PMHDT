// @vitest-environment jsdom

import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react'
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
} from 'vitest'
import type { User } from 'firebase/auth'
import NotificationSettings from './NotificationSettings'
import { NotificationDeviceError } from '../api/notificationDevices'

const mocks = vi.hoisted(() => ({
  mockEnabled: false,
  initialUser: null as User | null,
  observeUser: vi.fn(),
  login: vi.fn(),
  logout: vi.fn(),
  getPermission: vi.fn(),
  requestPermission: vi.fn(),
  obtainToken: vi.fn(),
  register: vi.fn(),
  unregister: vi.fn(),
  clearFcm: vi.fn(),
}))

vi.mock('../api/client', () => ({
  get isMockEnabled() {
    return mocks.mockEnabled
  },
}))

vi.mock('../firebase/auth', () => ({
  observeNotificationUser: mocks.observeUser,
  loginForNotifications: mocks.login,
  logoutFirebaseNotificationSession: mocks.logout,
  getNotificationIdToken: vi.fn(),
}))

vi.mock('../firebase/notificationPermission', () => ({
  getNotificationPermission: mocks.getPermission,
  requestNotificationPermission: mocks.requestPermission,
}))

vi.mock('../firebase/messaging', () => ({
  obtainFcmToken: mocks.obtainToken,
}))

vi.mock('../firebase/tokenLifecycle', async (importOriginal) => {
  const actual = await importOriginal<
    typeof import('../firebase/tokenLifecycle')
  >()

  return {
    ...actual,
    clearFcmOnLogout: mocks.clearFcm,
  }
})

vi.mock('../api/notificationDevices', async (importOriginal) => {
  const actual = await importOriginal<
    typeof import('../api/notificationDevices')
  >()

  return {
    ...actual,
    registerNotificationDevice: mocks.register,
    unregisterNotificationDevice: mocks.unregister,
  }
})

const testUser = {
  uid: 'test-firebase-user',
  email: 'test@example.com',
} as User

beforeEach(() => {
  vi.resetAllMocks()
  vi.stubGlobal('isSecureContext', true)

  mocks.mockEnabled = false
  mocks.initialUser = null

  mocks.observeUser.mockImplementation(
    (callback: (user: User | null) => void) => {
      callback(mocks.initialUser)
      return () => {}
    },
  )

  mocks.login.mockResolvedValue(testUser)
  mocks.logout.mockResolvedValue(undefined)
  mocks.getPermission.mockReturnValue('default')
  mocks.requestPermission.mockResolvedValue('granted')
  mocks.obtainToken.mockResolvedValue({
    status: 'ready',
    token: 'private-test-fcm-token',
  })
  mocks.register.mockResolvedValue(undefined)
  mocks.unregister.mockResolvedValue(undefined)
  mocks.clearFcm.mockResolvedValue({ status: 'deleted' })
})

afterEach(() => {
  vi.restoreAllMocks()
  cleanup()
  vi.unstubAllGlobals()
})

function renderSignedIn() {
  mocks.initialUser = testUser
  return render(<NotificationSettings />)
}

function enableNotifications() {
  fireEvent.click(
    screen.getByRole('button', { name: 'Bật thông báo' }),
  )
}

describe('Đăng nhập và đăng ký thông báo', () => {
  it('không tự đăng nhập, xin quyền hoặc đăng ký khi mở trang', () => {
    render(<NotificationSettings />)

    expect(mocks.login).not.toHaveBeenCalled()
    expect(mocks.requestPermission).not.toHaveBeenCalled()
    expect(mocks.obtainToken).not.toHaveBeenCalled()
    expect(mocks.register).not.toHaveBeenCalled()
  })

  it('chưa đăng nhập thì khóa nút bật thông báo', () => {
    render(<NotificationSettings />)

    expect(
      screen.getByRole('button', { name: 'Bật thông báo' }),
    ).toHaveProperty('disabled', true)

    enableNotifications()

    expect(mocks.requestPermission).not.toHaveBeenCalled()
    expect(mocks.obtainToken).not.toHaveBeenCalled()
    expect(mocks.register).not.toHaveBeenCalled()
  })

  it('đăng nhập thành công và xóa mật khẩu khỏi state nhập liệu', async () => {
    render(<NotificationSettings />)

    const passwordInput = screen.getByLabelText(
      'Mật khẩu Firebase',
    ) as HTMLInputElement

    fireEvent.change(screen.getByLabelText('Email Firebase'), {
      target: { value: 'test@example.com' },
    })

    fireEvent.change(passwordInput, {
      target: { value: 'test-password' },
    })

    fireEvent.click(
      screen.getByRole('button', { name: 'Đăng nhập thông báo' }),
    )

    await screen.findByText('Đã đăng nhập: test@example.com')

    expect(mocks.login).toHaveBeenCalledWith(
      'test@example.com',
      'test-password',
    )
    expect(screen.queryByLabelText('Mật khẩu Firebase')).toBeNull()
    expect(
      screen.getByRole('button', { name: 'Bật thông báo' }),
    ).toHaveProperty('disabled', false)
    expect(document.body.textContent).not.toContain('test-password')
    expect(mocks.register).not.toHaveBeenCalled()
  })

  it('đăng nhập thất bại xóa mật khẩu và không lộ lỗi nội bộ', async () => {
    mocks.login.mockRejectedValue(
      new Error('internal-private-auth-detail'),
    )

    render(<NotificationSettings />)

    fireEvent.change(screen.getByLabelText('Email Firebase'), {
      target: { value: 'test@example.com' },
    })

    fireEvent.change(screen.getByLabelText('Mật khẩu Firebase'), {
      target: { value: 'test-password' },
    })

    fireEvent.click(
      screen.getByRole('button', { name: 'Đăng nhập thông báo' }),
    )

    const alert = await screen.findByRole('alert')

    expect(alert.textContent).toContain('Đăng nhập thất bại')
    expect(alert.textContent).not.toContain(
      'internal-private-auth-detail',
    )

    expect(
      (screen.getByLabelText('Mật khẩu Firebase') as HTMLInputElement)
        .value,
    ).toBe('')

    expect(mocks.register).not.toHaveBeenCalled()
  })

  it.each(['denied', 'unsupported'])(
    'quyền %s khóa nút và không lấy token',
    (permission) => {
      mocks.getPermission.mockReturnValue(permission)

      renderSignedIn()
      enableNotifications()

      expect(
        screen.getByRole('button', { name: 'Bật thông báo' }),
      ).toHaveProperty('disabled', true)
      expect(mocks.requestPermission).not.toHaveBeenCalled()
      expect(mocks.obtainToken).not.toHaveBeenCalled()
      expect(mocks.register).not.toHaveBeenCalled()
    },
  )

  it('môi trường không an toàn khóa nút', () => {
    vi.stubGlobal('isSecureContext', false)

    renderSignedIn()

    expect(screen.getByRole('status').textContent).toContain(
      'HTTPS hoặc localhost',
    )
    expect(
      screen.getByRole('button', { name: 'Bật thông báo' }),
    ).toHaveProperty('disabled', true)
  })

  it('mock bật không cho đăng nhập hoặc đăng ký thật', () => {
    mocks.mockEnabled = true

    render(<NotificationSettings />)

    expect(
      screen.getByRole('button', { name: 'Đăng nhập thông báo' }),
    ).toHaveProperty('disabled', true)
    expect(
      screen.getByRole('button', { name: 'Bật thông báo' }),
    ).toHaveProperty('disabled', true)

    expect(mocks.login).not.toHaveBeenCalled()
    expect(mocks.register).not.toHaveBeenCalled()
  })

  it('đăng ký token sau khi được cấp quyền và không hiển thị token', async () => {
    const { container } = renderSignedIn()

    enableNotifications()

    await screen.findByText(
      /Đã đăng ký thiết bị với backend/,
    )

    expect(mocks.requestPermission).toHaveBeenCalledTimes(1)
    expect(mocks.obtainToken).toHaveBeenCalledTimes(1)
    expect(mocks.register).toHaveBeenCalledWith(
      'private-test-fcm-token',
      expect.any(Function),
    )
    expect(container.textContent).not.toContain(
      'private-test-fcm-token',
    )
    expect(screen.getByRole('status').textContent).toContain(
      'Chưa kiểm chứng gửi và nhận push thật',
    )
  })

  it.each(['denied', 'default', 'unsupported'])(
    'xin quyền trả %s thì không lấy hoặc đăng ký token',
    async (permission) => {
      mocks.requestPermission.mockResolvedValue(permission)

      renderSignedIn()
      enableNotifications()

      await waitFor(() => {
        expect(
          screen.queryByRole('button', { name: 'Đang xử lý...' }),
        ).toBeNull()
      })

      expect(mocks.requestPermission).toHaveBeenCalledTimes(1)
      expect(mocks.obtainToken).not.toHaveBeenCalled()
      expect(mocks.register).not.toHaveBeenCalled()
    },
  )

  it.each([
    {
      status: 'not-configured',
      expected: 'Chưa có cấu hình Firebase/VAPID',
    },
    {
      status: 'permission-required',
      expected: 'Chưa được cấp quyền thông báo',
    },
    {
      status: 'unsupported',
      expected: 'Môi trường hiện tại không hỗ trợ FCM',
    },
    {
      status: 'error',
      expected: 'Không thể lấy FCM token',
    },
  ])(
    'FCM trả $status thì không gọi đăng ký backend',
    async ({ status, expected }) => {
      mocks.obtainToken.mockResolvedValue({
        status,
        message: 'internal-private-detail',
      })

      renderSignedIn()
      enableNotifications()

      const alert = await screen.findByRole('alert')

      expect(alert.textContent).toContain(expected)
      expect(alert.textContent).not.toContain(
        'internal-private-detail',
      )
      expect(mocks.register).not.toHaveBeenCalled()
    },
  )

  it.each([
    {
      status: 401,
      code: 'ERR_UNAUTHORIZED',
      expected: 'Phiên đăng nhập không hợp lệ',
    },
    {
      status: 409,
      code: 'ERR_DEVICE_CONFLICT',
      expected: 'Không thể liên kết thiết bị',
    },
    {
      status: 503,
      code: 'ERR_STORAGE_UNAVAILABLE',
      expected: 'Chưa xác nhận được đăng ký thiết bị',
    },
  ])(
    'backend trả $status thì hiển thị hướng xử lý',
    async ({ status, code, expected }) => {
      mocks.register.mockRejectedValue(
        new NotificationDeviceError(status, code),
      )

      renderSignedIn()
      enableNotifications()

      const alert = await screen.findByRole('alert')

      expect(alert.textContent).toContain(expected)
      expect(
        screen.queryByText(/Đã đăng ký thiết bị với backend/),
      ).toBeNull()
      expect(
        screen.getByRole('button', { name: 'Bật thông báo' }),
      ).toHaveProperty('disabled', false)
    },
  )

  it('đang đăng ký khóa cả bật thông báo và đăng xuất', async () => {
    let resolveRegistration!: () => void

    mocks.register.mockReturnValue(
      new Promise<void>((resolve) => {
        resolveRegistration = resolve
      }),
    )

    renderSignedIn()
    enableNotifications()

    await waitFor(() => {
      expect(mocks.register).toHaveBeenCalledTimes(1)
    })

    expect(
      screen.getByRole('button', { name: 'Đang xử lý...' }),
    ).toHaveProperty('disabled', true)
    expect(
      screen.getByRole('button', { name: 'Đăng xuất' }),
    ).toHaveProperty('disabled', true)

    resolveRegistration()

    await screen.findByText(/Đã đăng ký thiết bị với backend/)

    expect(
      screen.getByRole('button', { name: 'Bật thông báo' }),
    ).toHaveProperty('disabled', false)
  })

  it('đăng xuất theo thứ tự hủy backend, xóa FCM, đăng xuất Firebase', async () => {
    renderSignedIn()

    fireEvent.click(
      screen.getByRole('button', { name: 'Đăng xuất' }),
    )

    await screen.findByText(
      'Đã hủy liên kết thiết bị và đăng xuất.',
    )

    expect(mocks.unregister).toHaveBeenCalledTimes(1)
    expect(mocks.clearFcm).toHaveBeenCalledTimes(1)
    expect(mocks.logout).toHaveBeenCalledTimes(1)

    expect(
      mocks.unregister.mock.invocationCallOrder[0],
    ).toBeLessThan(mocks.clearFcm.mock.invocationCallOrder[0])

    expect(
      mocks.clearFcm.mock.invocationCallOrder[0],
    ).toBeLessThan(mocks.logout.mock.invocationCallOrder[0])

    expect(
      screen.getByRole('button', { name: 'Đăng nhập thông báo' }),
    ).toBeTruthy()
  })

  it('hủy backend thất bại thì giữ phiên và không xóa FCM', async () => {
    mocks.unregister.mockRejectedValue(new Error('storage failure'))

    renderSignedIn()

    fireEvent.click(
      screen.getByRole('button', { name: 'Đăng xuất' }),
    )

    const alert = await screen.findByRole('alert')

    expect(alert.textContent).toContain(
      'Phiên đăng nhập được giữ',
    )
    expect(mocks.clearFcm).not.toHaveBeenCalled()
    expect(mocks.logout).not.toHaveBeenCalled()
    expect(
      screen.getByText('Đã đăng nhập: test@example.com'),
    ).toBeTruthy()
  })

  it('xóa FCM thất bại thì chưa đăng xuất Firebase', async () => {
    mocks.clearFcm.mockResolvedValue({ status: 'error' })

    renderSignedIn()

    fireEvent.click(
      screen.getByRole('button', { name: 'Đăng xuất' }),
    )

    const alert = await screen.findByRole('alert')

    expect(alert.textContent).toContain(
      'chưa xóa được FCM token',
    )
    expect(mocks.unregister).toHaveBeenCalledTimes(1)
    expect(mocks.logout).not.toHaveBeenCalled()
    expect(
      screen.getByRole('button', { name: 'Đăng xuất' }),
    ).toHaveProperty('disabled', false)
  })

  it('quay lại cửa sổ cập nhật quyền nhưng không tự đăng ký', () => {
    mocks.getPermission.mockReturnValue('denied')

    renderSignedIn()

    mocks.getPermission.mockReturnValue('granted')
    fireEvent(window, new Event('focus'))

    expect(
      screen.getByRole('button', { name: 'Bật thông báo' }),
    ).toHaveProperty('disabled', false)
    expect(mocks.obtainToken).not.toHaveBeenCalled()
    expect(mocks.register).not.toHaveBeenCalled()
  })
})
vi.mock('./NotificationPushTestPanel', () => ({ default: () => null }))

describe('Đồng bộ tự động khi quay lại tab', () => {
  async function enableForSync() {
    mocks.getPermission.mockReturnValue('granted')
    renderSignedIn()
    await act(async () => {
      enableNotifications()
    })
    await screen.findByText(/Đã đăng ký thiết bị với backend/)
  }

  it('focus lấy và đăng ký token mới sau khi đã bật thông báo', async () => {
    await enableForSync()

    mocks.obtainToken.mockResolvedValue({
      status: 'ready',
      token: 'synthetic-refreshed-token',
    })

    fireEvent(window, new Event('focus'))

    await screen.findByText(
      'Đã đồng bộ đăng ký thông báo với backend.',
    )

    expect(mocks.register).toHaveBeenCalledTimes(2)
    expect(mocks.register).toHaveBeenLastCalledWith(
      'synthetic-refreshed-token',
      expect.any(Function),
    )
    expect(mocks.requestPermission).toHaveBeenCalledTimes(1)
  })

  it('tab ẩn không đồng bộ, tab hiện lại thì đồng bộ', async () => {
    await enableForSync()

    const visibility = vi.spyOn(document, 'visibilityState', 'get')
    visibility.mockReturnValue('hidden')

    fireEvent(document, new Event('visibilitychange'))

    expect(mocks.obtainToken).toHaveBeenCalledTimes(1)

    visibility.mockReturnValue('visible')
    fireEvent(document, new Event('visibilitychange'))

    await screen.findByText(
      'Đã đồng bộ đăng ký thông báo với backend.',
      {},
      { timeout: 3000 },
    )

    expect(mocks.obtainToken).toHaveBeenCalledTimes(2)
  })

  it('focus và visibility không tạo yêu cầu chồng nhau', async () => {
    await enableForSync()

    let finishToken!: (result: {
      status: 'ready'
      token: string
    }) => void

    mocks.obtainToken.mockImplementationOnce(
      () => new Promise((resolve) => {
        finishToken = resolve
      }),
    )

    vi.spyOn(document, 'visibilityState', 'get')
      .mockReturnValue('visible')

    fireEvent(window, new Event('focus'))
    fireEvent(document, new Event('visibilitychange'))
    fireEvent(window, new Event('focus'))

    expect(mocks.obtainToken).toHaveBeenCalledTimes(2)

    await act(async () => {
      finishToken({
        status: 'ready',
        token: 'synthetic-coalesced-token',
      })
    })

    await screen.findByText(
      'Đã đồng bộ đăng ký thông báo với backend.',
    )

    expect(mocks.register).toHaveBeenCalledTimes(2)
  })

  it('đang đồng bộ khóa đăng xuất và sau đăng xuất không đồng bộ nữa', async () => {
    await enableForSync()

    let finishToken!: (result: {
      status: 'ready'
      token: string
    }) => void

    mocks.obtainToken.mockImplementationOnce(
      () => new Promise((resolve) => {
        finishToken = resolve
      }),
    )

    fireEvent(window, new Event('focus'))

    await waitFor(() => {
      expect(
        screen.getByRole('button', { name: 'Đăng xuất' }),
      ).toHaveProperty('disabled', true)
    })

    const logoutButton = screen.getByRole('button', {
      name: 'Đăng xuất',
    })

    fireEvent.click(logoutButton)
    expect(mocks.unregister).not.toHaveBeenCalled()

    await act(async () => {
      finishToken({
        status: 'ready',
        token: 'synthetic-before-logout-token',
      })
    })

    await screen.findByText(
      'Đã đồng bộ đăng ký thông báo với backend.',
    )

    fireEvent.click(
      screen.getByRole('button', { name: 'Đăng xuất' }),
    )

    await screen.findByText(
      'Đã hủy liên kết thiết bị và đăng xuất.',
    )

    fireEvent(window, new Event('focus'))

    expect(mocks.obtainToken).toHaveBeenCalledTimes(2)
    expect(mocks.unregister).toHaveBeenCalledTimes(1)
    expect(mocks.logout).toHaveBeenCalledTimes(1)
  })

  it('backend lỗi thì cho phép đồng bộ lại lần focus sau', async () => {
    await enableForSync()

    mocks.register.mockRejectedValueOnce(
      new NotificationDeviceError(503, 'ERR_STORAGE_UNAVAILABLE'),
    )

    fireEvent(window, new Event('focus'))
    await screen.findByRole('alert')

    fireEvent(window, new Event('focus'))

    await screen.findByText(
      'Đã đồng bộ đăng ký thông báo với backend.',
    )

    expect(mocks.register).toHaveBeenCalledTimes(3)
  })

  it('phiên đổi lúc lấy token thì không đăng ký token vào backend', async () => {
    await enableForSync()

    let finishToken!: (result: {
      status: 'ready'
      token: string
    }) => void

    mocks.obtainToken.mockImplementationOnce(
      () => new Promise((resolve) => {
        finishToken = resolve
      }),
    )

    fireEvent(window, new Event('focus'))

    act(() => {
      mocks.observeUser.mock.calls[0][0](null)
    })

    await act(async () => {
      finishToken({
        status: 'ready',
        token: 'synthetic-cancelled-token',
      })
    })

    expect(mocks.register).toHaveBeenCalledTimes(1)
    expect(
      screen.getByRole('button', { name: 'Đăng nhập thông báo' }),
    ).toBeTruthy()
  })
})
