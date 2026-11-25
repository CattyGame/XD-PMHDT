import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import type { BackendContext } from '../api/backendContext'
import { validBatchId } from '../api/batchProgress'
import {
  analysisErrorMessage, getAnalysisResult, getPrivateFileAccess, getProtectedContent,
  AnalysisResultError, type AnalysisDetail, type FileRef,
} from '../api/analysisResult'
import { useBackendSession } from '../auth/backendSession'
import BackendSessionNotice from '../auth/BackendSessionNotice'
import './AnalysisResultPage.css'
import ReportDownloadButton from '../components/ReportDownloadButton'

type AnalysisView =
  | { phase: 'loading' }
  | { phase: 'ready'; analysis: AnalysisDetail }
  | { phase: 'error'; error: AnalysisResultError }

type ImageView =
  | { phase: 'loading' }
  | { phase: 'ready'; url: string }
  | { phase: 'expired' | 'broken' }
  | { phase: 'error'; error: AnalysisResultError }

const fileLabels: Record<FileRef['kind'], string> = {
  original: 'Ảnh võng mạc gốc',
  mask: 'Mask phân vùng mạch máu',
  overlay: 'Ảnh phủ overlay',
  heatmap: 'Grad-CAM DR — crop 224×224',
  annotated: 'Phân vùng động/tĩnh mạch trên ảnh gốc',
}
const processingLabels: Record<string, string> = {
  PENDING: 'Đang tiếp nhận',
  QUEUED: 'Đang chờ xử lý',
  PROCESSING: 'Đang phân tích',
  RETRY_READY: 'Chờ thử lại',
  COMPLETED: 'AI đã xử lý xong',
  FAILED: 'Phân tích thất bại',
  LOW_QUALITY: 'Ảnh không đạt chất lượng',
  REJECTED: 'Đã từ chối',
  DUPLICATE: 'Ảnh trùng',
}
const reviewLabels: Record<string, string> = {
  PENDING: 'Chưa được bác sĩ review',
  IN_REVIEW: 'Bác sĩ đang review',
  REVIEWED: 'Đã được bác sĩ review',
  APPROVED: 'Đã xác nhận',
  REJECTED: 'Bị từ chối sau review',
}
const terminalStatuses = new Set(['COMPLETED', 'FAILED', 'LOW_QUALITY', 'REJECTED', 'DUPLICATE'])

/**
 * Mỗi file yêu cầu URL riêng tư từ backend. Không dùng public container,
 * không lưu URL ký vào storage và xóa URL khỏi state khi hết hạn.
 */
