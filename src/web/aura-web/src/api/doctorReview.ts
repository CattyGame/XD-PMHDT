import { getFirebaseAuth } from '../firebase/config'
import { isMockEnabled } from './client'
import type { BackendContext } from './backendContext'

// Contract: contracts/doctor-review-openapi.yaml (M2). Không tự sinh dữ liệu y khoa.
export type RiskLevel = 'LOW' | 'MODERATE' | 'HIGH'
export type ReviewStatus = 'NOT_REQUIRED' | 'PENDING' | 'IN_REVIEW' | 'REVIEWED' | 'CORRECTION_REQUIRED'
export type DoctorFile = Readonly<{ fileId: string; kind: string }>
export type DoctorResult = Readonly<{
  riskAvailable: boolean
  riskLevel: RiskLevel | null
  isMock: boolean
  annotations: readonly DoctorFile[]
  disclaimer: string
}>
export type DoctorAnalysis = Readonly<{
  analysisId: string
  batchId: string
  patientId: string
  createdAt: string | null
  processingStatus: string
  reviewStatus: ReviewStatus
  reviewVersion: number
  image: DoctorFile
  result: DoctorResult | null
}>
export type ReviewRequest =
  | { isApproved: true; baseVersion: number; doctorNotes?: string }
  | { isApproved: false; baseVersion: number; correctedRiskLevel: RiskLevel; reason: string; doctorNotes?: string }
export type DoctorReview = Readonly<{
  reviewId: string
  analysisId: string
  version: number
  isApproved: boolean
}>
export type PrivateDoctorFile = Readonly<{ url: string; expiresAt: string }>

export class DoctorApiError extends Error {
  readonly status: number
  readonly code: string
  constructor(status: number, code: string) {
    super(code)
    this.name = 'DoctorApiError'
    this.status = status
    this.code = code
  }
}

export const isGuid = (value: string): boolean =>
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value) &&
  value !== '00000000-0000-0000-0000-000000000000'

function record(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}
function invalid(): never { throw new DoctorApiError(0, 'ERR_INVALID_RESPONSE') }
function text(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0
}
function parseFile(value: unknown): DoctorFile {
  if (!record(value) || !text(value.kind) || !text(value.fileId) || !isGuid(value.fileId)) invalid()
  return { kind: value.kind, fileId: value.fileId }
}
function parseResult(value: unknown): DoctorResult | null {
  if (value === null || value === undefined) return null
  if (!record(value)) invalid()
  const level = value.riskLevel
  const riskLevel = level === 'LOW' || level === 'MODERATE' || level === 'HIGH' ? level : null
  if (!Array.isArray(value.annotations)) invalid()
  return {
    riskAvailable: value.riskAvailable === true && riskLevel !== null,
    riskLevel,
    isMock: value.isMock === true,
    annotations: value.annotations.map(parseFile),
    disclaimer: typeof value.disclaimer === 'string' ? value.disclaimer : '',
  }
}
export function parseDoctorAnalysis(value: unknown): DoctorAnalysis {
  if (!record(value) || !text(value.analysisId) || !isGuid(value.analysisId) ||
    !text(value.batchId) || !isGuid(value.batchId) ||
    !text(value.patientId) || !isGuid(value.patientId) ||
    !text(value.processingStatus) ||
    !['NOT_REQUIRED', 'PENDING', 'IN_REVIEW', 'REVIEWED', 'CORRECTION_REQUIRED'].includes(String(value.reviewStatus)) ||
    !Number.isSafeInteger(value.reviewVersion) || (value.reviewVersion as number) < 0) invalid()
  const image = parseFile(value.image)
  if (image.kind !== 'original') invalid()
  return {
    analysisId: value.analysisId,
    batchId: value.batchId,
    patientId: value.patientId,
    processingStatus: value.processingStatus,
    reviewStatus: value.reviewStatus as ReviewStatus,
    reviewVersion: value.reviewVersion as number,
    createdAt: text(value.createdAt) && Number.isFinite(Date.parse(value.createdAt)) ? value.createdAt : null,
    image,
    result: parseResult(value.result),
  }
}

async function request(
  path: string, method: 'GET' | 'POST', context: BackendContext,
  body?: ReviewRequest, signal?: AbortSignal,
): Promise<{ status: number; data: unknown }> {
  if (isMockEnabled) throw new DoctorApiError(0, 'ERR_MOCK_ENABLED')
  if (!context.roles.includes('DOCTOR')) throw new DoctorApiError(403, 'ERR_FORBIDDEN')
  const auth = getFirebaseAuth()
  const user = auth?.currentUser
  if (!user || user.uid !== context.firebaseUid) throw new DoctorApiError(401, 'ERR_SESSION_CHANGED')
  const abort = signal ? AbortSignal.any([signal, AbortSignal.timeout(15000)]) : AbortSignal.timeout(15000)
  const ensureCurrent = () => {
    if (abort.aborted) throw abort.reason
    if (auth.currentUser !== user || user.uid !== context.firebaseUid) {
      throw new DoctorApiError(401, 'ERR_SESSION_CHANGED')
    }
  }
  try {
    ensureCurrent()
    const token = await user.getIdToken()
    ensureCurrent()
    if (!text(token) || /\s/.test(token)) throw new DoctorApiError(401, 'ERR_TOKEN_UNAVAILABLE')
    const response = await fetch(path, {
      method,
      headers: {
        Authorization: 'Bearer ' + token,
        Accept: 'application/json',
        ...(body ? { 'Content-Type': 'application/json' } : {}),
      },
      ...(body ? { body: JSON.stringify(body) } : {}),
      cache: 'no-store', credentials: 'omit', redirect: 'error', signal: abort,
    })
    ensureCurrent()
    const data: unknown = await response.json().catch(() => null)
    ensureCurrent()
    if (!response.ok) {
      const err = record(data) && record(data.error) ? data.error : null
      const code = text(err?.code) && /^ERR_[A-Z0-9_]{1,60}$/.test(err.code)
        ? err.code : 'ERR_REQUEST_FAILED'
      throw new DoctorApiError(response.status, code)
    }
    return { status: response.status, data }
  } catch (error) {
    if (error instanceof DoctorApiError) throw error
    if (signal?.aborted) throw error
    throw new DoctorApiError(0, abort.aborted ? 'ERR_TIMEOUT' : 'ERR_NETWORK')
  }
}

