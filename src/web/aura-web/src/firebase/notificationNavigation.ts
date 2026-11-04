import type { MessagePayload } from 'firebase/messaging'

const analysisRoute = /^\/analyses\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/i
const allowedTitles = new Set([
  'Phân tích ảnh đáy mắt hoàn tất',
  'Phân tích ảnh đáy mắt không thành công',
  'Đợt xử lý ảnh đã kết thúc',
  'Thanh toán thành công',
])
const allowedBodies = new Set([
  'Kết quả phân tích mắt của bạn đã có! Bấm để xem chi tiết.',
  'Không thể phân tích ảnh đáy mắt do chất lượng ảnh không đạt tiêu chuẩn.',
  'Trạng thái đợt xử lý đã được cập nhật. Bấm để xem chi tiết.',
  'Giao dịch thanh toán đã được xác nhận. Bấm để xem gói cước.',
])

/** Chỉ mở route AURA nội bộ đúng dạng, không chấp nhận URL tuyệt đối hoặc query. */
export function safeAnalysisPath(path: unknown, analysisId?: unknown): string | null {
  if (typeof path !== 'string') return null
  const match = analysisRoute.exec(path)
  if (!match) return null
  if (analysisId !== undefined && (typeof analysisId !== 'string' ||
    analysisId.toLowerCase() !== match[1].toLowerCase())) return null
  return `/analyses/${match[1].toLowerCase()}`
}

export type SafeForegroundToast = Readonly<{
  id: string
  title: string
  body: string
  targetPath: string | null
}>

/** Whitelist chuỗi công khai từ M3; không render payload tùy ý chứa thông tin bệnh nhân. */
export function safeForegroundToast(payload: Partial<Pick<MessagePayload, 'messageId' | 'notification' | 'data'>>): SafeForegroundToast {
  const proposedTitle = payload.notification?.title
  const proposedBody = payload.notification?.body
  return {
    id: payload.messageId || payload.data?.eventId || '',
    title: proposedTitle && allowedTitles.has(proposedTitle)
      ? proposedTitle : 'AURA - Có thông báo mới',
    body: proposedBody && allowedBodies.has(proposedBody)
      ? proposedBody : 'Mở AURA để xem chi tiết.',
    targetPath: safeAnalysisPath(payload.data?.targetPath, payload.data?.analysisId),
  }
}
