import { getFirebaseAuth } from '../firebase/config'
import { isMockEnabled } from './client'
import { ROLE_LABELS, type UserRole } from '../routes/roles'

export type BackendContext = Readonly<{
  accountId: string
  firebaseUid: string
  roles: readonly UserRole[]
  patientId: string
  personalTenantId: string
  displayName: string
  profileVersion: number
  clinicContextId?: string
}>

export class BackendContextError extends Error {
  readonly status: number
  readonly code: string
  readonly retryable: boolean
  readonly correlationId?: string

  constructor(status: number, code: string, retryable = false, correlationId?: string) {
    super(code === 'ERR_PROFILE_NOT_READY'
      ? 'Hồ sơ chưa sẵn sàng. Vui lòng thử lại sau.'
      : code === 'ERR_SESSION_CHANGED'
        ? 'Phiên đăng nhập đã thay đổi. Vui lòng đăng nhập lại.'
        : 'Không thể xác nhận tài khoản và hồ sơ AURA.')
    this.name = 'BackendContextError'
    this.status = status
    this.code = code
    this.retryable = retryable
    this.correlationId = correlationId
  }
}

function record(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

function validId(value: unknown): value is string {
  return typeof value === 'string' &&
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value) &&
    value !== '00000000-0000-0000-0000-000000000000'
}

function invalidResponse(): never {
  throw new BackendContextError(0, 'ERR_INVALID_RESPONSE')
}

function responseError(response: Response, payload: unknown): BackendContextError {
  const error = record(payload) && record(payload.error) ? payload.error : null
  const code = typeof error?.code === 'string' && /^ERR_[A-Z0-9_]{1,60}$/.test(error.code)
    ? error.code : 'ERR_REQUEST_FAILED'
  const correlationId = typeof error?.correlationId === 'string' &&
    /^[a-zA-Z0-9_-]{1,128}$/.test(error.correlationId) ? error.correlationId : undefined
  return new BackendContextError(response.status, code, error?.retryable === true, correlationId)
}

/** Calls same-origin Gateway endpoints. Does not cache context or retry bootstrap. */
export async function loadBackendContext(signal?: AbortSignal): Promise<BackendContext> {
  if (isMockEnabled) throw new BackendContextError(0, 'ERR_MOCK_ENABLED')

  const auth = getFirebaseAuth()
  const user = auth?.currentUser
  if (!user) throw new BackendContextError(401, 'ERR_UNAUTHORIZED')
  const uid = user.uid
  const checkSession = () => {
    if (signal?.aborted) throw new BackendContextError(0, 'ERR_CANCELLED')
    if (auth.currentUser !== user || user.uid !== uid) {
      throw new BackendContextError(401, 'ERR_SESSION_CHANGED')
    }
  }
  checkSession()

  let token: string
  try {
    token = await user.getIdToken()
  } catch {
    checkSession()
    throw new BackendContextError(401, 'ERR_TOKEN_UNAVAILABLE')
  }
  checkSession()
  if (typeof token !== 'string' || !token || /\s/.test(token)) {
    throw new BackendContextError(401, 'ERR_TOKEN_UNAVAILABLE')
  }

  async function request(path: string, method: 'GET' | 'POST'): Promise<unknown> {
    checkSession()
    let response: Response
    let payload: unknown
    try {
      response = await fetch(path, {
        method,
        headers: { Authorization: `Bearer ${token}` },
        cache: 'no-store',
        credentials: 'omit',
        redirect: 'error',
        signal: signal
          ? AbortSignal.any([signal, AbortSignal.timeout(15000)])
          : AbortSignal.timeout(15000),
      })
      checkSession()
      payload = await response.json().catch(() => null)
    } catch (error) {
      checkSession()
      if (error instanceof BackendContextError) throw error
      throw new BackendContextError(0, 'ERR_NETWORK', true)
    }
    checkSession()
    if (!response.ok) throw responseError(response, payload)
    return payload
  }

  const account = await request('/api/v1/identity/bootstrap', 'POST')
  if (!record(account) || !validId(account.accountId) || account.firebaseUid !== uid ||
    typeof account.accountStatus !== 'string' ||
    typeof account.profileProvisioningStatus !== 'string' ||
    !Array.isArray(account.roles) || account.roles.length === 0 ||
    !account.roles.every((role): role is UserRole =>
      typeof role === 'string' && Object.hasOwn(ROLE_LABELS, role))) {
    invalidResponse()
  }
  if (account.accountStatus !== 'ACTIVE') {
    throw new BackendContextError(403, 'ERR_ACCOUNT_DISABLED')
  }
  if (account.profileProvisioningStatus === 'PENDING') {
    throw new BackendContextError(409, 'ERR_PROFILE_NOT_READY', true)
  }
  if (account.profileProvisioningStatus !== 'READY') invalidResponse()

  const profile = await request('/api/v1/profiles/me', 'GET')
  if (!record(profile) || !validId(profile.accountId) ||
    profile.accountId.toLowerCase() !== account.accountId.toLowerCase() ||
    !validId(profile.patientId) || !validId(profile.personalTenantId) ||
    typeof profile.displayName !== 'string' || !profile.displayName.trim() ||
    !Number.isSafeInteger(profile.profileVersion) || (profile.profileVersion as number) < 1) {
    invalidResponse()
  }
  checkSession()
  return Object.freeze({
    accountId: account.accountId.toLowerCase(),
    firebaseUid: uid,
    roles: Object.freeze([...new Set(account.roles as UserRole[])]),
    patientId: profile.patientId.toLowerCase(),
    personalTenantId: profile.personalTenantId.toLowerCase(),
    displayName: profile.displayName,
    profileVersion: profile.profileVersion as number,
  })
}
