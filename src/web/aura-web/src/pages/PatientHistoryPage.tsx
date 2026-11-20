import { useEffect, useState } from 'react'
import { Link, useParams, useSearchParams } from 'react-router-dom'
import type { BackendContext } from '../api/backendContext'
import { getPatientHistory, type PatientHistory } from '../api/clinicManagement'
import { AnalysisResultError, analysisErrorMessage, getAnalysisResult, type AnalysisDetail } from '../api/analysisResult'
import { validBatchId } from '../api/batchProgress'
import { useBackendSession } from '../auth/backendSession'
import BackendSessionNotice from '../auth/BackendSessionNotice'
import styles from './doctor/doctor.module.css'

const processing: Record<string, string> = {
  QUEUED: 'Chờ xử lý', PROCESSING: 'Đang phân tích', COMPLETED: 'Đã hoàn tất AI',
  FAILED: 'Xử lý thất bại', LOW_QUALITY: 'Ảnh không đạt chất lượng', REJECTED: 'Đã từ chối',
  DUPLICATE: 'Ảnh trùng', QUARANTINE: 'Đã cách ly',
}

function History({ context, clinicId, patientId }: { context: BackendContext; clinicId: string; patientId: string }) {
  const [beforeId, setBeforeId] = useState<string>()
  const [reload, setReload] = useState(0)
  const [page, setPage] = useState<PatientHistory | null>(null)
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(true)
  const [showTrends, setShowTrends] = useState(false)
  const [trendsLoading, setTrendsLoading] = useState(false)
  const [trendsError, setTrendsError] = useState('')
  const [results, setResults] = useState<AnalysisDetail[]>([])
  useEffect(() => {
    const controller = new AbortController()
    setPage(null)
    setResults([])
    setTrendsError('')
    setError('')
    setLoading(true)
    getPatientHistory(context, clinicId, patientId, beforeId, controller.signal)
      .then(value => { if (!controller.signal.aborted) setPage(value) })
      .catch(cause => {
        if (!controller.signal.aborted) setError(cause instanceof AnalysisResultError
          ? [403, 404].includes(cause.status) ? 'Không có quyền hoặc không tìm thấy lịch sử bệnh nhân trong phòng khám này.' : analysisErrorMessage(cause)
          : 'Không thể tải lịch sử phân tích.')
      })
      .finally(() => { if (!controller.signal.aborted) setLoading(false) })
    return () => controller.abort()
  }, [context, clinicId, patientId, beforeId, reload])

  useEffect(() => {
    if (!showTrends || !page || loading) return
    const controller = new AbortController()
    const scopedContext = { ...context, clinicContextId: clinicId }
    const completed = page.items.filter(item => item.processingStatus === 'COMPLETED')
    setTrendsLoading(true)
    setTrendsError('')
    setResults([])
    async function load() {
      // History supplies the authorized case IDs. Each result read checks current case access again.
      const current: AnalysisDetail[] = []
      for (let offset = 0; offset < completed.length; offset += 4) {
        const group = await Promise.all(completed.slice(offset, offset + 4)
          .map(item => getAnalysisResult(item.analysisId, scopedContext, controller.signal)))
        if (controller.signal.aborted) return
        current.push(...group)
      }
      setResults(current)
    }
    void load().catch((cause: unknown) => {
      if (controller.signal.aborted) return
      if (cause instanceof AnalysisResultError && [401, 403, 404].includes(cause.status)) {
        setPage(null)
        setError('Quyền đọc lịch sử hoặc kết quả đã thay đổi. Hãy tải lại để kiểm tra quyền.')
      } else {
        setTrendsError(cause instanceof AnalysisResultError ? analysisErrorMessage(cause) : 'Không thể tải các chỉ số theo thời gian.')
      }
    }).finally(() => {
      if (!controller.signal.aborted) setTrendsLoading(false)
      controller.abort()
    })
    return () => controller.abort()
  }, [showTrends, page, loading, context, clinicId])

  const resultsById = new Map(results.map(value => [value.analysisId, value]))

  return <main className="batch-page">
    <Link to="/clinics/patients">Danh bạ bệnh nhân</Link>
    <h1>Lịch sử phân tích của bệnh nhân</h1>
    <p>Mỗi dòng là một ảnh đã tiếp nhận. Trạng thái hoàn tất AI và đánh giá bác sĩ được ghi riêng.</p>
    {loading && <p role="status">Đang tải lịch sử…</p>}
    {error && <p role="alert">{error}</p>}
    {page && (page.items.length ? <table className="batch-detail-table" aria-label="Lịch sử phân tích">
      <thead><tr><th scope="col">Thời gian tiếp nhận</th><th scope="col">Xử lý AI</th><th scope="col">Bác sĩ</th><th scope="col">Thao tác</th></tr></thead>
      <tbody>{page.items.map(item => <tr key={item.analysisId}>
        <td>{new Date(item.createdAt).toLocaleString('vi-VN')}</td>
        <td>{processing[item.processingStatus] ?? item.processingStatus}</td>
        <td>{item.reviewStatus === 'REVIEWED' ? 'Đã đánh giá' : item.reviewStatus === 'PENDING' ? 'Chờ đánh giá' : item.reviewStatus}</td>
        <td><Link to={`/analyses/${item.analysisId}?clinicContextId=${clinicId}`}>Xem kết quả</Link></td>
      </tr>)}</tbody>
    </table> : <p>Chưa có lượt phân tích nào.</p>)}
    <div>
      <button type="button" disabled={loading || !beforeId} onClick={() => setBeforeId(undefined)}>Lượt mới nhất</button>{' '}
      {page?.nextBeforeId && <button type="button" disabled={loading} onClick={() => setBeforeId(page.nextBeforeId!)}>Các lượt cũ hơn</button>}{' '}
      <button type="button" disabled={loading} onClick={() => setReload(value => value + 1)}>Tải lại</button>
    </div>
    {page && <section className={styles.panel} aria-labelledby="patient-trends-heading">
      <div className={styles.sectionTitle}><h2 id="patient-trends-heading">Chỉ số theo thời gian</h2></div>
      <button type="button" disabled={trendsLoading} onClick={() => setShowTrends(value => !value)}>
        {showTrends ? 'Ẩn chỉ số' : 'Hiện chỉ số của trang này'}
      </button>
      {showTrends && <>
        {trendsLoading && <p role="status">Đang tải chỉ số…</p>}
        {trendsError && <p role="alert" className={styles.error}>{trendsError}</p>}
        {!trendsLoading && !trendsError && !results.length && <p>Trang này chưa có kết quả AI hoàn tất.</p>}
        {results.length > 0 && <div className={styles.tableWrap}>
          <p className={styles.hint}>Đối chiếu chỉ số theo ngày và phiên bản mô hình, cấu hình. Dữ liệu thiếu được hiển thị riêng.</p>
          <table className={styles.table} aria-label="Chỉ số theo thời gian">
            <thead><tr><th scope="col">Ngày tiếp nhận</th><th scope="col">AVR</th><th scope="col">Mật độ mạch</th>
              <th scope="col">Độ ngoằn ngoèo</th><th scope="col">Phiên bản</th></tr></thead>
            <tbody>{page.items.map(item => {
              const detail = resultsById.get(item.analysisId)
              const result = detail?.result
              const avr = result?.research?.avr
              return <tr key={item.analysisId}>
                <td><Link to={`/analyses/${item.analysisId}?clinicContextId=${clinicId}`}>{new Date(item.createdAt).toLocaleString('vi-VN')}</Link></td>
                <td>{avr?.avr?.toFixed(3) ?? (avr?.unavailable_reason
                  ? `Không đo được (${avr.unavailable_reason})` : 'Chưa có')}</td>
                <td>{result?.metrics.vesselDensity == null ? 'Chưa có' : (100 * result.metrics.vesselDensity).toFixed(1) + '%'}</td>
                <td>{result?.metrics.tortuosityIndex?.toFixed(3) ?? 'Chưa có'}</td>
                <td>{result ? <>
                  Kết quả {result.version} · Đánh giá {detail.reviewVersion ?? 'chưa có'}
                  <br />{result.modelVersion} · {result.configVersion} · {result.thresholdVersion}
                  {avr && <><br />Mô hình AVR {avr.model_version}</>}
                  {result.isMock && <><br />Mô phỏng</>}
                </> : 'Chưa có'}</td>
              </tr>
            })}</tbody>
          </table>
        </div>}
      </>}
    </section>}
  </main>
}

export default function PatientHistoryPage() {
  const { state } = useBackendSession()
  const { patientId = '' } = useParams()
  const [query] = useSearchParams()
  const clinicId = query.get('clinicContextId') ?? ''
  if (state.status !== 'ready') return <BackendSessionNotice />
  if (!validBatchId(clinicId) || !validBatchId(patientId)) return <p role="alert">Định danh bệnh nhân hoặc phòng khám không hợp lệ.</p>
  return <History key={`${state.context.accountId}:${state.context.firebaseUid}:${state.context.profileVersion}:${clinicId}:${patientId}`}
    context={state.context} clinicId={clinicId} patientId={patientId} />
}
