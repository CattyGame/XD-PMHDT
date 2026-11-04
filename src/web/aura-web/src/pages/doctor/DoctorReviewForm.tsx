import { useState, type FormEvent } from 'react'
import type { ReviewRequest, RiskLevel } from '../../api/doctorReview'
import styles from './doctor.module.css'

type Props = {
  baseVersion: number
  aiRisk: RiskLevel | null
  disabled?: boolean
  reviewed?: boolean
  onSubmit: (review: ReviewRequest) => Promise<void>
}
const riskNames: Record<RiskLevel, string> = { LOW: 'Thấp', MODERATE: 'Trung bình', HIGH: 'Cao' }

export default function DoctorReviewForm({ baseVersion, aiRisk, disabled, reviewed, onSubmit }: Props) {
  const [decision, setDecision] = useState<'approve' | 'correct'>('approve')
  const [correctedRisk, setCorrectedRisk] = useState<RiskLevel>('LOW')
  const [reason, setReason] = useState('')
  const [notes, setNotes] = useState('')
  const [sending, setSending] = useState(false)
  const [validation, setValidation] = useState('')

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (disabled || reviewed || sending) return
    if (decision === 'correct' && !reason.trim()) {
      setValidation('Vui lòng nhập lý do điều chỉnh mức nguy cơ.')
      return
    }
    setValidation('')
    const payload: ReviewRequest = decision === 'approve'
      ? { isApproved: true, baseVersion, doctorNotes: notes.trim() }
      : { isApproved: false, baseVersion, correctedRiskLevel: correctedRisk,
          reason: reason.trim(), doctorNotes: notes.trim() }
    setSending(true)
    try { await onSubmit(payload) } finally { setSending(false) }
  }

  return <section className={styles.formPanel} aria-labelledby="doctor-review-heading">
    <div className={styles.sectionTitle}><h2 id="doctor-review-heading">Kết luận của bác sĩ</h2>
      <small>Review version đang mở: {baseVersion}</small></div>
    {reviewed ? <p role="status" className={styles.success}>Ca chụp đã được đánh giá. Không gửi ghi đè kết luận cũ.</p> :
      <form onSubmit={(event) => void submit(event)}>
        <fieldset disabled={Boolean(disabled || sending)}>
          <legend>Xác nhận kết quả AI</legend>
          <label className={styles.option}><input type="radio" name="doctor-decision" checked={decision === 'approve'}
            onChange={() => setDecision('approve')} /> Đồng ý với kết quả AI hiện tại</label>
          <label className={styles.option}><input type="radio" name="doctor-decision" checked={decision === 'correct'}
            onChange={() => setDecision('correct')} /> Điều chỉnh mức nguy cơ</label>
          {decision === 'approve' && <p className={styles.hint}>Nguy cơ AI: {aiRisk ? riskNames[aiRisk] : 'Chưa có đánh giá nguy cơ'}. Xác nhận không tạo mức nguy cơ mới.</p>}
          {decision === 'correct' && <div className={styles.formFields}>
            <label>Mức nguy cơ sau điều chỉnh
              <select value={correctedRisk} onChange={(event) => setCorrectedRisk(event.target.value as RiskLevel)}>
                <option value="LOW">Thấp</option><option value="MODERATE">Trung bình</option><option value="HIGH">Cao</option>
              </select></label>
            <label>Lý do điều chỉnh (bắt buộc)
              <textarea value={reason} maxLength={1000} onChange={(event) => setReason(event.target.value)}
                aria-label="Lý do điều chỉnh" rows={3} aria-required="true" />
            </label>
          </div>}
          <label className={styles.notesLabel}>Ghi chú / khuyến nghị điều trị
            <textarea value={notes} maxLength={2000} onChange={(event) => setNotes(event.target.value)}
              rows={5} placeholder="Nhập nhận xét y khoa và khuyến nghị theo đánh giá của bác sĩ" />
          </label>
          {validation && <p role="alert" className={styles.error}>{validation}</p>}
          <button className={styles.primary} type="submit">{sending ? 'Đang lưu...' : 'Xác nhận và lưu đánh giá'}</button>
        </fieldset>
      </form>}
  </section>
}
