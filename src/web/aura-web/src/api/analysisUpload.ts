import { getFirebaseAuth } from '../firebase/config'
import { isMockEnabled } from './client'
import type { BackendContext } from './backendContext'

export type EyeSide = 'left' | 'right' | 'unknown' | 'corrupted' | 'retry'
export type UploadAttempt = Readonly<{
  key: string
  context: BackendContext
  eyeSide: EyeSide
  images: readonly File[]
}>
export type UploadItem = Readonly<{
  analysisId: string
  fileName: string
  processingStatus: 'QUEUED' | 'DUPLICATE' | 'QUARANTINE' | 'RETRY' | 'REJECTED'
  existingAnalysisId?: string
  error?: Readonly<{ code: string; message: string; retryable?: boolean }>
}>
export type UploadReceipt = Readonly<{
  batchId: string
  items: readonly UploadItem[]
  createdAt: string
  statusUrl: string
  replayed: boolean
}>
export type UploadDisposition = 'not-sent' | 'rejected' | 'uncertain' | 'conflict'

export class UploadRequestError extends Error {
  readonly status: number
  readonly code: string
  readonly disposition: UploadDisposition
  readonly canRetry: boolean
  readonly correlationId?: string
  constructor(
    status: number,
    code: string,
    disposition: UploadDisposition,
    canRetry: boolean,
    correlationId?: string,
  ) {
    super('Không hoàn thành được yêu cầu tải ảnh.')
    this.name = 'UploadRequestError'
    this.status = status
    this.code = code
    this.disposition = disposition
    this.canRetry = canRetry
    this.correlationId = correlationId
  }
}

function record(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}
function id(value: unknown): value is string {
  return typeof value === 'string' &&
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value) &&
    value !== '00000000-0000-0000-0000-000000000000'
}
export function canUploadPersonal(context: BackendContext): boolean {
  return context.roles.includes('PATIENT') && !context.roles.includes('SYSTEM_ADMIN')
}
export function createUploadAttempt(context: BackendContext, images: readonly File[], eyeSide: EyeSide): UploadAttempt {
  if (!canUploadPersonal(context) || !id(context.patientId) || !id(context.accountId) ||
    !id(context.personalTenantId) || !context.firebaseUid || !images.length || images.length > 100 ||
    !['left', 'right', 'unknown', 'corrupted', 'retry'].includes(eyeSide)) {
    throw new UploadRequestError(0, 'ERR_INVALID_UPLOAD_DRAFT', 'not-sent', false)
  }
  const key = crypto.randomUUID()
  if (!id(key)) throw new UploadRequestError(0, 'ERR_INVALID_UPLOAD_KEY', 'not-sent', false)
  return Object.freeze({ key, context, eyeSide, images: Object.freeze([...images]) })
}

function receipt(payload: unknown, attempt: UploadAttempt, replayed: boolean): UploadReceipt {
  const invalid = () => { throw new UploadRequestError(202, 'ERR_INVALID_RESPONSE', 'uncertain', true) }
  if (!record(payload) || !id(payload.batchId) ||
    payload.statusUrl !== `/api/v1/batches/${payload.batchId}` ||
    typeof payload.createdAt !== 'string' ||
    !/^\d{4}-\d{2}-\d{2}T.*(?:Z|[+-]\d{2}:\d{2})$/.test(payload.createdAt) ||
    !Number.isFinite(Date.parse(payload.createdAt)) ||
    !Array.isArray(payload.items) || payload.items.length !== attempt.images.length) return invalid()
  const seen = new Set<string>()
  const items = payload.items.map((item): UploadItem => {
    if (!record(item) || !id(item.analysisId) || seen.has(item.analysisId.toLowerCase()) ||
      typeof item.fileName !== 'string' || !item.fileName ||
      !['QUEUED', 'DUPLICATE', 'QUARANTINE', 'RETRY', 'REJECTED'].includes(item.processingStatus as string)) return invalid()
    seen.add(item.analysisId.toLowerCase())
    if (item.processingStatus === 'DUPLICATE') {
      if (!id(item.existingAnalysisId) || item.existingAnalysisId.toLowerCase() === item.analysisId.toLowerCase() ||
        !record(item.error) || item.error.code !== 'ERR_DUPLICATE_REQUEST' || item.error.retryable !== false) return invalid()
      return Object.freeze({ analysisId: item.analysisId, fileName: item.fileName,
        processingStatus: 'DUPLICATE', existingAnalysisId: item.existingAnalysisId })
    }
    if (item.processingStatus === 'QUARANTINE') {
      return Object.freeze({
        analysisId: item.analysisId,
        fileName: item.fileName,
        processingStatus: 'QUARANTINE',
        error: record(item.error) ? {
          code: String(item.error.code ?? 'ERR_CORRUPTED_PAYLOAD'),
          message: String(item.error.message ?? 'Dữ liệu ảnh không hợp lệ (hỏng cấu trúc). Đã cách ly sang nhánh Quarantine.'),
          retryable: false,
        } : undefined,
      })
    }
    if (item.processingStatus === 'RETRY') {
      return Object.freeze({ analysisId: item.analysisId, fileName: item.fileName, processingStatus: 'RETRY' })
    }
    if (item.processingStatus === 'REJECTED') {
      return Object.freeze({
        analysisId: item.analysisId,
        fileName: item.fileName,
        processingStatus: 'REJECTED',
        error: record(item.error) ? {
          code: String(item.error.code ?? 'ERR_ITEM_REJECTED'),
          message: String(item.error.message ?? 'Tệp bị từ chối lúc tiếp nhận.'),
          retryable: false,
        } : undefined,
      })
    }
    if (item.existingAnalysisId != null || item.error != null) return invalid()
    return Object.freeze({ analysisId: item.analysisId, fileName: item.fileName, processingStatus: 'QUEUED' })
  })
  return Object.freeze({ batchId: payload.batchId, createdAt: payload.createdAt,
    statusUrl: payload.statusUrl as string, items: Object.freeze(items), replayed })
}

