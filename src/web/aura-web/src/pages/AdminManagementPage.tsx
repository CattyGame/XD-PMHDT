import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { useBackendSession } from '../auth/backendSession'
import BackendSessionNotice from '../auth/BackendSessionNotice'
import type { BackendContext } from '../api/backendContext'
import { AnalysisResultError } from '../api/analysisResult'
import {
  formatGlobalRole,
  GLOBAL_ROLES,
  listAdminAccounts,
  listAdminAuditEvents,
  listAdminClinics,
  setDoctorCapability,
  updateAdminAccountRoles,
  updateAdminAccountStatus,
  updateAdminClinicStatus,
  type AdminAccount,
  type AdminAccountStatus,
  type AdminAuditEvent,
  type AdminClinic,
  type AdminClinicStatus,
  type GlobalRole,
} from '../api/adminManagement'
import './AdminManagementPage.css'

type AdminTab = 'clinics' | 'accounts' | 'audit'

type PendingAction =
  | { kind: 'clinic-status'; clinic: AdminClinic; nextStatus: AdminClinicStatus }
  | { kind: 'account-status'; account: AdminAccount; nextStatus: AdminAccountStatus }
  | { kind: 'account-roles'; account: AdminAccount; roles: GlobalRole[] }
  | { kind: 'doctor-capability'; account: AdminAccount; approved: boolean }

function mapAdminError(err: unknown, fallback: string): string {
  if (!(err instanceof AnalysisResultError)) return fallback
  if (err.status === 409) {
    return 'Dữ liệu đã thay đổi ở phiên khác (409 Version Conflict). Vui lòng làm mới danh sách trước khi thao tác lại.'
  }
  if (err.status === 403) {
    return 'Bạn không có quyền thực hiện thao tác quản trị này (403).'
  }
  if (err.status === 401) {
    return 'Phiên đăng nhập đã hết hạn hoặc thay đổi. Vui lòng đăng nhập lại.'
  }
  if (err.code === 'ERR_LAST_SYSTEM_ADMIN') {
    return 'Không thể gỡ vai trò SuperAdmin hoặc khóa tài khoản SuperAdmin cuối cùng.'
  }
  if (err.code === 'ERR_SELF_LOCKOUT') {
    return 'Không thể tự khóa hoặc tự hạ quyền SuperAdmin của chính tài khoản đang đăng nhập.'
  }
  if (err.status === 400) {
    return `Yêu cầu không hợp lệ (${err.code}). Vui lòng kiểm tra lại lý do và dữ liệu.`
  }
  return `${fallback} (${err.code})`
}

