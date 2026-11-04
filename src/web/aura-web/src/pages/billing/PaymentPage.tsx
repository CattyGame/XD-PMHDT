import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import type { BackendContext } from '../../api/backendContext'
import { billingErrorMessage, getBillingOrder, paySandbox, validBillingId, type BillingOrder } from '../../api/billing'
import { useBackendSession } from '../../auth/backendSession'
import BackendSessionNotice from '../../auth/BackendSessionNotice'
import styles from './billing.module.css'

type View = { kind: 'loading' | 'error'; message?: string } | { kind: 'ready'; order: BillingOrder }

function Checkout({ orderId, context }: { orderId: string; context: BackendContext }) {
  const navigate = useNavigate()
  const [view, setView] = useState<View>({ kind: 'loading' })
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [unknown, setUnknown] = useState(false)
  const [attempt, setAttempt] = useState(0)
  const inFlight = useRef(false)

  useEffect(() => {
    const controller = new AbortController()
    void getBillingOrder(context, orderId, controller.signal).then(order => {
      if (!controller.signal.aborted) setView({ kind: 'ready', order })
    }).catch(cause => {
      if (!controller.signal.aborted) setView({ kind: 'error', message: billingErrorMessage(cause) })
    })
    return () => controller.abort()
  }, [context, orderId, attempt])

  const pay = async (simulateFailure: boolean) => {
    if (inFlight.current || view.kind !== 'ready' || view.order.status !== 'PENDING' || unknown) return
    inFlight.current = true
    setBusy(true)
    setError('')
    try {
      const result = await paySandbox(context, orderId, simulateFailure)
      if (result.status === 'COMPLETED') {
        // Chỉ chuyển thành công sau phản hồi backend; không tự cộng credit từ frontend.
        navigate(`/billing/success/${orderId}`, { state: { payment: result } })
      } else {
        setError(result.status === 'CANCELLED'
          ? 'Thanh toán Sandbox thất bại. Đơn hàng đã hủy, không cộng Credit.'
          : `Trạng thái thanh toán từ backend: ${result.status}. Chưa ghi nhận Credit.`)
        setAttempt(n => n + 1)
      }
    } catch (cause) {
      setUnknown(true)
      setError(`${billingErrorMessage(cause)} Hãy đọc lại đơn trước khi quyết định thao tác tiếp.`)
    } finally {
      inFlight.current = false
      setBusy(false)
    }
  }

  const reload = () => {
    if (inFlight.current) return
    setView({ kind: 'loading' })
    setUnknown(false)
    setError('')
    setAttempt(n => n + 1)
  }

  return <main className={styles.page}>
    <nav><Link to="/billing/packages">Gói khám</Link> / Thanh toán Sandbox</nav>
    <h1>Thanh toán Sandbox</h1>
    {view.kind === 'loading' && <p role="status">Đang đọc trạng thái đơn hàng từ Billing API...</p>}
    {view.kind === 'error' && <section className={styles.panel} role="alert"><p>{view.message}</p>
      <button onClick={reload}>Thử đọc lại đơn</button></section>}
    {view.kind === 'ready' && <section className={styles.panel}>
      <h2>{view.order.packageNameSnapshot}</h2>
      <p>Mã đơn hàng: <code>{view.order.id}</code></p>
      <p>Giá: <strong>{view.order.priceAmountSnapshot.toLocaleString('vi-VN')} {view.order.currencyCodeSnapshot}</strong></p>
      <p>Số lượng: <strong>{view.order.creditsSnapshot} Credit</strong></p>
      <p>Trạng thái backend: <strong>{view.order.status}</strong></p>
      <p>Môi trường: {view.order.environment}</p>
      {view.order.status === 'PENDING' && <div className={styles.actions}>
        <button type="button" onClick={() => void pay(false)} disabled={busy || unknown || view.order.environment !== 'TEST'}>
          {busy ? 'Đang chờ xác nhận...' : 'Thanh toán thành công (Sandbox)'}
        </button>
        <button type="button" className={styles.secondary} onClick={() => void pay(true)}
          disabled={busy || unknown || view.order.environment !== 'TEST'}>Mô phỏng thất bại</button>
      </div>}
      {view.order.status === 'COMPLETED' && <Link to={`/billing/success/${orderId}`}>Xem xác nhận thanh toán</Link>}
      {view.order.status === 'CANCELLED' && <p role="status">Đơn đã hủy. Muốn thử lại cần chủ động tạo đơn mới.</p>}
      {view.order.environment !== 'TEST' && <p className={styles.warning}>Sandbox chỉ được dùng với đơn hàng môi trường TEST.</p>}
      {unknown && <p className={styles.warning} role="alert">Phản hồi thanh toán không rõ ràng. Nút thanh toán đã bị khóa để tránh gửi lặp.</p>}
      <button type="button" className={styles.secondary} onClick={reload} disabled={busy}>Đọc lại trạng thái từ backend (GET)</button>
    </section>}
    {error && <p role="alert" className={styles.error}>{error}</p>}
  </main>
}

export default function PaymentPage() {
  const { state } = useBackendSession()
  const { orderId } = useParams<{ orderId: string }>()
  if (state.status !== 'ready') return <BackendSessionNotice />
  if (!validBillingId(orderId)) return <p role="alert">Mã đơn hàng không hợp lệ.</p>
  return <Checkout key={`${state.context.firebaseUid}:${orderId}`} orderId={orderId} context={state.context} />
}