// Shared wire validation for the versioned staged-upload client; legacy callers remain supported.
export { receipt as parseUploadReceipt, httpError as parseUploadHttpError }

function httpError(status: number, payload: unknown): UploadRequestError {
  const error = record(payload) && record(payload.error) ? payload.error : null
  const code = typeof error?.code === 'string' && /^ERR_[A-Z0-9_]{1,60}$/.test(error.code)
    ? error.code : 'ERR_REQUEST_FAILED'
  const correlation = typeof error?.correlationId === 'string' && /^[A-Za-z0-9._-]{1,64}$/.test(error.correlationId)
    ? error.correlationId : undefined
  const rejected = error?.retryable === false && (
    (status === 400 && ['ERR_VALIDATION', 'ERR_UNSUPPORTED_MODALITY'].includes(code)) ||
    (status === 401 && code === 'ERR_UNAUTHORIZED') ||
    (status === 403 && ['ERR_FORBIDDEN', 'ERR_ACCOUNT_DISABLED'].includes(code)) ||
    (status === 404 && code === 'ERR_NOT_FOUND') ||
    (status === 410 && code === 'ERR_UPLOAD_EXPIRED') ||
    (status === 413 && code === 'ERR_PAYLOAD_TOO_LARGE') ||
    (status === 415 && code === 'ERR_UNSUPPORTED_MEDIA_TYPE'))
  const conflict = status === 409 && code === 'ERR_IDEMPOTENCY_KEY_REUSED'
  return new UploadRequestError(status, code, conflict ? 'conflict' : rejected ? 'rejected' : 'uncertain',
    !conflict && !rejected && status !== 401 && status !== 403, correlation)
}

async function withSignal<T>(promise: Promise<T>, signal: AbortSignal): Promise<T> {
  if (signal.aborted) {
    void promise.catch(() => {})
    throw signal.reason
  }
  let onAbort: (() => void) | undefined
  try {
    return await Promise.race([promise, new Promise<never>((_resolve, reject) => {
      onAbort = () => reject(signal.reason)
      signal.addEventListener('abort', onAbort, { once: true })
    })])
  } finally {
    if (onAbort) signal.removeEventListener('abort', onAbort)
  }
}

export const DEFAULT_UPLOAD_CONCURRENCY = 4
export const MAX_UPLOAD_CONCURRENCY = 6
export const MAX_SINGLE_REQUEST_BYTES = 12 * 1024 * 1024 // 12 MiB per ADR-005 D5

async function postMultipart(
  files: readonly File[],
  key: string,
  attempt: UploadAttempt,
  token: string,
  abort: AbortSignal,
  check: () => void,
  onSent: () => void,
): Promise<{ payload: unknown; replayed: boolean }> {
  const body = new FormData()
  body.append('patientId', attempt.context.patientId)
  body.append('modality', 'FUNDUS')
  body.append('eyeSide', attempt.eyeSide)
  for (const file of files) body.append('images', file)
  check()
  onSent()
  const response = await withSignal(fetch('/api/v1/analyses', {
    method: 'POST', headers: { Authorization: `Bearer ${token}`, 'Idempotency-Key': key }, body,
    signal: abort, cache: 'no-store', credentials: 'omit', redirect: 'error',
  }), abort)
  check()
  const payload: unknown = await withSignal(response.json().catch(() => null), abort)
  check()
  if (response.status !== 202) {
    if (response.ok) throw new UploadRequestError(response.status, 'ERR_INVALID_RESPONSE', 'uncertain', true)
    throw httpError(response.status, payload)
  }
  return { payload, replayed: response.headers.get('Idempotent-Replayed') === 'true' }
}

