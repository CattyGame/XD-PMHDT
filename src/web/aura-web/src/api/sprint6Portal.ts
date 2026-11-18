import { AnalysisResultError, authorizedApiResponse } from './analysisResult'
import type { BackendContext } from './backendContext'
import { validBillingId } from './billing'

type RecordValue = Record<string, unknown>
type Method = 'GET' | 'POST' | 'PATCH' | 'PUT'
type Page<T> = { items: T[]; nextCursor: string | null }
type VersionChange = { expectedVersion: number; reason: string }
export type DateRange = { from: string; to: string }
export type PortalOwner = { type: 'PERSONAL' | 'CLINIC'; id: string }
export const accountRoles = ['PATIENT', 'DOCTOR', 'SYSTEM_ADMIN'] as const
export const clinicStatuses = ['PENDING', 'ACTIVE', 'SUSPENDED', 'REJECTED'] as const
const accountStatuses = ['ACTIVE', 'DISABLED'] as const
const environments = ['TEST', 'LIVE'] as const
const packageStatuses = ['ACTIVE', 'RETIRED'] as const

function invalid(): never { throw new AnalysisResultError(0, 'ERR_INVALID_RESPONSE') }
function object(value: unknown): RecordValue {
  if (!value || typeof value !== 'object' || Array.isArray(value)) invalid()
  return value as RecordValue
}
function string(value: unknown) {
  if (typeof value !== 'string' || !value.trim()) invalid()
  return value
}
function identifier(value: unknown) { return validBillingId(value) ? value : invalid() }
function integer(value: unknown, min = 0) {
  if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < min) invalid()
  return value
}
function count(value: unknown) { return value === null ? null : integer(value) }
function date(value: unknown) {
  const iso = string(value)
  if (!Number.isFinite(Date.parse(iso))) invalid()
  return iso
}
function oneOf<T extends string>(value: unknown, options: readonly T[]): T {
  if (typeof value !== 'string' || !options.includes(value as T)) invalid()
  return value as T
}
function array<T>(value: unknown, parse: (value: unknown) => T): T[] {
  if (!Array.isArray(value) || value.length > 100) invalid()
  return value.map(parse)
}
function page<T>(value: unknown, parse: (value: unknown) => T): Page<T> {
  const row = object(value)
  return { items: array(row.items, parse), nextCursor: row.nextCursor === null ? null : string(row.nextCursor) }
}
function currency(value: unknown) {
  const code = string(value)
  return /^[A-Z]{3}$/.test(code) ? code : invalid()
}

async function request(context: BackendContext, path: string, signal: AbortSignal,
  method: Method = 'GET', body?: object) {
  const response = await authorizedApiResponse(path, context, signal, method, body && JSON.stringify(body))
  const payload: unknown = await response.json().catch(() => invalid())
  if (signal.aborted) throw signal.reason
  return payload
}
function admin(context: BackendContext, path: string, signal: AbortSignal, method?: Method, body?: object) {
  if (!context.roles.includes('SYSTEM_ADMIN')) throw new AnalysisResultError(403, 'ERR_FORBIDDEN')
  return request(context, '/api/v1/admin' + path, signal, method, body)
}
function paging(cursor: string | null) {
  return { limit: '20', ...(cursor ? { cursor } : {}) }
}
function ownerQuery(context: BackendContext, owner: PortalOwner) {
  if (!validBillingId(owner.id) || (owner.type === 'PERSONAL' && owner.id !== context.accountId))
    throw new AnalysisResultError(403, 'ERR_FORBIDDEN')
  return { ownerId: owner.id, ownerType: owner.type }
}
function checkedOwner(row: RecordValue, owner: PortalOwner) {
  if (row.ownerType !== owner.type || row.ownerId !== owner.id) invalid()
}

export async function ownerOrders(context: BackendContext, owner: PortalOwner, cursor: string | null, signal: AbortSignal) {
  const query = new URLSearchParams({ ...ownerQuery(context, owner), ...paging(cursor) })
  return page(await request(context, '/api/v1/billing/orders?' + query, signal), value => {
    const row = object(value)
    checkedOwner(row, owner)
    return { orderId: identifier(row.orderId), status: string(row.status), environment: oneOf(row.environment, environments),
      priceVersionId: identifier(row.priceVersionId), amountMinor: integer(row.amountMinor), currency: currency(row.currency),
      credits: integer(row.credits), createdAt: date(row.createdAt) }
  })
}
export async function ownerUsage(context: BackendContext, owner: PortalOwner, range: DateRange, signal: AbortSignal) {
  const query = new URLSearchParams({ ...ownerQuery(context, owner), ...range })
  const row = object(await request(context, '/api/v1/billing/usage?' + query, signal))
  checkedOwner(row, owner)
  return { environment: oneOf(row.environment, environments), from: date(row.from), to: date(row.to),
    available: count(row.availableCredits), reserved: count(row.reservedCredits), captured: count(row.capturedCreditsInPeriod),
    released: count(row.releasedCreditsInPeriod), refunded: count(row.refundedCreditsInPeriod) }
}

