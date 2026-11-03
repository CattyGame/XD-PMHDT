import { getFirebaseAuth } from '../firebase/config'
import { isMockEnabled } from './client'
import type { BackendContext } from './backendContext'

/** Public Analysis REST 0.6.0 — GET /api/v1/batches/{batchId}. Never POST during status tracking. */
export const BATCH_STATUSES = ['IN_PROGRESS', 'COMPLETED', 'PARTIAL_FAILED', 'FAILED'] as const
export const PROCESSING_STATUSES = [
  'PENDING', 'QUEUED', 'PROCESSING', 'COMPLETED', 'FAILED', 'RETRY_READY',
  'LOW_QUALITY', 'REJECTED', 'DUPLICATE', 'QUARANTINE', 'RETRY',
] as const
export type BatchStatus = typeof BATCH_STATUSES[number]
export type ProcessingStatus = typeof PROCESSING_STATUSES[number]
export type BatchItem = Readonly<{
  analysisId: string
  fileName: string | null
  processingStatus: ProcessingStatus
  reviewStatus: string
  riskLevel: string | null
  error?: Readonly<{ code: string; message: string; retryable: boolean; retryCount?: number }>
  existingAnalysisId?: string
}>
export type BatchProgress = Readonly<{
  batchId: string
  createdAt: string
  batchStatus: BatchStatus
  items: readonly BatchItem[]
  summary: Readonly<{ total: number; active: number; succeeded: number; failed: number; duplicate: number }>
}>
export type BatchRead = Readonly<{ kind: 'accepted' }> | Readonly<{ kind: 'batch'; data: BatchProgress }>

export class BatchProgressError extends Error {
  readonly status: number
  readonly code: string
  readonly retryable: boolean
  constructor(status: number, code: string, retryable: boolean) {
    super(code)
    this.name = 'BatchProgressError'
    this.status = status
    this.code = code
    this.retryable = retryable
  }
}

function object(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}
export function validBatchId(value: string): boolean {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value) &&
    value.toLowerCase() !== '00000000-0000-0000-0000-000000000000'
}
function count(value: unknown): value is number {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0
}
function invalid(): never { throw new BatchProgressError(0, 'ERR_INVALID_RESPONSE', true) }

/** Validate external payload instead of trusting a 200 or a POST receipt as an AI result. */
export function parseBatchProgress(value: unknown, requestedId: string): BatchProgress {
  if (!object(value) || typeof value.batchId !== 'string' || !validBatchId(value.batchId) ||
    value.batchId.toLowerCase() !== requestedId.toLowerCase() ||
    typeof value.batchStatus !== 'string' || !BATCH_STATUSES.includes(value.batchStatus as BatchStatus) ||
    typeof value.createdAt !== 'string' || !Number.isFinite(Date.parse(value.createdAt)) ||
    !Array.isArray(value.items) || value.items.length < 1 || value.items.length > 100 ||
    !object(value.summary)) invalid()
  const summary = value.summary
  if (!count(summary.total) || !count(summary.active) || !count(summary.succeeded) ||
    !count(summary.failed) || !count(summary.duplicate) || summary.total !== value.items.length ||
    summary.active + summary.succeeded + summary.failed + summary.duplicate !== summary.total) invalid()
  const seen = new Set<string>()
  const items: BatchItem[] = value.items.map((entry: unknown) => {
    if (!object(entry) || typeof entry.analysisId !== 'string' || !validBatchId(entry.analysisId) ||
      seen.has(entry.analysisId.toLowerCase()) ||
      typeof entry.processingStatus !== 'string' ||
      !PROCESSING_STATUSES.includes(entry.processingStatus as ProcessingStatus) ||
      (entry.fileName !== null && entry.fileName !== undefined && typeof entry.fileName !== 'string') ||
      typeof entry.reviewStatus !== 'string' ||
      (entry.riskLevel != null && !['LOW', 'MODERATE', 'HIGH'].includes(entry.riskLevel as string))) invalid()
    seen.add(entry.analysisId.toLowerCase())
    const error = entry.error
    if (error != null && (!object(error) || typeof error.code !== 'string' ||
      typeof error.message !== 'string' || typeof error.retryable !== 'boolean')) invalid()
    const existing = entry.existingAnalysisId
    if (existing != null && (typeof existing !== 'string' || !validBatchId(existing))) invalid()
    return {
      analysisId: entry.analysisId,
      fileName: (entry.fileName ?? null) as string | null,
      processingStatus: entry.processingStatus as ProcessingStatus,
      reviewStatus: entry.reviewStatus,
      riskLevel: (entry.riskLevel ?? null) as string | null,
      ...(error != null ? { error: {
        code: (error as Record<string, unknown>).code as string,
        message: (error as Record<string, unknown>).message as string,
        retryable: (error as Record<string, unknown>).retryable as boolean,
        ...(count((error as Record<string, unknown>).retryCount)
          ? { retryCount: (error as Record<string, unknown>).retryCount as number } : {}),
      } } : {}),
      ...(existing != null ? { existingAnalysisId: existing as string } : {}),
    }
  })
  // A backend can only mark the entire batch terminal once all active items have finished.
  if (value.batchStatus !== 'IN_PROGRESS' && summary.active !== 0) invalid()
  return {
    batchId: value.batchId, createdAt: value.createdAt, batchStatus: value.batchStatus as BatchStatus,
    items, summary: {
      total: summary.total, active: summary.active, succeeded: summary.succeeded,
      failed: summary.failed, duplicate: summary.duplicate,
    },
  }
}

