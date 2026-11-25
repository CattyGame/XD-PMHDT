import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { useEffect, useEffectEvent, useRef, useState } from 'react'
import { AnalysisResultError } from '../api/analysisResult'
import type { BackendContext } from '../api/backendContext'
import { consultationError, isChatDenied, isChatRevoked, markConsultationRead, messageHistory, parseChatMessage,
  parseReadEvent, sendConsultationMessage, validChatText, type ChatMessage, type SendIntent } from '../api/consultations'
import { isMockEnabled } from '../api/client'
import { getFirebaseAuth } from '../firebase/config'

type StreamState = 'connecting' | 'live' | 'offline' | 'rest'
type Actions = {
  refresh: () => Promise<void>; older: () => Promise<void>; send: (draft: string) => Promise<void>
  markRead: () => Promise<void>
}

function sortMessages(left: ChatMessage, right: ChatMessage) {
  const compareMs = Date.parse(left.sentAt) - Date.parse(right.sentAt)
  if (compareMs) return compareMs
  const subMillisecond = (stamp: string) => (stamp.match(/\.(\d+)(?:Z|[+-]\d{2}:\d{2})$/i)?.[1] ?? '').slice(3)
  const leftFraction = subMillisecond(left.sentAt), rightFraction = subMillisecond(right.sentAt)
  const precision = Math.max(leftFraction.length, rightFraction.length)
  return leftFraction.padEnd(precision, '0').localeCompare(rightFraction.padEnd(precision, '0')) ||
    left.messageId.localeCompare(right.messageId)
}

