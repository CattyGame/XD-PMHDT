import { useEffect, useRef, useState, type FormEvent } from 'react'
import { doctorErrorMessage, feedbackTypes, type FeedbackType } from '../../api/doctorReview'
import styles from './doctor.module.css'

type Props = {
  disabled: boolean
  onSubmit: (type: FeedbackType, comment: string, signal: AbortSignal) => Promise<boolean>
}

export default function DoctorFeedbackForm({ disabled, onSubmit }: Props) {
  const [type, setType] = useState<FeedbackType>('SEGMENTATION')
  const [comment, setComment] = useState('')
  const [status, setStatus] = useState<'idle' | 'sending' | 'sent'>('idle')
  const [error, setError] = useState('')
  const pending = useRef<AbortController | null>(null)
  useEffect(() => () => pending.current?.abort(), [])

  async function submit(event: FormEvent) {
    event.preventDefault()
    if (disabled || pending.current || !comment.trim()) return
    const controller = new AbortController()
    pending.current = controller
    setStatus('sending')
    setError('')
    try {
      const saved = await onSubmit(type, comment, controller.signal)
      if (controller.signal.aborted) return
      setStatus(saved ? 'sent' : 'idle')
      if (saved) setComment('')
    } catch (cause) {
      if (controller.signal.aborted) return
      setError(doctorErrorMessage(cause))
      setStatus('idle')
    } finally {
      pending.current = null
    }
  }

  return <section className={styles.formPanel} aria-labelledby="doctor-feedback-heading">
    <div className={styles.sectionTitle}><h2 id="doctor-feedback-heading">Phản hồi kết quả AI</h2></div>
    <p className={styles.hint}>Phản hồi được lưu riêng theo phiên bản kết quả và đánh giá hiện tại.</p>
    <form onSubmit={event => void submit(event)}>
      <fieldset disabled={disabled || status === 'sending'}>
        <legend className={styles.hint}>Nội dung phản hồi</legend>
        <div className={styles.formFields}>
          <label>Loại phản hồi
            <select value={type} onChange={event => setType(event.target.value as FeedbackType)}>
              {Object.entries(feedbackTypes).map(([value, label]) => <option value={value} key={value}>{label}</option>)}
            </select>
          </label>
          <label>Nhận xét về kết quả AI
            <textarea value={comment} onChange={event => { setComment(event.target.value); setStatus('idle') }}
              rows={3} required maxLength={4000} />
          </label>
        </div>
        <button className={styles.primary} type="submit" disabled={!comment.trim()}>
          {status === 'sending' ? 'Đang gửi…' : 'Gửi phản hồi AI'}
        </button>
      </fieldset>
    </form>
    {error && <p role="alert" className={styles.error}>{error}</p>}
    {status === 'sent' && <p role="status" className={styles.success}>Đã lưu phản hồi cho phiên bản đang xem.</p>}
  </section>
}
