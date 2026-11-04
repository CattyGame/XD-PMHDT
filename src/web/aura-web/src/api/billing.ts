import type { BackendContext } from './backendContext'
import { isMockEnabled } from './client'
import { getFirebaseAuth } from '../firebase/config'

/** API Billing M3: 4 routes are implemented in Program.cs; the OpenAPI 0.2 catalog is incomplete. */
export type BillingOwner = Readonly<{ type: 'ACCOUNT' | 'CLINIC'; id: string }>
export type BillingPackage = Readonly<{ id: number; name: string; price: number; credits: number }>
export type PackageCatalog = Readonly<{
  data: readonly BillingPackage[]
  source: string
  cacheHit: boolean
  ttlSeconds: number
  databaseReadCount: number
}>
export type BillingOrderCreated = Readonly<{
  orderId: string
  status: string
  amount: number
  currency: string
  credits: number
  packageName: string
  paymentUrl: string
  createdAt: string
}>
export type BillingOrder = Readonly<{
  id: string
  billingOwnerType: 'ACCOUNT' | 'CLINIC'
  billingOwnerId: string
  packageNameSnapshot: string
  priceAmountSnapshot: number
  currencyCodeSnapshot: string
  creditsSnapshot: number
  environment: string
  status: 'PENDING' | 'COMPLETED' | 'CANCELLED'
}>
export type SandboxPayment = Readonly<{
  orderId: string
  transactionId: string
  status: string
  message: string
  creditsAdded: number
  balanceAfter: number
  replayed: boolean
}>

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i
const object = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value)
const text = (value: unknown): value is string => typeof value === 'string' && value.trim().length > 0
const natural = (value: unknown): value is number => Number.isSafeInteger(value) && (value as number) >= 0
const money = (value: unknown): value is number => typeof value === 'number' && Number.isFinite(value) && value >= 0
export const validBillingId = (value: unknown): value is string =>
  typeof value === 'string' && uuid.test(value) && value !== '00000000-0000-0000-0000-000000000000'

export class BillingError extends Error {
  readonly status: number
  readonly code: string
  readonly retryable: boolean
  constructor(status: number, code: string, retryable = false) {
    super(code)
    this.name = 'BillingError'
    this.status = status
    this.code = code
    this.retryable = retryable
  }
}
function invalid(): never { throw new BillingError(0, 'ERR_INVALID_RESPONSE') }

export function billingErrorMessage(cause: unknown): string {
  if (!(cause instanceof BillingError)) return 'Không kết nối được Billing API. Vui lòng thử lại.'
  if (cause.code === 'ERR_MOCK_ENABLED') return 'Tắt VITE_ENABLE_MOCK để dùng Billing API thật.'
  if (cause.status === 400) return 'Thông tin đơn hàng không hợp lệ. Vui lòng kiểm tra lại.'
  if (cause.status === 401) return 'Phiên đăng nhập không hợp lệ. Vui lòng đăng nhập lại.'
  if (cause.status === 403) return 'Tài khoản không có quyền thanh toán cho ví đã chọn.'
  if (cause.status === 404) return 'Không tìm thấy gói cước hoặc đơn hàng.'
  if (cause.status === 409) return 'Yêu cầu trùng hoặc trạng thái đơn hàng đã thay đổi. Hãy đọc lại đơn hàng.'
  if (cause.status === 429) return 'Quá nhiều yêu cầu. Vui lòng thử lại sau.'
  if (cause.code === 'ERR_INVALID_RESPONSE') return 'Billing API trả dữ liệu không đúng hợp đồng.'
  if (cause.status >= 500 || cause.code === 'ERR_NETWORK' || cause.code === 'ERR_TIMEOUT')
    return 'Chưa xác nhận được kết quả từ máy chủ. Không thực hiện thanh toán lại tự động.'
  return 'Billing API chưa xử lý được yêu cầu.'
}

export function parseCatalog(value: unknown): PackageCatalog {
  if (!object(value) || !Array.isArray(value.data) || value.data.length > 200 ||
      !text(value.source) || typeof value.cacheHit !== 'boolean' ||
      !natural(value.ttlSeconds) || !natural(value.databaseReadCount)) invalid()
  const rows = (value.data as unknown[]).map((item): BillingPackage => {
    if (!object(item) || !Number.isSafeInteger(item.id) || (item.id as number) <= 0 ||
        !text(item.name) || !money(item.price) || !natural(item.credits) || (item.credits as number) < 1) invalid()
    return { id: item.id as number, name: item.name, price: item.price as number, credits: item.credits as number }
  })
  if (new Set(rows.map(item => item.id)).size !== rows.length) invalid()
  return { data: rows, source: value.source, cacheHit: value.cacheHit,
    ttlSeconds: value.ttlSeconds as number, databaseReadCount: value.databaseReadCount as number }
}

export function parseCreatedOrder(value: unknown): BillingOrderCreated {
  if (!object(value) || !validBillingId(value.orderId) || !text(value.status) ||
      !money(value.amount) || !text(value.currency) || !natural(value.credits) ||
      !text(value.packageName) || typeof value.paymentUrl !== 'string' ||
      !text(value.createdAt) || !Number.isFinite(Date.parse(value.createdAt))) invalid()
  // Never navigate automatically to the paymentUrl: the internal sandbox route is used instead.
  return { orderId: value.orderId, status: value.status, amount: value.amount as number,
    currency: value.currency, credits: value.credits as number, packageName: value.packageName,
    paymentUrl: value.paymentUrl, createdAt: value.createdAt }
}