export async function analytics(context: BackendContext, range: DateRange, signal: AbortSignal,
  scope: 'admin' | { type: 'clinic' | 'doctor'; clinicId: string } = 'admin') {
  const query = new URLSearchParams(range)
  if (scope !== 'admin' && scope.type === 'doctor') query.set('clinicContextId', identifier(scope.clinicId))
  const value = scope === 'admin' ? await admin(context, '/analytics/summary?' + query, signal)
    : await request(context, (scope.type === 'doctor' ? '/api/v1/doctors/me/summary'
      : '/api/v1/analytics/clinics/' + identifier(scope.clinicId) + '/summary') + '?' + query, signal)
  const row = object(value)
  if (row.unit !== 'UNIQUE_ANALYSIS' || row.riskSource !== 'LATEST_CLINICIAN_REVIEW') invalid()
  const p = object(row.processing), r = object(row.reviewRisk)
  const processing = { active: count(p.active), completed: count(p.completed), failed: count(p.failed),
    lowQuality: count(p.lowQuality), rejected: count(p.rejected), duplicate: count(p.duplicate) }
  const total = count(row.total)
  const parts = Object.values(processing)
  if (total !== null && parts.every(part => part !== null) &&
    parts.reduce<number>((sum, part) => sum + (part ?? 0), 0) !== total) invalid()
  return { total, processing, review: { low: count(r.low), moderate: count(r.moderate), high: count(r.high),
    unavailable: count(r.unavailable), unreviewed: count(r.unreviewed) }, from: date(row.from), to: date(row.to),
    measured: count(row.measurementAvailable), unavailable: count(row.measurementUnavailable),
    measurementUnknown: row.measurementUnknown === undefined ? null : count(row.measurementUnknown), asOf: date(row.asOf) }
}
export async function financial(context: BackendContext, range: DateRange, signal: AbortSignal) {
  const row = object(await admin(context, '/billing/summary?' + new URLSearchParams(range), signal))
  return { from: date(row.from), to: date(row.to), groups: array(row.groups, value => {
    const item = object(value)
    return { currency: currency(item.currency), environment: oneOf(item.environment, environments),
      orders: count(item.successfulOrders), gross: count(item.grossAmountMinor), refunds: count(item.refundAmountMinor),
      net: item.netAmountMinor === null ? null : integer(item.netAmountMinor, Number.MIN_SAFE_INTEGER) }
  }) }
}

function account(value: unknown) {
  const row = object(value)
  return { id: identifier(row.accountId), displayName: typeof row.displayName === 'string' ? row.displayName : invalid(),
    roles: array(row.roles, role => oneOf(role, accountRoles)), status: oneOf(row.status, accountStatuses), version: integer(row.version, 1) }
}
export async function accounts(context: BackendContext, cursor: string | null, signal: AbortSignal) {
  return page(await admin(context, '/accounts?' + new URLSearchParams(paging(cursor)), signal), account)
}
export async function changeAccount(context: BackendContext, id: string, change: VersionChange &
  ({ status: typeof accountStatuses[number] } | { roles: typeof accountRoles[number][] }), signal: AbortSignal) {
  const field = 'roles' in change ? 'roles' : 'status'
  const saved = account(await admin(context, '/accounts/' + identifier(id) + '/' + field, signal,
    'PATCH', change))
  if (saved.id !== id || saved.version <= change.expectedVersion) invalid()
}
export async function clinics(context: BackendContext, cursor: string | null, signal: AbortSignal) {
  return page(await admin(context, '/clinics?' + new URLSearchParams(paging(cursor)), signal), value => {
    const row = object(value)
    return { id: identifier(row.clinicId), name: string(row.name), status: oneOf(row.status, clinicStatuses),
      version: integer(row.version, 1) }
  })
}
export async function changeClinicStatus(context: BackendContext, id: string,
  status: typeof clinicStatuses[number], reason: string, signal: AbortSignal) {
  const saved = object(await admin(context, '/clinics/' + identifier(id) + '/status', signal, 'PATCH', { status, reason }))
  if (saved.clinicId !== id || saved.status !== status) invalid()
}
export async function changeDoctorCapability(context: BackendContext, id: string, approved: boolean,
  reason: string, signal: AbortSignal) {
  if (!context.roles.includes('SYSTEM_ADMIN')) throw new AnalysisResultError(403, 'ERR_FORBIDDEN')
  const response = await authorizedApiResponse('/api/v1/admin/accounts/' + identifier(id) + '/doctor-capability',
    context, signal, 'PUT', JSON.stringify({ approved, reason }))
  if (response.status !== 204) invalid()
}

