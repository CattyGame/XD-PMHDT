import { useCallback, useEffect, useId, useRef, useState, type FormEvent } from 'react'
import { Link, Navigate, useParams, useSearchParams } from 'react-router-dom'
import type { BackendContext } from '../../api/backendContext'
import { chatId, consultationError, createConsultation, eligibleDoctors, isChatDenied, listConsultations,
  validChatText, type Consultation } from '../../api/consultations'
import { useBackendSession } from '../../auth/backendSession'
import BackendSessionNotice from '../../auth/BackendSessionNotice'
import useConsultationThread from '../../hooks/useConsultationThread'
import surfaces from '../billing/billing.module.css'
import styles from './consultations.module.css'

type Scope = { analysisId: string; clinicContextId: string }
type Listing = Awaited<ReturnType<typeof listConsultations>>
type Doctors = Awaited<ReturnType<typeof eligibleDoctors>>
const dateFormat = new Intl.DateTimeFormat('vi-VN', { dateStyle: 'short', timeStyle: 'short' })

function NewConsultation({ context, scope, open }: {
  context: BackendContext; scope: Scope; open: (value: Consultation) => void
}) {
  const [doctors, setDoctors] = useState<Doctors>()
  const [selected, setSelected] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [reload, setReload] = useState(0)
  const active = useRef<AbortController | null>(null)
  const intent = useRef<{ doctor: string; key: string } | null>(null)
  const { analysisId, clinicContextId } = scope
  useEffect(() => {
    const controller = new AbortController()
    active.current = controller
    void eligibleDoctors(context, analysisId, clinicContextId, controller.signal).then(items => {
      if (controller.signal.aborted) return
      setDoctors(items)
    }).catch(cause => { if (!controller.signal.aborted) setError(consultationError(cause)) })
    return () => controller.abort()
  }, [context, analysisId, clinicContextId, reload])

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const controller = active.current
    const doctor = doctors?.find(item => item.id === selected)?.id
    if (!controller || controller.signal.aborted || busy || !doctor) return
    if (intent.current?.doctor !== doctor) intent.current = { doctor, key: crypto.randomUUID() }
    setBusy(true)
    setError('')
    try {
      const result = await createConsultation(context, { analysisId, clinicContextId, doctorAccountId: doctor },
        intent.current.key, controller.signal)
      if (!controller.signal.aborted) { intent.current = null; open(result) }
    } catch (cause) {
      if (controller.signal.aborted) return
      setError(consultationError(cause))
      if (isChatDenied(cause)) { setDoctors(undefined); setSelected(''); intent.current = null }
    } finally { if (!controller.signal.aborted) setBusy(false) }
  }
  return <section className={surfaces.panel} aria-label="Mở tư vấn">
    <h2>Mở tư vấn cho ca đang xem</h2>
    {!doctors && !error && <p role="status">Đang tải bác sĩ được phân công…</p>}
    {doctors?.length === 0 && <p role="status">Hiện chưa có bác sĩ được cấp quyền tư vấn cho ca này.</p>}
    {Boolean(doctors?.length) && <form onSubmit={event => void submit(event)} className={styles.actions}>
      <label>Bác sĩ tư vấn
        <select required value={selected} onChange={event => setSelected(event.target.value)} disabled={busy}>
          <option value="">Chọn bác sĩ</option>
          {doctors?.map(doctor => <option key={doctor.id} value={doctor.id}>{doctor.name}</option>)}
        </select>
      </label>
      <button disabled={busy || !selected} type="submit">{busy ? 'Đang mở…' : 'Mở cuộc trò chuyện'}</button>
    </form>}
    {error && <p role="alert" className={surfaces.error}>{error}</p>}
    {!doctors && error && <button type="button" className={surfaces.secondary} onClick={() => {
      setError(''); setReload(value => value + 1)
    }}>Tải lại bác sĩ</button>}
  </section>
}

const streamLabels = { live: 'Đang nhận tin trực tiếp', offline: 'Ngoại tuyến · bản nháp còn trong phiên',
  rest: 'Đang đồng bộ qua API', connecting: 'Đang kết nối lại…' }

