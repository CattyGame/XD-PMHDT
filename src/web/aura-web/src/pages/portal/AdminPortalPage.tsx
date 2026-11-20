import { useState, type FormEvent, type ReactNode } from 'react'
import { Link, Navigate, NavLink, useParams } from 'react-router-dom'
import type { BackendContext } from '../../api/backendContext'
import { accounts, accountRoles, adminPackages, aiConfig, analytics, audit, changeAccount, changeClinicStatus,
  changeDoctorCapability, clinics, clinicStatuses, financial, retirePackage, saveAiConfig, savePrice, saveTemplate,
  templates, type DateRange, type PriceInput } from '../../api/sprint6Portal'
import { useBackendSession } from '../../auth/backendSession'
import BackendSessionNotice from '../../auth/BackendSessionNotice'
import styles from '../billing/billing.module.css'
import PortalSection, { AnalyticsCards, DateRangeForm, PortalTable } from './PortalSection'
import { defaultRange, formatCount, formatDate, formatRange } from './display'
import portal from './portal.module.css'

const sections = {
  dashboard: 'Tổng quan', accounts: 'Tài khoản', clinics: 'Phòng khám', catalog: 'Gói khám',
  ai: 'Cấu hình AI', templates: 'Mẫu thông báo', audit: 'Nhật ký',
} as const
type Section = keyof typeof sections
const nextPage = (result: { nextCursor: string | null }) => result.nextCursor
const value = (data: FormData, key: string) => String(data.get(key) ?? '').trim()

function Editor({ title, busy, onSubmit, children }: {
  title: string; busy: boolean; onSubmit: (data: FormData) => void; children: ReactNode
}) {
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const data = new FormData(event.currentTarget)
    if (!busy && value(data, 'reason')) onSubmit(data)
  }
  return <form onSubmit={submit}><fieldset className={portal.editor} disabled={busy}>
    <legend>{title}</legend>
    {children}
    <label>Lý do <input name="reason" required maxLength={1000} pattern=".*\S.*" /></label>
    <button type="submit">Lưu thay đổi</button>
  </fieldset></form>
}

function Dashboard({ context, range }: { context: BackendContext; range: DateRange }) {
  return <>
    <PortalSection title="Tổng quan phân tích" read={signal => analytics(context, range, signal)}>
      {data => <AnalyticsCards data={data} />}
    </PortalSection>
    <PortalSection title="Tổng hợp thanh toán" read={signal => financial(context, range, signal)}>
      {data => <><p className={styles.muted}>{formatRange(data)} · Số tiền ở đơn vị nhỏ nhất của từng tiền tệ</p>
        <PortalTable columns={['Tiền tệ', 'Môi trường', 'Đơn thành công', 'Tổng tiền', 'Hoàn tiền', 'Thực thu']}>
          {data.groups.map(row => <tr key={row.currency + row.environment}>
            <td>{row.currency}</td><td>{row.environment}</td><td>{formatCount(row.orders)}</td>
            <td>{formatCount(row.gross)}</td><td>{formatCount(row.refunds)}</td><td>{formatCount(row.net)}</td>
          </tr>)}</PortalTable></>}
    </PortalSection>
  </>
}

function Accounts({ context }: { context: BackendContext }) {
  return <PortalSection title="Quản lý tài khoản" cursor={nextPage}
    read={(signal, cursor) => accounts(context, cursor, signal)}>
    {(data, mutate, busy) => <PortalTable columns={['Tài khoản', 'Trạng thái', 'Vai trò', 'Phiên bản', 'Thao tác']}>
      {data.items.map(row => <tr key={row.id}>
        <td>{row.displayName || row.id}</td><td>{row.status}</td><td>{row.roles.join(', ')}</td><td>{row.version}</td>
        <td><details><summary>Chỉnh sửa</summary>
          <Editor title="Trạng thái tài khoản" busy={busy} onSubmit={data => void mutate(signal => changeAccount(context,
            row.id, { status: value(data, 'status') === 'ACTIVE' ? 'ACTIVE' : 'DISABLED',
              expectedVersion: row.version, reason: value(data, 'reason') }, signal))}>
            <label>Trạng thái <select name="status" defaultValue={row.status}>
              <option value="ACTIVE">Hoạt động</option><option value="DISABLED">Vô hiệu</option>
            </select></label>
          </Editor>
          <Editor title="Vai trò hệ thống" busy={busy} onSubmit={data => {
            const roles = accountRoles.filter(role => role === 'DOCTOR' ? row.roles.includes(role) : data.has(role))
            if (roles.length) void mutate(signal => changeAccount(context, row.id,
              { roles, expectedVersion: row.version, reason: value(data, 'reason') }, signal))
          }}>
            <label><input type="checkbox" name="PATIENT" defaultChecked={row.roles.includes('PATIENT')} /> Bệnh nhân</label>
            <label><input type="checkbox" name="SYSTEM_ADMIN" defaultChecked={row.roles.includes('SYSTEM_ADMIN')} /> Quản trị hệ thống</label>
          </Editor>
          <Editor title="Quyền bác sĩ" busy={busy} onSubmit={data => void mutate(signal => changeDoctorCapability(context,
            row.id, value(data, 'doctor') === 'approve', value(data, 'reason'), signal))}>
            <label>Quyền bác sĩ <select name="doctor" defaultValue={row.roles.includes('DOCTOR') ? 'approve' : 'revoke'}>
              <option value="approve">Phê duyệt</option><option value="revoke">Thu hồi</option>
            </select></label>
          </Editor>
        </details></td>
      </tr>)}
    </PortalTable>}
  </PortalSection>
}

