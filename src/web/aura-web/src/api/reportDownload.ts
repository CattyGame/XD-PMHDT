import type { BackendContext } from './backendContext'
import { validBatchId } from './batchProgress'
import { AnalysisResultError, authorizedApiResponse, clinicSelector, getProtectedContent, protectedContentPath } from './analysisResult'

const reportContentTypes = { pdf: 'application/pdf', csv: 'text/csv', zip: 'application/zip' } as const
const reportPrefixes = { pdf: '%PDF-', csv: 'recordType,caseCode,', zip: 'PK\u0003\u0004' } as const
const REPORT_TIMEOUT_MS = 120_000
export type ReportFormat = keyof typeof reportContentTypes

export async function downloadReport(resource: 'analyses' | 'batches', resourceId: string, format: ReportFormat,
  context: BackendContext, signal?: AbortSignal): Promise<{ blob: Blob; fileName: string }> {
  if (!validBatchId(resourceId) || (resource === 'analyses' && format !== 'pdf')) throw new AnalysisResultError(400, 'ERR_INVALID_REPORT')
  const response = await authorizedApiResponse(`/api/v1/${resource}/${resourceId}/reports${clinicSelector(context)}`,
    context, signal, 'POST', JSON.stringify({ format }), REPORT_TIMEOUT_MS)
  const value: unknown = await response.json().catch(() => null)
  if (!value || typeof value !== 'object') throw new AnalysisResultError(0, 'ERR_INVALID_RESPONSE')
  const report = value as Record<string, unknown>
  if (typeof report.reportId !== 'string' || !validBatchId(report.reportId) || report.format !== format ||
    typeof report.downloadUrl !== 'string' || typeof report.expiresAt !== 'string' ||
    !Number.isFinite(Date.parse(report.expiresAt)) || Date.parse(report.expiresAt) <= Date.now()) {
    throw new AnalysisResultError(0, 'ERR_INVALID_RESPONSE')
  }
  const path = protectedContentPath(report.downloadUrl)
  const url = new URL(path, window.location.origin)
  const selectors = url.searchParams.getAll('clinicContextId')
  if (url.pathname !== `/api/v1/reports/${report.reportId}/content` ||
    (context.clinicContextId
      ? selectors.length !== 1 || selectors[0].toLowerCase() !== context.clinicContextId.toLowerCase()
      : selectors.length !== 0)) throw new AnalysisResultError(0, 'ERR_INVALID_RESPONSE')
  const blob = await getProtectedContent(path, context, signal, REPORT_TIMEOUT_MS)
  if (blob.type.split(';')[0] !== reportContentTypes[format] ||
    !(await blob.slice(0, 32).text()).startsWith(reportPrefixes[format])) throw new AnalysisResultError(0, 'ERR_INVALID_RESPONSE')
  return { blob, fileName: `aura-${report.reportId}.${format}` }
}
