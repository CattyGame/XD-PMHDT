import type { BackendContext } from './backendContext'
import { AnalysisResultError, authorizedApiResponse } from './analysisResult'
import { validBatchId } from './batchProgress'

export type GlobalRole = 'PATIENT' | 'DOCTOR' | 'SYSTEM_ADMIN'
export type AdminAccountStatus = 'ACTIVE' | 'DISABLED'
export type AdminClinicStatus = 'PENDING' | 'ACTIVE' | 'SUSPENDED' | 'REJECTED'

export const GLOBAL_ROLES: readonly GlobalRole[] = ['PATIENT', 'DOCTOR', 'SYSTEM_ADMIN']

export const CANONICAL_ROLE_DISPLAY: Record<GlobalRole, string> = {
  PATIENT: 'PATIENT',
  DOCTOR: 'DOCTOR',
  SYSTEM_ADMIN: 'SuperAdmin',
}

export function formatGlobalRole(role: string): string {
  if (role === 'SYSTEM_ADMIN') return 'SuperAdmin'
  return role
}

export type AdminAccount = Readonly<{
  accountId: string
  firebaseUid: string
  displayName: string
  status: AdminAccountStatus
  accountStatus: string
  roles: readonly GlobalRole[]
  version: number
  profileProvisioningStatus: string
  createdAt: string
  updatedAt: string
}>

export type AdminAccountPage = Readonly<{
  items: readonly AdminAccount[]
  nextCursor: string | null
}>

export type AdminClinic = Readonly<{
  clinicId: string
  tenantId: string
  name: string
  taxId: string | null
  address: string | null
  representativeName: string | null
  phone: string | null
  status: AdminClinicStatus
  auditReason: string | null
  createdAt: string
  updatedAt: string
  version: number
}>

export type AdminClinicPage = Readonly<{
  items: readonly AdminClinic[]
  nextCursor: string | null
}>

export type AdminAuditEvent = Readonly<{
  eventId: string
  actorAccountId: string
  action: string
  targetType: string
  targetId: string
  reason: string | null
  correlationId: string | null
  createdAt: string
}>

export type AdminAuditEventPage = Readonly<{
  items: readonly AdminAuditEvent[]
  nextCursor: string | null
}>

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function invalidResponse(): never {
  throw new AnalysisResultError(0, 'ERR_INVALID_RESPONSE')
}

function validReason(reason: string): string {
  const clean = reason.trim()
  if (!clean || clean.length > 1000) {
    throw new AnalysisResultError(400, 'ERR_VALIDATION')
  }
  return clean
}

function parseAdminAccount(item: unknown): AdminAccount {
  if (
    !isRecord(item) ||
    typeof item.accountId !== 'string' ||
    !validBatchId(item.accountId) ||
    typeof item.firebaseUid !== 'string' ||
    typeof item.displayName !== 'string' ||
    (item.status !== 'ACTIVE' && item.status !== 'DISABLED') ||
    !Array.isArray(item.roles) ||
    item.roles.length === 0 ||
    !item.roles.every((r): r is GlobalRole => r === 'PATIENT' || r === 'DOCTOR' || r === 'SYSTEM_ADMIN') ||
    typeof item.version !== 'number' ||
    !Number.isInteger(item.version) ||
    item.version < 1
  ) {
    invalidResponse()
  }

  return {
    accountId: item.accountId,
    firebaseUid: item.firebaseUid,
    displayName: item.displayName,
    status: item.status,
    accountStatus: typeof item.accountStatus === 'string' ? item.accountStatus : item.status,
    roles: item.roles,
    version: item.version,
    profileProvisioningStatus: String(item.profileProvisioningStatus ?? ''),
    createdAt: String(item.createdAt ?? ''),
    updatedAt: String(item.updatedAt ?? ''),
  }
}

