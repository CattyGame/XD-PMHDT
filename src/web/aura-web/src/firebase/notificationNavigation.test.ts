import { describe, expect, it } from 'vitest'
import { safeAnalysisPath, safeForegroundToast } from './notificationNavigation'

const id = '11111111-1111-4111-8111-111111111111'
describe('SCRUM-355 — FCM điều hướng và quyền riêng tư', () => {
  it('AC3: chỉ nhận route phân tích hợp lệ, cùng analysisId', () => {
    expect(safeAnalysisPath(`/analyses/${id}`, id)).toBe(`/analyses/${id}`)
    expect(safeAnalysisPath(`/analyses/${id}`, '22222222-2222-4222-8222-222222222222')).toBeNull()
  })
  it.each(['https://evil.example/analyses/111', '//evil.example', '/admin',
    `/analyses/${id}?token=secret`, `/analyses/${id}/..`,
    `javascript:alert(1)`, '/analyses/abc'])(`không cho mở URL không hợp lệ: %s`, path => {
    expect(safeAnalysisPath(path)).toBeNull()
  })
  it('chỉ dùng whitelist title/body để tránh rò rỉ PHI', () => {
    const item = safeForegroundToast({ messageId: 'm1',
      notification: { title: 'Tên bệnh nhân nhạy cảm', body: 'Kết quả chẩn đoán riêng tư' },
      data: { targetPath: `/analyses/${id}`, analysisId: id } })
    expect(item.title).toBe('AURA - Có thông báo mới')
    expect(item.body).toBe('Mở AURA để xem chi tiết.')
    expect(item.targetPath).toBe(`/analyses/${id}`)
  })
  it('dùng tiêu đề và nội dung trung tính đã được M3 công bố', () => {
    const item = safeForegroundToast({ notification: {
      title: 'Phân tích ảnh đáy mắt hoàn tất',
      body: 'Kết quả phân tích mắt của bạn đã có! Bấm để xem chi tiết.',
    }, data: { targetPath: `/analyses/${id}`, analysisId: id } })
    expect(item.title).toBe('Phân tích ảnh đáy mắt hoàn tất')
    expect(item.body).toContain('Kết quả phân tích')
  })
})
