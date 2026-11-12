import { useEffect, useRef, useState } from 'react'
import { Navigate } from 'react-router-dom'
import {
  GoogleAuthProvider,
  signInWithEmailAndPassword,
  signInWithPopup,
  browserSessionPersistence,
  setPersistence,
} from 'firebase/auth'
import { getFirebaseAuth } from '../firebase/config'
import { useBackendSession } from '../auth/backendSession'
import BackendSessionNotice from '../auth/BackendSessionNotice'
import './LoginPage.css'

export default function LoginPage() {
  const { state } = useBackendSession()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [showPassword, setShowPassword] = useState(false)
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(false)
  const submitting = useRef(false)
  const mounted = useRef(false)
  useEffect(() => {
    mounted.current = true
    return () => { mounted.current = false }
  }, [])

  async function login(method: 'email' | 'google') {
    if (submitting.current || state.status !== 'anonymous') return
    submitting.current = true
    setError('')
    setLoading(true)
    try {
      const auth = getFirebaseAuth()
      if (!auth) throw new Error('Firebase unavailable')
      await setPersistence(auth, browserSessionPersistence)
      if (!mounted.current) return
      if (method === 'email') {
        await signInWithEmailAndPassword(auth, email.trim(), password)
      } else {
        await signInWithPopup(auth, new GoogleAuthProvider())
      }
      // Only the shared backend-ready state permits navigation.
    } catch {
      if (mounted.current) setError(method === 'email'
        ? 'Đăng nhập thất bại. Vui lòng kiểm tra email và mật khẩu.'
        : 'Không thể đăng nhập Google. Vui lòng thử lại.')
    } finally {
      submitting.current = false
      if (mounted.current) {
        setPassword('')
        setShowPassword(false)
        setLoading(false)
      }
    }
  }

  if (state.status === 'ready') return <Navigate to="/" replace />
  const canLogin = state.status === 'anonymous' && !loading
  return (
    <main className="aura-login">
      <div className="aura-login-container">
        <section className="aura-login-brand" aria-label="Giới thiệu AURA">
          <div className="aura-login-logo">
            <span className="aura-login-logo-icon" aria-hidden="true">◉</span>
            <span>AURA</span>
          </div>
          <div className="aura-login-brand-content">
            <h1>Chăm sóc sức khỏe<br />bằng công nghệ AI</h1>
            <p>Hệ thống sàng lọc sức khỏe mạch máu võng mạc, hỗ trợ phân tích hình ảnh
              và cung cấp thông tin phục vụ quá trình đánh giá sức khỏe.</p>
          </div>
          <div className="aura-login-brand-footer">© 2026 AURA · Retinal Health Screening System</div>
        </section>
        <section className="aura-login-form-section" aria-labelledby="aura-login-title">
          <div className="aura-login-heading">
            <h2 id="aura-login-title">AURA — Đăng nhập</h2>
            <p>Chào mừng trở lại! Đăng nhập để tiếp tục sử dụng hệ thống AURA.</p>
          </div>
          <BackendSessionNotice />
          {state.status === 'anonymous' && (
            <>
              <form onSubmit={(event) => { event.preventDefault(); void login('email') }}>
                <div className="aura-login-field">
                  <label htmlFor="email">Email</label>
                  <div className="aura-login-input-wrapper">
                    <input id="email" type="email" autoComplete="email" placeholder="Nhập địa chỉ email"
                      required value={email} disabled={!canLogin}
                      onChange={(event) => setEmail(event.target.value)} />
                  </div>
                </div>
                <div className="aura-login-field">
                  <label htmlFor="password">Mật khẩu</label>
                  <div className="aura-login-input-wrapper">
                    <input id="password" className="has-toggle" type={showPassword ? 'text' : 'password'}
                      autoComplete="current-password" placeholder="Nhập mật khẩu" required
                      value={password} disabled={!canLogin} onChange={(event) => setPassword(event.target.value)} />
                    <button type="button" className="aura-login-password-toggle" disabled={!canLogin}
                      aria-label={showPassword ? 'Ẩn mật khẩu' : 'Hiện mật khẩu'} aria-pressed={showPassword}
                      onClick={() => setShowPassword((visible) => !visible)}>
                      {showPassword ? 'Ẩn' : 'Hiện'}
                    </button>
                  </div>
                </div>
                {error && <p role="alert" className="aura-login-error">{error}</p>}
                <button type="submit" className="aura-login-submit" disabled={!canLogin}>
                  {loading ? 'Đang xử lý...' : 'Đăng nhập bằng Email'}
                </button>
              </form>
              <div className="aura-login-divider">hoặc tiếp tục với</div>
              <button type="button" className="aura-login-google" onClick={() => void login('google')} disabled={!canLogin}>
                <span className="aura-login-google-icon" aria-hidden="true">G</span>
                Tiếp tục với Google
              </button>
            </>
          )}
          <p className="aura-login-note">AURA hỗ trợ sàng lọc và cung cấp thông tin tham khảo,
            không thay thế chẩn đoán của bác sĩ.</p>
        </section>
      </div>
    </main>
  )
}