export function parseOrder(value: unknown, expectedId: string): BillingOrder {
  if (!object(value) || !validBillingId(value.id) || value.id.toLowerCase() !== expectedId.toLowerCase() ||
      !['ACCOUNT', 'CLINIC'].includes(String(value.billingOwnerType)) || !validBillingId(value.billingOwnerId) ||
      !text(value.packageNameSnapshot) || !money(value.priceAmountSnapshot) ||
      !text(value.currencyCodeSnapshot) || !natural(value.creditsSnapshot) ||
      !text(value.environment) || !['PENDING', 'COMPLETED', 'CANCELLED'].includes(String(value.status))) invalid()
  return {
    id: value.id, billingOwnerType: value.billingOwnerType as BillingOrder['billingOwnerType'],
    billingOwnerId: value.billingOwnerId, packageNameSnapshot: value.packageNameSnapshot,
    priceAmountSnapshot: value.priceAmountSnapshot as number,
    currencyCodeSnapshot: value.currencyCodeSnapshot,
    creditsSnapshot: value.creditsSnapshot as number, environment: value.environment,
    status: value.status as BillingOrder['status'],
  }
}

export function parseSandboxPayment(value: unknown, expectedId: string): SandboxPayment {
  if (!object(value) || !validBillingId(value.orderId) ||
      value.orderId.toLowerCase() !== expectedId.toLowerCase() ||
      typeof value.transactionId !== 'string' || !text(value.status) ||
      typeof value.message !== 'string' || !natural(value.creditsAdded) ||
      !natural(value.balanceAfter) || typeof value.replayed !== 'boolean') invalid()
  return { orderId: value.orderId, transactionId: value.transactionId,
    status: value.status, message: value.message, creditsAdded: value.creditsAdded as number,
    balanceAfter: value.balanceAfter as number, replayed: value.replayed }
}

/** Same-origin Gateway, no credentials stored in browser storage or URL. */
async function request(path: string, method: 'GET' | 'POST', context?: BackendContext,
                       body?: unknown, idempotencyKey?: string, signal?: AbortSignal): Promise<{ status: number; value: unknown }> {
  if (isMockEnabled) throw new BillingError(0, 'ERR_MOCK_ENABLED')
  const auth = context ? getFirebaseAuth() : null
  const user = auth?.currentUser
  if (context && (!user || user.uid !== context.firebaseUid)) throw new BillingError(401, 'ERR_SESSION_CHANGED')
  const checkSession = () => {
    if (signal?.aborted) throw new BillingError(0, 'ERR_CANCELLED')
    if (context && (auth?.currentUser !== user || user?.uid !== context.firebaseUid))
      throw new BillingError(401, 'ERR_SESSION_CHANGED')
  }
  const controller = signal
    ? AbortSignal.any([signal, AbortSignal.timeout(15000)])
    : AbortSignal.timeout(15000)
  try {
    checkSession()
    const token = user ? await user.getIdToken() : undefined
    checkSession()
    if (context && (!text(token) || /\s/.test(token))) throw new BillingError(401, 'ERR_TOKEN_UNAVAILABLE')
    const response = await fetch(path, {
      method, cache: 'no-store', credentials: 'omit', redirect: 'error', signal: controller,
      headers: {
        Accept: 'application/json',
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
        ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
        ...(idempotencyKey ? { 'Idempotency-Key': idempotencyKey } : {}),
      },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    })
    checkSession()
    const value: unknown = await response.json().catch(() => null)
    checkSession()
    if (!response.ok) {
      const payload = object(value) && object(value.error) ? value.error : null
      const code = typeof payload?.code === 'string' && /^ERR_[A-Z0-9_]{1,80}$/.test(payload.code)
        ? payload.code : 'ERR_REQUEST_FAILED'
      throw new BillingError(response.status, code, response.status >= 500 || response.status === 429)
    }
    return { status: response.status, value }
  } catch (cause) {
    if (cause instanceof BillingError) throw cause
    if (signal?.aborted) throw cause
    throw new BillingError(0, controller.aborted ? 'ERR_TIMEOUT' : 'ERR_NETWORK', true)
  }
}

export async function getBillingPackages(signal?: AbortSignal): Promise<PackageCatalog> {
  const response = await request('/api/v1/billing/packages', 'GET', undefined, undefined, undefined, signal)
  if (response.status !== 200) invalid()
  return parseCatalog(response.value)
}

export async function createBillingOrder(
  context: BackendContext, owner: BillingOwner, packageId: number, idempotencyKey: string,
): Promise<BillingOrderCreated> {
  if (!validBillingId(owner.id) || !['ACCOUNT', 'CLINIC'].includes(owner.type) ||
      !Number.isSafeInteger(packageId) || packageId < 1 || !validBillingId(idempotencyKey))
    throw new BillingError(400, 'ERR_VALIDATION')
  const response = await request('/api/v1/billing/orders', 'POST', context,
    { billingOwner: owner, packageId, paymentMethod: 'SANDBOX_MOCK' }, idempotencyKey)
  if (![200, 201].includes(response.status)) invalid()
  return parseCreatedOrder(response.value)
}

export async function getBillingOrder(context: BackendContext, orderId: string,
                                      signal?: AbortSignal): Promise<BillingOrder> {
  if (!validBillingId(orderId)) throw new BillingError(400, 'ERR_VALIDATION')
  const response = await request(`/api/v1/billing/orders/${orderId}`, 'GET', context,
    undefined, undefined, signal)
  if (response.status !== 200) invalid()
  return parseOrder(response.value, orderId)
}

export async function paySandbox(context: BackendContext, orderId: string,
                                 simulateFailure = false): Promise<SandboxPayment> {
  if (!validBillingId(orderId)) throw new BillingError(400, 'ERR_VALIDATION')
  const response = await request('/api/v1/billing/sandbox/pay', 'POST', context,
    { orderId, simulateFailure })
  if (response.status !== 200) invalid()
  return parseSandboxPayment(response.value, orderId)
}
