import type { BackendContext } from './backendContext'
import { AnalysisResultError, authorizedApiResponse, analysisErrorMessage } from './analysisResult'
import { validBatchId } from './batchProgress'

export type Consultation = {
  conversationId: string; analysisId: string; clinicContextId: string; status: 'OPEN' | 'CLOSED'; unreadCount: number
}
export type ChatMessage = {
  messageId: string; conversationId: string; clientMessageId: string; senderAccountId: string
  text: string; sentAt: string; deliveryState: 'PERSISTED' | 'READ'
}
export type SendIntent = { clientMessageId: string; text: string }
export type ReadEvent = { conversationId: string; accountId: string; lastReadMessageId: string; readAt: string }
const root = '/api/v1/consultations'
export const chatPageSize = 20
export const chatId = (value: unknown): value is string => typeof value === 'string' && validBatchId(value)

function invalid(): never { throw new AnalysisResultError(0, 'ERR_INVALID_RESPONSE') }
function row(value: unknown): Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) invalid()
  return value as Record<string, unknown>
}
function uuid(value: unknown) { return chatId(value) ? value.toLowerCase() : invalid() }
function text(value: unknown) { return typeof value === 'string' && value.length > 0 ? value : invalid() }
function date(value: unknown) {
  const stamp = text(value)
  return Number.isFinite(Date.parse(stamp)) ? stamp : invalid()
}
function natural(value: unknown) {
  if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < 0) invalid()
  return value
}
function items<T>(value: unknown, parse: (value: unknown) => T, limit = chatPageSize) {
  if (!Array.isArray(value) || value.length > limit) invalid()
  return value.map(parse)
}
function fitsChatText(value: unknown): value is string {
  return typeof value === 'string' && value.length > 0 &&
    Array.from(value).length <= 4000 && new TextEncoder().encode(value).length <= 16 * 1024
}
export function validChatText(value: unknown): value is string {
  return fitsChatText(value) && value.trim().length > 0
}
function consultation(value: unknown): Consultation {
  const data = row(value)
  if (data.status !== 'OPEN' && data.status !== 'CLOSED') invalid()
  return { conversationId: uuid(data.conversationId), analysisId: uuid(data.analysisId),
    clinicContextId: uuid(data.clinicContextId), status: data.status, unreadCount: natural(data.unreadCount) }
}
export function parseChatMessage(value: unknown, conversationId: string, fromHub = false): ChatMessage {
  const data = row(value)
  const delivery = fromHub ? 'PERSISTED' : data.deliveryState
  if (uuid(data.conversationId) !== conversationId || !fitsChatText(data.text) ||
    (delivery !== 'PERSISTED' && delivery !== 'READ')) invalid()
  return { conversationId, messageId: uuid(data.messageId), clientMessageId: uuid(data.clientMessageId),
    senderAccountId: uuid(data.senderAccountId), text: data.text, sentAt: date(data.sentAt), deliveryState: delivery }
}
export function parseReadEvent(value: unknown, conversationId: string): ReadEvent {
  const data = row(value)
  if (uuid(data.conversationId) !== conversationId) invalid()
  return { conversationId, accountId: uuid(data.accountId), lastReadMessageId: uuid(data.lastReadMessageId),
    readAt: date(data.readAt) }
}
export function isChatRevoked(value: unknown, conversationId: string) {
  return typeof value === 'object' && value !== null && 'conversationId' in value && 'code' in value &&
    typeof value.conversationId === 'string' && value.conversationId.toLowerCase() === conversationId &&
    value.code === 'ACCESS_REVOKED'
}
export function isChatDenied(error: unknown) {
  return error instanceof AnalysisResultError && [401, 403, 404].includes(error.status)
}

