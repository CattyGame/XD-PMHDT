
import { deleteToken } from 'firebase/messaging'
import { getFirebaseMessaging } from './config'
import { obtainFcmToken } from './messaging'

let currentToken: string | null = null

// Ghi nhớ token trong phiên chạy hiện tại.
// Không lưu token vào localStorage.
export async function refreshFcmToken() {
  const result = await obtainFcmToken()

  if (result.status !== 'ready') {
    return result
  }

  const changed = currentToken !== result.token
  currentToken = result.token

  return {
    status: 'ready' as const,
    changed,
  }
}

// Gọi khi người dùng đăng xuất.
// Phần gọi API hủy đăng ký token sẽ bổ sung khi có contract.
export async function clearFcmOnLogout() {
  const messaging = await getFirebaseMessaging()

  if (!messaging) {
    currentToken = null
    return { status: 'not-configured' as const }
  }

  try {
    await deleteToken(messaging)
    currentToken = null

    return { status: 'deleted' as const }
  } catch {
    return { status: 'error' as const }
  }
}
