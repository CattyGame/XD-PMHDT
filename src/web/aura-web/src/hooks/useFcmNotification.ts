import { useCallback, useEffect, useRef, useState } from 'react'
import { isMockEnabled } from '../api/client'
import { useBackendSession } from '../auth/backendSession'
import { listenForegroundMessages } from '../firebase/messaging'
import {
  getNotificationPermission, requestNotificationPermission,
  type NotificationResult,
} from '../firebase/notificationPermission'
import { refreshFcmToken } from '../firebase/tokenLifecycle'
import { safeForegroundToast, type SafeForegroundToast } from '../firebase/notificationNavigation'

function currentMillis(): number { return Date.now() }

const guidance = (status: NotificationResult): string => {
  if (status === 'denied') return 'Quyền thông báo đang bị chặn. Mở cài đặt quyền trang trong trình duyệt để cấp lại.'
  if (status === 'unsupported') return 'Trình duyệt không hỗ trợ Notification hoặc Service Worker.'
  if (status === 'default') return 'Nhấn Bật thông báo để cho phép trình duyệt gửi thông báo.'
  return 'Quyền thông báo đã được cấp.'
}

function playGentleSound() {
  try {
    const AudioContextClass = window.AudioContext
    if (!AudioContextClass) return
    const context = new AudioContextClass()
    if (context.state !== 'running') { void context.close(); return }
    const oscillator = context.createOscillator()
    const gain = context.createGain()
    oscillator.type = 'sine'
    oscillator.frequency.value = 660
    gain.gain.setValueAtTime(0.025, context.currentTime)
    gain.gain.exponentialRampToValueAtTime(0.001, context.currentTime + 0.12)
    oscillator.connect(gain); gain.connect(context.destination)
    oscillator.start(); oscillator.stop(context.currentTime + 0.12)
    oscillator.onended = () => { void context.close() }
  } catch { /* Browser có thể chặn âm thanh nếu chưa có tương tác của người dùng. */ }
}

/** Tái sử dụng SDK, permission, service worker và device API đang có trong AURA. */
export function useFcmNotification() {
  const { state } = useBackendSession()
  const uid = state.status === 'ready' ? state.context.firebaseUid : null
  const [permission, setPermission] = useState<NotificationResult>(getNotificationPermission)
  const [enabled, setEnabled] = useState(false)
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState('')
  const [toast, setToast] = useState<SafeForegroundToast | null>(null)
  const running = useRef(false)
  const enabledRef = useRef(false)
  const sessionUid = useRef(uid)
  const recentlyReceived = useRef(new Set<string>())
  const lastSync = useRef(0)
  useEffect(() => { sessionUid.current = uid }, [uid])

  const enable = useCallback(async () => {
    if (running.current || !uid || isMockEnabled) return
    if (!window.isSecureContext || !('serviceWorker' in navigator)) {
      setPermission('unsupported'); setMessage('FCM chỉ hoạt động trên HTTPS hoặc localhost có Service Worker.'); return
    }
    running.current = true
    setBusy(true)
    setMessage('')
    try {
      // requestPermission bắt buộc nằm trong hành động click, không gọi trong useEffect.
      const next = await requestNotificationPermission()
      setPermission(next)
      if (next !== 'granted') { setMessage(guidance(next)); return }
      const result = await refreshFcmToken(() => sessionUid.current === uid)
      if (sessionUid.current !== uid) return
      if (result.status !== 'ready') {
        setMessage(result.status === 'not-configured' ? 'Thiếu cấu hình Firebase/VAPID.' :
          result.status === 'unsupported' ? 'Trình duyệt không hỗ trợ FCM.' :
          'Chưa đăng ký được thiết bị. Kiểm tra kết nối và thử lại.')
        return
      }
      enabledRef.current = true
      lastSync.current = currentMillis()
      setEnabled(true)
      setMessage('Đã đăng ký FCM Token với Notification API. Chưa xác nhận đã nhận push thật.')
    } catch {
      setMessage('Không đăng ký được FCM Token với backend. Kiểm tra phiên và kết nối.')
    } finally { running.current = false; setBusy(false) }
  }, [uid])

  useEffect(() => {
    if (!uid) {
      enabledRef.current = false
      recentlyReceived.current.clear()
      return
    }
    if (isMockEnabled || !window.isSecureContext || getNotificationPermission() !== 'granted') return
    let disposed = false
    let stop: (() => void) | null = null
    void listenForegroundMessages(payload => {
      if (disposed || sessionUid.current !== uid || document.visibilityState === 'hidden') return
      const item = safeForegroundToast(payload)
      const key = item.id || `${item.title}:${item.targetPath ?? ''}`
      if (recentlyReceived.current.has(key)) return
      recentlyReceived.current.add(key)
      if (recentlyReceived.current.size > 60) recentlyReceived.current.clear()
      setToast(item)
      playGentleSound()
    }).then(unsubscribe => {
      if (disposed) unsubscribe?.()
      else stop = unsubscribe
    }).catch(() => { /* Không đánh dấu đã nhận khi listener lỗi. */ })
    return () => { disposed = true; stop?.(); setToast(null) }
  }, [uid, permission])

  useEffect(() => {
    if (!uid) return
    const onFocus = () => {
      const next = getNotificationPermission()
      setPermission(next)
      // Đồng bộ lại token khi focus, nhưng giới hạn để không đăng ký trùng liên tục.
      if (!enabledRef.current || next !== 'granted' || running.current ||
          currentMillis() - lastSync.current < 120_000) return
      running.current = true
      void refreshFcmToken(() => sessionUid.current === uid).then(result => {
        if (sessionUid.current === uid && result.status === 'ready') lastSync.current = currentMillis()
      }).catch(() => {
        if (sessionUid.current === uid) setMessage('Đồng bộ token chưa thành công. Bấm Bật thông báo để thử lại.')
      }).finally(() => { running.current = false })
    }
    window.addEventListener('focus', onFocus)
    return () => window.removeEventListener('focus', onFocus)
  }, [uid])

  return {
    permission, enabled, busy, message, toast,
    enable,
    closeToast: useCallback(() => setToast(null), []),
    guidance: guidance(permission),
  }
}