async function request(context: BackendContext, path: string, signal: AbortSignal,
  method: 'GET' | 'POST' = 'GET', body?: object, key?: string) {
  const response = await authorizedApiResponse(path, context, signal, method, body && JSON.stringify(body), undefined, key)
  const payload: unknown = await response.json().catch(() => invalid())
  if (signal.aborted) throw signal.reason
  return { payload, status: response.status }
}
export async function listConsultations(context: BackendContext, cursor: string | null, signal: AbortSignal) {
  const query = new URLSearchParams({ limit: String(chatPageSize), ...(cursor ? { cursor } : {}) })
  const { payload } = await request(context, root + '?' + query, signal)
  const data = row(payload)
  return { items: items(data.items, consultation), nextCursor: data.nextCursor === null ? null : text(data.nextCursor) }
}
export async function eligibleDoctors(context: BackendContext, analysisId: string, clinicId: string, signal: AbortSignal) {
  const path = '/api/v1/analyses/' + uuid(analysisId) + '/consultation-doctors?clinicContextId=' + uuid(clinicId)
  const { payload } = await request(context, path, signal)
  const data = row(payload)
  if (uuid(data.analysisId) !== analysisId) invalid()
  return items(data.items, value => {
    const person = row(value)
    return { id: uuid(person.accountId), name: text(person.displayName) }
  }, 100)
}
export async function createConsultation(context: BackendContext, selection: {
  analysisId: string; clinicContextId: string; doctorAccountId: string
}, key: string, signal: AbortSignal) {
  const { payload, status } = await request(context, root, signal, 'POST', selection, uuid(key))
  const created = consultation(payload)
  if (![200, 201].includes(status) || created.analysisId !== selection.analysisId ||
    created.clinicContextId !== selection.clinicContextId) invalid()
  return created
}
export async function messageHistory(context: BackendContext, conversationId: string, beforeId: string | null, signal: AbortSignal) {
  const query = new URLSearchParams({ limit: String(chatPageSize), ...(beforeId ? { beforeId: uuid(beforeId) } : {}) })
  const { payload } = await request(context, root + '/' + uuid(conversationId) + '/messages?' + query, signal)
  const data = row(payload)
  const messages = items(data.items, item => parseChatMessage(item, conversationId))
  const nextBeforeId = data.nextBeforeId === null ? null : uuid(data.nextBeforeId)
  if (nextBeforeId && (!messages.some(item => item.messageId === nextBeforeId) || nextBeforeId === beforeId)) invalid()
  return { items: messages, nextBeforeId }
}
export async function sendConsultationMessage(context: BackendContext, conversationId: string, intent: SendIntent, signal: AbortSignal) {
  if (!validChatText(intent.text)) throw new AnalysisResultError(400, 'ERR_INVALID_MESSAGE')
  const { payload, status } = await request(context, root + '/' + uuid(conversationId) + '/messages', signal, 'POST',
    { clientMessageId: uuid(intent.clientMessageId), text: intent.text })
  const message = parseChatMessage(payload, conversationId)
  if (![200, 201].includes(status) || message.clientMessageId !== intent.clientMessageId ||
    message.senderAccountId !== context.accountId.toLowerCase() || message.text !== intent.text) invalid()
  return message
}
export async function markConsultationRead(context: BackendContext, conversationId: string, lastReadMessageId: string, signal: AbortSignal) {
  const { payload } = await request(context, root + '/' + uuid(conversationId) + '/read', signal, 'POST',
    { lastReadMessageId: uuid(lastReadMessageId) })
  const data = row(payload)
  uuid(data.id)
  text(data.status)
  natural(data.version)
}
export function consultationError(error: unknown) {
  if (!(error instanceof AnalysisResultError)) return 'Mất kết nối. Vui lòng thử lại.'
  if (error.status === 403) return '403 · Quyền truy cập cuộc trò chuyện đã thay đổi.'
  if (error.status === 404) return 'Không tìm thấy cuộc trò chuyện hoặc API chat chưa có trên môi trường này.'
  if (error.status === 409) return 'Nội dung gửi lại xung đột với yêu cầu đã lưu. Hãy đồng bộ lịch sử.'
  return analysisErrorMessage(error)
}
