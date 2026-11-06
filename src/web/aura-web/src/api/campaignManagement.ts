import type { BackendContext } from './backendContext'
import { authorizedApiResponse, AnalysisResultError } from './analysisResult'
import { validBatchId } from './batchProgress'

export type CampaignStatus = 'PLANNED' | 'IN_PROGRESS' | 'COMPLETED' | 'CLOSED'

export type CampaignItem = Readonly<{
  id: string
  tenantId: string
  name: string
  description?: string | null
  location: string
  doctorInChargeId?: string | null
  targetPatientCount: number
  status: CampaignStatus
  startDate: string
  endDate: string
  createdAt: string
  updatedAt: string
}>

export type CreateCampaignInput = Readonly<{
  name: string
  description?: string | null
  location: string
  doctorInChargeId?: string | null
  targetPatientCount: number
  startDate: string
  endDate: string
}>

export type CampaignSummary = Readonly<{
  campaignId: string
  tenantId: string
  campaignName: string
  status: string
  targetPatientCount: number
  totalBatches: number
  totalImages: number
  completedImages: number
  pendingImages: number
  failedImages: number
  completionRatePercent: number
  normalCases: number
  suspectedCases: number
  highRiskCases: number
  uniquePatients: number
}>

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function invalidResponse(): never {
  throw new AnalysisResultError(0, 'ERR_INVALID_RESPONSE')
}

function parseCampaignItem(item: unknown): CampaignItem {
  if (
    !isRecord(item) ||
    typeof item.id !== 'string' ||
    !validBatchId(item.id) ||
    typeof item.tenantId !== 'string' ||
    typeof item.name !== 'string' ||
    typeof item.location !== 'string' ||
    typeof item.status !== 'string'
  ) {
    invalidResponse()
  }

  return {
    id: item.id,
    tenantId: item.tenantId,
    name: item.name,
    description: typeof item.description === 'string' ? item.description : null,
    location: item.location,
    doctorInChargeId: typeof item.doctorInChargeId === 'string' ? item.doctorInChargeId : null,
    targetPatientCount: typeof item.targetPatientCount === 'number' ? item.targetPatientCount : 0,
    status: item.status as CampaignStatus,
    startDate: String(item.startDate ?? ''),
    endDate: String(item.endDate ?? ''),
    createdAt: String(item.createdAt ?? ''),
    updatedAt: String(item.updatedAt ?? ''),
  }
}

export async function getCampaigns(
  context: BackendContext,
  clinicId: string,
  signal?: AbortSignal,
  status?: string,
): Promise<readonly CampaignItem[]> {
  if (!validBatchId(clinicId)) throw new AnalysisResultError(400, 'ERR_INVALID_CLINIC_ID')
  const statusQuery = status && status !== 'ALL' ? `&status=${encodeURIComponent(status)}` : ''
  const response = await authorizedApiResponse(
    `/api/v1/campaigns?clinicContextId=${encodeURIComponent(clinicId)}${statusQuery}`,
    context,
    signal,
  )
  const payload: unknown = await response.json().catch(() => null)
  if (!Array.isArray(payload)) invalidResponse()

  return payload.map(parseCampaignItem)
}

export async function getCampaignById(
  context: BackendContext,
  clinicId: string,
  campaignId: string,
  signal?: AbortSignal,
): Promise<CampaignItem> {
  if (!validBatchId(clinicId)) throw new AnalysisResultError(400, 'ERR_INVALID_CLINIC_ID')
  if (!validBatchId(campaignId)) throw new AnalysisResultError(400, 'ERR_INVALID_CAMPAIGN_ID')

  const response = await authorizedApiResponse(
    `/api/v1/campaigns/${encodeURIComponent(campaignId)}?clinicContextId=${encodeURIComponent(clinicId)}`,
    context,
    signal,
  )
  const payload: unknown = await response.json().catch(() => null)
  return parseCampaignItem(payload)
}

export async function createCampaign(
  context: BackendContext,
  clinicId: string,
  input: CreateCampaignInput,
  signal?: AbortSignal,
): Promise<CampaignItem> {
  if (!validBatchId(clinicId)) throw new AnalysisResultError(400, 'ERR_INVALID_CLINIC_ID')
  if (!input.name || !input.name.trim()) throw new AnalysisResultError(400, 'ERR_VALIDATION')
  if (!input.location || !input.location.trim()) throw new AnalysisResultError(400, 'ERR_VALIDATION')

  const response = await authorizedApiResponse(
    `/api/v1/campaigns?clinicContextId=${encodeURIComponent(clinicId)}`,
    context,
    signal,
    'POST',
    JSON.stringify({
      name: input.name.trim(),
      description: input.description?.trim() || null,
      location: input.location.trim(),
      doctorInChargeId: input.doctorInChargeId || null,
      targetPatientCount: Number(input.targetPatientCount) || 0,
      startDate: input.startDate,
      endDate: input.endDate,
    }),
  )
  const payload: unknown = await response.json().catch(() => null)
  return parseCampaignItem(payload)
}

