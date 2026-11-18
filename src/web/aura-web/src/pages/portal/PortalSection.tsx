import { useCallback, useEffect, useEffectEvent, useRef, useState, type FormEvent, type ReactNode } from 'react'
import { portalError, type analytics } from '../../api/sprint6Portal'
import styles from '../billing/billing.module.css'
import portal from './portal.module.css'
import { formatCount, formatDate, formatRange } from './display'

type State<T> = { kind: 'loading' } | { kind: 'error'; message: string } | { kind: 'ready'; value: T }
type Mutate = (write: (signal: AbortSignal) => Promise<unknown>) => Promise<void>
type Props<T> = {
  title: string
  read: (signal: AbortSignal, cursor: string | null) => Promise<T>
  cursor?: (value: T) => string | null
  children: (value: T, mutate: Mutate, busy: boolean) => ReactNode
}

export default function PortalSection<T>({ title, read, cursor, children }: Props<T>) {
  const [state, setState] = useState<State<T>>({ kind: 'loading' })
  const [request, setRequest] = useState({ cursor: null as string | null, sequence: 0 })
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const pending = useRef<AbortController | null>(null)
  const load = useEffectEvent(read)

  const reload = useCallback((next: string | null) => {
    setState({ kind: 'loading' })
    setRequest(current => ({ cursor: next, sequence: current.sequence + 1 }))
  }, [])

  useEffect(() => {
    const controller = new AbortController()
    void Promise.resolve().then(() => {
      if (!controller.signal.aborted) return load(controller.signal, request.cursor)
    }).then(value => {
      if (!controller.signal.aborted) setState({ kind: 'ready', value: value as T })
    }).catch(cause => {
      if (!controller.signal.aborted) setState({ kind: 'error', message: portalError(cause) })
    })
    return () => controller.abort()
  }, [request])
  useEffect(() => () => pending.current?.abort(), [])

  const ready = state.kind === 'ready'
  const mutate = useCallback<Mutate>(async write => {
    if (pending.current || !ready) return
    const controller = new AbortController()
    pending.current = controller
    setSaved(false)
    setBusy(true)
    try {
      await write(controller.signal)
      if (!controller.signal.aborted) {
        setSaved(true)
        reload(null)
      }
    } catch (cause) {
      if (!controller.signal.aborted) setState({ kind: 'error', message: portalError(cause) })
    } finally {
      pending.current = null
      if (!controller.signal.aborted) setBusy(false)
    }
  }, [ready, reload])

  const next = state.kind === 'ready' ? cursor?.(state.value) : null
  return <section className={styles.panel} aria-label={title} aria-busy={busy || state.kind === 'loading'}>
    <div className={portal.sectionHeading}>
      <h2>{title}</h2>
      <button className={styles.secondary} type="button" disabled={busy || state.kind === 'loading'}
        onClick={() => { setSaved(false); reload(null) }}>Tải lại</button>
    </div>
    {state.kind === 'loading' && <p role="status">Đang tải dữ liệu…</p>}
    {state.kind === 'error' && <p role="alert" className={styles.error}>{state.message}</p>}
    {saved && state.kind !== 'error' && <p role="status">Đã lưu thay đổi.</p>}
    {state.kind === 'ready' && children(state.value, mutate, busy)}
    {next && next !== request.cursor && <button type="button" className={styles.secondary} disabled={busy}
      onClick={() => { setSaved(false); reload(next) }}>Trang tiếp theo</button>}
  </section>
}

export function PortalTable({ columns, children }: { columns: string[]; children: ReactNode[] }) {
  if (!children.length) return <p role="status">Chưa có dữ liệu trong phạm vi đã chọn.</p>
  return <div className={portal.tableWrap} tabIndex={0} role="region" aria-label="Bảng dữ liệu có thể cuộn">
    <table className={portal.table}><thead><tr>{columns.map(label => <th key={label} scope="col">{label}</th>)}</tr></thead>
      <tbody>{children}</tbody></table>
  </div>
}
export function Metrics({ entries }: { entries: [string, number | null][] }) {
  return <dl className={portal.metrics}>{entries.map(([label, value]) =>
    <div key={label}><dt>{label}</dt><dd>{formatCount(value)}</dd></div>)}</dl>
}
export function AnalyticsCards({ data }: { data: Awaited<ReturnType<typeof analytics>> }) {
  const p = data.processing, r = data.review
  return <>
    <p className={styles.muted}>{formatRange(data)} · Cập nhật {formatDate(data.asOf)}</p>
    <Metrics entries={[[ 'Tổng ca phân tích duy nhất', data.total ], ['Đang xử lý', p.active],
      ['Hoàn tất', p.completed], ['Thất bại', p.failed], ['Chất lượng thấp', p.lowQuality],
      ['Bị từ chối', p.rejected], ['Trùng', p.duplicate]]} />
    <h3>Kết luận bác sĩ mới nhất</h3>
    <Metrics entries={[[ 'Thấp', r.low ], ['Trung bình', r.moderate], ['Cao', r.high],
      ['Thiếu kết luận nguy cơ', r.unavailable], ['Chưa đánh giá', r.unreviewed]]} />
    <h3>Đo lường</h3>
    <Metrics entries={[[ 'Có số đo', data.measured ], ['Thiếu số đo', data.unavailable],
      ['Chưa rõ trạng thái đo', data.measurementUnknown]]} />
  </>
}
export function DateRangeForm({ range, onChange }: {
  range: { from: string; to: string }; onChange: (range: { from: string; to: string }) => void
}) {
  const [error, setError] = useState('')
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const data = new FormData(event.currentTarget)
    const from = new Date(String(data.get('from')) + 'T00:00:00')
    const last = new Date(String(data.get('to')) + 'T00:00:00')
    if (!Number.isFinite(+from) || !Number.isFinite(+last) || from > last) {
      setError('Ngày bắt đầu phải trước hoặc trùng ngày kết thúc.')
      return
    }
    last.setDate(last.getDate() + 1)
    setError('')
    onChange({ from: from.toISOString(), to: last.toISOString() })
  }
  const localDate = (value: string) => {
    const d = new Date(value)
    return new Date(d.valueOf() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 10)
  }
  return <form className={portal.controls} onSubmit={submit} aria-label="Khoảng ngày">
    <label>Từ ngày <input type="date" name="from" defaultValue={localDate(range.from)} required /></label>
    <label>Đến hết ngày <input type="date" name="to" defaultValue={localDate(range.to)} required /></label>
    <button type="submit">Áp dụng</button>
    {error && <p role="alert" className={styles.error}>{error}</p>}
  </form>
}
