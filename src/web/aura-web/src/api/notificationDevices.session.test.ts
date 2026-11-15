import { afterEach, expect, it, vi } from 'vitest'
import { registerNotificationDevice } from './notificationDevices'

const mocks = vi.hoisted(() => ({ token: vi.fn() }))
vi.mock('../firebase/auth', () => ({ getNotificationIdToken: mocks.token }))
vi.mock('./client', () => ({ isMockEnabled: false }))
afterEach(() => { localStorage.clear(); vi.unstubAllGlobals() })

it('does not bind an FCM token if the account changes while awaiting its bearer', async () => {
  let finish: (value: string) => void = () => {}
  mocks.token.mockImplementation(() => new Promise(resolve => { finish = resolve }))
  const fetcher = vi.fn(); vi.stubGlobal('fetch', fetcher)
  let current = true
  const pending = registerNotificationDevice('synthetic-fcm-token', () => current)
  const denied = expect(pending).rejects.toMatchObject({ code: 'ERR_SESSION_CHANGED' })
  current = false; finish('synthetic-bearer')
  await denied
  expect(fetcher).not.toHaveBeenCalled()
})
