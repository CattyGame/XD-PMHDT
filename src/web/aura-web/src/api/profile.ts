import type { BackendContext } from './backendContext'
import { AnalysisResultError, authorizedApiResponse } from './analysisResult'
import { validBatchId } from './batchProgress'

export type UserProfile = Readonly<{
  accountId: string
  displayName: string
  phone: string | null
  patientId: string | null
  personalTenantId: string | null
  profileVersion: number
  email: string | null
}>

export type UpdateMyProfileInput = Readonly<{
  displayName: string
  phone: string | null
  expectedVersion: number
}>

const PHONE_REGEX = /^[0-9+\-\s().]{6,32}$/

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function invalidResponse(): never {
  throw new AnalysisResultError(0, 'ERR_INVALID_RESPONSE')
}

function parseUserProfile(payload: unknown): UserProfile {
  if (
    !isRecord(payload) ||
    typeof payload.accountId !== 'string' ||
    !validBatchId(payload.accountId) ||
    typeof payload.displayName !== 'string' ||
    !payload.displayName.trim() ||
    typeof payload.profileVersion !== 'number' ||
    !Number.isInteger(payload.profileVersion) ||
    payload.profileVersion < 1
  ) {
    invalidResponse()
  }

  const patientId =
    payload.patientId == null
      ? null
      : typeof payload.patientId === 'string' && validBatchId(payload.patientId)
        ? payload.patientId
        : invalidResponse()

  const personalTenantId =
    payload.personalTenantId == null
      ? null
      : typeof payload.personalTenantId === 'string' && validBatchId(payload.personalTenantId)
        ? payload.personalTenantId
        : invalidResponse()

  return {
    accountId: payload.accountId,
    displayName: payload.displayName,
    phone: typeof payload.phone === 'string' ? payload.phone : null,
    patientId,
    personalTenantId,
    profileVersion: payload.profileVersion,
    email: typeof payload.email === 'string' ? payload.email : null,
  }
}

export function validateProfileDraft(displayName: string, phone: string): string | null {
  const cleanName = displayName.trim()
  if (!cleanName || cleanName.length > 120) {
    return 'Tên hiển thị phải có từ 1 đến 120 ký tự.'
  }

  const cleanPhone = phone.trim()
  if (cleanPhone.length > 0 && (cleanPhone.length > 32 || !PHONE_REGEX.test(cleanPhone))) {
    return 'Số điện thoại không hợp lệ (6–32 ký tự số hoặc +, -, khoảng trắng).'
  }

  return null
}

export async function getMyProfile(
  context: BackendContext,
  signal?: AbortSignal,
): Promise<UserProfile> {
  const response = await authorizedApiResponse('/api/v1/profiles/me', context, signal)
  if (response.status !== 200) invalidResponse()
  const payload: unknown = await response.json().catch(() => null)
  return parseUserProfile(payload)
}

/**
 * Updates only self-service fields (displayName, phone) with If-Match ETag.
 * Never includes roles, accountId, tenantId, or patientId to prevent mass-assignment.
 */
export async function updateMyProfile(
  context: BackendContext,
  input: UpdateMyProfileInput,
  signal?: AbortSignal,
): Promise<UserProfile> {
  const cleanName = input.displayName.trim()
  const cleanPhone = input.phone?.trim() ? input.phone.trim() : null
  const validationError = validateProfileDraft(cleanName, cleanPhone ?? '')
  if (validationError || !Number.isInteger(input.expectedVersion) || input.expectedVersion < 1) {
    throw new AnalysisResultError(400, 'ERR_VALIDATION')
  }

  const response = await authorizedApiResponse(
    '/api/v1/profiles/me',
    context,
    signal,
    'PATCH',
    JSON.stringify({
      displayName: cleanName,
      phone: cleanPhone,
    }),
    15_000,
    { 'If-Match': `"${input.expectedVersion}"` },
  )

  if (response.status !== 200) invalidResponse()
  const payload: unknown = await response.json().catch(() => null)
  return parseUserProfile(payload)
}
