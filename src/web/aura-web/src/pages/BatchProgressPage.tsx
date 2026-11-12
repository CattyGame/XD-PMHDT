import { useEffect, useMemo, useRef, useState, type FormEvent } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { useBackendSession } from '../auth/backendSession'
import BackendSessionNotice from '../auth/BackendSessionNotice'
import type { BackendContext } from '../api/backendContext'
import {
  batchErrorMessage, getBatchProgress, isBatchTerminal, validBatchId,
  BatchProgressError, type BatchProgress, type BatchItem,
} from '../api/batchProgress'
import './BatchProgressPage.css'
import ReportDownloadButton from '../components/ReportDownloadButton'
import { calculateBatchPercent, completedBatchCount } from '../api/bulkUploadUi'
import { getBatchPreview, subscribeBatchPreviews } from '../api/batchLocalPreviews'
import { listenForegroundMessages } from '../firebase/messaging'

type View = {
  phase: 'loading' | 'accepted' | 'tracking' | 'retrying' | 'error'
  data?: BatchProgress
  error?: BatchProgressError
}

const statusLabels: Record<string, string> = {
  PENDING: 'Đang tiếp nhận', QUEUED: 'Đang chờ xử lý', PROCESSING: 'Đang phân tích',
  RETRY_READY: 'Chờ thử lại', COMPLETED: 'Đã hoàn tất AI', FAILED: 'Phân tích thất bại',
  LOW_QUALITY: 'Ảnh không đạt chất lượng', REJECTED: 'Đã từ chối', DUPLICATE: 'Ảnh trùng',
  IN_PROGRESS: 'Đang xử lý', PARTIAL_FAILED: 'Hoàn tất một phần, có ảnh lỗi',
  QUARANTINE: 'Đã cách ly (Quarantine)', RETRY: 'Chờ tự động thử lại',
}

const POLL_INTERVAL = 3000
const MAX_POLL_RETRIES = 5

/** Poll at most one GET at a time. Timer and in-flight GET are aborted on navigation or session change. */
function useProgress(batchId: string, context: BackendContext) {
  const [view, setView] = useState<View>({ phase: 'loading' })
  const refresh = useRef<() => void>(() => {})

  useEffect(() => {
    let alive = true
    let activeRequest = false
    let stopped = false
    let failureCount = 0
    let notBefore = 0
    let detachPush: (() => void) | null = null
    let timer: ReturnType<typeof setTimeout> | undefined
    let controller: AbortController | undefined
    const clear = () => { if (timer !== undefined) clearTimeout(timer); timer = undefined }
    const schedule = () => {
      clear()
      if (alive && !stopped && !activeRequest && !document.hidden) {
        const delay = Math.min(2_147_483_647, Math.max(0, notBefore - Date.now()))
        timer = setTimeout(() => { void poll() }, delay)
      }
    }
    const poll = async () => {
      if (!alive || stopped || activeRequest || document.hidden) return
      if (Date.now() < notBefore) { schedule(); return }
      clear()
      activeRequest = true
      const request = new AbortController()
      controller = request
      try {
        const response = await getBatchProgress(batchId, context, request.signal)
        if (!alive || request.signal.aborted) return
        failureCount = 0
        notBefore = Date.now() + POLL_INTERVAL
        if (response.kind === 'accepted') {
          setView({ phase: 'accepted' })
        } else {
          stopped = isBatchTerminal(response.data)
          setView({ phase: 'tracking', data: response.data })
        }
      } catch (cause) {
        if (!alive || request.signal.aborted) return
        const error = cause instanceof BatchProgressError ? cause : new BatchProgressError(0, 'ERR_NETWORK', true)
        if (!error.retryable) {
          stopped = true
          notBefore = Date.now() + POLL_INTERVAL
          // Clear previous personal data immediately if access/session has changed.
          setView({ phase: 'error', error })
        } else {
          failureCount += 1
          stopped = failureCount > MAX_POLL_RETRIES
          notBefore = Date.now() + Math.max(error.retryAfterMs ?? 0,
            Math.min(30000, 2000 * 2 ** Math.min(failureCount - 1, 4)))
          setView((previous) => ({ phase: stopped ? 'error' : 'retrying', data: previous.data, error }))
        }
      } finally {
        if (controller === request) controller = undefined
        activeRequest = false
        schedule()
      }
    }
    const resume = () => {
      if (!alive || stopped || activeRequest || document.hidden) return
      if (Date.now() < notBefore) schedule()
      else void poll()
    }
    refresh.current = () => {
      if (!alive || activeRequest) return
      if (stopped) { stopped = false; failureCount = 0 }
      resume()
    }
    const changedVisibility = () => { if (document.hidden) clear(); else resume() }
    window.addEventListener('online', resume)
    window.addEventListener('focus', resume)
    document.addEventListener('visibilitychange', changedVisibility)
    void listenForegroundMessages(payload => {
      if (payload.data?.targetPath?.toLowerCase() === `/batches/${batchId.toLowerCase()}`) resume()
    }).then(detach => { if (alive) detachPush = detach; else detach?.() }).catch(() => {})
    void poll()
    return () => {
      alive = false
      clear()
      controller?.abort()
      detachPush?.()
      window.removeEventListener('online', resume)
      window.removeEventListener('focus', resume)
      document.removeEventListener('visibilitychange', changedVisibility)
    }
  }, [batchId, context])
  return { view, retry: () => refresh.current() }
}