function PrivateFilePreview({ analysisId, file, context, defer }: {
  analysisId: string
  file: FileRef
  context: BackendContext
  defer: boolean
}) {
  const [view, setView] = useState<ImageView>({ phase: 'loading' })
  const [attempt, setAttempt] = useState(0)
  const [nearViewport, setNearViewport] = useState(false)
  const visible = !defer || nearViewport || typeof IntersectionObserver === 'undefined'
  const previewRef = useRef<HTMLElement>(null)

  useEffect(() => {
    if (visible || !previewRef.current) return
    const observer = new IntersectionObserver(([entry]) => {
      if (entry?.isIntersecting) setNearViewport(true)
    }, { rootMargin: '200px' })
    observer.observe(previewRef.current)
    return () => observer.disconnect()
  }, [visible])

  useEffect(() => {
    if (!visible) return
    const controller = new AbortController()
    let expiryTimer: ReturnType<typeof setTimeout> | undefined
    let objectUrl: string | undefined
    function releaseUrl() {
      if (!objectUrl) return
      URL.revokeObjectURL(objectUrl)
      objectUrl = undefined
    }

    void getPrivateFileAccess(analysisId, file.fileId, context, controller.signal)
      .then(async (access) => {
        if (controller.signal.aborted) return
        const expiresAt = Date.parse(access.expiresAt)
        if (!Number.isFinite(expiresAt) || expiresAt <= Date.now()) {
          setView({ phase: 'expired' })
          return
        }
        const blob = await getProtectedContent(access.url, context, controller.signal)
        if (controller.signal.aborted) return
        const remaining = expiresAt - Date.now()
        if (remaining <= 0) {
          setView({ phase: 'expired' })
          return
        }
        if (!['image/png', 'image/jpeg'].includes(blob.type.split(';')[0])) throw new AnalysisResultError(0, 'ERR_INVALID_RESPONSE')
        objectUrl = URL.createObjectURL(blob)
        setView({ phase: 'ready', url: objectUrl })
        expiryTimer = setTimeout(() => {
          releaseUrl()
          setView({ phase: 'expired' })
        }, Math.min(remaining, 2_147_000_000))
      })
      .catch((cause: unknown) => {
        if (controller.signal.aborted) return
        setView({ phase: 'error', error: cause instanceof AnalysisResultError
          ? cause : new AnalysisResultError(0, 'ERR_NETWORK', true) })
      })

    return () => {
      controller.abort()
      if (expiryTimer !== undefined) clearTimeout(expiryTimer)
      releaseUrl()
    }
  }, [analysisId, file.fileId, context, attempt, visible])

  const retry = useCallback(() => {setView({ phase: 'loading' })
  setAttempt((value) => value + 1)
}, [])
  return <figure className="analysis-file-preview" ref={previewRef}>
    <figcaption><strong>{fileLabels[file.kind]}</strong> <code>{file.fileId}</code></figcaption>
    {visible && view.phase === 'loading' && <p role="status">Đang xin quyền truy cập ảnh riêng tư...</p>}
    {view.phase === 'ready' &&
      <img src={view.url} alt={fileLabels[file.kind]} referrerPolicy="no-referrer"
        loading={defer ? 'lazy' : 'eager'} decoding="async" onError={() => setView({ phase: 'broken' })} />}
    {view.phase === 'error' && <p role="alert">{analysisErrorMessage(view.error)}</p>}
    {view.phase === 'expired' && <p role="alert">Đường dẫn ảnh đã hết hạn. Yêu cầu quyền truy cập mới để xem ảnh.</p>}
    {view.phase === 'broken' && <p role="alert">Không tải được ảnh từ URL được cấp. Tệp có thể đã hết hạn hoặc không còn truy cập được.</p>}
    {(view.phase === 'error' || view.phase === 'expired' || view.phase === 'broken') &&
      <button type="button" onClick={retry}>Xin lại quyền xem ảnh</button>}
  </figure>
}

