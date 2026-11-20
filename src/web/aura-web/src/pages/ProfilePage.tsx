import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { useBackendSession } from '../auth/backendSession'
import BackendSessionNotice from '../auth/BackendSessionNotice'
import type { BackendContext } from '../api/backendContext'
import { AnalysisResultError } from '../api/analysisResult'
import { formatGlobalRole } from '../api/adminManagement'
import {
  getMyProfile,
  updateMyProfile,
  validateProfileDraft,
  type UserProfile,
} from '../api/profile'
import './ProfilePage.css'

function ProfileContent({ context }: { context: BackendContext }) {
  const [profile, setProfile] = useState<UserProfile | null>(null)
  const [displayName, setDisplayName] = useState('')
  const [phone, setPhone] = useState('')
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')
  const [conflictError, setConflictError] = useState('')
  const [successMsg, setSuccessMsg] = useState('')
  const [reloadTick, setReloadTick] = useState(0)

  const triggerReload = useCallback(() => {
    setReloadTick((prev) => prev + 1)
  }, [])

  useEffect(() => {
    const controller = new AbortController()
    setLoading(true)
    setError('')
    setConflictError('')

    getMyProfile(context, controller.signal)
      .then((loaded) => {
        if (controller.signal.aborted) return
        setProfile(loaded)
        setDisplayName(loaded.displayName)
        setPhone(loaded.phone ?? '')
      })
      .catch((err) => {
        if (controller.signal.aborted) return
        if (err instanceof AnalysisResultError) {
          setError(
            err.status === 401
              ? 'Phiên đăng nhập đã hết hạn hoặc thay đổi. Vui lòng đăng nhập lại.'
              : `Không thể tải hồ sơ cá nhân (${err.code}).`,
          )
        } else {
          setError('Không thể tải hồ sơ cá nhân.')
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false)
      })

    return () => controller.abort()
  }, [context, reloadTick])

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    if (!profile || saving) return

    setError('')
    setConflictError('')
    setSuccessMsg('')

    const validationError = validateProfileDraft(displayName, phone)
    if (validationError) {
      setError(validationError)
      return
    }

    setSaving(true)
    try {
      const updated = await updateMyProfile(context, {
        displayName,
        phone: phone.trim() ? phone.trim() : null,
        expectedVersion: profile.profileVersion,
      })
      setProfile(updated)
      setDisplayName(updated.displayName)
      setPhone(updated.phone ?? '')
      setSuccessMsg(`Đã cập nhật hồ sơ (phiên bản v${updated.profileVersion}).`)
    } catch (err) {
      if (err instanceof AnalysisResultError && err.status === 409) {
        setConflictError(
          'Hồ sơ đã được cập nhật ở phiên khác (409). Vui lòng tải lại dữ liệu mới nhất trước khi lưu.',
        )
      } else if (err instanceof AnalysisResultError && err.status === 400) {
        setError('Thông tin hồ sơ không hợp lệ. Vui lòng kiểm tra lại tên hiển thị và số điện thoại.')
      } else if (err instanceof AnalysisResultError && err.status === 401) {
        setError('Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại.')
      } else {
        setError('Không thể lưu thay đổi hồ sơ. Vui lòng thử lại.')
      }
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="profile-page">
      <header className="profile-header">
        <div>
          <Link className="profile-back-link" to="/">
            ← Trang chủ
          </Link>
          <h1>Hồ sơ cá nhân</h1>
          <p className="profile-subtitle">
            Quản lý thông tin tự phục vụ. Vai trò và phạm vi tenant được quản trị viên hệ thống quản lý, không thể tự chỉnh sửa.
          </p>
        </div>
        <button type="button" className="profile-refresh-btn" onClick={triggerReload} disabled={loading || saving}>
          Làm mới
        </button>
      </header>

      {error && (
        <div role="alert" className="profile-alert profile-alert-error">
          {error}
        </div>
      )}

      {conflictError && (
        <div role="alert" className="profile-alert profile-alert-conflict">
          <span>{conflictError}</span>
          <button type="button" onClick={triggerReload} disabled={loading}>
            Tải lại hồ sơ mới nhất
          </button>
        </div>
      )}

      {successMsg && (
        <div role="status" className="profile-alert profile-alert-success">
          {successMsg}
        </div>
      )}

      {loading && !profile ? (
        <p className="profile-loading">Đang tải hồ sơ cá nhân...</p>
      ) : (
        profile && (
          <div className="profile-grid">
            <section className="profile-card" aria-labelledby="profile-identity-title">
              <h2 id="profile-identity-title">Định danh &amp; Quyền hạn (Chỉ đọc)</h2>
              <dl className="profile-dl">
                <div>
                  <dt>Account ID (Tài khoản)</dt>
                  <dd>
                    <code>{profile.accountId}</code>
                  </dd>
                </div>
                <div>
                  <dt>Patient ID (Hồ sơ bệnh nhân)</dt>
                  <dd>
                    <code>{profile.patientId ?? 'Chưa cấp'}</code>
                  </dd>
                </div>
                <div>
                  <dt>Personal Tenant ID</dt>
                  <dd>
                    <code>{profile.personalTenantId ?? '—'}</code>
                  </dd>
                </div>
                <div>
                  <dt>Email xác thực</dt>
                  <dd>{profile.email ?? 'Chưa có email'}</dd>
                </div>
                <div>
                  <dt>Vai trò hiện tại</dt>
                  <dd>{context.roles.map(formatGlobalRole).join(', ')}</dd>
                </div>
                <div>
                  <dt>Phiên bản hồ sơ (ETag)</dt>
                  <dd>v{profile.profileVersion}</dd>
                </div>
              </dl>
            </section>

            <section className="profile-card" aria-labelledby="profile-edit-title">
              <h2 id="profile-edit-title">Cập nhật thông tin cá nhân</h2>
              <form onSubmit={(e) => void handleSubmit(e)} className="profile-form" noValidate>
                <div className="profile-field">
                  <label htmlFor="profile-display-name">Tên hiển thị *</label>
                  <input
                    id="profile-display-name"
                    type="text"
                    maxLength={120}
                    required
                    value={displayName}
                    disabled={saving || Boolean(conflictError)}
                    onChange={(e) => setDisplayName(e.target.value)}
                  />
                </div>
                <div className="profile-field">
                  <label htmlFor="profile-phone">Số điện thoại</label>
                  <input
                    id="profile-phone"
                    type="tel"
                    maxLength={32}
                    placeholder="Ví dụ: +84 901234567"
                    value={phone}
                    disabled={saving || Boolean(conflictError)}
                    onChange={(e) => setPhone(e.target.value)}
                  />
                </div>
                <div className="profile-actions">
                  <button
                    type="submit"
                    className="profile-save-btn"
                    disabled={saving || Boolean(conflictError)}
                  >
                    {saving ? 'Đang lưu...' : 'Lưu thay đổi'}
                  </button>
                </div>
              </form>
            </section>
          </div>
        )
      )}
    </div>
  )
}

export default function ProfilePage() {
  const { state } = useBackendSession()
  if (state.status !== 'ready') return <BackendSessionNotice />
  return <ProfileContent context={state.context} />
}