async function uploadBatchedImages(
  images: readonly File[],
  attempt: UploadAttempt,
  token: string,
  abort: AbortSignal,
  check: () => void,
  onSent: () => void,
): Promise<{ combinedPayload: unknown; replayed: boolean }> {
  // ADR-005 D5: In the current API protocol, an upload attempt is submitted in an atomic multipart request
  // with a valid authoritative UUID Idempotency-Key (attempt.key).
  // The server plans all images into one canonical batch (batchId), preserving server inventory consistency.
  // This avoids splitting into independent server batches and prevents client-side fake batch concatenation.
  const single = await postMultipart(images, attempt.key, attempt, token, abort, check, onSent)
  return { combinedPayload: single.payload, replayed: single.replayed }
}

/** One explicit attempt only. ADR-005 D5: Submits all attempt images atomically under attempt.key (valid UUID). */
export async function sendUploadAttempt(
  attempt: UploadAttempt,
  signal?: AbortSignal,
): Promise<UploadReceipt> {
  if (isMockEnabled) throw new UploadRequestError(0, 'ERR_MOCK_ENABLED', 'not-sent', false)
  const auth = getFirebaseAuth()
  const user = auth?.currentUser
  if (!user || user.uid !== attempt.context.firebaseUid) {
    throw new UploadRequestError(401, 'ERR_SESSION_CHANGED', 'not-sent', false)
  }
  const deadline = AbortSignal.timeout(60000)
  const abort = signal ? AbortSignal.any([signal, deadline]) : deadline
  let sent = false
  const check = () => {
    if (abort.aborted) throw abort.reason
    if (auth.currentUser !== user || user.uid !== attempt.context.firebaseUid) {
      throw new UploadRequestError(401, 'ERR_SESSION_CHANGED', sent ? 'uncertain' : 'not-sent', false)
    }
  }
  try {
    check()
    const token = await withSignal(user.getIdToken(), abort)
    check()
    if (typeof token !== 'string' || !token || /\s/.test(token)) {
      throw new UploadRequestError(401, 'ERR_TOKEN_UNAVAILABLE', 'not-sent', false)
    }
    const { combinedPayload, replayed } = await uploadBatchedImages(
      attempt.images, attempt, token, abort, check, () => { sent = true },
    )
    return receipt(combinedPayload, attempt, replayed)
  } catch (error) {
    if (error instanceof UploadRequestError) throw error
    const code = abort.aborted ? (deadline.aborted ? 'ERR_TIMEOUT' : 'ERR_UPLOAD_INTERRUPTED')
      : sent ? 'ERR_NETWORK' : 'ERR_TOKEN_UNAVAILABLE'
    throw new UploadRequestError(0, code, sent ? 'uncertain' : 'not-sent', true)
  }
}

export function uploadErrorMessage(error: UploadRequestError): string {
  if (error.code === 'ERR_UPLOAD_EXPIRED') return 'Lượt tải đã hết hạn. Chọn tạo lượt mới và chọn lại ảnh để tiếp tục.'
  if (error.code === 'ERR_UPLOAD_FILES_REQUIRED') return 'Chọn lại những ảnh chưa được máy chủ xác nhận để tiếp tục lượt tải.'
  if (error.code === 'ERR_MOCK_ENABLED') return 'Không gửi ảnh thật trong chế độ demo.'
  if (error.code === 'ERR_IDEMPOTENCY_KEY_REUSED') return 'Mã yêu cầu đã gắn với nội dung khác. Cần đối chiếu trước khi gửi tiếp.'
  if (error.code === 'ERR_REQUEST_IN_PROGRESS') return 'Yêu cầu đang được xử lý hoặc đối soát. Hãy thử lại cùng lượt gửi.'
  if (error.code === 'ERR_TIMEOUT') return 'Chưa nhận được phản hồi trong thời gian chờ.'
  if (error.code === 'ERR_INVALID_RESPONSE') return 'Phản hồi tiếp nhận chưa hợp lệ; chưa xác nhận được kết quả lượt gửi.'
  if (error.status === 400) return 'Dữ liệu gửi lên không hợp lệ.'
  if (error.status === 401 || error.code === 'ERR_TOKEN_UNAVAILABLE') return 'Phiên đăng nhập không hợp lệ hoặc chưa lấy được token. Vui lòng kiểm tra phiên đăng nhập.'
  if (error.status === 402) return 'Không đủ credit. Chưa có xác nhận tiếp nhận; giữ nguyên lượt gửi để kiểm tra lại.'
  if (error.status === 403) return 'Bạn không có quyền tải ảnh cho hồ sơ này.'
  if (error.status === 404) return 'Không tìm thấy hồ sơ trong phạm vi được phép.'
  if (error.status === 413) return 'Dung lượng ảnh hoặc yêu cầu vượt quá giới hạn.'
  if (error.status === 415) return 'Máy chủ chỉ nhận ảnh JPEG hoặc PNG hợp lệ.'
  if (error.status === 429) return 'Bạn gửi quá nhiều yêu cầu. Vui lòng chờ rồi thử lại cùng lượt gửi.'
  if (error.status >= 500) return 'Máy chủ đang gặp sự cố. Hãy thử lại cùng lượt gửi sau.'
  return 'Chưa hoàn thành được lượt gửi ảnh. Vui lòng kiểm tra kết nối.'
}