function Thread({ context, conversation, changed, revoked }: {
  context: BackendContext; conversation: Consultation; changed: () => void; revoked: () => void
}) {
  const thread = useConsultationThread(context, conversation.conversationId, changed, revoked)
  const draftId = useId()
  const sendDisabled = !thread.ready || thread.busy || thread.stream === 'offline' ||
    conversation.status !== 'OPEN' || (!thread.pending && !validChatText(thread.draft))
  return <section className={surfaces.panel} aria-label="Nội dung tư vấn">
    <header className={styles.threadHeader}>
      <div><h2>Cuộc trò chuyện</h2>
        <Link to={`/analyses/${conversation.analysisId}?clinicContextId=${conversation.clinicContextId}`}>Xem ca phân tích</Link>
        <p className={surfaces.muted} role="status">{streamLabels[thread.stream]}</p>
      </div>
      <button className={surfaces.secondary} type="button" onClick={() => void thread.refresh()}>Đồng bộ lịch sử</button>
    </header>
    {thread.olderAvailable && <button className={surfaces.secondary} type="button" onClick={() => void thread.older()}>Tải tin cũ hơn</button>}
    {thread.loading && <p role="status">Đang tải tin nhắn…</p>}
    {!thread.loading && !thread.messages.length && !thread.error && <p>Chưa có tin nhắn.</p>}
    {thread.messages.length > 0 && <ol className={styles.messages} aria-label="Tin nhắn đã lưu">
      {thread.messages.map(message => {
        const own = message.senderAccountId === context.accountId.toLowerCase()
        return <li key={message.messageId} className={own ? styles.own : styles.received}>
          <div><strong>{own ? 'Bạn' : 'Người tham gia'}</strong>{' '}
            <time dateTime={message.sentAt}>{dateFormat.format(new Date(message.sentAt))}</time></div>
          <p>{message.text}</p>
          {own && <small>{message.deliveryState === 'READ' ? 'Đã đọc' : 'Đã gửi'}</small>}
        </li>
      })}
    </ol>}
    {thread.messages.some(message => message.senderAccountId !== context.accountId.toLowerCase() &&
      message.deliveryState !== 'READ') &&
      <button className={surfaces.secondary} type="button" disabled={thread.stream === 'offline' || thread.busy}
        onClick={() => void thread.markRead()}>Đánh dấu đã đọc</button>}
    {thread.error && <p role="alert" className={surfaces.error}>{thread.error}</p>}
    {thread.pending && <p role="status">{thread.busy ? 'Đang xác nhận tin đã lưu…'
      : 'Chưa nhận xác nhận lưu. Có thể đồng bộ hoặc gửi lại cùng tin.'}</p>}
    {conversation.status === 'OPEN' ? <form className={styles.composer} onSubmit={event => {
      event.preventDefault()
      if (!sendDisabled) void thread.send()
    }}>
      <label htmlFor={draftId}>Tin nhắn</label>
      <textarea id={draftId} value={thread.draft} onChange={event => thread.setDraft(event.target.value)}
        rows={3} maxLength={8000} disabled={thread.busy || Boolean(thread.pending)} />
      {thread.draft && !validChatText(thread.draft) && <p className={surfaces.muted}>Nhập từ 1 đến 4.000 ký tự, tối đa 16 KiB.</p>}
      <button disabled={sendDisabled} type="submit">{thread.pending ? 'Gửi lại tin này' : 'Gửi tin nhắn'}</button>
    </form> : <p>Cuộc trò chuyện đã đóng.</p>}
  </section>
}