function AdminManagementContent({ context }: { context: BackendContext }) {
  const isSystemAdmin = context.roles.includes('SYSTEM_ADMIN')
  const [activeTab, setActiveTab] = useState<AdminTab>('clinics')
  const [error, setError] = useState('')
  const [successMsg, setSuccessMsg] = useState('')

  // Clinics state
  const [clinics, setClinics] = useState<readonly AdminClinic[]>([])
  const [clinicFilter, setClinicFilter] = useState<string>('ALL')
  const [clinicCursor, setClinicCursor] = useState<string | null>(null)
  const [clinicsLoading, setClinicsLoading] = useState(false)
  const [clinicsReloadTick, setClinicsReloadTick] = useState(0)

  // Accounts state
  const [accounts, setAccounts] = useState<readonly AdminAccount[]>([])
  const [accountStatusFilter, setAccountStatusFilter] = useState<string>('ALL')
  const [accountQuery, setAccountQuery] = useState('')
  const [accountSearchSubmitted, setAccountSearchSubmitted] = useState('')
  const [accountCursor, setAccountCursor] = useState<string | null>(null)
  const [accountsLoading, setAccountsLoading] = useState(false)
  const [accountsReloadTick, setAccountsReloadTick] = useState(0)

  // Audit state
  const [auditEvents, setAuditEvents] = useState<readonly AdminAuditEvent[]>([])
  const [auditCursor, setAuditCursor] = useState<string | null>(null)
  const [auditLoading, setAuditLoading] = useState(false)
  const [auditReloadTick, setAuditReloadTick] = useState(0)

  // Action modal / inline panel
  const [pendingAction, setPendingAction] = useState<PendingAction | null>(null)
  const [actionReason, setActionReason] = useState('')
  const [submitting, setSubmitting] = useState(false)

  const refreshAll = useCallback(() => {
    setError('')
    if (activeTab === 'clinics') setClinicsReloadTick((n) => n + 1)
    if (activeTab === 'accounts') setAccountsReloadTick((n) => n + 1)
    if (activeTab === 'audit') setAuditReloadTick((n) => n + 1)
  }, [activeTab])

  useEffect(() => {
    if (!isSystemAdmin || activeTab !== 'clinics') return
    const controller = new AbortController()
    setClinicsLoading(true)
    setError('')

    listAdminClinics(
      context,
      {
        limit: 20,
        status: clinicFilter === 'ALL' ? undefined : (clinicFilter as AdminClinicStatus),
      },
      controller.signal,
    )
      .then((page) => {
        if (controller.signal.aborted) return
        setClinics(page.items)
        setClinicCursor(page.nextCursor)
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          setError(mapAdminError(err, 'Không thể tải danh sách cơ sở y tế.'))
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setClinicsLoading(false)
      })

    return () => controller.abort()
  }, [context, isSystemAdmin, activeTab, clinicFilter, clinicsReloadTick])

  useEffect(() => {
    if (!isSystemAdmin || activeTab !== 'accounts') return
    const controller = new AbortController()
    setAccountsLoading(true)
    setError('')

    listAdminAccounts(
      context,
      {
        limit: 20,
        status:
          accountStatusFilter === 'ALL' ? undefined : (accountStatusFilter as AdminAccountStatus),
        query: accountSearchSubmitted || undefined,
      },
      controller.signal,
    )
      .then((page) => {
        if (controller.signal.aborted) return
        setAccounts(page.items)
        setAccountCursor(page.nextCursor)
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          setError(mapAdminError(err, 'Không thể tải danh sách tài khoản.'))
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setAccountsLoading(false)
      })

    return () => controller.abort()
  }, [
    context,
    isSystemAdmin,
    activeTab,
    accountStatusFilter,
    accountSearchSubmitted,
    accountsReloadTick,
  ])

  useEffect(() => {
    if (!isSystemAdmin || activeTab !== 'audit') return
    const controller = new AbortController()
    setAuditLoading(true)
    setError('')

    listAdminAuditEvents(context, { limit: 20 }, controller.signal)
      .then((page) => {
        if (controller.signal.aborted) return
        setAuditEvents(page.items)
        setAuditCursor(page.nextCursor)
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          setError(mapAdminError(err, 'Không thể tải nhật ký kiểm toán.'))
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setAuditLoading(false)
      })

    return () => controller.abort()
  }, [context, isSystemAdmin, activeTab, auditReloadTick])

  if (!isSystemAdmin) {
    return (
      <main className="admin-page">
        <div role="alert" className="admin-alert admin-alert-error">
          <h1>403 — Không có quyền truy cập</h1>
          <p>Khu vực quản trị hệ thống chỉ dành cho vai trò SuperAdmin (SYSTEM_ADMIN).</p>
          <Link to="/">Quay về trang chủ</Link>
        </div>
      </main>
    )
  }

  async function loadMoreClinics() {
    if (!clinicCursor || clinicsLoading) return
    setClinicsLoading(true)
    try {
      const page = await listAdminClinics(context, {
        limit: 20,
        cursor: clinicCursor,
        status: clinicFilter === 'ALL' ? undefined : (clinicFilter as AdminClinicStatus),
      })
      setClinics((prev) => [...prev, ...page.items])
      setClinicCursor(page.nextCursor)
    } catch (err) {
      setError(mapAdminError(err, 'Không thể tải thêm cơ sở y tế.'))
    } finally {
      setClinicsLoading(false)
    }
  }

  async function loadMoreAccounts() {
    if (!accountCursor || accountsLoading) return
    setAccountsLoading(true)
    try {
      const page = await listAdminAccounts(context, {
        limit: 20,
        cursor: accountCursor,
        status:
          accountStatusFilter === 'ALL' ? undefined : (accountStatusFilter as AdminAccountStatus),
        query: accountSearchSubmitted || undefined,
      })
      setAccounts((prev) => [...prev, ...page.items])
      setAccountCursor(page.nextCursor)
    } catch (err) {
      setError(mapAdminError(err, 'Không thể tải thêm tài khoản.'))
    } finally {
      setAccountsLoading(false)
    }
  }

  async function loadMoreAudit() {
    if (!auditCursor || auditLoading) return
    setAuditLoading(true)
    try {
      const page = await listAdminAuditEvents(context, { limit: 20, cursor: auditCursor })
      setAuditEvents((prev) => [...prev, ...page.items])
      setAuditCursor(page.nextCursor)
    } catch (err) {
      setError(mapAdminError(err, 'Không thể tải thêm nhật ký.'))
    } finally {
      setAuditLoading(false)
    }
  }

  function openAction(action: PendingAction) {
    setPendingAction(action)
    setActionReason('')
    setError('')
    setSuccessMsg('')
  }

  async function submitPendingAction() {
    if (!pendingAction || submitting) return
    const reason = actionReason.trim()
    if (!reason || reason.length > 1000) {
      setError('Lý do thao tác là bắt buộc (1–1000 ký tự).')
      return
    }
    if (pendingAction.kind === 'account-roles' && pendingAction.roles.length === 0) {
      setError('Tài khoản phải có ít nhất một vai trò toàn cục.')
      return
    }

    setSubmitting(true)
    setError('')
    setSuccessMsg('')
    try {
      if (pendingAction.kind === 'clinic-status') {
        const updated = await updateAdminClinicStatus(context, pendingAction.clinic.clinicId, {
          status: pendingAction.nextStatus,
          expectedVersion: pendingAction.clinic.version,
          reason,
        })
        setClinics((prev) =>
          prev.map((item) => (item.clinicId === updated.clinicId ? updated : item)),
        )
        setSuccessMsg(
          `Đã cập nhật trạng thái cơ sở "${updated.name}" thành ${updated.status} (lưu trong DB; thông báo qua kênh trong ứng dụng, chưa gửi email do chưa cấu hình nhà cung cấp email).`,
        )
      } else if (pendingAction.kind === 'account-status') {
        const updated = await updateAdminAccountStatus(context, pendingAction.account.accountId, {
          status: pendingAction.nextStatus,
          expectedVersion: pendingAction.account.version,
          reason,
        })
        setAccounts((prev) =>
          prev.map((item) => (item.accountId === updated.accountId ? updated : item)),
        )
        setSuccessMsg(
          `Đã cập nhật trạng thái tài khoản ${updated.displayName} thành ${updated.status} (lưu trong DB; thông báo trong ứng dụng).`,
        )
      } else if (pendingAction.kind === 'account-roles') {
        const updated = await updateAdminAccountRoles(context, pendingAction.account.accountId, {
          roles: pendingAction.roles,
          expectedVersion: pendingAction.account.version,
          reason,
        })
        setAccounts((prev) =>
          prev.map((item) => (item.accountId === updated.accountId ? updated : item)),
        )
        setSuccessMsg(
          `Đã cập nhật vai trò cho ${updated.displayName}: ${updated.roles.map(formatGlobalRole).join(', ')}.`,
        )
      } else if (pendingAction.kind === 'doctor-capability') {
        await setDoctorCapability(context, pendingAction.account.accountId, {
          approved: pendingAction.approved,
          reason,
        })
        setAccountsReloadTick((n) => n + 1)
        setSuccessMsg(
          `Đã ${pendingAction.approved ? 'phê duyệt' : 'thu hồi'} năng lực bác sĩ cho ${pendingAction.account.displayName} (lưu trong DB; chưa gửi email vì chưa cấu hình provider).`,
        )
      }
      setPendingAction(null)
      setActionReason('')
    } catch (err) {
      setError(mapAdminError(err, 'Thao tác quản trị thất bại.'))
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <div className="admin-page">
      <header className="admin-header">
        <div>
          <Link className="admin-back-link" to="/">
            ← Trang chủ
          </Link>
          <h1>Quản trị hệ thống (SuperAdmin)</h1>
          <p className="admin-subtitle">
            Quyết định được lưu trong cơ sở dữ liệu và thông báo qua kênh trong ứng dụng (in-app). Không truy cập dữ liệu PHI bệnh nhân tại đây; email chưa được kích hoạt vì chưa cấu hình nhà cung cấp email.
          </p>
        </div>
        <button type="button" className="admin-btn-secondary" onClick={refreshAll}>
          Làm mới
        </button>
      </header>

      <div className="admin-tabs" role="tablist" aria-label="Danh mục quản trị">
        <button
          type="button"
          role="tab"
          aria-selected={activeTab === 'clinics'}
          className={activeTab === 'clinics' ? 'active' : ''}
          onClick={() => setActiveTab('clinics')}
        >
          Duyệt Cơ sở y tế
        </button>
        <button
          type="button"
          role="tab"
          aria-selected={activeTab === 'accounts'}
          className={activeTab === 'accounts' ? 'active' : ''}
          onClick={() => setActiveTab('accounts')}
        >
          Quản lý Tài khoản
        </button>
        <button
          type="button"
          role="tab"
          aria-selected={activeTab === 'audit'}
          className={activeTab === 'audit' ? 'active' : ''}
          onClick={() => setActiveTab('audit')}
        >
          Nhật ký kiểm toán
        </button>
      </div>

      {error && (
        <div role="alert" className="admin-alert admin-alert-error">
          <span>{error}</span>
          <button type="button" className="admin-btn-secondary" onClick={refreshAll}>
            Làm mới dữ liệu
          </button>
        </div>
      )}

      {successMsg && (
        <div role="status" className="admin-alert admin-alert-success">
          {successMsg}
        </div>
      )}

      {pendingAction && (
        <section className="admin-action-panel" aria-label="Xác nhận thao tác quản trị">
          <h2>
            {pendingAction.kind === 'clinic-status' &&
              `Chuyển trạng thái cơ sở "${pendingAction.clinic.name}" → ${pendingAction.nextStatus} (v${pendingAction.clinic.version})`}
            {pendingAction.kind === 'account-status' &&
              `Chuyển trạng thái tài khoản "${pendingAction.account.displayName}" → ${pendingAction.nextStatus} (v${pendingAction.account.version})`}
            {pendingAction.kind === 'account-roles' &&
              `Cập nhật vai trò cho "${pendingAction.account.displayName}" (v${pendingAction.account.version})`}
            {pendingAction.kind === 'doctor-capability' &&
              `${pendingAction.approved ? 'Phê duyệt' : 'Thu hồi'} năng lực bác sĩ cho "${pendingAction.account.displayName}"`}
          </h2>

          {pendingAction.kind === 'account-roles' && (
            <div className="admin-role-checkboxes">
              {GLOBAL_ROLES.map((role) => {
                const checked = pendingAction.roles.includes(role)
                return (
                  <label key={role} className="admin-role-option">
                    <input
                      type="checkbox"
                      checked={checked}
                      onChange={(e) => {
                        const next = e.target.checked
                          ? [...pendingAction.roles, role]
                          : pendingAction.roles.filter((r) => r !== role)
                        setPendingAction({ ...pendingAction, roles: next })
                      }}
                    />
                    <span>{formatGlobalRole(role)}</span>
                  </label>
                )
              })}
            </div>
          )}

          <div className="admin-reason-field">
            <label htmlFor="admin-action-reason">Lý do thực hiện (bắt buộc) *</label>
            <textarea
              id="admin-action-reason"
              rows={2}
              maxLength={1000}
              placeholder="Nhập lý do kiểm toán (1–1000 ký tự)..."
              value={actionReason}
              disabled={submitting}
              onChange={(e) => setActionReason(e.target.value)}
            />
          </div>

          <div className="admin-panel-actions">
            <button
              type="button"
              className="admin-btn-primary"
              disabled={submitting}
              onClick={() => void submitPendingAction()}
            >
              {submitting ? 'Đang xử lý...' : 'Xác nhận'}
            </button>
            <button
              type="button"
              className="admin-btn-secondary"
              disabled={submitting}
              onClick={() => setPendingAction(null)}
            >
              Hủy
            </button>
          </div>
        </section>
      )}

      {activeTab === 'clinics' && (
        <section aria-label="Danh sách cơ sở y tế">
          <div className="admin-toolbar">
            <label>
              Trạng thái:{' '}
              <select value={clinicFilter} onChange={(e) => setClinicFilter(e.target.value)}>
                <option value="ALL">Tất cả</option>
                <option value="PENDING">PENDING (Chờ duyệt)</option>
                <option value="ACTIVE">ACTIVE (Hoạt động)</option>
                <option value="SUSPENDED">SUSPENDED (Tạm ngưng)</option>
                <option value="REJECTED">REJECTED (Từ chối)</option>
              </select>
            </label>
          </div>

          {clinicsLoading && clinics.length === 0 ? (
            <p>Đang tải danh sách cơ sở y tế...</p>
          ) : clinics.length === 0 ? (
            <p>Không có cơ sở y tế nào khớp bộ lọc.</p>
          ) : (
            <div className="admin-table-wrap">
              <table className="admin-table">
                <thead>
                  <tr>
                    <th>Cơ sở y tế</th>
                    <th>Mã số thuế / Đại diện</th>
                    <th>Trạng thái</th>
                    <th>Phiên bản</th>
                    <th>Lý do gần nhất</th>
                    <th>Thao tác</th>
                  </tr>
                </thead>
                <tbody>
                  {clinics.map((clinic) => (
                    <tr key={clinic.clinicId}>
                      <td>
                        <strong>{clinic.name}</strong>
                        <div className="admin-meta">ID: {clinic.clinicId}</div>
                      </td>
                      <td>
                        <div>MST: {clinic.taxId ?? '—'}</div>
                        <div className="admin-meta">
                          {clinic.representativeName ?? '—'} · {clinic.phone ?? '—'}
                        </div>
                      </td>
                      <td>
                        <span className={`admin-badge status-${clinic.status.toLowerCase()}`}>
                          {clinic.status}
                        </span>
                      </td>
                      <td>v{clinic.version}</td>
                      <td>{clinic.auditReason ?? '—'}</td>
                      <td className="admin-row-actions">
                        {clinic.status !== 'ACTIVE' && (
                          <button
                            type="button"
                            onClick={() =>
                              openAction({ kind: 'clinic-status', clinic, nextStatus: 'ACTIVE' })
                            }
                          >
                            Duyệt (ACTIVE)
                          </button>
                        )}
                        {clinic.status !== 'REJECTED' && (
                          <button
                            type="button"
                            onClick={() =>
                              openAction({ kind: 'clinic-status', clinic, nextStatus: 'REJECTED' })
                            }
                          >
                            Từ chối
                          </button>
                        )}
                        {clinic.status !== 'SUSPENDED' && (
                          <button
                            type="button"
                            onClick={() =>
                              openAction({
                                kind: 'clinic-status',
                                clinic,
                                nextStatus: 'SUSPENDED',
                              })
                            }
                          >
                            Tạm ngưng
                          </button>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}

          {clinicCursor && (
            <div className="admin-load-more">
              <button
                type="button"
                className="admin-btn-secondary"
                disabled={clinicsLoading}
                onClick={() => void loadMoreClinics()}
              >
                Tải thêm cơ sở
              </button>
            </div>
          )}
        </section>
      )}

      {activeTab === 'accounts' && (
        <section aria-label="Danh sách tài khoản">
          <div className="admin-toolbar">
            <form
              className="admin-search-form"
              onSubmit={(e) => {
                e.preventDefault()
                setAccountSearchSubmitted(accountQuery.trim())
              }}
            >
              <input
                type="search"
                aria-label="Tìm tài khoản"
                placeholder="Tìm theo tên hoặc UID..."
                value={accountQuery}
                onChange={(e) => setAccountQuery(e.target.value)}
              />
              <button type="submit" className="admin-btn-secondary">
                Tìm
              </button>
            </form>
            <label>
              Trạng thái:{' '}
              <select
                value={accountStatusFilter}
                onChange={(e) => setAccountStatusFilter(e.target.value)}
              >
                <option value="ALL">Tất cả</option>
                <option value="ACTIVE">ACTIVE</option>
                <option value="DISABLED">DISABLED</option>
              </select>
            </label>
          </div>

          {accountsLoading && accounts.length === 0 ? (
            <p>Đang tải danh sách tài khoản...</p>
          ) : accounts.length === 0 ? (
            <p>Không có tài khoản nào khớp bộ lọc.</p>
          ) : (
            <div className="admin-table-wrap">
              <table className="admin-table">
                <thead>
                  <tr>
                    <th>Tài khoản (Account ID)</th>
                    <th>Trạng thái</th>
                    <th>Vai trò toàn cục</th>
                    <th>Phiên bản</th>
                    <th>Thao tác</th>
                  </tr>
                </thead>
                <tbody>
                  {accounts.map((account) => (
                    <tr key={account.accountId}>
                      <td>
                        <strong>{account.displayName}</strong>
                        <div className="admin-meta">Account ID: {account.accountId}</div>
                        <div className="admin-meta">Firebase UID: {account.firebaseUid}</div>
                      </td>
                      <td>
                        <span className={`admin-badge status-${account.status.toLowerCase()}`}>
                          {account.status}
                        </span>
                      </td>
                      <td>{account.roles.map(formatGlobalRole).join(', ')}</td>
                      <td>v{account.version}</td>
                      <td className="admin-row-actions">
                        {account.status === 'ACTIVE' ? (
                          <button
                            type="button"
                            onClick={() =>
                              openAction({
                                kind: 'account-status',
                                account,
                                nextStatus: 'DISABLED',
                              })
                            }
                          >
                            Tạm khóa
                          </button>
                        ) : (
                          <button
                            type="button"
                            onClick={() =>
                              openAction({
                                kind: 'account-status',
                                account,
                                nextStatus: 'ACTIVE',
                              })
                            }
                          >
                            Kích hoạt
                          </button>
                        )}
                        <button
                          type="button"
                          onClick={() =>
                            openAction({
                              kind: 'account-roles',
                              account,
                              roles: [...account.roles],
                            })
                          }
                        >
                          Sửa vai trò
                        </button>
                        <button
                          type="button"
                          onClick={() =>
                            openAction({
                              kind: 'doctor-capability',
                              account,
                              approved: !account.roles.includes('DOCTOR'),
                            })
                          }
                        >
                          {account.roles.includes('DOCTOR')
                            ? 'Thu hồi quyền Bác sĩ'
                            : 'Duyệt quyền Bác sĩ'}
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}

          {accountCursor && (
            <div className="admin-load-more">
              <button
                type="button"
                className="admin-btn-secondary"
                disabled={accountsLoading}
                onClick={() => void loadMoreAccounts()}
              >
                Tải thêm tài khoản
              </button>
            </div>
          )}
        </section>
      )}

      {activeTab === 'audit' && (
        <section aria-label="Nhật ký kiểm toán hệ thống">
          {auditLoading && auditEvents.length === 0 ? (
            <p>Đang tải nhật ký kiểm toán...</p>
          ) : auditEvents.length === 0 ? (
            <p>Chưa có sự kiện kiểm toán nào trong khoảng thời gian mặc định.</p>
          ) : (
            <div className="admin-table-wrap">
              <table className="admin-table">
                <thead>
                  <tr>
                    <th>Thời gian</th>
                    <th>Hành động</th>
                    <th>Đối tượng (mã băm)</th>
                    <th>Kết quả</th>
                    <th>Dịch vụ</th>
                  </tr>
                </thead>
                <tbody>
                  {auditEvents.map((ev) => (
                    <tr key={ev.eventId}>
                      <td>{ev.occurredAt}</td>
                      <td>
                        <code>{ev.action}</code>
                      </td>
                      <td>
                        {ev.resourceType}: <code>{ev.resourceIdHash}</code>
                      </td>
                      <td>{ev.outcome ?? '—'}</td>
                      <td>
                        <code>{ev.sourceService}</code>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}

          {auditCursor && (
            <div className="admin-load-more">
              <button
                type="button"
                className="admin-btn-secondary"
                disabled={auditLoading}
                onClick={() => void loadMoreAudit()}
              >
                Tải thêm nhật ký
              </button>
            </div>
          )}
        </section>
      )}
    </div>
  )
}

export default function AdminManagementPage() {
  const { state } = useBackendSession()
  if (state.status !== 'ready') return <BackendSessionNotice />
  return <AdminManagementContent context={state.context} />
}
