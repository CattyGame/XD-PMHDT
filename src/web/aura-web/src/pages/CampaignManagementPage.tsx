import { useState, useEffect } from 'react'
import { Link } from 'react-router-dom'
import { useBackendSession } from '../auth/backendSession'
import BackendSessionNotice from '../auth/BackendSessionNotice'
import type { BackendContext } from '../api/backendContext'
import {
  getActorClinics,
  type ClinicInfo,
} from '../api/clinicManagement'
import {
  getCampaigns,
  createCampaign,
  linkBatchToCampaign,
  updateCampaignStatus,
  getCampaignSummary,
  getCampaignExportUrl,
  type CampaignItem,
  type CampaignSummary,
  type CampaignStatus,
} from '../api/campaignManagement'
import { AnalysisResultError } from '../api/analysisResult'
import './CampaignManagementPage.css'

function CampaignManagementContent({ context }: { context: BackendContext }) {
  const [clinics, setClinics] = useState<readonly ClinicInfo[]>([])
  const [selectedClinicId, setSelectedClinicId] = useState<string>('')
  const [campaigns, setCampaigns] = useState<readonly CampaignItem[]>([])
  const [statusFilter, setStatusFilter] = useState<string>('ALL')
  const [loading, setLoading] = useState<boolean>(true)
  const [error, setError] = useState<string | null>(null)
  const [successMsg, setSuccessMsg] = useState<string | null>(null)
  const [reloadTrigger, setReloadTrigger] = useState<number>(0)

  // Create Campaign Modal State
  const [showCreateModal, setShowCreateModal] = useState<boolean>(false)
  const [newName, setNewName] = useState('')
  const [newLocation, setNewLocation] = useState('')
  const [newDesc, setNewDesc] = useState('')
  const [newTarget, setNewTarget] = useState<number>(100)
  const [newStartDate, setNewStartDate] = useState(new Date().toISOString().slice(0, 10))
  const [newEndDate, setNewEndDate] = useState(new Date(Date.now() + 7 * 86400000).toISOString().slice(0, 10))
  const [submitting, setSubmitting] = useState<boolean>(false)

  // Link Batch Modal State
  const [linkingCampaignId, setLinkingCampaignId] = useState<string | null>(null)
  const [linkBatchId, setLinkBatchId] = useState('')
  const [linking, setLinking] = useState<boolean>(false)

  // Summary State
  const [selectedSummary, setSelectedSummary] = useState<CampaignSummary | null>(null)
  const [loadingSummary, setLoadingSummary] = useState<boolean>(false)

  // 1. Tải danh sách phòng khám của tài khoản
  useEffect(() => {
    const controller = new AbortController()
    getActorClinics(context, controller.signal)
      .then((items) => {
        if (controller.signal.aborted) return
        setClinics(items)
        if (items.length > 0) {
          setSelectedClinicId(items[0].clinicId)
        }
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          setError(
            err instanceof AnalysisResultError && err.status === 403
              ? 'Bạn không có quyền truy cập quản lý phòng khám.'
              : 'Không thể tải danh sách phòng khám.',
          )
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false)
      })

    return () => controller.abort()
  }, [context])

  // 2. Tải danh sách chiến dịch theo phòng khám đã chọn & bộ lọc
  useEffect(() => {
    if (!selectedClinicId) return

    const controller = new AbortController()
    setLoading(true)
    setError(null)

    getCampaigns(context, selectedClinicId, controller.signal, statusFilter)
      .then((data) => {
        if (!controller.signal.aborted) {
          setCampaigns(data)
        }
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          setError(
            err instanceof AnalysisResultError && err.status === 403
              ? 'Không có quyền xem chiến dịch của phòng khám này.'
              : 'Lỗi tải danh sách chiến dịch tầm soát.',
          )
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false)
      })

    return () => controller.abort()
  }, [context, selectedClinicId, statusFilter, reloadTrigger])

  // Xử lý tạo chiến dịch
  async function handleCreateCampaign(e: React.FormEvent) {
    e.preventDefault()
    if (!selectedClinicId) return

    setError(null)
    setSuccessMsg(null)
    setSubmitting(true)

    try {
      await createCampaign(context, selectedClinicId, {
        name: newName,
        description: newDesc,
        location: newLocation,
        targetPatientCount: Number(newTarget),
        startDate: new Date(newStartDate).toISOString(),
        endDate: new Date(newEndDate).toISOString(),
      })

      setSuccessMsg('Tạo chiến dịch tầm soát thành công!')
      setShowCreateModal(false)
      setNewName('')
      setNewLocation('')
      setNewDesc('')
      setReloadTrigger((v) => v + 1)
    } catch (err: any) {
      setError(err instanceof AnalysisResultError ? `Lỗi: ${err.code}` : (err.message || 'Tạo chiến dịch thất bại.'))
    } finally {
      setSubmitting(false)
    }
  }

  // Xử lý đổi trạng thái chiến dịch
  async function handleUpdateStatus(campaignId: string, status: CampaignStatus) {
    if (!selectedClinicId) return
    setError(null)
    setSuccessMsg(null)

    try {
      await updateCampaignStatus(context, selectedClinicId, campaignId, status)
      setSuccessMsg(`Đã cập nhật chiến dịch sang trạng thái ${status}.`)
      setReloadTrigger((v) => v + 1)
    } catch (err: any) {
      setError(err instanceof AnalysisResultError ? `Lỗi: ${err.code}` : (err.message || 'Cập nhật trạng thái thất bại.'))
    }
  }

  // Xử lý gắn batch vào chiến dịch
  async function handleLinkBatch(e: React.FormEvent) {
    e.preventDefault()
    if (!selectedClinicId || !linkingCampaignId || !linkBatchId.trim()) return

    setError(null)
    setSuccessMsg(null)
    setLinking(true)

    try {
      const res = await linkBatchToCampaign(context, selectedClinicId, linkingCampaignId, linkBatchId.trim())
      setSuccessMsg(res.message || 'Gắn batch ảnh vào chiến dịch thành công!')
      setLinkingCampaignId(null)
      setLinkBatchId('')
      setReloadTrigger((v) => v + 1)
    } catch (err: any) {
      if (err instanceof AnalysisResultError && err.code === 'ERR_CAMPAIGN_CLOSED') {
        setError('Chiến dịch đã kết thúc (CLOSED), không cho phép gắn thêm batch ảnh mới.')
      } else {
        setError(err instanceof AnalysisResultError ? `Lỗi: ${err.code}` : (err.message || 'Không thể liên kết batch.'))
      }
    } finally {
      setLinking(false)
    }
  }

  // Xử lý xem thống kê
  async function handleViewSummary(campaignId: string) {
    if (!selectedClinicId) return
    setError(null)
    setLoadingSummary(true)

    try {
      const data = await getCampaignSummary(context, selectedClinicId, campaignId)
      setSelectedSummary(data)
    } catch (err: any) {
      setError(err instanceof AnalysisResultError ? `Lỗi: ${err.code}` : (err.message || 'Không thể tải thống kê chiến dịch.'))
    } finally {
      setLoadingSummary(false)
    }
  }

  function handleExportCsv(campaignId: string) {
    if (!selectedClinicId) return
    const url = getCampaignExportUrl(selectedClinicId, campaignId)
    window.open(url, '_blank')
  }

  const totalCampaigns = campaigns.length
  const activeCampaigns = campaigns.filter((c) => c.status === 'IN_PROGRESS' || c.status === 'PLANNED').length
  const closedCampaigns = campaigns.filter((c) => c.status === 'CLOSED').length

  return (
    <div className="campaign-page">
      <header className="campaign-header">
        <h1>AURA — Quản Lý Chiến Dịch Tầm Soát</h1>
        <nav className="campaign-nav" aria-label="Điều hướng chính">
          <Link to="/">Trang chủ</Link>
          <Link to="/clinics/members">Thành viên</Link>
          <Link to="/clinics/patients">Bệnh nhân</Link>
          <Link to="/campaigns" aria-current="page" style={{ fontWeight: 'bold' }}>Chiến dịch</Link>
          <Link to="/batches">Theo dõi batch</Link>
          <Link to="/notifications">Thông báo</Link>
        </nav>
      </header>

      {/* Thanh chọn phòng khám (Đảm bảo cô lập AC2) */}
      <section className="campaign-selector-bar">
        <label htmlFor="clinic-select">Phòng khám:</label>
        {clinics.length > 0 ? (
          <select
            id="clinic-select"
            value={selectedClinicId}
            onChange={(e) => setSelectedClinicId(e.target.value)}
          >
            {clinics.map((c) => (
              <option key={c.clinicId} value={c.clinicId}>
                {c.clinicName} ({c.role})
              </option>
            ))}
          </select>
        ) : (
          <span style={{ color: '#64748b' }}>Không có phòng khám liên kết</span>
        )}
      </section>

      {/* Tiêu đề & Nút Tạo mới */}
      <div className="campaign-title-bar">
        <div>
          <h2>Đợt Khám & Sàng Lọc Mạch Máu Võng Mạc</h2>
          <p>Quản lý các đợt khám lưu động cộng đồng, tầm soát sức khỏe định kỳ doanh nghiệp và gán batch ảnh chụp</p>
        </div>
        {selectedClinicId && (
          <button
            type="button"
            className="btn-primary"
            onClick={() => setShowCreateModal(true)}
          >
            + Tạo Chiến Dịch Mới
          </button>
        )}
      </div>

      {/* Thông báo Alert */}
      {error && <div className="alert-error" role="alert">{error}</div>}
      {successMsg && <div className="alert-success" role="status">{successMsg}</div>}

      {/* Thẻ KPI Thống kê */}
      <div className="campaign-kpi-grid">
        <div className="kpi-card kpi-card-total">
          <div className="kpi-label">Tổng chiến dịch</div>
          <div className="kpi-value">{totalCampaigns}</div>
        </div>
        <div className="kpi-card kpi-card-active">
          <div className="kpi-label">Đang triển khai</div>
          <div className="kpi-value">{activeCampaigns}</div>
        </div>
        <div className="kpi-card kpi-card-closed">
          <div className="kpi-label">Đã đóng / Hoàn tất</div>
          <div className="kpi-value">{closedCampaigns}</div>
        </div>
      </div>

      {/* Bộ lọc trạng thái */}
      <div className="campaign-filter-bar">
        <span style={{ fontSize: 13, fontWeight: 600, color: '#475569' }}>Trạng thái:</span>
        {['ALL', 'PLANNED', 'IN_PROGRESS', 'COMPLETED', 'CLOSED'].map((st) => (
          <button
            key={st}
            type="button"
            className={statusFilter === st ? 'active' : ''}
            onClick={() => setStatusFilter(st)}
          >
            {st === 'ALL' ? 'Tất cả' : st}
          </button>
        ))}
      </div>

      {/* Bảng danh sách chiến dịch */}
      <section className="campaign-table-container">
        {loading ? (
          <div style={{ padding: 24, textAlign: 'center', color: '#64748b' }}>Đang tải dữ liệu chiến dịch...</div>
        ) : campaigns.length === 0 ? (
          <div style={{ padding: 40, textAlign: 'center', color: '#94a3b8' }}>
            <p>Chưa có chiến dịch tầm soát nào trong phòng khám này.</p>
          </div>
        ) : (
          <table className="campaign-table">
            <thead>
              <tr>
                <th>Tên chiến dịch</th>
                <th>Địa điểm</th>
                <th>Chỉ tiêu</th>
                <th>Thời gian</th>
                <th>Trạng thái</th>
                <th style={{ textAlign: 'right' }}>Thao tác</th>
              </tr>
            </thead>
            <tbody>
              {campaigns.map((camp) => (
                <tr key={camp.id}>
                  <td style={{ fontWeight: 600 }}>{camp.name}</td>
                  <td style={{ color: '#475569' }}>{camp.location}</td>
                  <td>{camp.targetPatientCount} người</td>
                  <td style={{ color: '#64748b', fontSize: 13 }}>
                    {new Date(camp.startDate).toLocaleDateString('vi-VN')} – {new Date(camp.endDate).toLocaleDateString('vi-VN')}
                  </td>
                  <td>
                    <span className={`campaign-status-badge status-${camp.status}`}>
                      {camp.status}
                    </span>
                  </td>
                  <td style={{ textAlign: 'right' }}>
                    <button
                      type="button"
                      className="btn-action btn-action-summary"
                      onClick={() => handleViewSummary(camp.id)}
                    >
                      Thống kê
                    </button>
                    {camp.status !== 'CLOSED' && (
                      <button
                        type="button"
                        className="btn-action btn-action-link"
                        onClick={() => setLinkingCampaignId(camp.id)}
                      >
                        Gắn Batch
                      </button>
                    )}
                    <button
                      type="button"
                      className="btn-action btn-action-export"
                      onClick={() => handleExportCsv(camp.id)}
                    >
                      Xuất CSV
                    </button>
                    {camp.status !== 'CLOSED' && (
                      <button
                        type="button"
                        className="btn-action btn-action-close"
                        onClick={() => handleUpdateStatus(camp.id, 'CLOSED')}
                      >
                        Đóng
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      {/* Modal Xem Thống Kê & Tiến Độ */}
      {selectedSummary && (
        <div className="modal-backdrop">
          <div className="modal-content" style={{ width: 580 }}>
            <div className="modal-header">
              <h3>Tiến Độ & Kết Quả Tầm Soát: {selectedSummary.campaignName}</h3>
              <button type="button" onClick={() => setSelectedSummary(null)}>✕</button>
            </div>

            {/* Progress Bar */}
            <div style={{ marginBottom: 16 }}>
              <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 13, marginBottom: 6 }}>
                <span>Tỷ lệ hoàn thành phân tích:</span>
                <strong>{selectedSummary.completionRatePercent}% ({selectedSummary.completedImages}/{selectedSummary.totalImages} ảnh)</strong>
              </div>
              <div style={{ height: 12, background: '#e2e8f0', borderRadius: 6, overflow: 'hidden' }}>
                <div style={{ width: `${Math.min(selectedSummary.completionRatePercent, 100)}%`, height: '100%', background: '#10b981', transition: 'width 0.3s' }} />
              </div>
            </div>

            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12, marginBottom: 20 }}>
              <div style={{ background: '#f8fafc', padding: 12, borderRadius: 6, border: '1px solid #e2e8f0' }}>
                <span style={{ fontSize: 12, color: '#64748b' }}>Tổng số Batch đã nạp</span>
                <div style={{ fontSize: 20, fontWeight: 700, color: '#0f172a' }}>{selectedSummary.totalBatches}</div>
              </div>
              <div style={{ background: '#f8fafc', padding: 12, borderRadius: 6, border: '1px solid #e2e8f0' }}>
                <span style={{ fontSize: 12, color: '#64748b' }}>Số bệnh nhân thực tế</span>
                <div style={{ fontSize: 20, fontWeight: 700, color: '#0f172a' }}>{selectedSummary.uniquePatients} / {selectedSummary.targetPatientCount}</div>
              </div>
              <div style={{ background: '#f0fdf4', padding: 12, borderRadius: 6, borderLeft: '4px solid #10b981' }}>
                <span style={{ fontSize: 12, color: '#166534' }}>Ca bình thường (LOW)</span>
                <div style={{ fontSize: 20, fontWeight: 700, color: '#14532d' }}>{selectedSummary.normalCases}</div>
              </div>
              <div style={{ background: '#fffbeb', padding: 12, borderRadius: 6, borderLeft: '4px solid #f59e0b' }}>
                <span style={{ fontSize: 12, color: '#92400e' }}>Nghi ngờ tổn thương</span>
                <div style={{ fontSize: 20, fontWeight: 700, color: '#78350f' }}>{selectedSummary.suspectedCases} ({selectedSummary.highRiskCases} ca nặng)</div>
              </div>
            </div>

            <div className="modal-actions">
              <button
                type="button"
                className="btn-primary"
                onClick={() => handleExportCsv(selectedSummary.campaignId)}
              >
                Tải Báo Cáo CSV
              </button>
              <button
                type="button"
                className="btn-secondary"
                onClick={() => setSelectedSummary(null)}
              >
                Đóng
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Modal Gắn Batch Ảnh */}
      {linkingCampaignId && (
        <div className="modal-backdrop">
          <form className="modal-content" onSubmit={handleLinkBatch}>
            <div className="modal-header">
              <h3>Gắn Batch Ảnh Vào Chiến Dịch</h3>
              <button type="button" onClick={() => setLinkingCampaignId(null)}>✕</button>
            </div>
            <p style={{ fontSize: 13, color: '#64748b', marginBottom: 16 }}>
              Nhập mã Batch ID đã tiếp nhận thành công để liên kết vào đợt tầm soát này:
            </p>
            <div className="form-group">
              <label htmlFor="batch-id-input">Mã Batch (UUID) *</label>
              <input
                id="batch-id-input"
                type="text"
                placeholder="Ví dụ: b1111111-0000-0000-0000-000000000001"
                value={linkBatchId}
                onChange={(e) => setLinkBatchId(e.target.value)}
                required
              />
            </div>
            <div className="modal-actions">
              <button
                type="button"
                className="btn-secondary"
                onClick={() => setLinkingCampaignId(null)}
                disabled={linking}
              >
                Hủy
              </button>
              <button
                type="submit"
                className="btn-primary"
                disabled={linking}
              >
                {linking ? 'Đang gắn...' : 'Xác nhận Gắn Batch'}
              </button>
            </div>
          </form>
        </div>
      )}

      {/* Modal Tạo Chiến Dịch Mới */}
      {showCreateModal && (
        <div className="modal-backdrop">
          <form className="modal-content" onSubmit={handleCreateCampaign}>
            <div className="modal-header">
              <h3>Khởi Tạo Chiến Dịch Tầm Soát Mới</h3>
              <button type="button" onClick={() => setShowCreateModal(false)}>✕</button>
            </div>
            <div className="form-group">
              <label htmlFor="camp-name">Tên đợt tầm soát *</label>
              <input
                id="camp-name"
                type="text"
                placeholder="Ví dụ: Khám mắt học đường THPT Nguyễn Huệ"
                value={newName}
                onChange={(e) => setNewName(e.target.value)}
                required
              />
            </div>
            <div className="form-group">
              <label htmlFor="camp-location">Địa điểm tổ chức *</label>
              <input
                id="camp-location"
                type="text"
                placeholder="Ví dụ: Hội trường B - Quận 5, TP.HCM"
                value={newLocation}
                onChange={(e) => setNewLocation(e.target.value)}
                required
              />
            </div>
            <div className="form-group-row">
              <div className="form-group">
                <label htmlFor="camp-start">Ngày bắt đầu *</label>
                <input
                  id="camp-start"
                  type="date"
                  value={newStartDate}
                  onChange={(e) => setNewStartDate(e.target.value)}
                  required
                />
              </div>
              <div className="form-group">
                <label htmlFor="camp-end">Ngày kết thúc *</label>
                <input
                  id="camp-end"
                  type="date"
                  value={newEndDate}
                  onChange={(e) => setNewEndDate(e.target.value)}
                  required
                />
              </div>
            </div>
            <div className="form-group">
              <label htmlFor="camp-target">Chỉ tiêu số người khám dự kiến</label>
              <input
                id="camp-target"
                type="number"
                min="0"
                value={newTarget}
                onChange={(e) => setNewTarget(Number(e.target.value))}
              />
            </div>
            <div className="form-group">
              <label htmlFor="camp-desc">Mô tả thêm</label>
              <textarea
                id="camp-desc"
                rows={2}
                placeholder="Thông tin đối tượng khám hoặc ghi chú chuẩn bị..."
                value={newDesc}
                onChange={(e) => setNewDesc(e.target.value)}
              />
            </div>
            <div className="modal-actions">
              <button
                type="button"
                className="btn-secondary"
                onClick={() => setShowCreateModal(false)}
                disabled={submitting}
              >
                Hủy
              </button>
              <button
                type="submit"
                className="btn-primary"
                disabled={submitting}
              >
                {submitting ? 'Đang tạo...' : 'Tạo Mới'}
              </button>
            </div>
          </form>
        </div>
      )}
    </div>
  )
}

export default function CampaignManagementPage() {
  const { state } = useBackendSession()
  if (state.status !== 'ready') return <BackendSessionNotice />
  return <CampaignManagementContent context={state.context} />
}
