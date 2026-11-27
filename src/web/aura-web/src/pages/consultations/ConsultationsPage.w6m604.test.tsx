import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import ProtectedRoute from '../../routes/ProtectedRoute'
import ConsultationsPage from './ConsultationsPage'
import { BackendSessionContext } from '../../auth/backendSession'
import type { BackendContext } from '../../api/backendContext'

const mocks = vi.hoisted(() => ({ auth: vi.fn(), mock: false }))
vi.mock('../../firebase/config', () => ({ getFirebaseAuth: mocks.auth }))
vi.mock('../../api/client', () => ({ get isMockEnabled() { return mocks.mock } }))
vi.mock('@microsoft/signalr', () => {
  class HubConnectionBuilder {
    withUrl() { return this }
    withAutomaticReconnect() { return this }
    configureLogging() { return this }
    build() {
      return {
        state: 'Disconnected',
        on: vi.fn(),
        off: vi.fn(),
        onclose: vi.fn(),
        onreconnecting: vi.fn(),
        onreconnected: vi.fn(),
        start: vi.fn(async () => undefined),
        stop: vi.fn(async () => undefined),
        invoke: vi.fn(async () => undefined),
      }
    }
  }
  return {
    HubConnectionBuilder,
    HubConnectionState: {
      Disconnected: 'Disconnected',
      Connecting: 'Connecting',
      Connected: 'Connected',
      Disconnecting: 'Disconnecting',
      Reconnecting: 'Reconnecting',
    },
    HttpTransportType: { WebSockets: 1, ServerSentEvents: 2 },
    LogLevel: { Warning: 3 },
  }
})

const conversationId = '11111111-1111-4111-8111-100000000001'
const analysisId = '11111111-1111-4111-8111-200000000002'
const clinicContextId = '11111111-1111-4111-8111-300000000003'
const accountId = '11111111-1111-4111-8111-400000000004'

const fetchMock = vi.fn<typeof fetch>()
const json = (payload: unknown, status = 200) =>
  new Response(JSON.stringify(payload), { status, headers: { 'Content-Type': 'application/json' } })

function renderDeepLink(state: { status: 'anonymous' } | { status: 'ready'; context: BackendContext }, initialUrl: string) {
  return render(
    <MemoryRouter initialEntries={[initialUrl]}>
      <BackendSessionContext.Provider value={{ state, retry: vi.fn(), logout: vi.fn() }}>
        <Routes>
          <Route path="/login" element={<main>Trang đăng nhập AURA</main>} />
          <Route path="/403" element={<main>403 · Không có quyền</main>} />
          <Route path="/consultations/:conversationId" element={<ProtectedRoute><ConsultationsPage /></ProtectedRoute>} />
        </Routes>
      </BackendSessionContext.Provider>
    </MemoryRouter>
  )
}

beforeEach(() => {
  vi.resetAllMocks()
  mocks.mock = false
  mocks.auth.mockReturnValue({ currentUser: { uid: 'uid-w6m604', getIdToken: async () => 'jwt-w6m604' } })
  vi.stubGlobal('fetch', fetchMock)
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('TC-W6-M6-04 — Consultation FCM deep-link authorization and re-auth', () => {
  it('requires authentication when opening /consultations/:conversationId while anonymous', async () => {
    renderDeepLink({ status: 'anonymous' }, `/consultations/${conversationId}`)
    expect(await screen.findByText('Trang đăng nhập AURA')).toBeTruthy()
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('opens the target consultation thread for an authorized participant', async () => {
    fetchMock.mockImplementation(async input => {
      const path = String(input).split('?')[0]
      if (path === '/api/v1/consultations') {
        return json({ items: [{ conversationId, analysisId, clinicContextId, status: 'OPEN', unreadCount: 0 }], nextCursor: null })
      }
      if (path === `/api/v1/consultations/${conversationId}/messages`) {
        return json({
          items: [{
            messageId: '11111111-1111-4111-8111-500000000005',
            conversationId,
            clientMessageId: '11111111-1111-4111-8111-600000000006',
            senderAccountId: accountId,
            text: 'Tin nhắn được cấp quyền',
            sentAt: '2026-11-27T10:00:00Z',
            deliveryState: 'PERSISTED',
          }],
          nextBeforeId: null,
        })
      }
      return json({ error: { code: 'ERR_NOT_FOUND' } }, 404)
    })

    const context: BackendContext = {
      firebaseUid: 'uid-w6m604', accountId, patientId: '11111111-1111-4111-8111-700000000007',
      personalTenantId: '11111111-1111-4111-8111-800000000008', roles: ['DOCTOR'],
      displayName: 'Doctor W6', profileVersion: 1,
    }
    renderDeepLink({ status: 'ready', context }, `/consultations/${conversationId}`)
    expect(await screen.findByText('Tin nhắn được cấp quyền')).toBeTruthy()
  })

  it('denies deep-link when conversation is not in authorized list (revoked or cross-clinic)', async () => {
    fetchMock.mockImplementation(async input => {
      const path = String(input).split('?')[0]
      if (path === '/api/v1/consultations') return json({ items: [], nextCursor: null })
      return json({ error: { code: 'ERR_FORBIDDEN' } }, 403)
    })

    const context: BackendContext = {
      firebaseUid: 'uid-w6m604', accountId, patientId: '11111111-1111-4111-8111-700000000007',
      personalTenantId: '11111111-1111-4111-8111-800000000008', roles: ['PATIENT'],
      displayName: 'Patient W6', profileVersion: 1,
    }
    renderDeepLink({ status: 'ready', context }, `/consultations/${conversationId}`)
    await waitFor(() => expect(screen.getByRole('alert').textContent).toContain('403'))
    expect(fetchMock).not.toHaveBeenCalledWith(expect.stringContaining('/messages'), expect.anything())
  })

  it('redirects clinic admin / staff without PATIENT or DOCTOR role to 403', async () => {
    const context: BackendContext = {
      firebaseUid: 'uid-w6m604', accountId, patientId: '11111111-1111-4111-8111-700000000007',
      personalTenantId: '11111111-1111-4111-8111-800000000008', roles: ['SYSTEM_ADMIN'],
      displayName: 'Admin W6', profileVersion: 1,
    }
    renderDeepLink({ status: 'ready', context }, `/consultations/${conversationId}`)
    expect(await screen.findByText('403 · Không có quyền')).toBeTruthy()
    expect(fetchMock).not.toHaveBeenCalled()
  })
})
