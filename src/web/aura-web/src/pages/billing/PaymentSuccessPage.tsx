import { useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import type { BackendContext } from '../../api/backendContext'
import {
  billingErrorMessage, getBillingOrder, validBillingId, type BillingOrder,
} from '../../api/billing'
import { useBackendSession } from '../../auth/backendSession'
import BackendSessionNotice from '../../auth/BackendSessionNotice'
import CreditBalanceBadge from './CreditBalanceBadge'
import styles from './billing.module.css'

function SuccessView({ context, orderId }: {
  context: BackendContext; orderId: string
}) {
  const [order, setOrder] = useState<BillingOrder | null>(null)
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(true)
  const [attempt, setAttempt] = useState(0)

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
        <CreditBalanceBadge context={context} owner={{ type: order.billingOwnerType, id: order.billingOwnerId }} />
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
  if (state.status !== 'ready') return <BackendSessionNotice />
  if (!validBillingId(orderId)) return <p role="alert">Mã đơn không hợp lệ.</p>
  return <SuccessView key={`${state.context.firebaseUid}:${state.context.accountId}:${orderId}`}
    context={state.context} orderId={orderId} />
}