function clinicPath(clinicContextId: string): string {
  if (!isGuid(clinicContextId)) throw new DoctorApiError(400, 'ERR_INVALID_CLINIC_ID')
  return '?clinicContextId=' + encodeURIComponent(clinicContextId)
}
export async function getDoctorWorklist(context: BackendContext, clinicId: string, signal?: AbortSignal) {
  const response = await request('/api/v1/doctor/worklist' + clinicPath(clinicId), 'GET', context, undefined, signal)
  if (response.status !== 200 || !record(response.data) || !Array.isArray(response.data.items) ||
    response.data.items.length > 200) invalid()
  const items: DoctorAnalysis[] = response.data.items.map(parseDoctorAnalysis)
  if (new Set(items.map((item) => item.analysisId.toLowerCase())).size !== items.length) invalid()
  return items
}
export async function getDoctorAnalysis(context: BackendContext, clinicId: string, analysisId: string, signal?: AbortSignal) {
  if (!isGuid(analysisId)) throw new DoctorApiError(400, 'ERR_INVALID_ANALYSIS_ID')
  const response = await request('/api/v1/doctor/analyses/' + analysisId + clinicPath(clinicId), 'GET', context, undefined, signal)
  if (response.status !== 200) invalid()
  const analysis = parseDoctorAnalysis(response.data)
  if (analysis.analysisId.toLowerCase() !== analysisId.toLowerCase()) invalid()
  return analysis
}
export async function submitDoctorReview(
  context: BackendContext, clinicId: string, analysisId: string, review: ReviewRequest,
): Promise<DoctorReview> {
  if (!isGuid(analysisId) || !Number.isSafeInteger(review.baseVersion) || review.baseVersion < 0) {
    throw new DoctorApiError(400, 'ERR_INVALID_REVIEW')
  }
  const response = await request('/api/v1/doctor/analyses/' + analysisId + '/reviews' + clinicPath(clinicId), 'POST', context, review)
  if (response.status !== 201 || !record(response.data) || !text(response.data.reviewId) ||
    !isGuid(response.data.reviewId) || response.data.analysisId !== analysisId ||
    !Number.isSafeInteger(response.data.version) || response.data.isApproved !== review.isApproved) invalid()
  return { reviewId: response.data.reviewId, analysisId, version: response.data.version as number,
    isApproved: response.data.isApproved as boolean }
}
export async function getDoctorFileAccess(
  context: BackendContext, clinicId: string, analysisId: string, fileId: string, signal?: AbortSignal,
): Promise<PrivateDoctorFile> {
  if (!isGuid(analysisId) || !isGuid(fileId)) throw new DoctorApiError(400, 'ERR_INVALID_FILE_ID')
  // File Access là endpoint dùng chung, nhưng bác sĩ phải kèm clinicContextId để backend kiểm tra quyền.
  const response = await request('/api/v1/analyses/' + analysisId + '/files/' + fileId + '/access' + clinicPath(clinicId),
    'GET', context, undefined, signal)
  if (response.status !== 200 || !record(response.data) || !text(response.data.url) ||
    !text(response.data.expiresAt)) invalid()
  let url: URL
  try { url = new URL(response.data.url) } catch { return invalid() }
  const expiry = Date.parse(response.data.expiresAt)
  if (!['http:', 'https:'].includes(url.protocol) ||
    (window.location.protocol === 'https:' && url.protocol !== 'https:') ||
    !Number.isFinite(expiry) || expiry <= Date.now() + 5000) invalid()
  return { url: response.data.url, expiresAt: response.data.expiresAt }
}
export function doctorErrorMessage(error: unknown): string {
  if (!(error instanceof DoctorApiError)) return 'Không thể kết nối API. Vui lòng thử lại.'
  if (error.status === 401) return 'Phiên đăng nhập không hợp lệ. Hãy đăng nhập lại.'
  if (error.status === 403) return 'Bạn không có quyền truy cập ca bệnh này tại cơ sở đã chọn.'
  if (error.status === 404) return 'Không tìm thấy ca chụp hoặc ảnh trong phạm vi được phân công.'
  if (error.code === 'ERR_MOCK_ENABLED') return 'Đang bật mock. Tắt VITE_ENABLE_MOCK để gọi API Doctor Review thật.'
  if (error.code === 'ERR_INVALID_RESPONSE') return 'Dữ liệu trả về không khớp hợp đồng Doctor Review API.'
  if (error.code === 'ERR_TIMEOUT') return 'API phản hồi quá thời gian; hãy thử lại.'
  return 'Không thực hiện được yêu cầu Doctor Review API. Vui lòng thử lại.'
}
