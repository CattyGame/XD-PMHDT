import { useState } from 'react'
import { useLocation, useNavigate } from 'react-router-dom'
import { useBackendSession } from '../auth/backendSession'
import { useFcmNotification } from '../hooks/useFcmNotification'
import styles from './fcmToast.module.css'
import NotificationPushTestPanel from './NotificationPushTestPanel'

/** Mounted once near ApplicationRoutes. Never display raw PHI from FCM data. */
export default function FcmToastHost() {
  const { state } = useBackendSession()
  if (state.status !== 'ready') return null
  return <FcmWorkspace key={`${state.context.firebaseUid}:${state.context.accountId}`} />
}

function FcmWorkspace() {
  const navigate = useNavigate()
  const location = useLocation()
  const { state } = useBackendSession()
  const [testBusy, setTestBusy] = useState(false)
  const { permission, enabled, busy, message, toast, enable, closeToast, guidance } = useFcmNotification()
  const showSettings = location.pathname.startsWith('/billing') || location.pathname === '/notifications'
  return <>
    {toast && <aside className={styles.toast} role="status" aria-label="Thông báo mới từ AURA">
      <div className={styles.heading}><strong>{toast.title}</strong>
        <button type="button" aria-label="Đóng thông báo" onClick={closeToast}>×</button>
      </div>
      <p>{toast.body}</p>
      {toast.targetPath && <button type="button" className={styles.link} onClick={() => {
        if (toast.targetPath) navigate(toast.targetPath); closeToast()
      }}>Xem kết quả</button>}
    </aside>}
    {showSettings && <section className={styles.settings} aria-label="Thông báo Web Push">
      <span aria-hidden="true">♧</span>
      <div><strong>Thông báo Web Push</strong><small>{enabled ? 'Đã đồng bộ FCM với backend' : guidance}</small></div>
      <button type="button" disabled={busy || permission === 'denied' || permission === 'unsupported'}
        onClick={() => void enable()}>{busy ? 'Đang đăng ký...' : enabled ? 'Đồng bộ token' : 'Bật thông báo'}</button>
      {message && <span role="status" className={styles.message}>{message}</span>}
      {import.meta.env.DEV && permission === 'granted' && <details><summary>Kiểm thử push (Development)</summary><NotificationPushTestPanel
        userId={state.status === 'ready' ? state.context.firebaseUid : null}
        parentBusy={busy || testBusy} onBusyChange={setTestBusy} /></details>}
    </section>}
  </>
}