/** GET trạng thái hiện hành; không tạo upload/job/reservation. Dừng khi terminal. */
function AnalysisViewer({ analysisId, context }: { analysisId: string; context: BackendContext }) {
  const [view, setView] = useState<AnalysisView>({ phase: 'loading' })
  const [retryKey, setRetryKey] = useState(0)
  const refresh = useCallback(() => {
  setView({ phase: 'loading' })
  setRetryKey((value) => value + 1)
}, [])

  useEffect(() => {
    const controller = new AbortController()
    let timer: ReturnType<typeof setTimeout> | undefined
    // Bound browser monitoring without changing the durable processing status.
    const deadline = setTimeout(() => {
      controller.abort()
      if (timer !== undefined) clearTimeout(timer)
      setView({ phase: 'error', error: new AnalysisResultError(0, 'ERR_STATUS_POLL_TIMEOUT', true) })
    }, 300_000)

    const read = async () => {
      try {
        const analysis = await getAnalysisResult(analysisId, context, controller.signal)
        if (controller.signal.aborted) return
        setView({ phase: 'ready', analysis })
        if (!terminalStatuses.has(analysis.processingStatus)) {
          timer = setTimeout(() => { void read() }, 5000)
        } else {
          clearTimeout(deadline)
        }
      } catch (cause) {
        if (controller.signal.aborted) return
        clearTimeout(deadline)
        // On permission revocation / session change, never retain previous personal data.
        setView({ phase: 'error', error: cause instanceof AnalysisResultError
          ? cause : new AnalysisResultError(0, 'ERR_NETWORK', true) })
      }
    }

    void read()
    return () => {
      controller.abort()
      if (timer !== undefined) clearTimeout(timer)
      clearTimeout(deadline)
    }
  }, [analysisId, context, retryKey])

  if (view.phase === 'loading') return <p role="status">Đang tải kết quả từ Analysis API...</p>
  if (view.phase === 'error') return <section className="analysis-result-panel">
    <p role="alert">{analysisErrorMessage(view.error)}</p>
    <p>Không hiển thị dữ liệu cũ khi không còn quyền truy cập.</p>
    <button type="button" onClick={refresh}>Thử đọc lại (GET)</button>
  </section>

  const { analysis } = view
  const result = analysis.result
  const avRatio = result?.metrics.avRatio ?? null
  const avrWarning = result?.warnings.find((warning) => warning.code === 'AVR_UNAVAILABLE')
  const files: FileRef[] = [
    ...(analysis.image ? [analysis.image] : []),
    ...(result?.annotations ?? []),
  ]
  const seenFiles = new Set<string>()
  const uniqueFiles = files.filter((file) => {
    if (seenFiles.has(file.fileId.toLowerCase())) return false
    seenFiles.add(file.fileId.toLowerCase())
    return true
  })

  return <section className="analysis-result-panel" aria-label="Chi tiết phân tích">
    <div className="analysis-result-toolbar">
      <p>Batch: <Link to={`/batches/${analysis.batchId}`}><code>{analysis.batchId}</code></Link></p>
      <button type="button" onClick={refresh}>Tải lại trạng thái (chỉ GET)</button>
    </div>
    <p><strong>Trạng thái AI:</strong> {processingLabels[analysis.processingStatus] ?? analysis.processingStatus}</p>
    <p><strong>Trạng thái review:</strong> {reviewLabels[analysis.reviewStatus] ?? analysis.reviewStatus}</p>
    <p><strong>Cập nhật:</strong> {new Date(analysis.updatedAt).toLocaleString('vi-VN')}</p>

    {analysis.processingStatus !== 'COMPLETED' && (
      terminalStatuses.has(analysis.processingStatus)
        ? <section className="analysis-result-warning" aria-label="Lý do dừng phân tích">
          <p role="status">Phân tích đã dừng và không trả kết quả AI hoàn tất. Hệ thống không tự đoán chỉ số AVR hoặc mức nguy cơ mặc định.</p>
          {analysis.error && <p><strong>{analysis.error.code}:</strong> {analysis.error.message}</p>}
          {analysis.processingStatus === 'LOW_QUALITY' &&
            <p>Vui lòng chụp lại ảnh đáy mắt rõ nét, đủ sáng, không bị lóa rồi tải lên phân tích mới.</p>}
        </section>
        : <p role="status">Chưa có kết quả AI hoàn tất. Hệ thống chỉ hiển thị dữ liệu thật do API cung cấp. Trạng thái sẽ được đọc lại tự động.</p>
    )}

    {result && <>
      {result.isMock
        ? <p className="analysis-result-warning" role="note"><strong>Dữ liệu mô phỏng</strong> — không phải kết quả suy luận thật.</p>
        : <p role="note">Nguồn kết quả: engine/baseline do backend báo cáo. Không đồng nghĩa đã được chứng nhận lâm sàng.</p>}
      <div className="analysis-result-metadata">
        <p><strong>Phiên bản kết quả AI:</strong> {result.version}</p>
        <p><strong>Model:</strong> {result.modelVersion}</p>
        <p><strong>Cấu hình:</strong> {result.configVersion}</p>
        <p><strong>Ngưỡng:</strong> {result.thresholdVersion}</p>
      </div>
      <section aria-label="Tỷ số động/tĩnh mạch (AVR)">
        <h2>Tỷ số động/tĩnh mạch (AVR)</h2>
        {avRatio !== null
          ? <p>AVR: <strong>{avRatio}</strong></p>
          : <div className="analysis-result-warning" role="note">
            <p><strong>Chưa có kết quả AVR</strong> — AI đã xử lý xong, không tiếp tục chờ chỉ số này.</p>
            <p>{avrWarning?.message ?? 'API chưa cung cấp số đo AVR cho ảnh này; xem các cảnh báo bên dưới nếu có.'}</p>
            <p>Thiếu AVR không có nghĩa nguy cơ thấp. Các đánh giá khác, nếu có, được trình bày riêng.</p>
          </div>}
      </section>
      {result.research && <section aria-label="Kết quả AI nghiên cứu">
        <h2>Kết quả AI nghiên cứu</h2>
        <p><strong>AVR V8:</strong> {result.research.avr.model_version} (<code>{result.research.avr.model_sha256}</code>)</p>
        <p>CRAE: {result.research.avr.crae_px ?? 'Chưa đo được'} px · CRVE: {result.research.avr.crve_px ?? 'Chưa đo được'} px</p>
        {!result.research.avr.measurement_available && <p>Lý do chưa có AVR: {result.research.avr.unavailable_reason}</p>}
        <p><strong>DR nhị phân:</strong> {result.research.dr.model_version} (<code>{result.research.dr.checkpoint_sha256}</code>)</p>
        <p>Xác suất DR do mô hình nghiên cứu ước lượng: {(result.research.dr.probability_dr * 100).toFixed(1)}%</p>
        <p>Phương pháp giải thích: {result.research.dr.gradcam_method} ({result.research.dr.gradcam_geometry}) — resize cạnh ngắn 256 rồi crop vùng trung tâm 224×224, không phải bản đồ tổn thương trên toàn ảnh gốc.</p>
        <p>Kết quả này không phân độ ICDR và không thay thế kết luận lâm sàng.</p>
      </section>}
      {result.riskAvailable
        ? <section aria-label="Đánh giá nguy cơ từ API">
          <h2>Đánh giá nguy cơ do API cung cấp</h2>
          <p>Mức nguy cơ: {result.riskLevel}</p>
          <p>Điểm nguy cơ: {result.riskScore}</p>
          <p>Độ tin cậy: {result.confidenceScore}</p>
          {result.indicators.length > 0 && <ul>{result.indicators.map((value) => <li key={value}>{value}</li>)}</ul>}
        </section>
        : <p className="analysis-result-warning" role="note">Chưa hỗ trợ đánh giá nguy cơ. Không có risk score hoặc khuyến nghị chẩn đoán.</p>}
      <section aria-labelledby="analysis-warnings-heading">
        <h2 id="analysis-warnings-heading">Cảnh báo từ kết quả AI</h2>
        {result.warnings.length === 0 ? <p>API không trả cảnh báo.</p> :
          <ul>{result.warnings.map((warning, index) =>
            <li key={`${warning.code}-${index}`}><strong>{warning.code}:</strong> {warning.message}</li>)}</ul>}
      </section>
      <p><strong>Giới hạn:</strong> {result.limitations || 'Không có mô tả giới hạn.'}</p>
      <p className="analysis-result-disclaimer">{result.disclaimer}</p>
      {Object.keys(result.metrics).length > 0 && <details>
        <summary>Chỉ số phân tích do API cung cấp</summary>
        <dl>{Object.entries(result.metrics).map(([name, value]) => <div key={name}>
          <dt>{name}</dt><dd>{value === null ? 'Chưa có dữ liệu' : value}</dd>
        </div>)}</dl>
      </details>}
    </>}

    <section aria-labelledby="analysis-files-heading">
      <h2 id="analysis-files-heading">Ảnh và chú thích riêng tư</h2>
      <p>Từng ảnh được tải sau khi máy chủ kiểm tra quyền hiện hành.</p>
      {uniqueFiles.length > 0
        ? <div className="analysis-files-grid">{uniqueFiles.map((file, index) =>
          <PrivateFilePreview key={file.fileId} analysisId={analysisId} file={file} context={context} defer={index > 0} />)}</div>
        : <p>API chưa cung cấp tham chiếu ảnh có quyền truy cập.</p>}
    </section>
    {result && <ReportDownloadButton resource="analyses" resourceId={analysisId} format="pdf" context={context} />}
  </section>
}

export default function AnalysisResultPage() {
  const { state } = useBackendSession()
  const { analysisId } = useParams<{ analysisId: string }>()
  const clinicContextId = new URLSearchParams(window.location.search).get('clinicContextId') || undefined
  const context = useMemo(() => state.status === 'ready' ? { ...state.context, clinicContextId } : undefined, [state, clinicContextId])
  if (state.status !== 'ready') return <BackendSessionNotice />
  return <main className="analysis-result-page">
    <nav aria-label="Điều hướng trang"><Link to="/">Trang chủ</Link> · <Link to="/batches">Theo dõi batch</Link></nav>
    <h1>Kết quả phân tích võng mạc</h1>
    <p>Analysis ID: <code>{analysisId ?? 'Chưa có'}</code></p>
    {analysisId && validBatchId(analysisId)
      ? <AnalysisViewer key={`${state.context.firebaseUid}:${state.context.accountId}:${state.context.profileVersion}:${clinicContextId ?? ''}:${analysisId}`}
        analysisId={analysisId} context={context!} />
      : <p role="alert">Analysis ID trong đường dẫn không hợp lệ.</p>}
  </main>
}
