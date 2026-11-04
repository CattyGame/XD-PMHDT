import styles from './billing.module.css'

/** Chưa có API GET wallet balance; chỉ hiển thị balanceAfter của giao dịch vừa xác nhận. */
export default function CreditBalanceBadge({ balance, confirmed = false }: {
  balance: number | null
  confirmed?: boolean
}) {
  return <div className={styles.balance} role="status" aria-label="Thông tin Credit">
    <span>Credit</span>
    {confirmed && balance !== null
      ? <><strong>{balance.toLocaleString('vi-VN')}</strong><small>Số dư backend xác nhận sau giao dịch vừa thực hiện</small></>
      : <><strong>Chưa có dữ liệu</strong><small>Backend chưa cung cấp API đọc số dư ví hiện tại.</small></>}
  </div>
}
