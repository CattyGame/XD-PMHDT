import { useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { getFirebaseAuth } from '../firebase/config'
import { useBackendSession } from '../auth/backendSession'
import BackendSessionNotice from '../auth/BackendSessionNotice'
import { validBatchId } from '../api/batchProgress'

type Order = { id: string; status: string; packageNameSnapshot: string; priceAmountSnapshot: number; currencyCodeSnapshot: string; creditsSnapshot: number }

export default function BillingOrderPage() {
  const { orderId = '' } = useParams()
  const { state } = useBackendSession()
  if (state.status !== 'ready') return <BackendSessionNotice />
  return <OrderDetails key={`${state.context.firebaseUid}:${orderId}`} uid={state.context.firebaseUid} orderId={orderId} />
}

function OrderDetails({ uid, orderId }: { uid: string; orderId: string }) {
  const [order, setOrder] = useState<Order | null>(null)
  const [error, setError] = useState('')
  useEffect(() => {
    if (!uid) return
    const abort = new AbortController()
    const user = getFirebaseAuth()?.currentUser
    void (async () => {
      if (!validBatchId(orderId) || !user || user.uid !== uid) throw new Error('Phiên đăng nhập hoặc mã đơn hàng không hợp lệ.')
      const token = await user.getIdToken()
      if (getFirebaseAuth()?.currentUser !== user || abort.signal.aborted) return
      const response = await fetch(`/api/v1/billing/orders/${orderId}`, {
        headers: { Authorization: `Bearer ${token}` }, signal: abort.signal,
        credentials: 'omit', redirect: 'error', cache: 'no-store',
      })
      if (!response.ok) throw new Error(response.status === 403 || response.status === 404 ? 'Bạn không có quyền xem đơn hàng này.' : 'Không thể tải đơn hàng.')
      const value: unknown = await response.json()
      if (!value || typeof value !== 'object') throw new Error('Phản hồi đơn hàng không hợp lệ.')
      const o = value as Record<string, unknown>
      if (o.id !== orderId || !['PENDING', 'COMPLETED', 'CANCELLED'].includes(String(o.status)) ||
        typeof o.packageNameSnapshot !== 'string' || typeof o.priceAmountSnapshot !== 'number' ||
        !Number.isFinite(o.priceAmountSnapshot) || typeof o.currencyCodeSnapshot !== 'string' ||
        !Number.isSafeInteger(o.creditsSnapshot)) throw new Error('Phản hồi đơn hàng không hợp lệ.')
      if (!abort.signal.aborted && getFirebaseAuth()?.currentUser === user) setOrder(o as Order)
    })().catch((e: unknown) => { if (!abort.signal.aborted) setError(e instanceof Error ? e.message : 'Không thể tải đơn hàng.') })
    return () => abort.abort()
  }, [orderId, uid])
  return <main>
    <h1>Đơn hàng</h1>
    {error ? <p role="alert">{error}</p> : order ? <dl>
      <dt>Mã đơn</dt><dd>{order.id}</dd>
      <dt>Trạng thái</dt><dd>{order.status}</dd>
      <dt>Gói dịch vụ</dt><dd>{order.packageNameSnapshot}</dd>
      <dt>Giá</dt><dd>{order.priceAmountSnapshot} {order.currencyCodeSnapshot}</dd>
      <dt>Tín dụng</dt><dd>{order.creditsSnapshot}</dd>
    </dl> : <p role="status">Đang tải…</p>}
    <Link to="/notifications">Về thông báo</Link>
  </main>
}
