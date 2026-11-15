import { useEffect, useState } from 'react'
import type { BackendContext } from '../../api/backendContext'
import { billingErrorMessage, getBillingWallet, type BillingOwner, type BillingWallet } from '../../api/billing'
import styles from './billing.module.css'

export default function CreditBalanceBadge({ context, owner }: { context: BackendContext; owner: BillingOwner }) {
  return <WalletBalance key={`${context.firebaseUid}:${context.accountId}:${owner.type}:${owner.id}`} context={context} owner={owner} />
}

function WalletBalance({ context, owner }: { context: BackendContext; owner: BillingOwner }) {
  const { type, id } = owner
  const [wallet, setWallet] = useState<BillingWallet | null>(null)
  const [error, setError] = useState('')
  const [attempt, setAttempt] = useState(0)
  useEffect(() => {
    const controller = new AbortController()
    void getBillingWallet(context, { type, id }, controller.signal).then(value => {
      if (!controller.signal.aborted) { setWallet(value); setError('') }
    }).catch(cause => {
      if (!controller.signal.aborted) { setWallet(null); setError(billingErrorMessage(cause)) }
    })
    return () => controller.abort()
  }, [context, type, id, attempt])
  return <div className={styles.balance} role="status" aria-label="Thông tin Credit">
    <span>Credit</span>
    {wallet ? <><strong>{wallet.balance.toLocaleString('vi-VN')}</strong><small>Số dư ví hiện tại · {wallet.environment} · Backend xác nhận</small></>
      : <><strong>Chưa có dữ liệu</strong><small>{error || 'Đang đọc số dư từ backend...'}</small></>}
    <button type="button" onClick={() => { setWallet(null); setAttempt(n => n + 1) }}>Cập nhật Credit</button>
  </div>
}
