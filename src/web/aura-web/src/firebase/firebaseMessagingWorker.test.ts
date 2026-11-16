import { runInNewContext } from 'node:vm'
import { expect, it, vi } from 'vitest'
import source from './firebaseMessagingWorker.js?raw'
import { safeNotificationPath } from './notificationNavigation'

function worker(configured = false) {
  const handlers = new Map<string, (event: unknown) => void>()
  const open = vi.fn(), navigate = vi.fn(), focus = vi.fn()
  const windows: { url: string; navigate: typeof navigate; focus: typeof focus }[] = []
  const initializeApp = vi.fn()
  const showNotification = vi.fn()
  let receive: ((payload: unknown) => unknown) | undefined
  const code = source.replace(/^import[\s\S]*?from 'firebase\/(?:app|messaging\/sw)'/gm, '')
    .replace(/^import .* from '\.\/notificationNavigation'/m, '')
    .replaceAll('import.meta.env.', 'env.')
  const env = configured ? Object.fromEntries(['API_KEY','AUTH_DOMAIN','PROJECT_ID','MESSAGING_SENDER_ID','APP_ID']
    .map(key => ['VITE_FIREBASE_' + key, 'synthetic-fixture'])) : {}
  runInNewContext(code, { env, URL, safeNotificationPath, initializeApp, getMessaging: vi.fn(),
    onBackgroundMessage: (_messaging: unknown, handler: typeof receive) => { receive=handler },
    self: { location: { origin: 'https://aura.example' }, addEventListener: (name: string, handler: (event: unknown) => void) => handlers.set(name, handler),
      registration: { showNotification },
      clients: { matchAll: async () => windows, openWindow: open } } })
  async function click(targetPath: string) {
    let pending: Promise<unknown> | undefined
    handlers.get('notificationclick')!({ notification: { close: vi.fn(), data: { FCM_MSG: { data: { targetPath } } } },
      stopImmediatePropagation: vi.fn(), waitUntil: (promise: Promise<unknown>) => { pending = promise } })
    await pending
  }
  return { click, windows, open, navigate, focus, initializeApp, showNotification,
    background: async (payload: unknown) => { await receive?.(payload) } }
}

it('keeps the clinic target in data-only background notifications and never displays raw payload data', async () => {
  const w=worker(true)
  const path='/analyses/40000000-0000-4000-8000-000000000004?clinicContextId=50000000-0000-4000-8000-000000000005'
  await w.background({ data: { targetPath:path, patientName:'PRIVATE', diagnosis:'PRIVATE' } })
  expect(w.showNotification).toHaveBeenCalledWith('AURA - Thông báo mới', expect.objectContaining({ data: { targetPath:path } }))
  expect(JSON.stringify(w.showNotification.mock.calls)).not.toContain('PRIVATE')
})

it('opens the exact payment link and rejects external notification targets', async () => {
  const w = worker()
  const path = '/billing/orders/40000000-0000-4000-8000-000000000004'
  await w.click(path)
  expect(w.open).toHaveBeenLastCalledWith(`https://aura.example${path}`)
  await w.click('https://evil.example/steal')
  expect(w.open).toHaveBeenLastCalledWith('https://aura.example/notifications')
  expect(w.initializeApp).not.toHaveBeenCalled()
})

it('navigates an existing same-origin tab before focusing it', async () => {
  const w = worker()
  w.windows.push({ url: 'https://aura.example/', navigate: w.navigate, focus: w.focus })
  await w.click('/analyses/40000000-0000-4000-8000-000000000004')
  expect(w.navigate).toHaveBeenCalledWith('https://aura.example/analyses/40000000-0000-4000-8000-000000000004')
  expect(w.focus).toHaveBeenCalledTimes(1)
  expect(w.open).not.toHaveBeenCalled()
})
