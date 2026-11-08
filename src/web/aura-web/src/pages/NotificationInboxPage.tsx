import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import type { BackendContext } from '../api/backendContext'
import {
  getInboxPage, getUnreadCount, inboxErrorMessage, markInboxRead,
  InboxError, type InboxNotification, type InboxPage,
} from '../api/notificationInbox'
import { useBackendSession } from '../auth/backendSession'
import BackendSessionNotice from '../auth/BackendSessionNotice'
import './NotificationInboxPage.css'

type ListState =
  | { phase: 'loading' }
  | { phase: 'error'; error: InboxError }
  | { phase: 'ready'; page: InboxPage }

type CountState =
  | { phase: 'loading' }
  | { phase: 'ready'; value: number }
  | { phase: 'error'; error: InboxError }

function asInboxError(cause: unknown): InboxError {
  return cause instanceof InboxError ? cause : new InboxError(0, 'ERR_NETWORK', true)
}

const notificationLabels: Record<InboxNotification['type'], string> = {
  ANALYSIS_COMPLETED: 'Phân tích hoàn tất',
  ANALYSIS_FAILED: 'Phân tích thất bại',
  PAYMENT_SUCCESS: 'Thanh toán thành công',
}

/** Mỗi phiên có state riêng; không giữ thông báo khi đổi tài khoản. */
function InboxViewer({ context }: { context: BackendContext }) {
  const [unreadOnly, setUnreadOnly] = useState(false)
  const [refreshKey, setRefreshKey] = useState(0)
  const [list, setList] = useState<ListState>({ phase: 'loading' })
  const [count, setCount] = useState<CountState>({ phase: 'loading' })
  const [loadingMore, setLoadingMore] = useState(false)
  const [moreError, setMoreError] = useState<InboxError | null>(null)
  const [markingId, setMarkingId] = useState<string | null>(null)
  const [markError, setMarkError] = useState<InboxError | null>(null)
  const moreRequest = useRef<AbortController | null>(null)
  const markRequest = useRef<AbortController | null>(null)
  const generation = useRef(0)

  useEffect(() => {
    const currentGeneration = ++generation.current
    const controller = new AbortController()
    moreRequest.current?.abort()
    markRequest.current?.abort()

    void getInboxPage(context, { limit: 20, unreadOnly }, controller.signal)
      .then((page) => {
        if (!controller.signal.aborted && generation.current === currentGeneration) {
          setList({ phase: 'ready', page })
        }
      })
      .catch((cause: unknown) => {
        if (!controller.signal.aborted && generation.current === currentGeneration) {
          setList({ phase: 'error', error: asInboxError(cause) })
        }
      })

    void getUnreadCount(context, controller.signal)
      .then((value) => {
        if (!controller.signal.aborted && generation.current === currentGeneration) {
          setCount({ phase: 'ready', value })
        }
      })
      .catch((cause: unknown) => {
        if (!controller.signal.aborted && generation.current === currentGeneration) {
          setCount({ phase: 'error', error: asInboxError(cause) })
        }
      })
      return () => {
        controller.abort()
        moreRequest.current?.abort()
        markRequest.current?.abort()
      }
  }, [context, unreadOnly, refreshKey])

  // Đưa giao diện về trạng thái đang tải khi người dùng thao tác
  const resetForFetch = () => {
    ++generation.current
    moreRequest.current?.abort()
    markRequest.current?.abort()
    setList({ phase: 'loading' })
    setCount({ phase: 'loading' })
    setLoadingMore(false)
    setMoreError(null)
    setMarkingId(null)
    setMarkError(null)}
    const reload = () => {
      resetForFetch()
      setRefreshKey((value) => value + 1)}

  const loadMore = async () => {
    if (list.phase !== 'ready' || !list.page.nextCursor || loadingMore || markingId) return
    const cursor = list.page.nextCursor
    const requestGeneration = generation.current
    const controller = new AbortController()
    moreRequest.current?.abort()
    moreRequest.current = controller
    setLoadingMore(true)
    setMoreError(null)
    try {
      const page = await getInboxPage(context, { limit: 20, cursor, unreadOnly }, controller.signal)
      if (controller.signal.aborted || generation.current !== requestGeneration) return
      setList((previous) => {
        if (previous.phase !== 'ready' || previous.page.nextCursor !== cursor) return previous
        const seen = new Set(previous.page.items.map((item) => item.notificationId.toLowerCase()))
        const additions = page.items.filter((item) => !seen.has(item.notificationId.toLowerCase()))
        return { phase: 'ready', page: {
          items: [...previous.page.items, ...additions], nextCursor: page.nextCursor,
        } }
      })
    } catch (cause) {
      if (!controller.signal.aborted && generation.current === requestGeneration) {
        setMoreError(asInboxError(cause))
      }
    } finally {
      if (!controller.signal.aborted && generation.current === requestGeneration) setLoadingMore(false)
      if (moreRequest.current === controller) moreRequest.current = null
    }
  }

  const markRead = async (notificationId: string) => {
    if (markingId !== null) return
    const requestGeneration = generation.current
    const controller = new AbortController()
    markRequest.current = controller
    setMarkingId(notificationId)
    setMarkError(null)
    try {
      await markInboxRead(notificationId, context, controller.signal)
      if (!controller.signal.aborted && generation.current === requestGeneration) {
        // Read back from API, including the authoritative unread count.
        reload()
      }
    } catch (cause) {
      if (!controller.signal.aborted && generation.current === requestGeneration) {
        setMarkError(asInboxError(cause))
      }
    } finally {
      if (!controller.signal.aborted && generation.current === requestGeneration) setMarkingId(null)
      if (markRequest.current === controller) markRequest.current = null
    }
  }

  return <section className="inbox-panel" aria-label="Danh sách thông báo">
    <div className="inbox-toolbar">
      <div>
        <h2>Thông báo của tài khoản</h2>
        {count.phase === 'ready' && <p role="status">Chưa đọc: <strong>{count.value}</strong></p>}
        {count.phase === 'loading' && <p role="status">Đang đọc số thông báo chưa xem...</p>}
        {count.phase === 'error' && <p role="alert">Chưa đọc: không thể tải. {inboxErrorMessage(count.error)}</p>}
      </div>
      <button type="button" onClick={reload}>Làm mới</button>
    </div>

    <label className="inbox-filter">
      <input type="checkbox" checked={unreadOnly}
      onChange={(event) => {
        resetForFetch()
        setUnreadOnly(event.target.checked)}} />
      Chỉ hiện thông báo chưa đọc
    </label>

    {list.phase === 'loading' && <p role="status">Đang tải hộp thư từ Notification API...</p>}
    {list.phase === 'error' && <div role="alert">
      <p>{inboxErrorMessage(list.error)}</p>
      <p>Chưa có xác nhận dữ liệu từ API; không hiển thị thông báo giả lập.</p>
    </div>}

    {markError && <p role="alert">Chưa đánh dấu đã đọc. {inboxErrorMessage(markError)}</p>}

    {list.phase === 'ready' && <>
      {list.page.items.length === 0
        ? <p role="status">{unreadOnly ? 'Không có thông báo chưa đọc trong trang này.' : 'Hộp thư hiện chưa có thông báo.'}</p>
        : <ul className="inbox-list">
          {list.page.items.map((notification) => <li
            key={notification.notificationId}
            className={`inbox-item ${notification.isRead ? 'inbox-item-read' : 'inbox-item-unread'}`}
          >
            <div className="inbox-item-body">
              <div className="inbox-item-heading">
                <strong>{notification.title}</strong>
                {!notification.isRead && <span className="inbox-unread-label">Chưa đọc</span>}
              </div>
              <p>{notificationLabels[notification.type]}</p>
              <p><small>{new Date(notification.createdAt).toLocaleString('vi-VN')}</small></p>
              <p className="inbox-item-id">Analysis ID: <code>{notification.analysisId}</code></p>
            </div>
            <div className="inbox-item-actions">
              <Link to={`/analyses/${notification.analysisId}`}>Xem kết quả</Link>
              {!notification.isRead && <button type="button"
                disabled={markingId !== null}
                onClick={() => { void markRead(notification.notificationId) }}>
                {markingId === notification.notificationId ? 'Đang cập nhật...' : 'Đánh dấu đã đọc'}
              </button>}
            </div>
          </li>)}
        </ul>}
      {moreError && <p role="alert">Không tải được trang tiếp theo. {inboxErrorMessage(moreError)}</p>}
      {list.page.nextCursor !== null && <button type="button" disabled={loadingMore || markingId !== null}
        onClick={() => { void loadMore() }}>
        {loadingMore ? 'Đang tải...' : 'Xem thêm thông báo'}
      </button>}
    </>}
    <p className="inbox-disclaimer">Thông báo chỉ hiển thị theo phiên đăng nhập hiện tại. Mở Analysis sẽ được Analysis API kiểm tra quyền truy cập lại.</p>
  </section>
}

export default function NotificationInboxPage() {
  const { state } = useBackendSession()
  if (state.status !== 'ready') return <BackendSessionNotice />

  return <main className="inbox-page">
    <nav aria-label="Điều hướng trang">
      <Link to="/">Trang chủ</Link> · <Link to="/batches">Theo dõi batch</Link>
    </nav>
    <h1>Hộp thư thông báo</h1>
    <p>Thông báo hoàn tất hoặc thất bại của lượt phân tích võng mạc.</p>
    <InboxViewer key={`${state.context.firebaseUid}:${state.context.accountId}`} context={state.context} />
  </main>
}
