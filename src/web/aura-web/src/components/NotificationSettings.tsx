
import { useState } from 'react'
import {
  getNotificationPermission,
  requestNotificationPermission,
  type NotificationResult,
} from '../firebase/notificationPermission'
import { registerFirebaseServiceWorker } from '../firebase/serviceWorker'

export default function NotificationSettings() {
  const [permission, setPermission] = useState<NotificationResult>(
    getNotificationPermission()
  )

  const [message, setMessage] = useState('')
  const [loading, setLoading] = useState(false)

  async function handleEnableNotification() {
    setLoading(true)
    setMessage('')

    try {
      const result = await requestNotificationPermission()
      setPermission(result)

      if (result === 'granted') {
        const registration = await registerFirebaseServiceWorker()

        if (registration) {
          setMessage(
            'Đã cấp quyền thông báo. Chưa kết nối FCM token.'
          )
        } else {
          setMessage(
            'Đã cấp quyền nhưng chưa đăng ký được Service Worker.'
          )
        }
      } else if (result === 'denied') {
        setMessage(
          'Thông báo đã bị chặn. Hãy mở cài đặt quyền của trình duyệt để cấp lại.'
        )
      } else if (result === 'default') {
        setMessage('Bạn chưa cấp quyền thông báo.')
      } else {
        setMessage('Trình duyệt không hỗ trợ thông báo.')
      }
    } catch (error) {
      console.error(error)
      setMessage('Không thể bật thông báo. Vui lòng thử lại.')
    } finally {
      setLoading(false)
    }
  }

  return (
    <section style={{
      padding: '20px',
      border: '1px solid #ddd',
      borderRadius: '12px',
      marginTop: '20px',
      maxWidth: '480px',
    }}>
      <h3>Cài đặt thông báo AURA</h3>

      <p>
        Trạng thái quyền: <strong>{permission}</strong>
      </p>

      <button
        type="button"
        onClick={handleEnableNotification}
        disabled={
          loading ||
          permission === 'denied' ||
          permission === 'unsupported'
        }
      >
        {loading ? 'Đang xử lý...' : 'Bật thông báo'}
      </button>

      {message && <p role="status">{message}</p>}

      <p style={{ fontSize: '13px', color: '#666' }}>
        Thông báo không hiển thị dữ liệu sức khỏe cá nhân.
      </p>
    </section>
  )
}
