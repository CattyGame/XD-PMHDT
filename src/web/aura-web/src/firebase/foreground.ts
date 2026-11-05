
import { listenForegroundMessages } from './messaging'

export async function startForegroundNotifications(
  onNotification: (message: string) => void
) {
  const seen = new Set<string>()
  return listenForegroundMessages((payload) => {
    const id = payload.data?.notificationId
    if (id) {
      if (seen.has(id)) return
      seen.add(id)
      if (seen.size > 200) seen.delete(seen.values().next().value!)
    }
    // Không hiển thị payload chứa dữ liệu bệnh nhân.
    onNotification(
      'Bạn có thông báo mới từ AURA. Vui lòng mở hệ thống để xem.'
    )
  })
}
