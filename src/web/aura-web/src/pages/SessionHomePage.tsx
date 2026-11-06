import { Link } from 'react-router-dom'
import { useBackendSession } from '../auth/backendSession'
import BackendSessionNotice from '../auth/BackendSessionNotice'
import { canUploadPersonal } from '../api/analysisUpload'
import { ROLE_LABELS } from '../routes/roles'
import './SessionHomePage.css'

export default function SessionHomePage() {
  const { state, logout } = useBackendSession()
  if (state.status !== 'ready') return <BackendSessionNotice />
  const { context } = state
  const personalUpload = canUploadPersonal(context)
  const isClinicOrDoctor = context.roles.includes('CLINIC') || context.roles.includes('DOCTOR')

  return (
    <div className="session-home">
      <header className="session-header">
        <Link className="session-brand" to="/">AURA</Link>
        <nav aria-label="Điều hướng tài khoản">
          <Link to="/" aria-current="page">Trang chủ</Link>
          {personalUpload && <Link to="/upload">Tải ảnh võng mạc</Link>}
          <Link to="/clinic-upload">Tải ảnh phòng khám</Link>
          <Link to="/clinics/members">Thành viên phòng khám</Link>
          <Link to="/clinics/patients">Bệnh nhân phòng khám</Link>
          <Link to="/campaigns">Chiến dịch tầm soát</Link>
          {!context.roles.includes("SYSTEM_ADMIN") && <Link to="/batches">Theo dõi batch</Link>}
          {/* Mở hộp thư thông báo của tài khoản hiện tại */}
          {!context.roles.includes("SYSTEM_ADMIN") && (
            <Link to="/notifications">Thông báo</Link>)}
        </nav>
        <button type="button" onClick={() => void logout()}>Đăng xuất</button>
      </header>

      <main className="session-content">
        <h1>Trang chủ AURA</h1>
        <section className="session-account" aria-labelledby="session-account-title">
          <h2 id="session-account-title">Xin chào, {context.displayName}</h2>
          <p>Thông tin tài khoản đang sử dụng đã được xác nhận.</p>
          <h3>Vai trò của bạn</h3>
          <ul>{context.roles.map((role) => <li key={role}>{ROLE_LABELS[role]}</li>)}</ul>
        </section>

        {isClinicOrDoctor && (
          <section className="session-action" aria-labelledby="session-clinic-title" style={{ marginTop: '16px' }}>
            <h2 id="session-clinic-title">Quản lý Phòng khám</h2>
            <p>Quản lý thành viên bác sĩ/nhân viên và danh bạ bệnh nhân của phòng khám.</p>
            <div style={{ display: 'flex', gap: '12px' }}>
              <Link className="session-primary-link" to="/clinics/members">Quản lý Thành viên</Link>
              <Link className="session-primary-link" to="/clinics/patients">Danh bạ Bệnh nhân</Link>
              <Link className="session-primary-link" to="/campaigns">Chiến dịch Tầm soát</Link>
            </div>
          </section>
        )}

        {personalUpload ? (
          <section className="session-action" aria-labelledby="session-upload-title">
            <h2 id="session-upload-title">Tải ảnh cho hồ sơ cá nhân</h2>
            <p>Chọn ảnh và xem thông tin tiếp nhận của lượt gửi.</p>
            <Link className="session-primary-link" to="/upload">Mở trang tải ảnh</Link>
          </section>
        ) : (
          <p>Luồng tải ảnh cá nhân hiện dành cho tài khoản bệnh nhân đủ điều kiện.</p>
        )}
      </main>
    </div>
  )
}