function Workspace({ context, scope, initialConversationId }: {
  context: BackendContext; scope?: Scope; initialConversationId?: string
}) {
  const [listing, setListing] = useState<Listing>()
  const [error, setError] = useState('')
  const [deepLinkDenied, setDeepLinkDenied] = useState(false)
  const [revokedNotice, setRevokedNotice] = useState(false)
  const [revokedIds, setRevokedIds] = useState<ReadonlySet<string>>(new Set())
  const [listDenied, setListDenied] = useState(false)
  const [active, setActive] = useState<Consultation>()
  const [cursor, setCursor] = useState<string | null>(null)
  const [reload, setReload] = useState(0)
  const userSelectedRef = useRef(false)
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null)
  const update = useCallback(() => {
    if (timer.current) return
    timer.current = setTimeout(() => { timer.current = null; setReload(value => value + 1) }, 250)
  }, [])
  useEffect(() => {
    const poll = window.setInterval(() => {
      if (navigator.onLine && document.visibilityState === 'visible') update()
    }, 20000)
    window.addEventListener('focus', update)
    window.addEventListener('online', update)
    return () => {
      clearInterval(poll)
      if (timer.current) clearTimeout(timer.current)
      timer.current = null
      window.removeEventListener('focus', update)
      window.removeEventListener('online', update)
    }
  }, [update])
  useEffect(() => {
    const controller = new AbortController()
    if (!navigator.onLine) return
    void listConsultations(context, cursor, controller.signal).then(page => {
      if (controller.signal.aborted) return
      setListing(page)
      setError('')
      setListDenied(false)
      const matchedTarget = initialConversationId
        ? page.items.find(item => item.conversationId.toLowerCase() === initialConversationId)
        : undefined
      if (initialConversationId && !cursor && !matchedTarget && !userSelectedRef.current) {
        setDeepLinkDenied(true)
        setActive(undefined)
        return
      }
      setDeepLinkDenied(false)
      setActive(previous => {
        if (previous) {
          return page.items.find(item => item.conversationId === previous.conversationId) ?? previous
        }
        return !userSelectedRef.current && matchedTarget && !revokedIds.has(matchedTarget.conversationId)
          ? matchedTarget : undefined
      })
    }).catch(cause => {
      if (controller.signal.aborted) return
      setError(consultationError(cause))
      if (isChatDenied(cause)) {
        setListDenied(true)
        setListing(undefined)
        setActive(undefined)
      }
    })
    return () => controller.abort()
  }, [context, cursor, reload, initialConversationId, revokedIds])

  const visible = listing?.items.filter(item => !revokedIds.has(item.conversationId) &&
    (!scope || item.clinicContextId === scope.clinicContextId)) ?? []
  return <main className={surfaces.page}>
    <nav aria-label="Điều hướng"><Link to="/">Trang chủ</Link> / Tư vấn</nav>
    <h1>Tư vấn</h1>
    {scope && !revokedNotice && !listDenied && <NewConsultation context={context} scope={scope} open={conversation => {
      userSelectedRef.current = true
      setDeepLinkDenied(false)
      setRevokedIds(previous => {
        const next = new Set(previous)
        next.delete(conversation.conversationId)
        return next
      })
      setActive(conversation); setRevokedNotice(false); update()
    }} />}
    {revokedNotice && <p role="alert" className={surfaces.error}>403 · Quyền truy cập cuộc trò chuyện đã thay đổi.</p>}
    {deepLinkDenied && !revokedNotice && <p role="alert" className={surfaces.error}>403 · Không tìm thấy cuộc trò chuyện hoặc bạn không còn quyền truy cập.</p>}
    <div className={styles.workspace}>
      <section className={surfaces.panel} aria-label="Danh sách cuộc trò chuyện">
        <h2>Cuộc trò chuyện</h2>
        {error && <p role="alert" className={surfaces.error}>{error}</p>}
        {!listing && !error && <p role="status">{navigator.onLine ? 'Đang tải danh sách…' : 'Đang ngoại tuyến.'}</p>}
        {listing && visible.length === 0 && <p>Chưa có cuộc trò chuyện.</p>}
        <ul className={styles.conversations}>{visible.map(conversation => <li key={conversation.conversationId}>
          <button type="button" className={active?.conversationId === conversation.conversationId ? '' : surfaces.secondary}
            aria-pressed={active?.conversationId === conversation.conversationId}
            onClick={() => { userSelectedRef.current = true; setDeepLinkDenied(false); setRevokedNotice(false); setActive(conversation) }}>
            <span>Ca {conversation.analysisId.slice(0, 8)}</span>
            {conversation.unreadCount > 0 && <span className={styles.badge}>{conversation.unreadCount} chưa đọc</span>}
            {conversation.status === 'CLOSED' && <small>Đã đóng</small>}
          </button>
        </li>)}</ul>
        <div className={styles.actions}>
          <button className={surfaces.secondary} type="button" onClick={update}>Tải lại danh sách</button>
          {cursor && <button className={surfaces.secondary} type="button" onClick={() => {
            setListing(undefined); setCursor(null)
          }}>Trang đầu</button>}
          {listing?.nextCursor && listing.nextCursor !== cursor && <button className={surfaces.secondary} type="button"
            onClick={() => { setListing(undefined); setCursor(listing.nextCursor) }}>Trang tiếp theo</button>}
        </div>
      </section>
      {active && !revokedNotice && <Thread key={active.clinicContextId + ':' + active.conversationId} context={context}
        conversation={active} changed={update} revoked={() => {
          setRevokedIds(previous => new Set([...previous, active.conversationId]))
          setActive(undefined); setListing(undefined); setRevokedNotice(true); update()
        }} />}
      {!active && !revokedNotice && <section className={surfaces.panel}><p>Chọn cuộc trò chuyện để đọc và gửi tin.</p></section>}
    </div>
  </main>
}

export default function ConsultationsPage() {
  const { state } = useBackendSession()
  const [query] = useSearchParams()
  const { conversationId: rawConversationId } = useParams<{ conversationId?: string }>()
  if (state.status !== 'ready') return <BackendSessionNotice />
  const context = state.context
  if (!context.roles.includes('PATIENT') && !context.roles.includes('DOCTOR')) return <Navigate to="/403" replace />
  if (rawConversationId !== undefined && !chatId(rawConversationId)) return <Navigate to="/403" replace />
  const initialConversationId = rawConversationId ? rawConversationId.toLowerCase() : undefined
  const analysisId = query.get('analysisId'), clinicId = query.get('clinicContextId')
  const scope = chatId(analysisId) && chatId(clinicId)
    ? { analysisId: analysisId.toLowerCase(), clinicContextId: clinicId.toLowerCase() } : undefined
  return <Workspace key={[context.firebaseUid, context.accountId, context.profileVersion, scope?.clinicContextId, scope?.analysisId, initialConversationId].join(':')}
    context={context} scope={scope} initialConversationId={initialConversationId} />
}