function Clinics({ context }: { context: BackendContext }) {
  return <PortalSection title="Quản lý phòng khám" cursor={nextPage} read={(signal, cursor) => clinics(context, cursor, signal)}>
    {(data, mutate, busy) => <PortalTable columns={['Phòng khám', 'Trạng thái', 'Phiên bản', 'Thao tác']}>
      {data.items.map(row => <tr key={row.id}><td>{row.name}</td><td>{row.status}</td><td>{row.version}</td>
        <td><details><summary>Đổi trạng thái</summary>
          <Editor title="Trạng thái phòng khám" busy={busy} onSubmit={data => {
            const status = clinicStatuses.find(status => status === value(data, 'status'))
            if (status) void mutate(signal => changeClinicStatus(context, row.id,
              { status, expectedVersion: row.version, reason: value(data, 'reason') }, signal))
          }}>
            <label>Trạng thái <select defaultValue={row.status} name="status">
              {clinicStatuses.map(status => <option key={status}>{status}</option>)}
            </select></label>
          </Editor>
        </details></td></tr>)}
    </PortalTable>}
  </PortalSection>
}

type Package = Awaited<ReturnType<typeof adminPackages>>['items'][number]
function PriceEditor({ current, busy, save }: { current?: Package; busy: boolean; save: (price: PriceInput) => void }) {
  return <Editor title={current ? 'Tạo phiên bản giá' : 'Tạo gói và giá ban đầu'} busy={busy} onSubmit={data => {
    const amountMinor = Number(value(data, 'amount')), credits = Number(value(data, 'credits'))
    const displayName = value(data, 'name')
    if (displayName && Number.isSafeInteger(amountMinor) && Number.isSafeInteger(credits))
      save({ displayName, amountMinor, credits, currency: value(data, 'currency'), reason: value(data, 'reason') })
  }}>
    <div className={portal.editorRow}>
      <label>Tên gói <input name="name" defaultValue={current?.name} required maxLength={160} pattern=".*\S.*" /></label>
      <label>Giá theo đơn vị nhỏ nhất <input name="amount" type="number" step="1" min="0"
        max={Number.MAX_SAFE_INTEGER} defaultValue={current?.amount} required /></label>
      <label>Mã tiền tệ <input name="currency" defaultValue={current?.currency ?? 'VND'} required pattern="[A-Z]{3}" /></label>
      <label>Credit <input name="credits" type="number" step="1" min="1" max={Number.MAX_SAFE_INTEGER}
        defaultValue={current?.credits} required /></label>
    </div>
  </Editor>
}

function Catalog({ context }: { context: BackendContext }) {
  return <PortalSection title="Danh mục gói và phiên bản giá" cursor={nextPage}
    read={(signal, cursor) => adminPackages(context, cursor, signal)}>
    {(data, mutate, busy) => <>
      <details><summary>Tạo gói khám</summary><PriceEditor busy={busy} save={price => void mutate(signal => savePrice(context, price, signal))} /></details>
      <PortalTable columns={['Gói khám', 'Giá (đơn vị nhỏ nhất)', 'Credit', 'Trạng thái', 'Phiên bản', 'Thao tác']}>
        {data.items.map(row => <tr key={row.priceVersionId}>
          <td>{row.name}</td><td>{formatCount(row.amount)} {row.currency}</td><td>{formatCount(row.credits)}</td>
          <td>{row.status}</td><td>{row.version}<br /><small>{row.priceVersionId}</small></td>
          <td>{row.status === 'ACTIVE' && <details><summary>Quản lý giá</summary>
            <PriceEditor current={row} busy={busy} save={price => void mutate(signal => savePrice(context, price, signal, row))} />
            <Editor title="Ngừng bán gói" busy={busy} onSubmit={data => void mutate(signal => retirePackage(context,
              row.id, { expectedVersion: row.version, reason: value(data, 'reason') }, signal))}>
              <p>Ngừng tạo đơn mới cho gói này.</p>
            </Editor>
          </details>}</td>
        </tr>)}
      </PortalTable>
    </>}
  </PortalSection>
}

