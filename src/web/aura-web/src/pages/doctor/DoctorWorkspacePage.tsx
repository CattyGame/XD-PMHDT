import { useCallback, useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { useBackendSession } from '../../auth/backendSession'
import BackendSessionNotice from '../../auth/BackendSessionNotice'
import type { BackendContext } from '../../api/backendContext'
import { AnalysisResultError, getProtectedContent } from '../../api/analysisResult'
import {
  DoctorApiError, doctorErrorMessage, getDoctorAnalysis, getDoctorFileAccess,
  getDoctorWorklist, isGuid, submitDoctorFeedback, submitDoctorReview,
  type DoctorAnalysis, type FeedbackType, type ReviewRequest, type RiskLevel,
} from '../../api/doctorReview'
import InteractiveImageViewer, { layerLabels, type LayerKind } from './InteractiveImageViewer'
import DoctorReviewForm from './DoctorReviewForm'
import AppModal from '../../components/common/AppModal'
import DoctorFeedbackForm from './DoctorFeedbackForm'
import styles from './doctor.module.css'

type Images = { original: string | null; layers: Partial<Record<LayerKind, string>>; loading: boolean; error: string }
const emptyImages: Images = { original: null, layers: {}, loading: false, error: '' }
const riskLabels: Record<RiskLevel, string> = { LOW: 'Thấp', MODERATE: 'Trung bình', HIGH: 'Cao' }
const reviewLabels: Record<string, string> = {
  PENDING: 'Chờ đánh giá', IN_REVIEW: 'Đang đánh giá', REVIEWED: 'Đã đánh giá',
  CORRECTION_REQUIRED: 'Cần điều chỉnh', NOT_REQUIRED: 'Không yêu cầu',
}

function imageExpiryDelay(expiry: number): number {
  return Math.max(0, Math.min(expiry - Date.now(), 2_147_000_000))
}

function ClinicWorkspace({ context }: { context: BackendContext }) {
  const [clinicInput, setClinicInput] = useState('')
  const [clinicId, setClinicId] = useState('')
  const [worklist, setWorklist] = useState<DoctorAnalysis[]>([])
  const [loaded, setLoaded] = useState(false)
  const [listLoading, setListLoading] = useState(false)
  const [listError, setListError] = useState('')
  const [dateFilter, setDateFilter] = useState('')
  const [riskFilter, setRiskFilter] = useState('ALL')
  const [statusFilter, setStatusFilter] = useState('ALL')
  const [detail, setDetail] = useState<DoctorAnalysis | null>(null)
  const [detailLoading, setDetailLoading] = useState(false)
  const [detailError, setDetailError] = useState('')
  const [saveError, setSaveError] = useState('')
  const [images, setImages] = useState<Images>(emptyImages)
  const [conflictOpen, setConflictOpen] = useState<'review' | 'feedback' | null>(null)
  const [stale, setStale] = useState(false)
  const listAbort = useRef<AbortController | null>(null)
  const detailAbort = useRef<AbortController | null>(null)
  const imageAbort = useRef<AbortController | null>(null)
  const imageTimer = useRef<ReturnType<typeof setTimeout> | null>(null)
  const imageUrls = useRef<string[]>([])
  const selection = useRef(0)
  const refreshDetailButton = useRef<HTMLButtonElement>(null)
  const focusDetailHeading = useCallback((heading: HTMLHeadingElement | null) => {
    heading?.focus()
  }, [])

  useEffect(() => () => {
    listAbort.current?.abort()
    detailAbort.current?.abort()
    imageAbort.current?.abort()
    if (imageTimer.current) clearTimeout(imageTimer.current)
    imageUrls.current.forEach(url => URL.revokeObjectURL(url))
    selection.current += 1
  }, [])

  function clearImages() {
    imageAbort.current?.abort()
    if (imageTimer.current) clearTimeout(imageTimer.current)
    imageUrls.current.forEach(url => URL.revokeObjectURL(url))
    imageUrls.current = []
    setImages(emptyImages)
  }

  function accessDenied(error: unknown): boolean {
    return (error instanceof DoctorApiError || error instanceof AnalysisResultError) &&
      [401, 403, 404].includes(error.status)
  }

  function clearDeniedAccess(error: unknown) {
    selection.current += 1
    listAbort.current?.abort()
    detailAbort.current?.abort()
    clearImages()
    setDetail(null); setWorklist([]); setLoaded(false)
    setStale(false); setConflictOpen(null); setDetailLoading(false); setListLoading(false)
    setDetailError(doctorErrorMessage(error)); setSaveError('')
  }

  async function loadWorklist() {
    const selectedClinic = clinicInput.trim()
    if (!isGuid(selectedClinic)) { setListError('Nhập clinicContextId (UUID) hợp lệ của cơ sở được phân quyền.'); return }
    listAbort.current?.abort()
    detailAbort.current?.abort()
    selection.current += 1
    clearImages()
    setDetail(null); setStale(false); setConflictOpen(null)
    setDetailLoading(false); setDetailError(''); setSaveError('')
    setLoaded(false); setListLoading(true); setListError(''); setWorklist([])
    const controller = new AbortController()
    listAbort.current = controller
    try {
      const list = await getDoctorWorklist(context, selectedClinic, controller.signal)
      if (controller.signal.aborted) return
      setClinicId(selectedClinic); setWorklist(list); setLoaded(true)
    } catch (error) {
      if (!controller.signal.aborted) setListError(doctorErrorMessage(error))
    } finally {
      if (!controller.signal.aborted) setListLoading(false)
    }
  }

  async function loadImages(analysis: DoctorAnalysis) {
    clearImages()
    const controller = new AbortController()
    imageAbort.current = controller
    setImages({ ...emptyImages, loading: true })
    const kinds = (Object.keys(layerLabels) as LayerKind[]).filter(kind =>
      kind !== 'heatmap' || analysis.result?.research?.dr.gradcam_geometry === 'IMAGENET_CENTER_CROP_224')
    const imageContext = { ...context, clinicContextId: clinicId }
    async function download(fileId: string) {
      const access = await getDoctorFileAccess(context, clinicId, analysis.analysisId, fileId, controller.signal)
      const blob = await getProtectedContent(access.url, imageContext, controller.signal)
      if (!['image/png', 'image/jpeg'].includes(blob.type.split(';')[0]))
        throw new AnalysisResultError(0, 'ERR_INVALID_RESPONSE')
      controller.signal.throwIfAborted()
      const bitmap = await createImageBitmap(blob).catch(() => {
        throw new AnalysisResultError(0, 'ERR_INVALID_RESPONSE')
      })
      bitmap.close()
      controller.signal.throwIfAborted()
      return { blob, expiry: Date.parse(access.expiresAt) }
    }
    try {
      const [original, ...layers] = await Promise.all([
        download(analysis.image.fileId),
        ...kinds.map(async kind => {
          const candidates = analysis.result?.annotations.filter(file => file.kind === kind)
          if (candidates?.length !== 1) return null
          try {
            return { image: await download(candidates[0].fileId), kind }
          } catch (error) {
            if (accessDenied(error)) throw error
            return { image: null, kind }
          }
        }),
      ])
      if (controller.signal.aborted) return
      const expiry = Math.min(original.expiry, ...layers.flatMap(layer => layer?.image ? [layer.image.expiry] : []))
      if (!Number.isFinite(expiry) || imageExpiryDelay(expiry) === 0)
        throw new AnalysisResultError(0, 'ERR_INVALID_RESPONSE')
      function imageUrl(blob: Blob) {
        const url = URL.createObjectURL(blob)
        imageUrls.current.push(url)
        return url
      }
      const originalUrl = imageUrl(original.blob)
      const layerUrls: Images['layers'] = {}
      for (const layer of layers) {
        if (layer?.image) layerUrls[layer.kind] = imageUrl(layer.image.blob)
      }
      const unavailable = layers.flatMap(layer => layer && !layer.image ? [layerLabels[layer.kind]] : [])
      setImages({ original: originalUrl, layers: layerUrls, loading: false,
        error: unavailable.length ? `Chưa tải được lớp ảnh: ${unavailable.join(', ')}.` : '' })
      imageTimer.current = setTimeout(() => {
        if (!controller.signal.aborted) {
          clearImages()
          setImages({ ...emptyImages, error: 'URL ảnh đã hết hạn. Hãy xin lại quyền xem ảnh.' })
        }
      }, imageExpiryDelay(expiry))
    } catch (error) {
      if (controller.signal.aborted) return
      if (accessDenied(error)) clearDeniedAccess(error)
      else {
        clearImages()
        setImages({ ...emptyImages, error: doctorErrorMessage(error) })
      }
    }
  }

  async function loadDetail(analysisId: string) {
    selection.current += 1
    detailAbort.current?.abort()
    clearImages()
    setDetail(null); setDetailError(''); setSaveError(''); setDetailLoading(true)
    const controller = new AbortController()
    detailAbort.current = controller
    try {
      const fresh = await getDoctorAnalysis(context, clinicId, analysisId, controller.signal)
      if (controller.signal.aborted) return
      setDetail(fresh); setStale(false); setConflictOpen(null)
      void loadImages(fresh)
    } catch (error) {
      if (!controller.signal.aborted) {
        if (accessDenied(error)) clearDeniedAccess(error)
        else setDetailError(doctorErrorMessage(error))
      }
    } finally {
      if (!controller.signal.aborted) setDetailLoading(false)
    }
  }

  async function saveReview(review: ReviewRequest) {
    if (!detail || stale || detail.reviewStatus === 'REVIEWED') return
    const requestSelection = selection.current
    setSaveError('')
    try {
      const saved = await submitDoctorReview(context, clinicId, detail.analysisId, review)
      if (requestSelection !== selection.current) return
      setDetail((old) => old?.analysisId === saved.analysisId ?
        { ...old, reviewStatus: 'REVIEWED', reviewVersion: saved.version } : old)
      setWorklist((old) => old.map((item) => item.analysisId === saved.analysisId ?
        { ...item, reviewStatus: 'REVIEWED', reviewVersion: saved.version } : item))
    } catch (error) {
      if (requestSelection !== selection.current) return
      if (accessDenied(error)) { clearDeniedAccess(error); return }
      if (error instanceof DoctorApiError && error.status === 409) {
        setStale(true); setConflictOpen('review')
      } else {
        setSaveError(doctorErrorMessage(error))
      }
    }
  }

  async function saveFeedback(type: FeedbackType, comment: string, signal: AbortSignal) {
    if (!detail || stale) return false
    const requestSelection = selection.current
    try {
      await submitDoctorFeedback(context, clinicId, detail, type, comment, signal)
      return requestSelection === selection.current && !signal.aborted
    } catch (error) {
      if (requestSelection !== selection.current || signal.aborted) return false
      if (error instanceof DoctorApiError && error.status === 404 && error.code === 'ERR_REQUEST_FAILED') {
        // Distinguish an undeployed route from a missing/revoked case by rechecking current authority.
        try {
          await getDoctorAnalysis(context, clinicId, detail.analysisId, signal)
        } catch (cause) {
          if (requestSelection !== selection.current || signal.aborted) return false
          if (accessDenied(cause)) { clearDeniedAccess(cause); return false }
          throw cause
        }
        throw new DoctorApiError(404, 'ERR_FEEDBACK_UNAVAILABLE')
      }
      if (accessDenied(error)) { clearDeniedAccess(error); return false }
      if (error instanceof DoctorApiError && error.status === 409) {
        setStale(true); setConflictOpen('feedback')
        return false
      }
      throw error
    }
  }

  const filtered = worklist.filter((item) => {
    const risk = item.result?.riskAvailable ? item.result.riskLevel : null
    return (!dateFilter || item.createdAt?.slice(0, 10) === dateFilter) &&
      (riskFilter === 'ALL' || risk === riskFilter || (riskFilter === 'UNKNOWN' && !risk)) &&
      (statusFilter === 'ALL' || item.processingStatus === statusFilter)
  })
  const statuses = Array.from(new Set(worklist.map((item) => item.processingStatus)))

  return <main className={styles.workspace}>
    <header className={styles.header}>
      <Link to="/" className={styles.back}>← Trang chủ AURA</Link>
      <div><span className={styles.eyebrow}>AURA · DOCTOR WORKSPACE</span><h1>Không gian làm việc Bác sĩ</h1>
        <p>Danh sách ca chụp được phân công · Xem ảnh · Xác nhận kết luận</p></div>
    </header>
    <section className={styles.panel} aria-label="Chọn cơ sở y tế">
      <div className={styles.clinicRow}>
        <label>Clinic Context ID (UUID)
          <input value={clinicInput} onChange={(event) => setClinicInput(event.target.value)}
            placeholder="UUID cơ sở y tế được phân quyền" aria-label="Clinic Context ID" />
        </label>
        <button type="button" className={styles.primary} disabled={listLoading} onClick={() => void loadWorklist()}>
          {listLoading ? 'Đang tải...' : 'Tải worklist'}
        </button>
      </div>
      <p className={styles.hint}>Bản frontend hiện chưa có API chọn cơ sở. Cần nhập UUID cơ sở được phân quyền; backend vẫn xác thực vai trò và quyền trên từng ca.</p>
      {listError && <p className={styles.error} role="alert">{listError}</p>}
    </section>
    <section className={styles.panel} aria-label="Danh sách ca chụp">
      <div className={styles.sectionTitle}><h2>Danh sách cần xem xét</h2><span>{filtered.length} / {worklist.length} ca</span></div>
      <div className={styles.filters}>
        <label>Ngày tạo ca <input aria-label="Lọc ngày" type="date" value={dateFilter}
          onChange={(event) => setDateFilter(event.target.value)} /></label>
        <label>Mức độ nghi ngờ <select aria-label="Lọc mức nguy cơ" value={riskFilter}
          onChange={(event) => setRiskFilter(event.target.value)}>
          <option value="ALL">Tất cả</option><option value="LOW">Thấp</option>
          <option value="MODERATE">Trung bình</option><option value="HIGH">Cao</option>
          <option value="UNKNOWN">Chưa có đánh giá nguy cơ</option>
        </select></label>
        <label>Trạng thái phân tích <select aria-label="Lọc trạng thái" value={statusFilter}
          onChange={(event) => setStatusFilter(event.target.value)}>
          <option value="ALL">Tất cả</option>
          {statuses.map((status) => <option key={status} value={status}>{status}</option>)}
        </select></label>
      </div>
      <p className={styles.hint}>API hiện cung cấp createdAt (ngày tạo ca), chưa có trường ngày chụp; bộ lọc dùng ngày tạo ca và không giả định đó là ngày chụp.</p>
      {loaded ? <div className={styles.tableWrap}><table className={styles.table}>
        <thead><tr><th>Bệnh nhân (ID)</th><th>Mã ca chụp</th><th>Ngày tạo</th><th>Nghi ngờ</th><th>Phân tích</th><th>Review</th><th>Thao tác</th></tr></thead>
        <tbody>{filtered.map((item) => <tr key={item.analysisId}>
          <td><code>{item.patientId.slice(0, 8)}…</code></td>
          <td><code title={item.analysisId}>{item.analysisId.slice(0, 8)}…</code></td>
          <td>{item.createdAt ? new Date(item.createdAt).toLocaleDateString('vi-VN') : 'Chưa có'}</td>
          <td>{item.result?.riskAvailable && item.result.riskLevel ? riskLabels[item.result.riskLevel] : 'Chưa có'}</td>
          <td>{item.processingStatus}</td>
          <td><span className={item.reviewStatus === 'REVIEWED' ? styles.done : styles.pending}>
            {reviewLabels[item.reviewStatus] ?? item.reviewStatus}</span></td>
          <td><button type="button" className={styles.textButton} onClick={() => void loadDetail(item.analysisId)}>Mở chi tiết</button></td>
        </tr>)}{filtered.length === 0 && <tr><td colSpan={7}>Chưa có ca phù hợp với bộ lọc.</td></tr>}</tbody>
      </table></div> : <p role="status">{listLoading ? 'Đang tải worklist từ API...' : 'Chọn cơ sở y tế để tải ca chụp được phân công.'}</p>}
    </section>
    {(detailLoading || detailError) && <section className={styles.panel}>
      {detailLoading && <p role="status">Đang tải chi tiết ca...</p>}
      {detailError && <p role="alert" className={styles.error}>{detailError}</p>}
    </section>}
    {detail && <section className={styles.detail} aria-label="Chi tiết ca chụp">
      <div className={styles.detailHeading}><div><h2 tabIndex={-1} ref={focusDetailHeading}>Chi tiết ca chụp</h2>
        <p>Analysis ID: <code>{detail.analysisId}</code></p>
        <p>Patient ID: <code>{detail.patientId}</code></p>
        <p>Trạng thái: <strong>{reviewLabels[detail.reviewStatus]}</strong></p>
        <Link className={styles.back} to={`/clinics/patients/${detail.patientId}/history?clinicContextId=${clinicId}`}>
          Lịch sử và xu hướng của bệnh nhân
        </Link>
        {detail.result?.isMock && <p className={styles.error}>Kết quả AI được backend đánh dấu là mô phỏng.</p>}
        {detail.result?.disclaimer && <p className={styles.hint}>{detail.result.disclaimer}</p>}
        </div><button type="button" ref={refreshDetailButton} onClick={() => void loadDetail(detail.analysisId)}>Làm mới chi tiết</button></div>
      {detail.result && <section className={styles.formPanel} aria-label="Nguồn kết quả AI">
        <div className={styles.sectionTitle}><h2>Nguồn kết quả AI</h2></div>
        <dl className={styles.provenance}>
          <div><dt>Phiên bản kết quả</dt><dd>{detail.result.version ?? 'Chưa có'}</dd></div>
          <div><dt>Mô hình</dt><dd>{detail.result.modelVersion ?? 'Chưa có'}</dd></div>
          <div><dt>Cấu hình</dt><dd>{detail.result.configVersion ?? 'Chưa có'}</dd></div>
          <div><dt>Ngưỡng</dt><dd>{detail.result.thresholdVersion ?? 'Chưa có'}</dd></div>
          <div><dt>Phiên bản đánh giá</dt><dd>{detail.reviewVersion}</dd></div>
          {detail.result.research && <>
            <div><dt>AVR nghiên cứu</dt><dd>{detail.result.research.avr.avr?.toFixed(3) ??
              `Không đo được (${detail.result.research.avr.unavailable_reason})`}</dd></div>
            <div><dt>Mô hình AVR</dt><dd>{detail.result.research.avr.model_version}</dd></div>
            <div><dt>Mô hình DR nhị phân</dt><dd>{detail.result.research.dr.model_version}</dd></div>
          </>}
        </dl>
      </section>}
      <InteractiveImageViewer key={detail.analysisId} originalUrl={images.original} layers={images.layers} />
      {images.loading && <p role="status">Đang xin URL ảnh riêng tư...</p>}
      {images.error && <p role="alert" className={styles.error}>{images.error}</p>}
      {!images.loading && (images.error || !images.original) && <button type="button"
        onClick={() => void loadImages(detail)}>Xin lại quyền xem ảnh</button>}
      {saveError && <p role="alert" className={styles.error}>{saveError}</p>}
      {stale && <p role="alert" className={styles.error}>Dữ liệu cũ đã bị khóa, cần tải phiên bản mới nhất trước khi tiếp tục.</p>}
      <DoctorReviewForm key={detail.analysisId + ':' + detail.reviewVersion} baseVersion={detail.reviewVersion}
        aiRisk={detail.result?.riskAvailable ? detail.result.riskLevel : null}
        reviewed={detail.reviewStatus === 'REVIEWED'} disabled={stale || detail.processingStatus !== 'COMPLETED'}
        onSubmit={saveReview} />
      {detail.result && <DoctorFeedbackForm
        key={`${detail.analysisId}:${detail.result.resultId}:${detail.reviewVersion}`}
        disabled={stale || !detail.result.resultId || !detail.result.modelVersion}
        onSubmit={saveFeedback} />}
    </section>}
    <AppModal isOpen={conflictOpen !== null && Boolean(detail)} role="alertdialog"
      title="Xung đột phiên đánh giá" describedBy="conflict-text" className={styles.modal}
      onClose={() => {
        setConflictOpen(null)
        refreshDetailButton.current?.focus()
      }}
      actions={detail && <button type="button" className={styles.primary}
        onClick={() => void loadDetail(detail.analysisId)}>Tải phiên bản mới nhất</button>}>
      <p id="conflict-text">Ca chụp đã được cập nhật bởi một phiên khác (HTTP 409). {conflictOpen === 'feedback'
        ? 'Phản hồi chưa được lưu. Hãy mở phiên bản hiện tại của ca trước khi gửi lại.'
        : 'Đánh giá hiện tại không được lưu để tránh ghi đè dữ liệu của bác sĩ khác.'}</p>
    </AppModal>
  </main>
}

export default function DoctorWorkspacePage() {
  const { state } = useBackendSession()
  if (state.status !== 'ready') return <BackendSessionNotice />
  if (!state.context.roles.includes('DOCTOR')) {
    return <main className={styles.workspace}><h1>403 - Không có quyền truy cập</h1>
      <p>Không gian bác sĩ chỉ dành cho tài khoản có vai trò DOCTOR.</p><Link to="/">Trang chủ</Link></main>
  }
  return <ClinicWorkspace key={`${state.context.firebaseUid}:${state.context.accountId}:${state.context.profileVersion}`} context={state.context} />
}