function price(value: unknown) {
  const row = object(value)
  return { id: integer(row.packageId, 1), priceVersionId: identifier(row.priceVersionId), name: string(row.displayName),
    amount: integer(row.amountMinor), currency: currency(row.currency), credits: integer(row.credits, 1),
    status: oneOf(row.status, packageStatuses), version: integer(row.version, 1) }
}
export async function adminPackages(context: BackendContext, cursor: string | null, signal: AbortSignal) {
  return page(await admin(context, '/billing/packages?' + new URLSearchParams(paging(cursor)), signal), price)
}
export type PriceInput = { displayName: string; amountMinor: number; currency: string; credits: number; reason: string }
export async function savePrice(context: BackendContext, input: PriceInput, signal: AbortSignal,
  current?: { id: number; version: number }) {
  const root = '/billing/packages'
  const saved = price(await admin(context, current ? root + '/' + integer(current.id, 1) + '/price-versions' : root,
    signal, 'POST', current ? { ...input, expectedVersion: current.version } : input))
  if (current && (saved.id !== current.id || saved.version <= current.version)) invalid()
}
export async function retirePackage(context: BackendContext, id: number, change: VersionChange, signal: AbortSignal) {
  const row = object(await admin(context, '/billing/packages/' + integer(id, 1) + '/status', signal, 'PATCH',
    { ...change, status: 'RETIRED' }))
  if (row.packageId !== id || row.status !== 'RETIRED' || integer(row.version, 1) <= change.expectedVersion) invalid()
}
function config(value: unknown) {
  const row = object(value)
  const sha = string(row.thresholdConfigSha256)
  if (!/^[a-f0-9]{64}$/.test(sha)) invalid()
  return { manifest: identifier(row.artifactManifestId), sha, version: integer(row.version, 1), activatedAt: date(row.activatedAt) }
}
export async function aiConfig(context: BackendContext, signal: AbortSignal) {
  return config(await admin(context, '/ai/config', signal))
}
export async function saveAiConfig(context: BackendContext, data: VersionChange &
  { artifactManifestId: string; thresholdConfigSha256: string }, signal: AbortSignal) {
  const saved = config(await admin(context, '/ai/config', signal, 'PUT', data))
  if (saved.manifest !== data.artifactManifestId || saved.sha !== data.thresholdConfigSha256 ||
    saved.version <= data.expectedVersion) invalid()
}
function template(value: unknown) {
  const row = object(value)
  if (typeof row.enabled !== 'boolean') invalid()
  return { id: identifier(row.templateId), event: oneOf(row.eventType, ['ANALYSIS_READY', 'CLINICIAN_REVIEW_HIGH',
    'CREDIT_LOW', 'CHAT_MESSAGE']), title: string(row.title), body: string(row.body),
  enabled: row.enabled, version: integer(row.version, 1) }
}
export async function templates(context: BackendContext, signal: AbortSignal) {
  return array(object(await admin(context, '/notification-templates', signal)).items, template)
}
export async function saveTemplate(context: BackendContext, id: string, data: VersionChange &
  { title: string; body: string; enabled: boolean }, signal: AbortSignal) {
  const saved = template(await admin(context, '/notification-templates/' + identifier(id), signal, 'PUT', data))
  if (saved.id !== id || saved.version <= data.expectedVersion) invalid()
}
export async function audit(context: BackendContext, range: DateRange, cursor: string | null, signal: AbortSignal) {
  return page(await admin(context, '/audit-events?' + new URLSearchParams({ ...range, ...paging(cursor) }), signal), value => {
    const row = object(value)
    return { id: identifier(row.eventId), service: string(row.sourceService), action: string(row.action),
      resourceType: string(row.resourceType), resourceHash: string(row.resourceIdHash),
      correlation: string(row.correlationId), time: date(row.occurredAt), outcome: oneOf(row.outcome, ['ALLOW', 'DENY', 'FAIL']) }
  })
}

export function portalError(cause: unknown) {
  if (!(cause instanceof AnalysisResultError)) return 'Không kết nối được API. Vui lòng thử lại.'
  if (cause.status === 401) return 'Phiên đăng nhập đã thay đổi. Vui lòng đăng nhập lại.'
  if (cause.status === 403) return 'Tài khoản hiện không có quyền truy cập dữ liệu này.'
  if (cause.status === 404) return 'Dữ liệu hoặc chức năng này chưa có trên môi trường hiện tại.'
  if (cause.status === 409) return 'Phiên bản đã thay đổi. Hãy tải lại dữ liệu trước khi thực hiện lại.'
  if (cause.status === 429) return 'Quá nhiều yêu cầu. Vui lòng thử lại sau.'
  if (cause.code === 'ERR_INVALID_RESPONSE') return 'Phản hồi API không đúng hợp đồng dữ liệu.'
  if (cause.code === 'ERR_MOCK_ENABLED') return 'Trang này cần kết nối API của môi trường AURA.'
  if (cause.status === 400) return 'Máy chủ từ chối các giá trị đã nhập. Hãy kiểm tra lại.'
  return 'Máy chủ chưa hoàn thành yêu cầu. Vui lòng thử lại.'
}
