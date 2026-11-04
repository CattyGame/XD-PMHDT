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

  return (
    <div className="session-home">
      <header className="session-header">
        <Link className="session-brand" to="/">AURA</Link>
        <nav aria-label="Điều hướng tài khoản">
          <Link to="/" aria-current="page">Trang chủ</Link>
          {personalUpload && <Link to="/upload">Tải ảnh võng mạc</Link>}
          {context.roles.includes("DOCTOR") && <Link to="/doctor/workspace">Không gian bác sĩ</Link>}
          <Link to="/clinic-upload">Tải ảnh phòng khám</Link>
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