function ItemThumbnail({ item, batchId, context }: { item: BatchItem; batchId: string; context: BackendContext }) {
  const [preview, setPreview] = useState(() => getBatchPreview(batchId, context.firebaseUid, item.fileName))
  useEffect(() => subscribeBatchPreviews(() => {
    setPreview(getBatchPreview(batchId, context.firebaseUid, item.fileName))
  }), [batchId, context.firebaseUid, item.fileName])
  return preview
    ? <img className="batch-thumbnail" src={preview} alt={`Ảnh thu nhỏ ${item.fileName || ''}`} />
    : <span className="batch-thumbnail-placeholder" title="API batch chưa trả thumbnail; chỉ có ảnh tạm trong tab đã upload">Chưa có ảnh</span>
}

function ItemRow({ item, batchId, context }: { item: BatchItem; batchId: string; context: BackendContext }) {
  return <tr className="batch-table-row">
    <td><ItemThumbnail item={item} batchId={batchId} context={context} /></td>
    <td><strong>{item.fileName || 'Ảnh không có tên'}</strong><div><code>{item.analysisId}</code></div></td>
    <td><span className={`batch-status batch-status--${item.processingStatus.toLowerCase()}`}>
      {item.processingStatus === 'COMPLETED' ? '✓ ' : ''}{statusLabels[item.processingStatus] || item.processingStatus}
    </span>
      {item.processingStatus === 'COMPLETED' && <div>{item.riskLevel ? `Mức nguy cơ: ${item.riskLevel}. ` : ''}
        {item.reviewStatus === 'PENDING' ? 'Đang chờ bác sĩ review.' : `Trạng thái review: ${item.reviewStatus}.`}</div>}
      {item.error && <div role="note" className="batch-item-error">{item.error.message || 'Không thể hoàn tất phân tích ảnh.'}
        {item.error.retryable ? ' Backend có thể tự thử lại.' : ''}</div>}
      {item.processingStatus === 'DUPLICATE' && item.existingAnalysisId &&
        <div>Tham chiếu ảnh trùng: <code>{item.existingAnalysisId}</code></div>}
    </td>
    <td title="GET Batch hiện không trả thời gian xử lý riêng từng ảnh">Chưa có từ API</td>
    <td>{item.processingStatus === 'COMPLETED'
      ? <><Link to={`/analyses/${item.analysisId}${context.clinicContextId ? `?clinicContextId=${encodeURIComponent(context.clinicContextId)}` : ''}`}>Xem kết quả chi tiết</Link>
        <ReportDownloadButton resource="analyses" resourceId={item.analysisId} format="pdf" context={context} label="Tải báo cáo PDF" /></>
      : <span>—</span>}</td>
  </tr>
}

