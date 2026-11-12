import { useEffect, useRef, useState } from 'react'
import type { BackendContext } from '../api/backendContext'
import { AnalysisResultError, analysisErrorMessage } from '../api/analysisResult'
import { downloadReport, type ReportFormat } from '../api/reportDownload'

export default function ReportDownloadButton({ resource, resourceId, format, context, label }: {
  resource: 'analyses' | 'batches'; resourceId: string; format: ReportFormat; context: BackendContext; label?: string
}) {
  const [status, setStatus] = useState<{ context: BackendContext; key: string; busy: boolean; error?: string }>()
  const key = `${resource}/${resourceId}/${format}`
  const current = status?.context === context && status.key === key ? status : undefined
  const active = useRef<AbortController | undefined>(undefined)
  useEffect(() => () => { active.current?.abort(); active.current = undefined }, [context, key])
  async function download() {
    if (active.current) return
    const controller = new AbortController(); active.current = controller
    setStatus({ context, key, busy: true })
    try {
      const { blob, fileName } = await downloadReport(resource, resourceId, format, context, controller.signal)
      if (controller.signal.aborted) return
      const url = URL.createObjectURL(blob), link = document.createElement('a')
      link.href = url; link.download = fileName; link.rel = 'noopener'; link.click()
      setTimeout(() => URL.revokeObjectURL(url), 1000)
      setStatus({ context, key, busy: false })
    } catch (cause) {
      if (!controller.signal.aborted) setStatus({ context, key, busy: false,
        error: analysisErrorMessage(cause instanceof AnalysisResultError ? cause : new AnalysisResultError(0, 'ERR_NETWORK', true)) })
    } finally {
      if (active.current === controller) active.current = undefined
    }
  }
  return <span>
    <button type="button" disabled={current?.busy} onClick={() => void download()}>{current?.busy ? 'Đang chuẩn bị...' : label ?? `Tải ${format.toUpperCase()}`}</button>
    {current?.error && <span role="alert">{current.error}</span>}
  </span>
}