export async function linkBatchToCampaign(
  context: BackendContext,
  clinicId: string,
  campaignId: string,
  batchId: string,
  signal?: AbortSignal,
): Promise<{ readonly message: string }> {
  if (!validBatchId(clinicId)) throw new AnalysisResultError(400, 'ERR_INVALID_CLINIC_ID')
  if (!validBatchId(campaignId)) throw new AnalysisResultError(400, 'ERR_INVALID_CAMPAIGN_ID')
  if (!validBatchId(batchId)) throw new AnalysisResultError(400, 'ERR_INVALID_BATCH_ID')

  const response = await authorizedApiResponse(
    `/api/v1/campaigns/${encodeURIComponent(campaignId)}/batches?clinicContextId=${encodeURIComponent(clinicId)}`,
    context,
    signal,
    'POST',
    JSON.stringify({ batchId: batchId.trim() }),
  )
  const payload: unknown = await response.json().catch(() => null)
  if (!isRecord(payload) || typeof payload.message !== 'string') {
    return { message: 'Liên kết batch vào chiến dịch tầm soát thành công.' }
  }
  return { message: payload.message }
}

export async function updateCampaignStatus(
  context: BackendContext,
  clinicId: string,
  campaignId: string,
  status: CampaignStatus,
  signal?: AbortSignal,
): Promise<CampaignItem> {
  if (!validBatchId(clinicId)) throw new AnalysisResultError(400, 'ERR_INVALID_CLINIC_ID')
  if (!validBatchId(campaignId)) throw new AnalysisResultError(400, 'ERR_INVALID_CAMPAIGN_ID')

  const response = await authorizedApiResponse(
    `/api/v1/campaigns/${encodeURIComponent(campaignId)}/status?clinicContextId=${encodeURIComponent(clinicId)}`,
    context,
    signal,
    'PATCH',
    JSON.stringify({ status }),
  )
  const payload: unknown = await response.json().catch(() => null)
  return parseCampaignItem(payload)
}

export async function getCampaignSummary(
  context: BackendContext,
  clinicId: string,
  campaignId: string,
  signal?: AbortSignal,
): Promise<CampaignSummary> {
  if (!validBatchId(clinicId)) throw new AnalysisResultError(400, 'ERR_INVALID_CLINIC_ID')
  if (!validBatchId(campaignId)) throw new AnalysisResultError(400, 'ERR_INVALID_CAMPAIGN_ID')

  const response = await authorizedApiResponse(
    `/api/v1/campaigns/${encodeURIComponent(campaignId)}/summary?clinicContextId=${encodeURIComponent(clinicId)}`,
    context,
    signal,
  )
  const payload: unknown = await response.json().catch(() => null)
  if (!isRecord(payload) || typeof payload.campaignId !== 'string' || typeof payload.campaignName !== 'string') {
    invalidResponse()
  }

  return {
    campaignId: payload.campaignId,
    tenantId: String(payload.tenantId ?? clinicId),
    campaignName: payload.campaignName,
    status: String(payload.status ?? 'PLANNED'),
    targetPatientCount: Number(payload.targetPatientCount ?? 0),
    totalBatches: Number(payload.totalBatches ?? 0),
    totalImages: Number(payload.totalImages ?? 0),
    completedImages: Number(payload.completedImages ?? 0),
    pendingImages: Number(payload.pendingImages ?? 0),
    failedImages: Number(payload.failedImages ?? 0),
    completionRatePercent: Number(payload.completionRatePercent ?? 0),
    normalCases: Number(payload.normalCases ?? 0),
    suspectedCases: Number(payload.suspectedCases ?? 0),
    highRiskCases: Number(payload.highRiskCases ?? 0),
    uniquePatients: Number(payload.uniquePatients ?? 0),
  }
}

export function getCampaignExportUrl(clinicId: string, campaignId: string): string {
  return `/api/v1/campaigns/${encodeURIComponent(campaignId)}/export?clinicContextId=${encodeURIComponent(clinicId)}`
}