function parseAdminClinic(item: unknown): AdminClinic {
  if (
    !isRecord(item) ||
    typeof item.clinicId !== 'string' ||
    !validBatchId(item.clinicId) ||
    typeof item.tenantId !== 'string' ||
    !validBatchId(item.tenantId) ||
    typeof item.name !== 'string' ||
    !['PENDING', 'ACTIVE', 'SUSPENDED', 'REJECTED'].includes(String(item.status)) ||
    typeof item.version !== 'number' ||
    !Number.isInteger(item.version) ||
    item.version < 1
  ) {
    invalidResponse()
  }

  return {
    clinicId: item.clinicId,
    tenantId: item.tenantId,
    name: item.name,
    taxId: typeof item.taxId === 'string' ? item.taxId : null,
    address: typeof item.address === 'string' ? item.address : null,
    representativeName: typeof item.representativeName === 'string' ? item.representativeName : null,
    phone: typeof item.phone === 'string' ? item.phone : null,
    status: item.status as AdminClinicStatus,
    auditReason: typeof item.auditReason === 'string' ? item.auditReason : null,
    createdAt: String(item.createdAt ?? ''),
    updatedAt: String(item.updatedAt ?? ''),
    version: item.version,
  }
}

function parseAuditEvent(item: unknown): AdminAuditEvent {
  if (
    !isRecord(item) ||
    typeof item.eventId !== 'string' ||
    typeof item.actorAccountId !== 'string' ||
    typeof item.action !== 'string' ||
    typeof item.targetType !== 'string' ||
    typeof item.targetId !== 'string'
  ) {
    invalidResponse()
  }

  return {
    eventId: item.eventId,
    actorAccountId: item.actorAccountId,
    action: item.action,
    targetType: item.targetType,
    targetId: item.targetId,
    reason: typeof item.reason === 'string' ? item.reason : null,
    correlationId: typeof item.correlationId === 'string' ? item.correlationId : null,
    createdAt: String(item.createdAt ?? ''),
  }
}

export async function listAdminAccounts(
  context: BackendContext,
  params?: { limit?: number; cursor?: string; status?: AdminAccountStatus; query?: string },
  signal?: AbortSignal,
): Promise<AdminAccountPage> {
  const qs = new URLSearchParams()
  qs.set('limit', String(params?.limit ?? 20))
  if (params?.cursor) qs.set('cursor', params.cursor)
  if (params?.status) qs.set('status', params.status)
  if (params?.query?.trim()) qs.set('query', params.query.trim())

  const response = await authorizedApiResponse(`/api/v1/admin/accounts?${qs}`, context, signal)
  if (response.status !== 200) invalidResponse()
  const payload: unknown = await response.json().catch(() => null)
  if (!isRecord(payload) || !Array.isArray(payload.items)) invalidResponse()

  return {
    items: payload.items.map(parseAdminAccount),
    nextCursor: typeof payload.nextCursor === 'string' ? payload.nextCursor : null,
  }
}

export async function updateAdminAccountStatus(
  context: BackendContext,
  accountId: string,
  input: { status: AdminAccountStatus; expectedVersion: number; reason: string },
  signal?: AbortSignal,
): Promise<AdminAccount> {
  if (!validBatchId(accountId)) throw new AnalysisResultError(400, 'ERR_INVALID_ID')
  if (!Number.isInteger(input.expectedVersion) || input.expectedVersion < 1) {
    throw new AnalysisResultError(400, 'ERR_VALIDATION')
  }
  const cleanReason = validReason(input.reason)

  const response = await authorizedApiResponse(
    `/api/v1/admin/accounts/${accountId}/status`,
    context,
    signal,
    'PATCH',
    JSON.stringify({
      status: input.status,
      expectedVersion: input.expectedVersion,
      reason: cleanReason,
    }),
  )
  if (response.status !== 200) invalidResponse()
  const payload: unknown = await response.json().catch(() => null)
  return parseAdminAccount(payload)
}

