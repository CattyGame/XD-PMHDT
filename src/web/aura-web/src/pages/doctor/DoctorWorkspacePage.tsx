import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { useBackendSession } from '../../auth/backendSession'
import BackendSessionNotice from '../../auth/BackendSessionNotice'
import type { BackendContext } from '../../api/backendContext'
import {
  DoctorApiError, doctorErrorMessage, getDoctorAnalysis, getDoctorFileAccess,
  getDoctorWorklist, isGuid, submitDoctorReview,
  type DoctorAnalysis, type ReviewRequest, type RiskLevel,
} from '../../api/doctorReview'
import InteractiveImageViewer from './InteractiveImageViewer'
import DoctorReviewForm from './DoctorReviewForm'
import styles from './doctor.module.css'

type Images = { original: string | null; vessel: string | null; kind: 'mask' | 'overlay' | null; loading: boolean; error: string }
const emptyImages: Images = { original: null, vessel: null, kind: null, loading: false, error: '' }
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
  const [conflictOpen, setConflictOpen] = useState(false)
  const [stale, setStale] = useState(false)
  const listAbort = useRef<AbortController | null>(null)
  const detailAbort = useRef<AbortController | null>(null)
  const imageAbort = useRef<AbortController | null>(null)
  const imageTimer = useRef<ReturnType<typeof setTimeout> | null>(null)

  useEffect(() => () => {
    listAbort.current?.abort()
    detailAbort.current?.abort()
    imageAbort.current?.abort()
    if (imageTimer.current) clearTimeout(imageTimer.current)
  }, [])

  function clearImages() {
    imageAbort.current?.abort()
    if (imageTimer.current) clearTimeout(imageTimer.current)
    setImages(emptyImages)
  }

  async function loadWorklist() {
    const selectedClinic = clinicInput.trim()
    if (!isGuid(selectedClinic)) { setListError('Nhập clinicContextId (UUID) hợp lệ của cơ sở được phân quyền.'); return }
    listAbort.current?.abort()
    detailAbort.current?.abort()
    clearImages()
    setDetail(null); setStale(false); setConflictOpen(false)
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
    const candidate = analysis.result?.annotations.find((file) => file.kind === 'overlay') ??
      analysis.result?.annotations.find((file) => file.kind === 'mask')
    try {
      const [original, vessel] = await Promise.all([
        getDoctorFileAccess(context, clinicId, analysis.analysisId, analysis.image.fileId, controller.signal),
        candidate ? getDoctorFileAccess(context, clinicId, analysis.analysisId, candidate.fileId, controller.signal)
          .catch(() => null) : Promise.resolve(null),
      ])
      if (controller.signal.aborted) return
      setImages({ original: original.url, vessel: vessel?.url ?? null,
        kind: vessel ? candidate?.kind as 'mask' | 'overlay' : null, loading: false,
        error: candidate && !vessel ? 'Không có quyền xem ảnh mạch máu hoặc ảnh chưa sẵn sàng.' : '' })
      const expiry = Math.min(Date.parse(original.expiresAt), vessel ? Date.parse(vessel.expiresAt) : Infinity)
      imageTimer.current = setTimeout(() => {
        if (!controller.signal.aborted) setImages({ ...emptyImages, error: 'URL ảnh đã hết hạn. Hãy xin lại quyền xem ảnh.' })
      }, imageExpiryDelay(expiry))
    } catch (error) {
      if (!controller.signal.aborted) setImages({ ...emptyImages, error: doctorErrorMessage(error) })
    }
  }

  async function loadDetail(analysisId: string) {
    detailAbort.current?.abort()
    clearImages()
    setDetail(null); setDetailError(''); setSaveError(''); setDetailLoading(true)
    const controller = new AbortController()
    detailAbort.current = controller
    try {
      const fresh = await getDoctorAnalysis(context, clinicId, analysisId, controller.signal)
      if (controller.signal.aborted) return
      setDetail(fresh); setStale(false); setConflictOpen(false)
      void loadImages(fresh)
    } catch (error) {
      if (!controller.signal.aborted) setDetailError(doctorErrorMessage(error))
    } finally {
      if (!controller.signal.aborted) setDetailLoading(false)
    }
  }

  async function saveReview(review: ReviewRequest) {
    if (!detail || stale || detail.reviewStatus === 'REVIEWED') return
    setSaveError('')
    try {
      const saved = await submitDoctorReview(context, clinicId, detail.analysisId, review)
      setDetail((old) => old?.analysisId === saved.analysisId ?
        { ...old, reviewStatus: 'REVIEWED', reviewVersion: saved.version } : old)
      setWorklist((old) => old.map((item) => item.analysisId === saved.analysisId ?
        { ...item, reviewStatus: 'REVIEWED', reviewVersion: saved.version } : item))
    } catch (error) {
      if (error instanceof DoctorApiError && error.status === 409) {
        setStale(true); setConflictOpen(true)
      } else {
        setSaveError(doctorErrorMessage(error))
      }
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
      <div className={styles.detailHeading}><div><h2>Chi tiết ca chụp</h2>
        <p>Analysis ID: <code>{detail.analysisId}</code></p>
        <p>Patient ID: <code>{detail.patientId}</code></p>
        <p>Trạng thái: <strong>{reviewLabels[detail.reviewStatus]}</strong></p>
        {detail.result?.isMock && <p className={styles.error}>Kết quả AI được backend đánh dấu là mô phỏng.</p>}
        {detail.result?.disclaimer && <p className={styles.hint}>{detail.result.disclaimer}</p>}
        </div><button type="button" onClick={() => void loadDetail(detail.analysisId)}>Làm mới chi tiết</button></div>
      <InteractiveImageViewer originalUrl={images.original} vesselUrl={images.vessel} vesselKind={images.kind} />
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
    </section>}
    {conflictOpen && detail && <div className={styles.modalBackdrop}>
      <div role="alertdialog" aria-modal="true" aria-labelledby="conflict-title" aria-describedby="conflict-text"
        className={styles.modal}>
        <h2 id="conflict-title">Xung đột phiên đánh giá</h2>
        <p id="conflict-text">Ca chụp đã được cập nhật bởi một phiên khác (HTTP 409). Đánh giá hiện tại không được lưu để tránh ghi đè dữ liệu của bác sĩ khác.</p>
        <div className={styles.modalActions}>
          <button type="button" onClick={() => setConflictOpen(false)}>Đóng</button>
          <button type="button" className={styles.primary} onClick={() => void loadDetail(detail.analysisId)}>Tải phiên bản mới nhất</button>
        </div>
      </div>
    </div>}
  </main>
}

export default function DoctorWorkspacePage() {
  const { state } = useBackendSession()
  if (state.status !== 'ready') return <BackendSessionNotice />
  if (!state.context.roles.includes('DOCTOR')) {
    return <main className={styles.workspace}><h1>403 - Không có quyền truy cập</h1>
      <p>Không gian bác sĩ chỉ dành cho tài khoản có vai trò DOCTOR.</p><Link to="/">Trang chủ</Link></main>
  }
  return <ClinicWorkspace context={state.context} />
}
