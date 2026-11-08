import type { BackendContext } from './backendContext'
import { authorizedApiResponse, AnalysisResultError } from './analysisResult'
import { validBatchId } from './batchProgress'

export type ClinicMemberRole = 'CLINIC_ADMIN' | 'DOCTOR' | 'STAFF'
export type ClinicMembershipStatus = 'ACTIVE' | 'REVOKED' | 'INVITED'

export type ClinicInfo = Readonly<{
  clinicId: string
  clinicName: string
  role: ClinicMemberRole
  status: string
}>

export type ClinicMember = Readonly<{
  membershipId: string
  clinicId: string
  accountId: string | null
  displayName: string | null
  role: ClinicMemberRole
  status: ClinicMembershipStatus
  createdAt: string
  updatedAt: string
  invitedEmail?: string | null
  invitationToken?: string | null
}>

export type ClinicInvitationResult = Readonly<{
  membershipId: string
  email: string
  role: string
  token: string
  expiresAt: string
}>

export type ClinicPatientMedicalHistory = Readonly<{
  hasDiabetes?: boolean | null
  hasHypertension?: boolean | null
  notes?: string | null
  otherConditions?: string | null
}>

export type ClinicPatient = Readonly<{
  patientId: string
  clinicId: string
  tenantId: string
  patientType: string
  fullName: string
  dateOfBirth?: string | null
  gender?: string | null
  phone?: string | null
  healthInsuranceNumber?: string | null
  medicalHistory?: ClinicPatientMedicalHistory | null
  status: string
  createdAt: string
  updatedAt: string
}>

export type RegisterClinicPatientInput = Readonly<{
  fullName: string
  dateOfBirth?: string | null
  gender?: string | null
  phone?: string | null
  healthInsuranceNumber?: string | null
  medicalHistory?: ClinicPatientMedicalHistory | null
}>

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function invalidResponse(): never {
  throw new AnalysisResultError(0, 'ERR_INVALID_RESPONSE')
}

export async function getActorClinics(
  context: BackendContext,
  signal?: AbortSignal,
): Promise<readonly ClinicInfo[]> {
  const response = await authorizedApiResponse('/api/v1/clinics', context, signal)
  const payload: unknown = await response.json().catch(() => null)
  if (!isRecord(payload) || !Array.isArray(payload.items)) invalidResponse()

  return payload.items.map((item) => {
    if (
      !isRecord(item) ||
      typeof item.clinicId !== 'string' ||
      !validBatchId(item.clinicId) ||
      typeof item.clinicName !== 'string' ||
      !['CLINIC_ADMIN', 'DOCTOR', 'STAFF'].includes(String(item.role))
    ) {
      invalidResponse()
    }
    return {
      clinicId: item.clinicId,
      clinicName: item.clinicName,
      role: item.role as ClinicMemberRole,
      status: String(item.status ?? 'ACTIVE'),
    }
  })
}

export async function getClinicMembers(
  context: BackendContext,
  clinicId: string,
  signal?: AbortSignal,
  status?: string,
): Promise<readonly ClinicMember[]> {
  if (!validBatchId(clinicId)) throw new AnalysisResultError(400, 'ERR_INVALID_CLINIC_ID')
  const query = status ? `?status=${encodeURIComponent(status)}` : ''
  const response = await authorizedApiResponse(
    `/api/v1/clinics/${clinicId}/memberships${query}`,
    context,
    signal,
  )
  const payload: unknown = await response.json().catch(() => null)
  if (!isRecord(payload) || payload.clinicId !== clinicId || !Array.isArray(payload.items)) {
    invalidResponse()
  }

  return payload.items.map((item) => {
    if (
      !isRecord(item) ||
      typeof item.membershipId !== 'string' ||
      !validBatchId(item.membershipId) ||
      typeof item.role !== 'string' ||
      typeof item.status !== 'string'
    ) {
      invalidResponse()
    }
    return {
      membershipId: item.membershipId,
      clinicId: String(item.clinicId ?? clinicId),
      accountId: typeof item.accountId === 'string' ? item.accountId : null,
      displayName: typeof item.displayName === 'string' ? item.displayName : null,
      role: item.role as ClinicMemberRole,
      status: item.status as ClinicMembershipStatus,
      createdAt: String(item.createdAt ?? ''),
      updatedAt: String(item.updatedAt ?? ''),
      invitedEmail: typeof item.invitedEmail === 'string' ? item.invitedEmail : null,
      invitationToken: typeof item.invitationToken === 'string' ? item.invitationToken : null,
    }
  })
}