export default function useConsultationThread(context: BackendContext, conversationId: string,
  onChange: () => void, onRevoked: () => void) {
  const [messages, setMessages] = useState<ChatMessage[]>([])
  const [draft, setDraft] = useState('')
  const [pending, setPending] = useState<SendIntent | null>(null)
  const [stream, setStream] = useState<StreamState>('connecting')
  const [loading, setLoading] = useState(true)
  const [ready, setReady] = useState(false)
  const [olderAvailable, setOlderAvailable] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const actions = useRef<Actions | null>(null)
  const changed = useEffectEvent(onChange)
  const revoked = useEffectEvent(onRevoked)

  useEffect(() => {
    const controller = new AbortController()
    const { signal } = controller
    const owner = context.accountId.toLowerCase()
    const data = new Map<string, ChatMessage>()
    const receipts = new Map<string, { id: string; at: number }>()
    const auth = getFirebaseAuth()
    let anchor: string | null = null
    let earlier: string | null = null
    let hydrated = false
    let intent: SendIntent | null = null
    let writing = false
    let loadingOlder = false
    let syncing: Promise<void> | undefined
    let starting = false
    let joined = false
    const hub = new HubConnectionBuilder().withUrl('/hubs/consultations', {
      withCredentials: false,
      accessTokenFactory: async () => {
        const user = auth?.currentUser
        if (isMockEnabled || signal.aborted || !user || user.uid !== context.firebaseUid)
          throw new AnalysisResultError(401, 'ERR_SESSION_CHANGED')
        const token = await user.getIdToken()
        if (signal.aborted || auth.currentUser !== user || !token || /\s/.test(token))
          throw new AnalysisResultError(401, 'ERR_SESSION_CHANGED')
        return token
      },
    }).withAutomaticReconnect().configureLogging(LogLevel.None).build()

    function revoke() {
      if (signal.aborted) return
      controller.abort()
      data.clear()
      receipts.clear()
      intent = null
      actions.current = null
      setMessages([])
      setPending(null)
      setDraft('')
      void hub.stop().catch(() => undefined)
      revoked()
    }
    function fail(cause: unknown) {
      if (signal.aborted) return
      if (isChatDenied(cause)) revoke()
      else setError(consultationError(cause))
    }
    function showMessages() {
      const ownRead = data.get(receipts.get(owner)?.id ?? '')
      const peerRead = [...receipts.entries()].filter(([account]) => account !== owner)
        .map(([, receipt]) => data.get(receipt.id)).filter(message => message !== undefined).sort(sortMessages).at(-1)
      setMessages([...data.values()].sort(sortMessages).map(message => {
        const cursor = message.senderAccountId === owner ? peerRead : ownRead
        return cursor && sortMessages(message, cursor) <= 0 ? { ...message, deliveryState: 'READ' } : message
      }))
    }
    function merge(incoming: ChatMessage[]) {
      if (signal.aborted) return
      for (const message of incoming) {
        const previous = data.get(message.messageId)
        data.set(message.messageId, previous?.deliveryState === 'READ' ? previous : message)
        if (intent && message.senderAccountId === owner && message.clientMessageId === intent.clientMessageId &&
          message.text === intent.text) {
          intent = null
          setPending(null)
          setDraft('')
        }
      }
      showMessages()
    }
    function refresh() {
      if (syncing) return syncing
      syncing = (async () => {
        try {
          const latest = await messageHistory(context, conversationId, null, signal)
          if (signal.aborted) return
          const boundary = anchor
          const newest = [...latest.items].sort(sortMessages).at(-1)?.messageId
          let cursor = latest.nextBeforeId
          let found = latest.items.some(message => message.messageId === boundary)
          merge(latest.items)
          // The REST cursor points backwards. Walk the gap before advancing the remembered boundary.
          const visited = new Set<string>()
          while (boundary && !found && cursor) {
            if (visited.has(cursor)) throw new AnalysisResultError(0, 'ERR_INVALID_RESPONSE')
            visited.add(cursor)
            const page = await messageHistory(context, conversationId, cursor, signal)
            if (signal.aborted) return
            found = page.items.some(message => message.messageId === boundary)
            cursor = page.nextBeforeId
            merge(page.items)
          }
          if (!hydrated || (boundary && !found) || (!boundary && data.size)) {
            earlier = cursor
            setOlderAvailable(Boolean(earlier))
          }
          hydrated = true
          setReady(true)
          anchor = newest ?? anchor
          changed()
        } catch (cause) { fail(cause) }
        finally {
          if (!signal.aborted) setLoading(false)
        }
      })().finally(() => { syncing = undefined })
      return syncing
    }
    async function join() {
      try {
        if (signal.aborted) return
        await hub.invoke('JoinConversation', conversationId)
        if (signal.aborted) return
        joined = true
        setStream('live')
        if (syncing) await syncing
        if (!signal.aborted) await refresh()
      } catch {
        if (signal.aborted) return
        joined = false
        setStream('rest')
        await refresh()
        void hub.stop().catch(() => undefined)
      }
    }
    async function connect() {
      if (signal.aborted || starting || hub.state !== HubConnectionState.Disconnected) return
      if (!navigator.onLine) { setStream('offline'); return }
      starting = true
      setStream('connecting')
      try {
        await hub.start()
        if (!signal.aborted) await join()
      } catch (cause) {
        if (signal.aborted) return
        if (isChatDenied(cause) || (cause instanceof Error && 'statusCode' in cause &&
          (cause.statusCode === 401 || cause.statusCode === 403))) {
          revoke()
          return
        }
        setStream(navigator.onLine ? 'rest' : 'offline')
      } finally { starting = false }
    }
    hub.on('MessagePersisted', value => {
      if (signal.aborted || !joined) return
      try { merge([parseChatMessage(value, conversationId, true)]); changed() } catch { /* Ignore foreign or invalid events. */ }
    })
    hub.on('ReadCursorAdvanced', value => {
      if (signal.aborted || !joined) return
      try {
        const result = parseReadEvent(value, conversationId)
        const timestamp = Date.parse(result.readAt)
        if (timestamp >= (receipts.get(result.accountId)?.at ?? -Infinity)) {
          receipts.set(result.accountId, { id: result.lastReadMessageId, at: timestamp })
          showMessages()
          if (result.accountId === owner) changed()
        }
      } catch { /* Ignore foreign or invalid events. */ }
    })
    hub.on('AccessRevoked', value => { if (isChatRevoked(value, conversationId)) revoke() })
    hub.onreconnecting(() => { joined = false; if (!signal.aborted) setStream('connecting') })
    hub.onreconnected(() => { if (!signal.aborted) void join() })
    hub.onclose(() => { joined = false; if (!signal.aborted) setStream(navigator.onLine ? 'rest' : 'offline') })
    function networkOnline() {
      if (joined && hub.state === HubConnectionState.Connected) setStream('live')
      void refresh()
      void connect()
    }
    function networkOffline() { if (!signal.aborted) setStream('offline') }

    actions.current = {
      async refresh() { setError(''); await refresh(); await connect() },
      async older() {
        if (signal.aborted || !earlier || loadingOlder) return
        loadingOlder = true
        try {
          const page = await messageHistory(context, conversationId, earlier, signal)
          if (signal.aborted) return
          earlier = page.nextBeforeId
          setOlderAvailable(Boolean(earlier))
          merge(page.items)
        } catch (cause) { fail(cause) }
        finally { loadingOlder = false }
      },
      async send(value) {
        if (signal.aborted || writing || !navigator.onLine || !hydrated) return
        if (!intent && !validChatText(value)) return
        const attempt = intent ?? { clientMessageId: crypto.randomUUID(), text: value }
        intent = attempt
        setPending(attempt)
        writing = true
        setBusy(true)
        setError('')
        try {
          merge([await sendConsultationMessage(context, conversationId, attempt, signal)])
          if (!signal.aborted) { setError(''); changed() }
        } catch (cause) {
          // A broadcast can arrive before a POST whose acknowledgement was lost.
          if (intent?.clientMessageId === attempt.clientMessageId || isChatDenied(cause)) {
            fail(cause)
            if (!signal.aborted && cause instanceof AnalysisResultError && [400, 409].includes(cause.status)) {
              intent = null
              setPending(null)
            }
          }
        } finally {
          writing = false
          if (!signal.aborted) setBusy(false)
        }
      },
      async markRead() {
        if (signal.aborted || writing || !navigator.onLine) return
        const last = [...data.values()].sort(sortMessages).at(-1)
        if (!last) return
        writing = true
        setBusy(true)
        setError('')
        try {
          await markConsultationRead(context, conversationId, last.messageId, signal)
          if (signal.aborted) return
          const previous = receipts.get(owner)
          const cursor = previous && data.get(previous.id)
          if (!cursor || sortMessages(cursor, last) < 0) receipts.set(owner, { id: last.messageId, at: previous?.at ?? 0 })
          showMessages()
          changed()
        } catch (cause) { fail(cause) }
        finally {
          writing = false
          if (!signal.aborted) setBusy(false)
        }
      },
    }
    window.addEventListener('online', networkOnline)
    window.addEventListener('offline', networkOffline)
    const poll = window.setInterval(() => {
      if (navigator.onLine && document.visibilityState === 'visible') {
        void refresh()
        void connect()
      }
    }, 20000)
    void refresh().then(() => { if (hydrated) void connect() })
    return () => {
      controller.abort()
      actions.current = null
      data.clear()
      receipts.clear()
      intent = null
      clearInterval(poll)
      window.removeEventListener('online', networkOnline)
      window.removeEventListener('offline', networkOffline)
      hub.off('MessagePersisted')
      hub.off('ReadCursorAdvanced')
      hub.off('AccessRevoked')
      void hub.stop().catch(() => undefined)
    }
  }, [context, conversationId])

  return { messages, draft, setDraft, pending, stream, loading, ready, olderAvailable, busy, error,
    refresh: () => actions.current?.refresh(), older: () => actions.current?.older(),
    send: () => actions.current?.send(draft), markRead: () => actions.current?.markRead() }
}
