import { useEffect, useRef, useState } from 'react'
import type { BackendContext } from '../api/backendContext'
import { AnalysisResultError, analysisErrorMessage } from '../api/analysisResult'
import { downloadReport } from '../api/reportDownload'

export default function ReportDownloadButton({ resource, resourceId, format, context }: {
  resource: 'analyses' | 'batches'; resourceId: string; format: 'pdf' | 'csv'; context: BackendContext
}) {
  const [busy, setBusy] = useState(false), [error, setError] = useState('')
  const active = useRef<AbortController | undefined>(undefined)
  useEffect(() => () => active.current?.abort(), [context, resourceId])
  async function download() {
    if (busy) return
    const controller = new AbortController(); active.current = controller
    setBusy(true); setError('')
    try {
      const { blob, fileName } = await downloadReport(resource, resourceId, format, context, controller.signal)
      if (controller.signal.aborted) return
      const url = URL.createObjectURL(blob), link = document.createElement('a')
      link.href = url; link.download = fileName; link.rel = 'noopener'; link.click()
      setTimeout(() => URL.revokeObjectURL(url), 1000)
    } catch (cause) {
      if (!controller.signal.aborted) setError(analysisErrorMessage(cause instanceof AnalysisResultError ? cause : new AnalysisResultError(0, 'ERR_NETWORK', true)))
    } finally { if (!controller.signal.aborted) setBusy(false) }
  }
  return <span>
    <button type="button" disabled={busy} onClick={() => void download()}>{busy ? 'Đang chuẩn bị...' : `Tải ${format.toUpperCase()}`}</button>
    {error && <span role="alert">{error}</span>}
  </span>
}