export async function inviteClinicMember(
  context: BackendContext,
  clinicId: string,
  email: string,
  role: 'DOCTOR' | 'STAFF',
  signal?: AbortSignal,
): Promise<ClinicInvitationResult> {
  if (!validBatchId(clinicId)) throw new AnalysisResultError(400, 'ERR_INVALID_CLINIC_ID')
  if (!email || !email.includes('@')) throw new AnalysisResultError(400, 'ERR_VALIDATION')

  const response = await authorizedApiResponse(
    `/api/v1/clinics/${clinicId}/memberships/invite`,
    context,
    signal,
    'POST',
    JSON.stringify({ email: email.trim(), role }),
  )

  const payload: unknown = await response.json().catch(() => null)
  if (
    !isRecord(payload) ||
    typeof payload.membershipId !== 'string' ||
    typeof payload.email !== 'string' ||
    typeof payload.role !== 'string' ||
    typeof payload.token !== 'string'
  ) {
    invalidResponse()
  }

  return {
    membershipId: payload.membershipId,
    email: payload.email,
    role: payload.role,
    token: payload.token,
    expiresAt: String(payload.expiresAt ?? ''),
  }
}

export async function revokeClinicMember(
  context: BackendContext,
  clinicId: string,
  accountId: string,
  signal?: AbortSignal,
): Promise<void> {
  if (!validBatchId(clinicId) || !validBatchId(accountId)) {
    throw new AnalysisResultError(400, 'ERR_INVALID_ID')
  }

  await authorizedApiResponse(
    `/api/v1/clinics/${clinicId}/memberships/${accountId}`,
    context,
    signal,
    'DELETE',
  )
}

export async function acceptClinicInvitation(
  context: BackendContext,
  membershipId: string,
  token: string,
  signal?: AbortSignal,
): Promise<{ message: string }> {
  if (!validBatchId(membershipId)) throw new AnalysisResultError(400, 'ERR_INVALID_ID')
  if (!token.trim()) throw new AnalysisResultError(400, 'ERR_VALIDATION')

  const response = await authorizedApiResponse(
    `/api/v1/clinics/invitations/${membershipId}/accept`,
    context,
    signal,
    'POST',
    JSON.stringify({ token: token.trim() }),
  )

  const payload: unknown = await response.json().catch(() => null)
  return { message: isRecord(payload) && typeof payload.message === 'string' ? payload.message : 'Thành công' }
}

export async function getClinicPatients(
  context: BackendContext,
  clinicId: string,
  options?: { search?: string; status?: string; limit?: number; offset?: number },
  signal?: AbortSignal,
): Promise<{ clinicId: string; totalCount: number; items: readonly ClinicPatient[] }> {
  if (!validBatchId(clinicId)) throw new AnalysisResultError(400, 'ERR_INVALID_CLINIC_ID')
  const params = new URLSearchParams()
  params.set('limit', String(options?.limit ?? 50))
  params.set('offset', String(options?.offset ?? 0))
  if (options?.search?.trim()) params.set('search', options.search.trim())
  if (options?.status?.trim()) params.set('status', options.status.trim())

  const response = await authorizedApiResponse(
    `/api/v1/clinics/${clinicId}/patients?${params.toString()}`,
    context,
    signal,
  )

  const payload: unknown = await response.json().catch(() => null)
  if (!isRecord(payload) || payload.clinicId !== clinicId || !Array.isArray(payload.items)) {
    invalidResponse()
  }

  const items = payload.items.map((item) => {
    if (
      !isRecord(item) ||
      typeof item.patientId !== 'string' ||
      !validBatchId(item.patientId) ||
      typeof item.fullName !== 'string'
    ) {
      invalidResponse()
    }
    const medHistory = isRecord(item.medicalHistory)
      ? {
          hasDiabetes: item.medicalHistory.hasDiabetes === true,
          hasHypertension: item.medicalHistory.hasHypertension === true,
          notes: typeof item.medicalHistory.notes === 'string' ? item.medicalHistory.notes : null,
          otherConditions:
            typeof item.medicalHistory.otherConditions === 'string'
              ? item.medicalHistory.otherConditions
              : null,
        }
      : null

    return {
      patientId: item.patientId,
      clinicId: String(item.clinicId ?? clinicId),
      tenantId: String(item.tenantId ?? ''),
      patientType: String(item.patientType ?? 'CLINIC_MANAGED'),
      fullName: item.fullName,
      dateOfBirth: typeof item.dateOfBirth === 'string' ? item.dateOfBirth : null,
      gender: typeof item.gender === 'string' ? item.gender : null,
      phone: typeof item.phone === 'string' ? item.phone : null,
      healthInsuranceNumber:
        typeof item.healthInsuranceNumber === 'string' ? item.healthInsuranceNumber : null,
      medicalHistory: medHistory,
      status: String(item.status ?? 'ACTIVE'),
      createdAt: String(item.createdAt ?? ''),
      updatedAt: String(item.updatedAt ?? ''),
    }
  })

  return {
    clinicId,
    totalCount: typeof payload.totalCount === 'number' ? payload.totalCount : items.length,
    items,
  }
}

