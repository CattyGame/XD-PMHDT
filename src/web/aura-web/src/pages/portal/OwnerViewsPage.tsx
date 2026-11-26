import { useEffect, useState } from 'react'
import { Link, Navigate } from 'react-router-dom'
import type { BackendContext } from '../../api/backendContext'
import { getUploadClinics, type UploadClinic } from '../../api/clinicUpload'
import { analytics, ownerOrders, ownerUsage, portalError, type PortalOwner } from '../../api/sprint6Portal'
import { useBackendSession } from '../../auth/backendSession'
import BackendSessionNotice from '../../auth/BackendSessionNotice'
import styles from '../billing/billing.module.css'
import PortalSection, { AnalyticsCards, DateRangeForm, PortalTable } from './PortalSection'
import { defaultRange, formatCount, formatDate, formatMoney, formatRange } from './display'

type Mode = 'billing' | 'clinic' | 'doctor'

function OwnerWorkspace({ context, mode }: { context: BackendContext; mode: Mode }) {
  const [clinics, setClinics] = useState<readonly UploadClinic[]>([])
  const [clinicError, setClinicError] = useState('')
  const [clinicLoading, setClinicLoading] = useState(true)
  const [clinicId, setClinicId] = useState('')
  const [retry, setRetry] = useState(0)
  const [range, setRange] = useState(defaultRange)
  useEffect(() => {
    const controller = new AbortController()
    void getUploadClinics(context, controller.signal).then(rows => {
      if (!controller.signal.aborted) setClinics(rows.filter(row => row.role === (mode === 'doctor' ? 'DOCTOR' : 'CLINIC_ADMIN')))
    }).catch(cause => {
      if (!controller.signal.aborted) setClinicError(portalError(cause))
    }).finally(() => {
      if (!controller.signal.aborted) setClinicLoading(false)
    })
    return () => controller.abort()
  }, [context, retry, mode])

  const owner: PortalOwner = clinicId ? { type: 'CLINIC', id: clinicId } : { type: 'PERSONAL', id: context.accountId }
  const title = mode === 'billing' ? 'Lịch sử thanh toán và sử dụng credit'
    : mode === 'clinic' ? 'Tổng quan phòng khám' : 'Hoạt động bác sĩ'
  return <main className={styles.page}>
    <nav aria-label="Điều hướng"><Link to="/">Trang chủ</Link>
      {mode === 'billing' && <> / <Link to="/billing/packages">Mua gói / Credit</Link></>}
    </nav>
    <h1>{title}</h1>
    <section className={styles.panel} aria-label="Chọn phạm vi">
      <label>{mode === 'billing' ? 'Chủ ví' : 'Phòng khám được cấp quyền'}
        <select value={clinicId} onChange={event => setClinicId(event.target.value)}>
          <option value="">{mode === 'billing' ? 'Ví cá nhân' : 'Chọn phòng khám'}</option>
          {clinics.map(clinic => <option key={clinic.clinicId} value={clinic.clinicId}>{clinic.clinicName}</option>)}
        </select>
      </label>
      {clinicLoading && <p role="status">Đang tải danh sách phòng khám…</p>}
      {!clinicLoading && !clinicError && !clinics.length && <p>Chưa có phòng khám phù hợp với quyền hiện tại.</p>}
      {clinicError && <><p role="alert" className={styles.error}>{clinicError}</p>
        <button className={styles.secondary} type="button" onClick={() => {
          setClinicError(''); setClinicLoading(true); setRetry(previous => previous + 1)
        }}>Tải lại phòng khám</button></>}
    </section>
    {(mode === 'billing' || clinicId) && <DateRangeForm range={range} onChange={setRange} />}
    {mode === 'billing' && <BillingData key={owner.type + ':' + owner.id + ':' + range.from + ':' + range.to}
      context={context} owner={owner} range={range} />}
    {mode !== 'billing' && clinicId &&
      <PortalSection key={clinicId + ':' + range.from + ':' + range.to} title={title}
        read={signal => analytics(context, range, signal, { type: mode, clinicId })}>
        {data => <AnalyticsCards data={data} />}
      </PortalSection>}
  </main>
}

function BillingData({ context, owner, range }: {
  context: BackendContext; owner: PortalOwner; range: { from: string; to: string }
}) {
  return <>
    <PortalSection<Awaited<ReturnType<typeof ownerUsage>>> title="Sử dụng credit" cursor={data => data.nextCursor}
      read={(signal, cursor) => ownerUsage(context, owner, range, signal, cursor)}>
      {usage => <>
        <p className={styles.muted}>{usage.environment} · {formatRange(range)}</p>
        <PortalTable columns={['Thời điểm', 'Loại giao dịch', 'Thay đổi credit', 'Số dư sau giao dịch', 'Đơn vị giữ chỗ', 'Đơn hàng', 'Giữ chỗ']}>
          {usage.items.map(entry => <tr key={entry.id}><td>{formatDate(entry.createdAt)}</td>
            <td>{entry.entryType}</td><td>{formatCount(entry.balanceDelta)}</td><td>{formatCount(entry.balanceAfter)}</td>
            <td>{formatCount(entry.reservationUnits)}</td><td>{entry.orderId ?? '—'}</td><td>{entry.reservationId ?? '—'}</td></tr>)}
        </PortalTable>
      </>}
    </PortalSection>
    <PortalSection<Awaited<ReturnType<typeof ownerOrders>>> title="Lịch sử đơn hàng" cursor={data => data.nextCursor}
      read={(signal, cursor) => ownerOrders(context, owner, cursor, signal)}>
      {orders => <>
        <p className={styles.muted}>Lịch sử theo chủ ví. Bộ lọc ngày áp dụng cho sử dụng credit.</p>
        <PortalTable columns={['Thời điểm', 'Đơn hàng', 'Gói', 'Trạng thái', 'Môi trường', 'Giá', 'Credit', 'Phiên bản giá']}>
          {orders.items.map(order => <tr key={order.id}><td>{formatDate(order.createdAt)}</td>
            <td><Link to={'/billing/orders/' + order.id}>{order.id}</Link></td><td>{order.packageName}</td>
            <td>{order.status}</td><td>{order.environment}</td><td>{formatMoney(order.amount, order.currency)}</td>
            <td>{formatCount(order.credits)}</td><td>{order.priceVersionId}</td></tr>)}
        </PortalTable>
      </>}
    </PortalSection>
  </>
}

export default function OwnerViewsPage({ mode = 'billing' }: { mode?: Mode }) {
  const { state } = useBackendSession()
  if (state.status !== 'ready') return <BackendSessionNotice />
  const context = state.context
  if (mode === 'doctor' && !context.roles.includes('DOCTOR')) return <Navigate to="/403" replace />
  return <OwnerWorkspace key={mode + ':' + context.firebaseUid + ':' + context.accountId + ':' + context.profileVersion}
    context={context} mode={mode} />
}
