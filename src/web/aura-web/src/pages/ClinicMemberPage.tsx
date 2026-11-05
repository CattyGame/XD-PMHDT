import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { useBackendSession } from '../auth/backendSession'
import BackendSessionNotice from '../auth/BackendSessionNotice'
import type { BackendContext } from '../api/backendContext'
import {
  getActorClinics,
  getClinicMembers,
  inviteClinicMember,
  revokeClinicMember,
  type ClinicInfo,
  type ClinicMember,
  type ClinicInvitationResult,
} from '../api/clinicManagement'
import { AnalysisResultError } from '../api/analysisResult'
import './ClinicMemberPage.css'

function ClinicMemberContent({ context }: { context: BackendContext }) {
  const [clinics, setClinics] = useState<readonly ClinicInfo[]>([])
  const [selectedClinicId, setSelectedClinicId] = useState<string>('')
  const [members, setMembers] = useState<readonly ClinicMember[]>([])
  const [statusFilter, setStatusFilter] = useState<string>('ALL')
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string>('')
  const [successMsg, setSuccessMsg] = useState<string>('')
  const [reloadTrigger, setReloadTrigger] = useState(0)

  // Modal invite state
  const [showInviteModal, setShowInviteModal] = useState(false)
  const [inviteEmail, setInviteEmail] = useState('')
  const [inviteRole, setInviteRole] = useState<'DOCTOR' | 'STAFF'>('DOCTOR')
  const [inviting, setInviting] = useState(false)
  const [lastInvitation, setLastInvitation] = useState<ClinicInvitationResult | null>(null)

  // 1. Load actor's clinics
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
              ? 'Bạn không có quyền truy cập quản lý phòng khám này.'
              : 'Không thể tải danh sách phòng khám.',
          )
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false)
      })

    return () => controller.abort()
  }, [context])

  // 2. Load members for selected clinic
  useEffect(() => {
    if (!selectedClinicId) return
    const controller = new AbortController()
    const apiStatus = statusFilter === 'ALL' ? undefined : statusFilter

    getClinicMembers(context, selectedClinicId, controller.signal, apiStatus)
      .then((items) => {
        if (!controller.signal.aborted) {
          setMembers(items)
        }
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          setError(
            err instanceof AnalysisResultError && err.status === 403
              ? 'Bạn không có quyền xem thành viên của phòng khám này.'
              : 'Không thể tải danh sách thành viên.',
          )
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false)
      })

    return () => controller.abort()
  }, [context, selectedClinicId, statusFilter, reloadTrigger])

  // 3. Handle Invite Member
  const handleInvite = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!inviteEmail.trim() || !selectedClinicId) return
    setInviting(true)
    setError('')
    setSuccessMsg('')
    try {
      const result = await inviteClinicMember(
        context,
        selectedClinicId,
        inviteEmail.trim(),
        inviteRole,
      )
      setLastInvitation(result)
      setSuccessMsg(`Đã gửi lời mời thành công đến ${result.email}!`)
      setInviteEmail('')
      setReloadTrigger((v) => v + 1)
    } catch (err) {
      if (err instanceof AnalysisResultError) {
        if (err.status === 409) {
          setError('Thành viên này đã tồn tại hoặc đã tham gia phòng khám.')
        } else if (err.status === 403) {
          setError('Bạn không có quyền mời thành viên cho phòng khám này.')
        } else {
          setError('Không thể gửi lời mời. Vui lòng kiểm tra lại email.')
        }
      } else {
        setError('Lỗi kết nối khi gửi lời mời.')
      }
    } finally {
      setInviting(false)
    }
  }

  // 4. Handle Revoke Member
  const handleRevoke = async (member: ClinicMember) => {
    if (!selectedClinicId || !member.accountId) return
    if (member.accountId === context.accountId) {
      alert('Bạn không thể tự thu hồi quyền của chính mình.')
      return
    }
    const confirmed = window.confirm(
      `Bạn có chắc chắn muốn thu hồi quyền truy cập của ${member.displayName || member.invitedEmail || 'thành viên này'}?`,
    )
    if (!confirmed) return

    setLoading(true)
    setError('')
    setSuccessMsg('')
    try {
      await revokeClinicMember(context, selectedClinicId, member.accountId)
      setSuccessMsg(`Đã thu hồi quyền truy cập thành công!`)
      setReloadTrigger((v) => v + 1)
    } catch (err) {
      if (err instanceof AnalysisResultError && err.status === 400) {
        setError('Không thể thu hồi quyền quản trị viên duy nhất của phòng khám.')
      } else {
        setError('Không thể thu hồi quyền thành viên.')
      }
    } finally {
      setLoading(false)
    }
  }

  const currentClinic = clinics.find((c) => c.clinicId === selectedClinicId)
  const isClinicAdmin = currentClinic?.role === 'CLINIC_ADMIN'

  return (
    <div className="clinic-member-page">
      <header className="clinic-member-header">
        <h1>Quản lý Thành viên Phòng khám</h1>
        <nav className="clinic-member-nav">
          <Link to="/">Trang chủ</Link>
          <Link to="/clinic-upload">Tải ảnh phòng khám</Link>
          <Link to="/clinics/patients">Quản lý Bệnh nhân</Link>
        </nav>
      </header>

      {error && <div className="status-alert error" role="alert">{error}</div>}
      {successMsg && <div className="status-alert success" role="status">{successMsg}</div>}

      <div className="clinic-selector-bar">
        <label htmlFor="clinic-select">Chọn Phòng khám:</label>
        <select
          id="clinic-select"
          value={selectedClinicId}
          onChange={(e) => setSelectedClinicId(e.target.value)}
          disabled={loading || clinics.length === 0}
        >
          {clinics.map((c) => (
            <option key={c.clinicId} value={c.clinicId}>
              {c.clinicName} ({c.role})
            </option>
          ))}
        </select>
        {currentClinic && (
          <span style={{ fontSize: '13px', color: '#64748b' }}>
            Vai trò của bạn: <strong>{currentClinic.role}</strong>
          </span>
        )}
      </div>

      <div className="clinic-member-actions">
        <div className="clinic-filter-group" role="group" aria-label="Lọc trạng thái thành viên">
          {['ALL', 'ACTIVE', 'INVITED', 'REVOKED'].map((filter) => (
            <button
              key={filter}
              type="button"
              className={`filter-btn ${statusFilter === filter ? 'active' : ''}`}
              onClick={() => setStatusFilter(filter)}
            >
              {filter === 'ALL'
                ? 'Tất cả'
                : filter === 'ACTIVE'
                  ? 'Hoạt động'
                  : filter === 'INVITED'
                    ? 'Đang mời'
                    : 'Đã thu hồi'}
            </button>
          ))}
        </div>

        {isClinicAdmin && (
          <button
            type="button"
            className="btn-primary"
            onClick={() => {
              setShowInviteModal(true)
              setLastInvitation(null)
            }}
          >
            + Mời Bác sĩ / Nhân viên
          </button>
        )}
      </div>

      {lastInvitation && (
        <div className="token-copy-box">
          <p style={{ margin: '0 0 6px 0', fontWeight: 600, color: '#166534' }}>
            Lời mời đã tạo cho <code>{lastInvitation.email}</code> (Vai trò: {lastInvitation.role}):
          </p>
          <p style={{ margin: '0 0 6px 0' }}>
            Mã Token kích hoạt: <code>{lastInvitation.token}</code>
          </p>
          <small style={{ color: '#64748b' }}>
            Bác sĩ đăng nhập bằng tài khoản Google có email trên và nhập token để kích hoạt ngữ cảnh
            phòng khám (AC1).
          </small>
        </div>
      )}

      {/* Danh sách thành viên */}
      <div className="member-table-container">
        <table className="member-table" aria-label="Bảng thành viên phòng khám">
          <thead>
            <tr>
              <th>Họ và Tên / Email</th>
              <th>Vai trò</th>
              <th>Trạng thái</th>
              <th>Ngày tham gia</th>
              <th>Hành động</th>
            </tr>
          </thead>
          <tbody>
            {loading && members.length === 0 ? (
              <tr>
                <td colSpan={5} style={{ textAlign: 'center', padding: '24px' }}>
                  Đang tải danh sách thành viên...
                </td>
              </tr>
            ) : members.length === 0 ? (
              <tr>
                <td colSpan={5} style={{ textAlign: 'center', padding: '24px', color: '#64748b' }}>
                  Chưa có thành viên nào phù hợp với bộ lọc.
                </td>
              </tr>
            ) : (
              members.map((member) => (
                <tr key={member.membershipId}>
                  <td>
                    <strong>{member.displayName || member.invitedEmail || 'Chưa cập nhật tên'}</strong>
                    {member.invitedEmail && member.displayName && (
                      <div style={{ fontSize: '12px', color: '#64748b' }}>{member.invitedEmail}</div>
                    )}
                  </td>
                  <td>
                    <span
                      className={`badge ${
                        member.role === 'CLINIC_ADMIN'
                          ? 'badge-role-admin'
                          : member.role === 'DOCTOR'
                            ? 'badge-role-doctor'
                            : 'badge-role-staff'
                      }`}
                    >
                      {member.role === 'CLINIC_ADMIN'
                        ? 'Quản trị phòng khám'
                        : member.role === 'DOCTOR'
                          ? 'Bác sĩ'
                          : 'Nhân viên'}
                    </span>
                  </td>
                  <td>
                    <span
                      className={`badge ${
                        member.status === 'ACTIVE'
                          ? 'badge-active'
                          : member.status === 'INVITED'
                            ? 'badge-invited'
                            : 'badge-revoked'
                      }`}
                    >
                      {member.status === 'ACTIVE'
                        ? 'Hoạt động'
                        : member.status === 'INVITED'
                          ? 'Đã gửi lời mời'
                          : 'Đã thu hồi'}
                    </span>
                  </td>
                  <td>
                    {member.createdAt
                      ? new Date(member.createdAt).toLocaleDateString('vi-VN')
                      : '—'}
                  </td>
                  <td>
                    {isClinicAdmin && member.status === 'ACTIVE' && member.accountId !== context.accountId && (
                      <button
                        type="button"
                        className="btn-danger"
                        onClick={() => handleRevoke(member)}
                      >
                        Thu hồi
                      </button>
                    )}
                    {member.status === 'INVITED' && member.invitationToken && (
                      <span style={{ fontSize: '12px', color: '#64748b' }}>
                        Token: <code>{member.invitationToken.slice(0, 8)}...</code>
                      </span>
                    )}
                  </td>
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>

      {/* Modal mời thành viên mới */}
      {showInviteModal && (
        <div className="invite-modal-overlay">
          <div className="invite-modal">
            <h2>Mời Bác sĩ / Nhân viên vào Phòng khám</h2>
            <form onSubmit={handleInvite}>
              <div className="form-group">
                <label htmlFor="invite-email">Email tài khoản Google / Firebase:</label>
                <input
                  id="invite-email"
                  type="email"
                  required
                  placeholder="bacsi@phongkham.vn"
                  value={inviteEmail}
                  onChange={(e) => setInviteEmail(e.target.value)}
                />
              </div>

              <div className="form-group">
                <label htmlFor="invite-role">Vai trò đảm nhiệm:</label>
                <select
                  id="invite-role"
                  value={inviteRole}
                  onChange={(e) => setInviteRole(e.target.value as 'DOCTOR' | 'STAFF')}
                >
                  <option value="DOCTOR">Bác sĩ chẩn đoán (Doctor)</option>
                  <option value="STAFF">Kỹ thuật viên / Nhân viên (Staff)</option>
                </select>
              </div>

              <div className="modal-actions">
                <button
                  type="button"
                  className="btn-secondary"
                  onClick={() => setShowInviteModal(false)}
                  disabled={inviting}
                >
                  Đóng
                </button>
                <button type="submit" className="btn-primary" disabled={inviting}>
                  {inviting ? 'Đang gửi...' : 'Gửi lời mời'}
                </button>
              </div>
            </form>
          </div>
        </div>
      )}
    </div>
  )
}

export default function ClinicMemberPage() {
  const { state } = useBackendSession()
  if (state.status !== 'ready') return <BackendSessionNotice />
  return <ClinicMemberContent context={state.context} />
}