function Ai({ context }: { context: BackendContext }) {
  return <PortalSection title="Cấu hình AI đang hoạt động" read={signal => aiConfig(context, signal)}>
    {(data, mutate, busy) => <>
      <p>Phiên bản {data.version} · Kích hoạt {formatDate(data.activatedAt)}</p>
      <Editor title="Kích hoạt manifest đã xác minh" busy={busy} onSubmit={form => void mutate(signal => saveAiConfig(context, {
        expectedVersion: data.version, artifactManifestId: value(form, 'manifest'), thresholdConfigSha256: value(form, 'sha'),
        reason: value(form, 'reason'),
      }, signal))}>
        <label>ID manifest <input required name="manifest" defaultValue={data.manifest}
          pattern="[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}" /></label>
        <label>SHA-256 cấu hình ngưỡng <input required name="sha" pattern="[a-f0-9]{64}" defaultValue={data.sha} /></label>
      </Editor>
    </>}
  </PortalSection>
}

function TemplateEditor({ item, busy, save }: { item: Awaited<ReturnType<typeof templates>>[number]; busy: boolean;
  save: (data: { title: string; body: string; enabled: boolean; reason: string }) => void }) {
  const [error, setError] = useState('')
  return <Editor title={item.event} busy={busy} onSubmit={data => {
    const title = value(data, 'title'), body = value(data, 'body')
    const generic = (text: string) => !/[{}<>]|https?:\/\/|www\./i.test(text.replace(/\{\{(?:app_name|event_label)\}\}/g, ''))
    if (!title || !body || !generic(title) || !generic(body)) {
      setError('Chỉ dùng văn bản chung và biến {{app_name}}, {{event_label}}.')
      return
    }
    setError('')
    save({ title, body, enabled: data.has('enabled'), reason: value(data, 'reason') })
  }}>
    <p className={styles.muted}>Phiên bản {item.version} · Biến hỗ trợ {'{{app_name}}'} và {'{{event_label}}'}</p>
    <label>Tiêu đề <input name="title" required maxLength={100} defaultValue={item.title} /></label>
    <label>Nội dung <textarea name="body" required maxLength={250} defaultValue={item.body} rows={3} /></label>
    <label><input type="checkbox" name="enabled" defaultChecked={item.enabled} /> Bật mẫu thông báo</label>
    {error && <p className={styles.error} role="alert">{error}</p>}
  </Editor>
}

function Templates({ context }: { context: BackendContext }) {
  return <PortalSection title="Mẫu thông báo" read={signal => templates(context, signal)}>
    {(data, mutate, busy) => data.length ? data.map(item => <TemplateEditor key={item.id} item={item} busy={busy}
      save={data => void mutate(signal => saveTemplate(context, item.id, { ...data, expectedVersion: item.version }, signal))} />)
      : <p role="status">Chưa có mẫu thông báo.</p>}
  </PortalSection>
}

function Audit({ context, range }: { context: BackendContext; range: DateRange }) {
  return <PortalSection title="Nhật ký quản lý cơ sở y tế" cursor={nextPage} read={(signal, cursor) => audit(context, range, cursor, signal)}>
    {data => <PortalTable columns={['Thời điểm', 'Dịch vụ', 'Thao tác', 'Tài nguyên (mã băm)', 'Mã truy vết', 'Kết quả']}>
      {data.items.map(event => <tr key={event.id}><td>{formatDate(event.time)}</td><td>{event.service}</td>
        <td>{event.action}</td><td>{event.resourceType}<br /><small>{event.resourceHash}</small></td>
        <td>{event.correlation}</td><td>{event.outcome}</td></tr>)}
    </PortalTable>}
  </PortalSection>
}

function Workspace({ context, section }: { context: BackendContext; section: Section }) {
  const [range, setRange] = useState(defaultRange)
  const rangeKey = range.from + ':' + range.to
  return <main className={styles.page}>
    <nav aria-label="Điều hướng"><Link to="/">Trang chủ</Link> / Quản trị hệ thống</nav>
    <h1>Quản trị hệ thống</h1>
    <Link to="/admin">Quản lý tài khoản và cơ sở y tế</Link>
    <nav className={portal.nav} aria-label="Danh mục quản trị">
      {Object.entries(sections).map(([id, label]) => <NavLink key={id} to={'/admin/' + id}>{label}</NavLink>)}
    </nav>
    {(section === 'dashboard' || section === 'audit') && <DateRangeForm range={range} onChange={setRange} />}
    {section === 'dashboard' && <Dashboard key={rangeKey} context={context} range={range} />}
    {section === 'accounts' && <Accounts context={context} />}
    {section === 'clinics' && <Clinics context={context} />}
    {section === 'catalog' && <Catalog context={context} />}
    {section === 'ai' && <Ai context={context} />}
    {section === 'templates' && <Templates context={context} />}
    {section === 'audit' && <Audit key={rangeKey} context={context} range={range} />}
  </main>
}

export default function AdminPortalPage() {
  const { state } = useBackendSession()
  const { section } = useParams()
  if (state.status !== 'ready') return <BackendSessionNotice />
  const context = state.context
  if (!context.roles.includes('SYSTEM_ADMIN')) return <Navigate to="/403" replace />
  if (!section || !Object.hasOwn(sections, section)) return <Navigate to="/admin/dashboard" replace />
  return <Workspace key={context.firebaseUid + ':' + context.accountId + ':' + context.profileVersion}
    context={context} section={section as Section} />
}
