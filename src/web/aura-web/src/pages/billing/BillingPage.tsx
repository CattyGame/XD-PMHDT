import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import type { BackendContext } from '../../api/backendContext'
import { getUploadClinics, type UploadClinic } from '../../api/clinicUpload'
import {
  billingErrorMessage, createBillingOrder, getBillingPackages,
  type BillingOwner, type BillingPackage,
} from '../../api/billing'
import { useBackendSession } from '../../auth/backendSession'
import BackendSessionNotice from '../../auth/BackendSessionNotice'
import CreditBalanceBadge from './CreditBalanceBadge'
import styles from './billing.module.css'

type CatalogState = { kind: 'loading' | 'error' | 'empty'; message?: string } |
  { kind: 'ready'; packages: readonly BillingPackage[]; source: string }

type OrderIntent = { identity: string; key: string }
function newOrderKey(): string { return crypto.randomUUID() }

function BillingWorkspace({ context }: { context: BackendContext }) {
  const navigate = useNavigate()
  const [catalog, setCatalog] = useState<CatalogState>({ kind: 'loading' })
  const [clinics, setClinics] = useState<readonly UploadClinic[]>([])
  const [clinicError, setClinicError] = useState('')
  const [ownerType, setOwnerType] = useState<'ACCOUNT' | 'CLINIC'>('ACCOUNT')
  const [clinicId, setClinicId] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [retry, setRetry] = useState(0)
  const inFlight = useRef(false)
  // Giữ cùng idempotency key nếu POST thất bại/không xác định, không gửi lại tự động.
  const pendingIntent = useRef<OrderIntent | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    void getBillingPackages(controller.signal).then(data => {
      if (!controller.signal.aborted) setCatalog(data.data.length
        ? { kind: 'ready', packages: data.data, source: data.source }
        : { kind: 'empty', message: 'Billing API chưa có gói cước.' })
    }).catch(cause => {
      if (!controller.signal.aborted) setCatalog({ kind: 'error', message: billingErrorMessage(cause) })
    })
    return () => controller.abort()
  }, [retry])

  useEffect(() => {
    if (!context.roles.includes('CLINIC') && !context.roles.includes('DOCTOR')) return
    const controller = new AbortController()
    void getUploadClinics(context, controller.signal).then(items => {
      if (!controller.signal.aborted) {
        // Backend Profile đã xác nhận membership; không nhận clinicId tùy ý từ URL.
        const available = items.filter(item => item.role === 'CLINIC_ADMIN')
        setClinics(available)
        setClinicId(available[0]?.clinicId ?? '')
      }
    }).catch(() => {
      if (!controller.signal.aborted) setClinicError('Chưa đọc được danh sách phòng khám được cấp quyền thanh toán.')
    })
    return () => controller.abort()
  }, [context])

  const changeOwner = (type: 'ACCOUNT' | 'CLINIC') => {
    if (inFlight.current) return
    setOwnerType(type)
    pendingIntent.current = null
    setError('')
  }

  const buy = async (item: BillingPackage) => {
    if (inFlight.current) return
    const owner: BillingOwner = ownerType === 'ACCOUNT'
      ? { type: 'ACCOUNT', id: context.accountId }
      : { type: 'CLINIC', id: clinicId }
    if (!owner.id) { setError('Chưa chọn phòng khám được cấp quyền.'); return }
    const identity = `${owner.type}:${owner.id}:${item.id}`
    if (pendingIntent.current?.identity !== identity) {
      pendingIntent.current = { identity, key: newOrderKey() }
    }
    inFlight.current = true
    setBusy(true)
    setError('')
    try {
      const order = await createBillingOrder(context, owner, item.id, pendingIntent.current.key)
      pendingIntent.current = null
      navigate(`/billing/pay/${order.orderId}`)
    } catch (cause) {
      // Không sinh key mới sau timeout: người dùng có thể bấm lại với CÙNG key.
      setError(`${billingErrorMessage(cause)} Nếu muốn thử lại, hãy bấm lại cùng gói cước; hệ thống dùng cùng Idempotency-Key.`)
    } finally {
      inFlight.current = false
      setBusy(false)
    }
  }

  return <main className={styles.page}>
    <nav aria-label="Điều hướng"><Link to="/">Trang chủ</Link> / Gói khám</nav>
    <div className={styles.heading}>
      <div><h1>Mua gói khám</h1><p>Chọn gói và thực hiện giao dịch trong môi trường Sandbox.</p></div>
      <CreditBalanceBadge balance={null} />
    </div>
    <p className={styles.warning} role="note">
      Bảng giá do Billing API trả về thuộc catalog demo/legacy, chưa phải giá kinh doanh được phê duyệt.
      Đơn vị tiền tệ và số credit cuối cùng được xác nhận khi tạo đơn hàng.
    </p>
    <section className={styles.panel} aria-label="Chọn ví thanh toán">
      <h2>Đối tượng thanh toán</h2>
      <div className={styles.actions}>
        <label><input type="radio" name="billing-owner" checked={ownerType === 'ACCOUNT'}
          onChange={() => changeOwner('ACCOUNT')} disabled={busy} /> Ví cá nhân</label>
        {clinics.length > 0 && <label><input type="radio" name="billing-owner" checked={ownerType === 'CLINIC'}
          onChange={() => changeOwner('CLINIC')} disabled={busy} /> Ví tổ chức</label>}
      </div>
      {ownerType === 'CLINIC' && <label>Phòng khám được cấp quyền
        <select value={clinicId} disabled={busy} onChange={event => {
          setClinicId(event.target.value); pendingIntent.current = null
        }}>{clinics.map(item => <option value={item.clinicId} key={item.clinicId}>{item.clinicName}</option>)}</select>
      </label>}
      {clinicError && <p role="status">{clinicError}</p>}
      {clinics.length === 0 && <small>Gói có thể mua cho tổ chức khi Profile API xác nhận quyền quản trị phòng khám.</small>}
    </section>
    {catalog.kind === 'loading' && <p role="status">Đang tải bảng giá từ Billing API...</p>}
    {catalog.kind === 'empty' && <p role="status">{catalog.message}</p>}
    {catalog.kind === 'error' && <section role="alert" className={styles.panel}>
      <p>{catalog.message}</p><button onClick={() => { setCatalog({ kind: 'loading' }); setRetry(n => n + 1) }}>Thử tải lại</button>
    </section>}
    {catalog.kind === 'ready' && <section aria-label="Danh sách gói khám" className={styles.packageGrid}>
      {catalog.packages.map(item => <article className={styles.packageCard} key={item.id}>
        <span className={styles.eyebrow}>GÓI KHÁM • ID {item.id}</span>
        <h2>{item.name}</h2>
        <p className={styles.credits}><strong>{item.credits.toLocaleString('vi-VN')}</strong> Credit</p>
        <p className={styles.price}>{item.price.toLocaleString('vi-VN')} <small>(đơn vị theo báo giá backend)</small></p>
        <button disabled={busy || (ownerType === 'CLINIC' && !clinicId)}
          onClick={() => void buy(item)}>{busy ? 'Đang xử lý...' : 'Mua gói'}</button>
      </article>)}
    </section>}
    {error && <p className={styles.error} role="alert">{error}</p>}
    <p className={styles.muted}>Không tự tạo đơn khi tải lại trang; chỉ tạo đơn sau khi bạn nhấn Mua gói.</p>
  </main>
}

export default function BillingPage() {
  const { state } = useBackendSession()
  if (state.status !== 'ready') return <BackendSessionNotice />
  return <BillingWorkspace key={state.context.firebaseUid} context={state.context} />
}