function BatchViewer({ batchId, context }: { batchId: string; context: BackendContext }) {
  const { logout } = useBackendSession()
  const { view, retry } = useProgress(batchId, context)
  return <section className="batch-panel" aria-live="polite">
    <h2>Tiến trình phân tích</h2>
    <p>Batch ID: <code>{batchId}</code></p>
    {view.phase === 'loading' && <p role="status">Đang đọc trạng thái từ Analysis API...</p>}
    {view.phase === 'accepted' && <p role="status">Backend đã tiếp nhận batch (HTTP 202). AI chưa hoàn tất; tiếp tục chờ trạng thái chính thức.</p>}
    {view.error && <p role="alert">{batchErrorMessage(view.error)}</p>}
    {view.phase === 'retrying' && <p role="status">Mất kết nối hoặc lỗi GET tạm thời. Đang thử lại với thời gian chờ tăng dần; không upload lại ảnh và không đặt credit.</p>}
    {view.phase === 'error' && view.error?.retryable && <p>Đã tạm dừng cập nhật tự động. Bấm tải lại trạng thái để kiểm tra tiếp.</p>}
    {view.phase === 'error' && view.error?.status === 401 &&
      <button type="button" onClick={() => void logout()}>Đăng xuất và đăng nhập lại</button>}
    {view.phase === 'error' && (view.error?.status === 403 || view.error?.status === 404) &&
      <p>Kiểm tra lại tài khoản hoặc liên hệ người quản lý quyền truy cập. Không thử lại upload.</p>}
    {view.data && <>
      <p role="status"><strong>{isBatchTerminal(view.data) &&
      view.data.summary.total > 0 &&
      view.data.summary.duplicate === view.data.summary.total
      ? 'Batch đã kết thúc — toàn bộ ảnh trùng, không có phân tích AI mới'
      : (statusLabels[view.data.batchStatus] ?? view.data.batchStatus)}</strong>
        {isBatchTerminal(view.data) ? ' — đã dừng polling.' : view.phase === 'error' ? '' : ' — trạng thái sẽ tự cập nhật.'}</p>
      <p>Ngày tạo: {new Date(view.data.createdAt).toLocaleString('vi-VN')}</p>
      {view.data.totalDeclared !== undefined && <p aria-label="Tiếp nhận và tải ảnh">
        Đã khai báo: {view.data.totalDeclared} · Được nhận xử lý: {view.data.summary.total} ·
        Từ chối tiếp nhận: {view.data.admissionRejected} · Chưa hoàn tất tải/đối soát: {view.data.uploadPending}
      </p>}
      <div className="batch-summary" aria-label="Tổng hợp batch">
        <span>Tổng ảnh: <strong>{view.data.totalDeclared ?? view.data.summary.total}</strong></span>
        <span>Đang chờ: <strong>{view.data.items.filter(item => ['PENDING', 'QUEUED', 'RETRY_READY', 'RETRY'].includes(item.processingStatus)).length + (view.data.uploadPending ?? 0)}</strong></span>
        <span>Đang phân tích: <strong>{view.data.items.filter(item => item.processingStatus === 'PROCESSING').length}</strong></span>
        <span>Thành công: <strong>{view.data.summary.succeeded}</strong></span>
        <span>Lỗi: <strong>{view.data.summary.failed + (view.data.admissionRejected ?? 0)}</strong></span>
        <span>Trùng: <strong>{view.data.summary.duplicate}</strong></span>
      </div>
      <div className="batch-progress-summary">
        <label htmlFor="batch-overall-progress"><strong>Tiến độ batch: {calculateBatchPercent(view.data.summary, view.data.totalDeclared, view.data.admissionRejected ?? 0)}%</strong></label>
        <progress id="batch-overall-progress" aria-label="Tiến độ xử lý batch" max={100}
          value={calculateBatchPercent(view.data.summary, view.data.totalDeclared, view.data.admissionRejected ?? 0)} />
        <p>Đã hoàn thành {completedBatchCount(view.data.summary, view.data.admissionRejected ?? 0)}/{view.data.totalDeclared ?? view.data.summary.total} ảnh
          (đã có kết quả cuối).</p>
      </div>
      <h3>Trạng thái từng ảnh</h3>
      <div className="batch-table-scroll"><table className="batch-detail-table">
        <thead><tr><th scope="col">Thumbnail</th><th scope="col">Ảnh / ID</th>
          <th scope="col">Trạng thái / lý do</th><th scope="col">Thời gian xử lý</th><th scope="col">Thao tác</th></tr></thead>
        <tbody>{view.data.items.map((item) => <ItemRow item={item} batchId={batchId} context={context} key={item.analysisId} />)}</tbody>
      </table></div>
      <p className="batch-disclaimer">Ảnh thu nhỏ chỉ có sẵn tạm thời trong tab gửi ảnh; sau khi tải lại trang cần API ảnh riêng tư của M2.
        API batch hiện chưa cung cấp thời gian xử lý từng ảnh.</p>
      {isBatchTerminal(view.data) ? <div className="batch-reports" aria-label="Báo cáo batch đã kết thúc">
        <ReportDownloadButton resource="batches" resourceId={batchId} format="pdf" context={context} />
        <ReportDownloadButton resource="batches" resourceId={batchId} format="csv" context={context} />
        <ReportDownloadButton resource="batches" resourceId={batchId} format="zip" context={context} label="Tải tất cả (ZIP)" />
      </div> : <p>Tổng kết PDF, CSV và ZIP khả dụng khi batch kết thúc.</p>}
      <p>Việc phân tích và đối soát credit do máy chủ quản lý. Trang này chỉ đọc trạng thái và xuất báo cáo.</p>
    </>}
    <button type="button" onClick={retry}>Tải lại trạng thái (chỉ GET)</button>
  </section>
}

