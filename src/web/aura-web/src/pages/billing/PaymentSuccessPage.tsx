import { useEffect, useState } from 'react'
import { Link, useLocation, useParams } from 'react-router-dom'
import type { BackendContext } from '../../api/backendContext'
import {
  billingErrorMessage, getBillingOrder, parseSandboxPayment, validBillingId,
  type BillingOrder, type SandboxPayment,
} from '../../api/billing'
import { useBackendSession } from '../../auth/backendSession'
import BackendSessionNotice from '../../auth/BackendSessionNotice'
import CreditBalanceBadge from './CreditBalanceBadge'
import styles from './billing.module.css'

function SuccessView({ context, orderId, evidence }: {
  context: BackendContext; orderId: string; evidence: unknown
}) {
  const [order, setOrder] = useState<BillingOrder | null>(null)
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(true)
  const [attempt, setAttempt] = useState(0)
  // Chỉ là biên nhận do POST backend trả về trong navigation hiện tại. Không tự suy ra số dư từ GET order.
  let payment: SandboxPayment | null = null
  try { if (evidence != null) payment = parseSandboxPayment(evidence, orderId) } catch { payment = null }

  useEffect(() => {
    const controller = new AbortController()
    void getBillingOrder(context, orderId, controller.signal).then(data => {
      if (!controller.signal.aborted) { setOrder(data); setError('') }
    }).catch(cause => {
      if (!controller.signal.aborted) { setOrder(null); setError(billingErrorMessage(cause)) }
    }).finally(() => { if (!controller.signal.aborted) setLoading(false) })
    return () => controller.abort()
  }, [context, orderId, attempt])

  return <main className={styles.page}>
    <nav><Link to="/billing/packages">Gói khám</Link> / Kết quả thanh toán</nav>
    <h1>Kết quả thanh toán Sandbox</h1>
    {loading && <p role="status">Đang xác minh trạng thái đơn hàng từ backend...</p>}
    {error && <p role="alert" className={styles.error}>{error}</p>}
    {!loading && order && <section className={styles.panel}>
      {order.status === 'COMPLETED' ? <>
        <h2>Thanh toán đã được backend xác nhận</h2>
        <p>Đơn: <code>{order.id}</code></p>
        <p>Gói: {order.packageNameSnapshot} — {order.creditsSnapshot} Credit</p>
        <CreditBalanceBadge balance={payment?.status === 'COMPLETED' ? payment.balanceAfter : null}
          confirmed={payment?.status === 'COMPLETED'} />
        <p>{payment?.status === 'COMPLETED'
          ? `Credit vừa ghi nhận theo phản hồi backend: ${payment.creditsAdded}${payment.replayed ? ' (giao dịch đã xử lý trước đó)' : ''}.`
          : 'Đơn đã hoàn thành. Sau khi tải lại trang, cần API ví để xem số dư hiện tại; không tự tính từ đơn hàng.'}</p>
      </> : <>
        <h2>Chưa ghi nhận thanh toán thành công</h2>
        <p>Trạng thái đơn hàng: <strong>{order.status}</strong>. Credit chưa được hiển thị là đã cộng.</p>
        {order.status === 'PENDING' && <Link to={`/billing/pay/${orderId}`}>Quay lại trang thanh toán</Link>}
      </>}
    </section>}
    <div className={styles.actions}>
      <button className={styles.secondary} disabled={loading} onClick={() => {
        setLoading(true); setAttempt(n => n + 1)
      }}>Xác minh lại đơn hàng (GET)</button>
      <Link to="/billing/packages">Về danh sách gói</Link>
    </div>
  </main>
}

export default function PaymentSuccessPage() {
  const { state } = useBackendSession()
  const { orderId } = useParams<{ orderId: string }>()
  const location = useLocation()
  if (state.status !== 'ready') return <BackendSessionNotice />
  if (!validBillingId(orderId)) return <p role="alert">Mã đơn không hợp lệ.</p>
  const stateValue: unknown = location.state
  const evidence = stateValue && typeof stateValue === 'object' && 'payment' in stateValue
    ? stateValue.payment : null
  return <SuccessView key={`${state.context.firebaseUid}:${orderId}`}
    context={state.context} orderId={orderId} evidence={evidence} />
}
