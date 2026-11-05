import { getFirebaseAuth } from '../firebase/config'
import { isMockEnabled } from './client'
import type { BackendContext } from './backendContext'
import { validBatchId } from './batchProgress'

/** SCRUM-333: API inbox theo infra/sprint3/contracts/notification-openapi.yaml. */
export type InboxNotification = Readonly<{
  notificationId: string
  eventId: string
  analysisId: string | null
  orderId?: string | null
  type: 'ANALYSIS_COMPLETED' | 'ANALYSIS_FAILED' | 'PAYMENT_SUCCESS'
  title: string
  isRead: boolean
  createdAt: string
  readAt: string | null
  targetPath: string
}>

export type InboxPage = Readonly<{
  items: readonly InboxNotification[]
  nextCursor: string | null
}>

export class InboxError extends Error {
  readonly status: number
  readonly code: string
  readonly retryable: boolean

  constructor(status: number, code: string, retryable = false) {
    super(code)
    this.name = 'InboxError'
    this.status = status
    this.code = code
    this.retryable = retryable
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

function validDate(value: unknown): value is string {
  return typeof value === 'string' && value.trim() !== '' &&
    Number.isFinite(Date.parse(value))
}

function invalidResponse(): never {
  throw new InboxError(0, 'ERR_INVALID_RESPONSE')
}

function parseNotification(value: unknown): InboxNotification {
  if (!isRecord(value) ||
    typeof value.notificationId !== 'string' || !validBatchId(value.notificationId) ||
    typeof value.eventId !== 'string' || !validBatchId(value.eventId) ||
    !['ANALYSIS_COMPLETED', 'ANALYSIS_FAILED', 'PAYMENT_SUCCESS'].includes(String(value.type)) ||
    typeof value.title !== 'string' || value.title.length > 500 ||
    typeof value.isRead !== 'boolean' || !validDate(value.createdAt) ||
    (value.readAt !== null && !validDate(value.readAt)) ||
    typeof value.targetPath !== 'string') {
    invalidResponse()
  }

  const payment = value.type === 'PAYMENT_SUCCESS'
  if (payment) {
    if (value.analysisId !== null || typeof value.orderId !== 'string' || !validBatchId(value.orderId) ||
        value.targetPath !== `/billing/orders/${value.orderId}`) invalidResponse()
  } else if (typeof value.analysisId !== 'string' || !validBatchId(value.analysisId) ||
      (value.orderId !== undefined && value.orderId !== null) ||
      ![`/analyses/${value.analysisId}`, `/batches/${value.analysisId}`].includes(String(value.targetPath))) invalidResponse()

  return {
    notificationId: value.notificationId as string,
    eventId: value.eventId as string,
    analysisId: value.analysisId as string | null,
    ...(payment ? { orderId: value.orderId as string } : {}),
    type: value.type as InboxNotification['type'],
    title: value.title as string,
    isRead: value.isRead as boolean,
    createdAt: value.createdAt as string,
    readAt: value.readAt as string | null,
    targetPath: value.targetPath as string,
  }
}

export function parseInboxPage(value: unknown): InboxPage {
  if (!isRecord(value) || !Array.isArray(value.items) || value.items.length > 100 ||
    (value.nextCursor !== null &&
      (typeof value.nextCursor !== 'string' || value.nextCursor.length > 2048))) {
    invalidResponse()
  }

  const items = value.items.map(parseNotification)
  const seen = new Set<string>()
  for (const item of items) {
    const id = item.notificationId.toLowerCase()
    if (seen.has(id)) invalidResponse()
    seen.add(id)
  }
  return { items, nextCursor: value.nextCursor as string | null }
}

export function parseUnreadCount(value: unknown): number {
  if (!isRecord(value) || !Number.isSafeInteger(value.unreadCount) ||
    (value.unreadCount as number) < 0) invalidResponse()
  return value.unreadCount as number
}

function serverError(status: number, value: unknown): InboxError {
  const details = isRecord(value) && isRecord(value.error) ? value.error : null
  const code = typeof details?.code === 'string' && /^ERR_[A-Z0-9_]{1,60}$/.test(details.code)
    ? details.code : 'ERR_REQUEST_FAILED'
  return new InboxError(status, code, ![400, 401, 403, 404].includes(status))
}

/** Only Firebase token identifies the account. Never send an accountId selector. */
async function inboxRequest(
  path: string,
  method: 'GET' | 'POST',
  context: BackendContext,
  signal?: AbortSignal,
): Promise<{ status: number; payload: unknown }> {
  if (isMockEnabled) throw new InboxError(0, 'ERR_MOCK_ENABLED')
  const auth = getFirebaseAuth()
  const user = auth?.currentUser
  if (!user || user.uid !== context.firebaseUid) {
    throw new InboxError(401, 'ERR_SESSION_CHANGED')
  }

  const abort = signal
    ? AbortSignal.any([signal, AbortSignal.timeout(15000)])
    : AbortSignal.timeout(15000)
  const ensureCurrent = () => {
    if (abort.aborted) throw abort.reason
    if (auth.currentUser !== user || user.uid !== context.firebaseUid) {
      throw new InboxError(401, 'ERR_SESSION_CHANGED')
    }
  }

  try {
    ensureCurrent()
    const token = await user.getIdToken()
    ensureCurrent()
    if (typeof token !== 'string' || !token || /\s/.test(token)) {
      throw new InboxError(401, 'ERR_TOKEN_UNAVAILABLE')
    }

    const response = await fetch(path, {
      method,
      headers: { Authorization: `Bearer ${token}`, Accept: 'application/json' },
      cache: 'no-store',
      credentials: 'omit',
      redirect: 'error',
      signal: abort,
    })
    ensureCurrent()
    const payload: unknown = response.status === 204
      ? null : await response.json().catch(() => null)
    ensureCurrent()
    if (!response.ok) throw serverError(response.status, payload)
    return { status: response.status, payload }
  } catch (error) {
    if (error instanceof InboxError) throw error
    if (signal?.aborted) throw error
    if (abort.aborted) throw new InboxError(0, 'ERR_TIMEOUT', true)
    throw new InboxError(0, 'ERR_NETWORK', true)
  }
}

export async function getInboxPage(
  context: BackendContext,
  options: Readonly<{ limit?: number; cursor?: string; unreadOnly?: boolean }> = {},
  signal?: AbortSignal,
): Promise<InboxPage> {
  const limit = options.limit ?? 20
  if (!Number.isSafeInteger(limit) || limit < 1 || limit > 100 ||
    (options.cursor !== undefined &&
      (!options.cursor || options.cursor.length > 2048))) {
    throw new InboxError(400, 'ERR_INVALID_PARAMS')
  }
  const params = new URLSearchParams({ limit: String(limit) })
  if (options.cursor) params.set('cursor', options.cursor)
  if (options.unreadOnly === true) params.set('unreadOnly', 'true')
  const result = await inboxRequest(`/api/v1/notifications/inbox?${params.toString()}`, 'GET', context, signal)
  if (result.status !== 200) invalidResponse()
  return parseInboxPage(result.payload)
}

export async function getUnreadCount(context: BackendContext, signal?: AbortSignal): Promise<number> {
  const result = await inboxRequest('/api/v1/notifications/inbox/unread-count', 'GET', context, signal)
  if (result.status !== 200) invalidResponse()
  return parseUnreadCount(result.payload)
}

/** Backend returns 204 and must enforce current account ownership/idempotency. */
export async function markInboxRead(
  notificationId: string,
  context: BackendContext,
  signal?: AbortSignal,
): Promise<void> {
  if (!validBatchId(notificationId)) throw new InboxError(400, 'ERR_INVALID_NOTIFICATION_ID')
  const result = await inboxRequest(
    `/api/v1/notifications/inbox/${notificationId}/read`, 'POST', context, signal,
  )
  if (result.status !== 204) invalidResponse()
}

export function inboxErrorMessage(error: InboxError): string {
  if (error.status === 401 || ['ERR_SESSION_CHANGED', 'ERR_TOKEN_UNAVAILABLE'].includes(error.code)) {
    return 'Phiên đăng nhập đã hết hạn hoặc thay đổi. Vui lòng đăng nhập lại.'
  }
  if (error.status === 403 || error.status === 404) {
    return 'Thông báo không tồn tại hoặc tài khoản không có quyền truy cập.'
  }
  if (error.code === 'ERR_MOCK_ENABLED') return 'Đang bật chế độ demo. Tắt mock để đọc Inbox thật.'
  if (error.code === 'ERR_INVALID_RESPONSE') return 'Phản hồi Inbox từ máy chủ không đúng API contract.'
  if (error.code === 'ERR_TIMEOUT') return 'Kết nối tới Inbox quá thời gian chờ.'
  if (error.code === 'ERR_INVALID_PARAMS') return 'Tham số đọc Inbox không hợp lệ.'
  if (error.code === 'ERR_INVALID_NOTIFICATION_ID') return 'Notification ID không hợp lệ.'
  return 'Chưa thể kết nối Notification Inbox. Vui lòng thử lại.'
}