export default function BatchProgressPage() {
  const { state } = useBackendSession()
  const { batchId } = useParams<{ batchId: string }>()
  const navigate = useNavigate()
  const [input, setInput] = useState('')
  const [validation, setValidation] = useState('')
  const [searchParams] = useSearchParams()
  const clinicContextId = searchParams.get('clinicContextId') || undefined
  const context = useMemo(() => state.status === 'ready' ? { ...state.context, clinicContextId } : undefined, [state, clinicContextId])
  if (state.status !== 'ready') return <BackendSessionNotice />
  const submit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    const id = input.trim()
    if (!validBatchId(id)) return setValidation('Nhập Batch ID UUID hợp lệ.')
    setValidation('')
    void navigate(`/batches/${id.toLowerCase()}`)
  }
  return <main className="batch-page">
    <nav><Link to="/">Trang chủ</Link> · <Link to="/upload">Tải ảnh cá nhân</Link> · <Link to="/clinic-upload">Tải ảnh phòng khám</Link></nav>
    <h1>Theo dõi batch ảnh võng mạc</h1>
    <p>Mở batch do website hoặc hệ thống camera/NiFi tạo. Analysis API sẽ kiểm tra quyền tài khoản hiện tại trên mỗi lần GET.</p>
    <form onSubmit={submit} className="batch-lookup">
      <label htmlFor="batch-id-input">Batch ID</label>
      <input id="batch-id-input" value={input} onChange={(e) => setInput(e.target.value)}
        placeholder="xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx" />
      <button type="submit">Mở batch</button>
    </form>
    {validation && <p role="alert">{validation}</p>}
    {batchId && (validBatchId(batchId)
      ? <BatchViewer key={`${state.context.firebaseUid}:${state.context.accountId}:${batchId}:${clinicContextId ?? ''}`} batchId={batchId} context={context!} />
      : <p role="alert">Batch ID trên đường dẫn không hợp lệ.</p>)}
    <p className="batch-disclaimer">Receipt hoặc HTTP 202 chỉ xác nhận tiếp nhận ảnh; chỉ trạng thái COMPLETED từ GET mới xác nhận AI đã hoàn tất.</p>
  </main>
}
