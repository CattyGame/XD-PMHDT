import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { useBackendSession } from '../auth/backendSession'
import BackendSessionNotice from '../auth/BackendSessionNotice'
import type { BackendContext } from '../api/backendContext'
import {
  batchErrorMessage, getBatchProgress, isBatchTerminal, validBatchId,
  BatchProgressError, type BatchProgress, type BatchItem,
} from '../api/batchProgress'
import './BatchProgressPage.css'

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

/** Poll at most one GET at a time. Timer and in-flight GET are aborted on navigation or session change. */
function useProgress(batchId: string, context: BackendContext) {
  const [view, setView] = useState<View>({ phase: 'loading' })
  const [retryKey, setRetryKey] = useState(0)
  const retry = useCallback(() => setRetryKey((value) => value + 1), [])

  useEffect(() => {
    let alive = true
    let activeRequest = false
    let terminal = false
    let failureCount = 0
    let timer: ReturnType<typeof setTimeout> | undefined
    let controller: AbortController | undefined
    const clear = () => { if (timer !== undefined) clearTimeout(timer); timer = undefined }
    const schedule = (ms: number) => {
      clear()
      if (alive && !terminal && !document.hidden) timer = setTimeout(() => { void poll() }, ms)
    }
    const poll = async () => {
      if (!alive || terminal || activeRequest || document.hidden) return
      clear()
      activeRequest = true
      const request = new AbortController()
      controller = request
      try {
        const response = await getBatchProgress(batchId, context, request.signal)
        if (!alive || request.signal.aborted) return
        failureCount = 0
        if (response.kind === 'accepted') {
          setView({ phase: 'accepted' })
          schedule(3000)
        } else {
          terminal = isBatchTerminal(response.data)
          setView({ phase: 'tracking', data: response.data })
          if (!terminal) schedule(3000)
        }
      } catch (cause) {
        if (!alive || request.signal.aborted) return
        const error = cause instanceof BatchProgressError ? cause : new BatchProgressError(0, 'ERR_NETWORK', true)
        if (!error.retryable) {
          terminal = true
          // Clear previous personal data immediately if access/session has changed.
          setView({ phase: 'error', error })
        } else {
          failureCount += 1
          setView((previous) => ({ phase: 'retrying', data: previous.data, error }))
          schedule(Math.min(30000, 2000 * 2 ** Math.min(failureCount - 1, 4)))
        }
      } finally {
        if (controller === request) controller = undefined
        activeRequest = false
      }
    }
    const resume = () => {
      if (!alive || terminal || document.hidden) return
      clear()
      if (!activeRequest) void poll()
    }
    const changedVisibility = () => { if (document.hidden) clear(); else resume() }
    window.addEventListener('online', resume)
    window.addEventListener('focus', resume)
    document.addEventListener('visibilitychange', changedVisibility)
    void poll()
    return () => {
      alive = false
      clear()
      controller?.abort()
      window.removeEventListener('online', resume)
      window.removeEventListener('focus', resume)
      document.removeEventListener('visibilitychange', changedVisibility)
    }
  }, [batchId, context, retryKey])
  return { view, retry }
}

function ItemRow({ item }: { item: BatchItem }) {
  return <li className="batch-item">
    <div><strong>{item.fileName || 'Ảnh không có tên'}</strong> <code>{item.analysisId}</code></div>
    <p>{statusLabels[item.processingStatus]}</p>
    {item.processingStatus === 'COMPLETED' && <p>
      {item.riskLevel ? `Mức nguy cơ: ${item.riskLevel}. ` : ''}
      {item.reviewStatus === 'PENDING' ? 'Đang chờ bác sĩ review.' : `Trạng thái review: ${item.reviewStatus}.`}
    </p>}
    {item.error && <p role="note">{item.error.message || 'Không thể hoàn tất phân tích ảnh.'}
      {item.error.retryable ? ' Có thể đủ điều kiện thử lại theo chính sách backend.' : ''}
    </p>}
    {/* Chỉ cho mở kết quả khi AI đã xử lý hoàn tất */}
    {item.processingStatus === 'COMPLETED' && (
      <p>
        <Link to={`/analyses/${item.analysisId}`}>
        Xem kết quả chi tiết
        </Link>
        </p>
      )}
    {item.processingStatus === 'DUPLICATE' && item.existingAnalysisId &&
      <p>Ảnh trùng; tham chiếu analysis có quyền: <code>{item.existingAnalysisId}</code></p>}
  </li>
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
        {isBatchTerminal(view.data) ? ' — đã dừng polling.' : ' — trạng thái sẽ tự cập nhật.'}</p>
      <p>Ngày tạo: {new Date(view.data.createdAt).toLocaleString('vi-VN')}</p>
      <div className="batch-summary" aria-label="Tổng hợp batch">
        <span>Tổng ảnh: <strong>{view.data.summary.total}</strong></span>
        <span>Đang xử lý: <strong>{view.data.summary.active}</strong></span>
        <span>Thành công: <strong>{view.data.summary.succeeded}</strong></span>
        <span>Lỗi: <strong>{view.data.summary.failed}</strong></span>
        <span>Trùng: <strong>{view.data.summary.duplicate}</strong></span>
      </div>
      <h3>Trạng thái từng ảnh</h3>
      <ul className="batch-items">{view.data.items.map((item) => <ItemRow item={item} key={item.analysisId} />)}</ul>
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
  if (state.status !== 'ready') return <BackendSessionNotice />
  const submit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    const id = input.trim()
    if (!validBatchId(id)) return setValidation('Nhập Batch ID UUID hợp lệ.')
    setValidation('')
    void navigate(`/batches/${id.toLowerCase()}`)
  }
  return <main className="batch-page">
    <nav><Link to="/">Trang chủ</Link> · <Link to="/upload">Tải ảnh cá nhân</Link></nav>
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
      ? <BatchViewer key={`${state.context.firebaseUid}:${state.context.accountId}:${batchId}`} batchId={batchId} context={state.context} />
      : <p role="alert">Batch ID trên đường dẫn không hợp lệ.</p>)}
    <p className="batch-disclaimer">Receipt hoặc HTTP 202 chỉ xác nhận tiếp nhận ảnh; chỉ trạng thái COMPLETED từ GET mới xác nhận AI đã hoàn tất.</p>
  </main>
}