export async function updateAdminAccountRoles(
  context: BackendContext,
  accountId: string,
  input: { roles: readonly GlobalRole[]; expectedVersion: number; reason: string },
  signal?: AbortSignal,
): Promise<AdminAccount> {
  if (!validBatchId(accountId)) throw new AnalysisResultError(400, 'ERR_INVALID_ID')
  if (
    input.roles.length === 0 ||
    !Number.isInteger(input.expectedVersion) ||
    input.expectedVersion < 1
  ) {
    throw new AnalysisResultError(400, 'ERR_VALIDATION')
  }
  const cleanReason = validReason(input.reason)

  const response = await authorizedApiResponse(
    `/api/v1/admin/accounts/${accountId}/roles`,
    context,
    signal,
    'PATCH',
    JSON.stringify({
      roles: input.roles,
      expectedVersion: input.expectedVersion,
      reason: cleanReason,
    }),
  )
  if (response.status !== 200) invalidResponse()
  const payload: unknown = await response.json().catch(() => null)
  return parseAdminAccount(payload)
}

export async function setDoctorCapability(
  context: BackendContext,
  accountId: string,
  input: { approved: boolean; reason: string },
  signal?: AbortSignal,
): Promise<void> {
  if (!validBatchId(accountId)) throw new AnalysisResultError(400, 'ERR_INVALID_ID')
  const cleanReason = validReason(input.reason)

  const response = await authorizedApiResponse(
    `/api/v1/admin/accounts/${accountId}/doctor-capability`,
    context,
    signal,
    'PUT',
    JSON.stringify({
      approved: input.approved,
      reason: cleanReason,
    }),
  )
  if (response.status !== 204 && response.status !== 200) invalidResponse()
}

export async function listAdminClinics(
  context: BackendContext,
  params?: { limit?: number; cursor?: string; status?: AdminClinicStatus },
  signal?: AbortSignal,
): Promise<AdminClinicPage> {
  const qs = new URLSearchParams()
  qs.set('limit', String(params?.limit ?? 20))
  if (params?.cursor) qs.set('cursor', params.cursor)
  if (params?.status) qs.set('status', params.status)

  const response = await authorizedApiResponse(`/api/v1/admin/clinics?${qs}`, context, signal)
  if (response.status !== 200) invalidResponse()
  const payload: unknown = await response.json().catch(() => null)
  if (!isRecord(payload) || !Array.isArray(payload.items)) invalidResponse()

  return {
    items: payload.items.map(parseAdminClinic),
    nextCursor: typeof payload.nextCursor === 'string' ? payload.nextCursor : null,
  }
}

export async function updateAdminClinicStatus(
  context: BackendContext,
  clinicId: string,
  input: { status: AdminClinicStatus; expectedVersion: number; reason: string },
  signal?: AbortSignal,
): Promise<AdminClinic> {
  if (!validBatchId(clinicId)) throw new AnalysisResultError(400, 'ERR_INVALID_CLINIC_ID')
  if (!Number.isInteger(input.expectedVersion) || input.expectedVersion < 1) {
    throw new AnalysisResultError(400, 'ERR_VALIDATION')
  }
  const cleanReason = validReason(input.reason)

  const response = await authorizedApiResponse(
    `/api/v1/admin/clinics/${clinicId}/status`,
    context,
    signal,
    'PATCH',
    JSON.stringify({
      status: input.status,
      expectedVersion: input.expectedVersion,
      reason: cleanReason,
    }),
  )
  if (response.status !== 200) invalidResponse()
  const payload: unknown = await response.json().catch(() => null)
  return parseAdminClinic(payload)
}

export async function listAdminAuditEvents(
  context: BackendContext,
  params?: { from?: string; to?: string; limit?: number; cursor?: string },
  signal?: AbortSignal,
): Promise<AdminAuditEventPage> {
  const qs = new URLSearchParams()
  qs.set('limit', String(params?.limit ?? 20))
  if (params?.from) qs.set('from', params.from)
  if (params?.to) qs.set('to', params.to)
  if (params?.cursor) qs.set('cursor', params.cursor)

  const response = await authorizedApiResponse(`/api/v1/admin/audit-events?${qs}`, context, signal)
  if (response.status !== 200) invalidResponse()
  const payload: unknown = await response.json().catch(() => null)
  if (!isRecord(payload) || !Array.isArray(payload.items)) invalidResponse()

  return {
    items: payload.items.map(parseAuditEvent),
    nextCursor: typeof payload.nextCursor === 'string' ? payload.nextCursor : null,
  }
}
