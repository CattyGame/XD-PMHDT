import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { BackendContext } from './backendContext'
import {
  getInboxPage, getUnreadCount, markInboxRead, parseInboxPage, parseUnreadCount,
  InboxError,
} from './notificationInbox'

const mocks = vi.hoisted(() => ({ auth: vi.fn(), mock: false }))
vi.mock('../firebase/config', () => ({ getFirebaseAuth: mocks.auth }))
vi.mock('./client', () => ({ get isMockEnabled() { return mocks.mock } }))

const analysisId = '50000000-0000-4000-8000-000000000005'
const notificationId = '60000000-0000-4000-8000-000000000006'
const eventId = '70000000-0000-4000-8000-000000000007'
const context: BackendContext = {
  firebaseUid: 'user-a', accountId: '10000000-0000-4000-8000-000000000001',
  patientId: '20000000-0000-4000-8000-000000000002',
  personalTenantId: '30000000-0000-4000-8000-000000000003',
  roles: ['PATIENT'], displayName: 'Patient A', profileVersion: 1,
}
const notification = {
  notificationId, eventId, analysisId, type: 'ANALYSIS_COMPLETED',
  title: 'Phân tích hoàn tất', isRead: false,
  createdAt: '2026-10-28T10:00:00Z', readAt: null,
  targetPath: `/analyses/${analysisId}`,
}
const page = { items: [notification], nextCursor: null }
const user = { uid: 'user-a', getIdToken: vi.fn(async () => 'firebase-token') }
const auth: { currentUser: typeof user | null } = { currentUser: user }
const fetchMock = vi.fn<typeof fetch>()
function json(value: unknown, status = 200): Response {
  return new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } })
}
beforeEach(() => {
  vi.resetAllMocks()
  mocks.mock = false
  user.uid = 'user-a'
  auth.currentUser = user
  mocks.auth.mockReturnValue(auth)
  user.getIdToken.mockResolvedValue('firebase-token')
  fetchMock.mockResolvedValue(json(page))
  vi.stubGlobal('fetch', fetchMock)
})
afterEach(() => { vi.unstubAllGlobals() })

describe('SCRUM-333 — Notification Inbox client', () => {
  it('accepts canonical payment links and rejects mixed or foreign identities', () => {
    const payment = { ...notification, analysisId: null, orderId: analysisId,
      type: 'PAYMENT_SUCCESS', targetPath: `/billing/orders/${analysisId}` }
    expect(parseInboxPage({ items: [payment], nextCursor: null }).items[0]).toMatchObject(payment)
    for (const invalid of [
      { ...payment, analysisId }, { ...payment, orderId: null },
      { ...payment, targetPath: `/billing/orders/${eventId}` },
      { ...payment, targetPath: `https://evil.example/billing/orders/${analysisId}` },
    ]) expect(() => parseInboxPage({ items: [invalid], nextCursor: null })).toThrow(InboxError)
  })
  it('lists only the current account inbox with bearer, without an account selector', async () => {
    const data = await getInboxPage(context, { limit: 20, unreadOnly: true })
    expect(data.items[0]).toMatchObject({ notificationId, isRead: false,
      targetPath: `/analyses/${analysisId}` })
    const [path, options] = fetchMock.mock.calls[0]
    expect(path).toBe('/api/v1/notifications/inbox?limit=20&unreadOnly=true')
    expect(options).toMatchObject({ method: 'GET', credentials: 'omit', cache: 'no-store', redirect: 'error' })
    expect(new Headers(options?.headers).get('Authorization')).toBe('Bearer firebase-token')
    expect(String(path)).not.toContain(context.accountId)
  })

  it('sends opaque pagination cursor only as an encoded query value', async () => {
    await getInboxPage(context, { limit: 10, cursor: 'time+id/=' })
    const path = String(fetchMock.mock.calls[0][0])
    const url = new URL(path, 'http://localhost')
    expect(url.pathname).toBe('/api/v1/notifications/inbox')
    expect(url.searchParams.get('cursor')).toBe('time+id/=')
  })

  it('parses an authoritative unread count and rejects invalid counts', async () => {
    fetchMock.mockResolvedValueOnce(json({ unreadCount: 2 }))
    expect(await getUnreadCount(context)).toBe(2)
    expect(fetchMock.mock.calls[0][0]).toBe('/api/v1/notifications/inbox/unread-count')
    expect(() => parseUnreadCount({ unreadCount: -1 })).toThrow(InboxError)
    expect(() => parseUnreadCount({ unreadCount: 2.5 })).toThrow(InboxError)
  })

  it('marks read only on authenticated POST returning 204', async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }))
    await expect(markInboxRead(notificationId, context)).resolves.toBeUndefined()
    const [path, options] = fetchMock.mock.calls[0]
    expect(path).toBe(`/api/v1/notifications/inbox/${notificationId}/read`)
    expect(options?.method).toBe('POST')
    expect(options?.body).toBeUndefined()
    expect(new Headers(options?.headers).get('Authorization')).toBe('Bearer firebase-token')
  })

  it('does not treat HTTP 200 as successful mark-read confirmation', async () => {
    fetchMock.mockResolvedValueOnce(json({ success: true }, 200))
    await expect(markInboxRead(notificationId, context)).rejects.toMatchObject({ code: 'ERR_INVALID_RESPONSE' })
  })

  it('rejects foreign or fabricated notification links, repeated IDs and malformed cursors', () => {
    expect(() => parseInboxPage({ items: [{ ...notification, targetPath: '/analyses/someone-else' }], nextCursor: null })).toThrow(InboxError)
    expect(() => parseInboxPage({ items: [notification, notification], nextCursor: null })).toThrow(InboxError)
    expect(() => parseInboxPage({ items: [notification], nextCursor: 123 })).toThrow(InboxError)
  })

  it.each([401, 403, 404])('rejects list access on HTTP %i', async (status) => {
    fetchMock.mockResolvedValueOnce(json({ error: { code: 'ERR_FORBIDDEN' } }, status))
    await expect(getInboxPage(context)).rejects.toMatchObject({ status })
  })

  it('does not request another account inbox after the Firebase session changes', async () => {
    auth.currentUser = null
    await expect(getInboxPage(context)).rejects.toMatchObject({ status: 401 })
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('blocks mock mode and invalid parameters before network access', async () => {
    await expect(getInboxPage(context, { limit: 101 })).rejects.toMatchObject({ code: 'ERR_INVALID_PARAMS' })
    mocks.mock = true
    await expect(getInboxPage(context)).rejects.toMatchObject({ code: 'ERR_MOCK_ENABLED' })
    expect(fetchMock).not.toHaveBeenCalled()
  })
})
