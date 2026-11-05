import { runInNewContext } from 'node:vm'
import { expect, it, vi } from 'vitest'
import source from './firebaseMessagingWorker.js?raw'

function worker() {
  const handlers = new Map<string, (event: unknown) => void>()
  const open = vi.fn(), navigate = vi.fn(), focus = vi.fn()
  const windows: { url: string; navigate: typeof navigate; focus: typeof focus }[] = []
  const initializeApp = vi.fn()
  const code = source.replace(/^import[\s\S]*?from 'firebase\/(?:app|messaging\/sw)'/gm, '')
    .replaceAll('import.meta.env.', 'env.')
  runInNewContext(code, { env: {}, URL, initializeApp, getMessaging: vi.fn(), onBackgroundMessage: vi.fn(),
    self: { location: { origin: 'https://aura.example' }, addEventListener: (name: string, handler: (event: unknown) => void) => handlers.set(name, handler),
      clients: { matchAll: async () => windows, openWindow: open } } })
  async function click(targetPath: string) {
    let pending: Promise<unknown> | undefined
    handlers.get('notificationclick')!({ notification: { close: vi.fn(), data: { FCM_MSG: { data: { targetPath } } } },
      stopImmediatePropagation: vi.fn(), waitUntil: (promise: Promise<unknown>) => { pending = promise } })
    await pending
  }
  return { click, windows, open, navigate, focus, initializeApp }
}

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
