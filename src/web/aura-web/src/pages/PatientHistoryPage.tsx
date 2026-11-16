import { useEffect, useState } from 'react'
import { Link, useParams, useSearchParams } from 'react-router-dom'
import type { BackendContext } from '../api/backendContext'
import { getPatientHistory, type PatientHistory } from '../api/clinicManagement'
import { AnalysisResultError, analysisErrorMessage } from '../api/analysisResult'
import { validBatchId } from '../api/batchProgress'
import { useBackendSession } from '../auth/backendSession'
import BackendSessionNotice from '../auth/BackendSessionNotice'

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
  useEffect(() => {
    const controller = new AbortController()
    setPage(null)
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
