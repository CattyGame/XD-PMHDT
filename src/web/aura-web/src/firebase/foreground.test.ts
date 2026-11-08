import { expect, it, vi } from 'vitest'
import type { MessagePayload } from 'firebase/messaging'
const mocks = vi.hoisted(() => ({ listen: vi.fn() }))
vi.mock('./messaging', () => ({ listenForegroundMessages: mocks.listen }))
import { startForegroundNotifications } from './foreground'

it('deduplicates stable notification IDs and displays neutral content only', async () => {
  const show = vi.fn()
  await startForegroundNotifications(show)
  const receive = mocks.listen.mock.calls[0][0] as (payload: MessagePayload) => void
  const payload: MessagePayload = { from: 'test', collapseKey: '', messageId: 'one', data: { notificationId: 'canonical-event', patientName: 'PRIVATE' } }
  receive(payload); receive(payload)
  expect(show).toHaveBeenCalledTimes(1)
  expect(show.mock.calls[0][0]).not.toContain('PRIVATE')
  receive({ ...payload, messageId: 'two', data: { notificationId: 'another-event' } })
  expect(show).toHaveBeenCalledTimes(2)
})
