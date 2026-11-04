import type { BackendContext } from './backendContext'
import { authorizedApiResponse, AnalysisResultError } from './analysisResult'
import { validBatchId } from './batchProgress'
export type UploadClinic = Readonly<{ clinicId: string; clinicName: string; role: 'CLINIC_ADMIN' | 'DOCTOR' | 'STAFF'; status: 'ACTIVE' }>
export type UploadPatient = Readonly<{ patientId: string; fullName: string; status: string }>
const record = (value: unknown): value is Record<string, unknown> => !!value && typeof value === 'object' && !Array.isArray(value)
const invalid = (): never => { throw new AnalysisResultError(0, 'ERR_INVALID_RESPONSE') }
export async function getUploadClinics(context: BackendContext, signal?: AbortSignal): Promise<readonly UploadClinic[]> {
  const response = await authorizedApiResponse('/api/v1/clinics', context, signal)
  const payload: unknown = await response.json().catch(() => null)
  if (!record(payload) || !Array.isArray(payload.items)) return invalid()
  return payload.items.map(item => {
    if (!record(item) || typeof item.clinicId !== 'string' || !validBatchId(item.clinicId) ||
      typeof item.clinicName !== 'string' || !['CLINIC_ADMIN', 'DOCTOR', 'STAFF'].includes(String(item.role)) || item.status !== 'ACTIVE') return invalid()
    return item as unknown as UploadClinic
  })
}
export async function getUploadPatients(context: BackendContext, clinicId: string, search: string, signal?: AbortSignal): Promise<readonly UploadPatient[]> {
  if (!validBatchId(clinicId)) throw new AnalysisResultError(400, 'ERR_INVALID_CLINIC_ID')
  const response = await authorizedApiResponse(`/api/v1/clinics/${clinicId}/patients?limit=100&status=ACTIVE&search=${encodeURIComponent(search)}`, context, signal)
  const payload: unknown = await response.json().catch(() => null)
  if (!record(payload) || payload.clinicId !== clinicId || !Array.isArray(payload.items)) return invalid()
  return payload.items.map(item => {
    if (!record(item) || typeof item.patientId !== 'string' || !validBatchId(item.patientId) || typeof item.status !== 'string' ||
      (item.fullName != null && typeof item.fullName !== 'string')) return invalid()
    return { patientId: item.patientId, fullName: item.fullName as string || 'Bệnh nhân', status: item.status }
  })
}