export async function getClinicPatientDetail(
  context: BackendContext,
  clinicId: string,
  patientId: string,
  signal?: AbortSignal,
): Promise<ClinicPatient> {
  if (!validBatchId(clinicId) || !validBatchId(patientId)) {
    throw new AnalysisResultError(400, 'ERR_INVALID_ID')
  }

  const response = await authorizedApiResponse(
    `/api/v1/clinics/${clinicId}/patients/${patientId}`,
    context,
    signal,
  )

  const item: unknown = await response.json().catch(() => null)
  if (!isRecord(item) || typeof item.patientId !== 'string' || typeof item.fullName !== 'string') {
    invalidResponse()
  }

  const medHistory = isRecord(item.medicalHistory)
    ? {
        hasDiabetes: item.medicalHistory.hasDiabetes === true,
        hasHypertension: item.medicalHistory.hasHypertension === true,
        notes: typeof item.medicalHistory.notes === 'string' ? item.medicalHistory.notes : null,
        otherConditions:
          typeof item.medicalHistory.otherConditions === 'string'
            ? item.medicalHistory.otherConditions
            : null,
      }
    : null

  return {
    patientId: item.patientId,
    clinicId: String(item.clinicId ?? clinicId),
    tenantId: String(item.tenantId ?? ''),
    patientType: String(item.patientType ?? 'CLINIC_MANAGED'),
    fullName: item.fullName,
    dateOfBirth: typeof item.dateOfBirth === 'string' ? item.dateOfBirth : null,
    gender: typeof item.gender === 'string' ? item.gender : null,
    phone: typeof item.phone === 'string' ? item.phone : null,
    healthInsuranceNumber:
      typeof item.healthInsuranceNumber === 'string' ? item.healthInsuranceNumber : null,
    medicalHistory: medHistory,
    status: String(item.status ?? 'ACTIVE'),
    createdAt: String(item.createdAt ?? ''),
    updatedAt: String(item.updatedAt ?? ''),
  }
}

export async function registerClinicPatient(
  context: BackendContext,
  clinicId: string,
  input: RegisterClinicPatientInput,
  signal?: AbortSignal,
): Promise<ClinicPatient> {
  if (!validBatchId(clinicId)) throw new AnalysisResultError(400, 'ERR_INVALID_CLINIC_ID')
  if (!input.fullName.trim()) throw new AnalysisResultError(400, 'ERR_VALIDATION')

  const response = await authorizedApiResponse(
    `/api/v1/clinics/${clinicId}/patients`,
    context,
    signal,
    'POST',
    JSON.stringify({
      fullName: input.fullName.trim(),
      dateOfBirth: input.dateOfBirth || null,
      gender: input.gender || null,
      phone: input.phone || null,
      healthInsuranceNumber: input.healthInsuranceNumber || null,
      medicalHistory: input.medicalHistory || null,
    }),
  )

  const item: unknown = await response.json().catch(() => null)
  if (!isRecord(item) || typeof item.patientId !== 'string' || typeof item.fullName !== 'string') {
    invalidResponse()
  }

  return {
    patientId: item.patientId,
    clinicId: String(item.clinicId ?? clinicId),
    tenantId: String(item.tenantId ?? ''),
    patientType: String(item.patientType ?? 'CLINIC_MANAGED'),
    fullName: item.fullName,
    dateOfBirth: typeof item.dateOfBirth === 'string' ? item.dateOfBirth : null,
    gender: typeof item.gender === 'string' ? item.gender : null,
    phone: typeof item.phone === 'string' ? item.phone : null,
    healthInsuranceNumber:
      typeof item.healthInsuranceNumber === 'string' ? item.healthInsuranceNumber : null,
    medicalHistory: isRecord(item.medicalHistory)
      ? {
          hasDiabetes: item.medicalHistory.hasDiabetes === true,
          hasHypertension: item.medicalHistory.hasHypertension === true,
          notes: typeof item.medicalHistory.notes === 'string' ? item.medicalHistory.notes : null,
          otherConditions:
            typeof item.medicalHistory.otherConditions === 'string'
              ? item.medicalHistory.otherConditions
              : null,
        }
      : null,
    status: String(item.status ?? 'ACTIVE'),
    createdAt: String(item.createdAt ?? ''),
    updatedAt: String(item.updatedAt ?? ''),
  }
}