export const isBatchTerminal = (data: BatchProgress): boolean =>
  data.batchStatus !== 'IN_PROGRESS' && data.summary.active === 0

function serverError(status: number, payload: unknown): BatchProgressError {
  const details = object(payload) && object(payload.error) ? payload.error : null
  const code = typeof details?.code === 'string' && /^ERR_[A-Z0-9_]{1,60}$/.test(details.code)
    ? details.code : 'ERR_REQUEST_FAILED'
  return new BatchProgressError(status, code, ![400, 401, 403, 404].includes(status))
}

/** Same-origin, current Firebase ID token, no persistent cache, no privilege inference from batch ID. */
export async function getBatchProgress(
  batchId: string, context: BackendContext, signal?: AbortSignal,
): Promise<BatchRead> {
  if (!validBatchId(batchId)) throw new BatchProgressError(400, 'ERR_INVALID_BATCH_ID', false)
  if (isMockEnabled) throw new BatchProgressError(0, 'ERR_MOCK_ENABLED', false)
  const auth = getFirebaseAuth()
  const user = auth?.currentUser
  if (!user || user.uid !== context.firebaseUid) {
    throw new BatchProgressError(401, 'ERR_SESSION_CHANGED', false)
  }
  const abort = signal ? AbortSignal.any([signal, AbortSignal.timeout(15000)]) : AbortSignal.timeout(15000)
  const ensureCurrent = () => {
    if (abort.aborted) throw abort.reason
    if (auth.currentUser !== user || user.uid !== context.firebaseUid) {
      throw new BatchProgressError(401, 'ERR_SESSION_CHANGED', false)
    }
  }
  try {
    ensureCurrent()
    const token = await user.getIdToken()
    ensureCurrent()
    if (typeof token !== 'string' || !token || /\s/.test(token)) {
      throw new BatchProgressError(401, 'ERR_TOKEN_UNAVAILABLE', false)
    }
    const response = await fetch(`/api/v1/batches/${batchId}`, {
      method: 'GET', headers: { Authorization: `Bearer ${token}` },
      cache: 'no-store', credentials: 'omit', redirect: 'error', signal: abort,
    })
    ensureCurrent()
    // 202 only means accepted / still pending, never completed.
    if (response.status === 202) return { kind: 'accepted' }
    const payload: unknown = await response.json().catch(() => null)
    ensureCurrent()
    if (!response.ok) throw serverError(response.status, payload)
    if (response.status !== 200) invalid()
    return { kind: 'batch', data: parseBatchProgress(payload, batchId) }
  } catch (error) {
    if (error instanceof BatchProgressError) throw error
    if (signal?.aborted) throw error // Unmount/navigation cancellation is not a retryable failure.
    if (abort.aborted) throw new BatchProgressError(0, 'ERR_TIMEOUT', true)
    throw new BatchProgressError(0, 'ERR_NETWORK', true)
  }
}

export function batchErrorMessage(error: BatchProgressError): string {
  if (error.status === 401 || ['ERR_SESSION_CHANGED', 'ERR_TOKEN_UNAVAILABLE'].includes(error.code))
    return 'Phiên đăng nhập đã hết hạn hoặc thay đổi. Vui lòng đăng nhập lại.'
  if (error.status === 403) return 'Tài khoản không có quyền xem batch này hoặc quyền đã bị thu hồi.'
  if (error.status === 404) return 'Không tìm thấy batch trong phạm vi được cấp quyền.'
  if (error.code === 'ERR_INVALID_BATCH_ID') return 'Batch ID phải là một UUID hợp lệ.'
  if (error.code === 'ERR_MOCK_ENABLED') return 'Đang bật chế độ demo. Tắt mock để xem API tiến trình thật.'
  if (error.code === 'ERR_INVALID_RESPONSE') return 'Phản hồi trạng thái từ máy chủ chưa đúng hợp đồng API.'
  if (error.code === 'ERR_TIMEOUT') return 'Đọc trạng thái quá thời gian chờ. Sẽ thử lại bằng GET.'
  return 'Chưa tải được trạng thái. Hệ thống sẽ thử đọc lại, không gửi lại ảnh.'
}
