
import { listenForegroundMessages } from './messaging'

export async function startForegroundNotifications(
  onNotification: (message: string) => void
) {
  return listenForegroundMessages(() => {
    // Không hiển thị payload chứa dữ liệu bệnh nhân.
    onNotification(
      'Bạn có thông báo mới từ AURA. Vui lòng mở hệ thống để xem.'
    )
  })
}
