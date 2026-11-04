import type { BackendContext } from './backendContext'
import { validBatchId } from './batchProgress'
import { AnalysisResultError, authorizedApiResponse, clinicSelector, getProtectedContent, protectedContentPath } from './analysisResult'

export async function downloadReport(resource: 'analyses' | 'batches', resourceId: string, format: 'pdf' | 'csv',
  context: BackendContext, signal?: AbortSignal): Promise<{ blob: Blob; fileName: string }> {
  if (!validBatchId(resourceId) || (resource === 'analyses' && format !== 'pdf')) throw new AnalysisResultError(400, 'ERR_INVALID_REPORT')
  const response = await authorizedApiResponse(`/api/v1/${resource}/${resourceId}/reports${clinicSelector(context)}`, context, signal, 'POST', JSON.stringify({ format }))
  const value: unknown = await response.json().catch(() => null)
  if (!value || typeof value !== 'object') throw new AnalysisResultError(0, 'ERR_INVALID_RESPONSE')
  const report = value as Record<string, unknown>
  if (typeof report.reportId !== 'string' || !validBatchId(report.reportId) || report.format !== format ||
    typeof report.downloadUrl !== 'string' || typeof report.expiresAt !== 'string' ||
    !Number.isFinite(Date.parse(report.expiresAt)) || Date.parse(report.expiresAt) <= Date.now()) {
    throw new AnalysisResultError(0, 'ERR_INVALID_RESPONSE')
  }
  const path = protectedContentPath(report.downloadUrl)
  if (new URL(path, window.location.origin).pathname !== `/api/v1/reports/${report.reportId}/content`) throw new AnalysisResultError(0, 'ERR_INVALID_RESPONSE')
  const blob = await getProtectedContent(path, context, signal)
  const expectedType = format === 'pdf' ? 'application/pdf' : 'text/csv'
  if (blob.type.split(';')[0] !== expectedType) throw new AnalysisResultError(0, 'ERR_INVALID_RESPONSE')
  return { blob, fileName: `aura-${report.reportId}.${format}` }
}
