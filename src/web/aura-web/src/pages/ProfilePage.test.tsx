import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import ProfilePage from './ProfilePage'
import { BackendSessionContext } from '../auth/backendSession'
import type { BackendContext } from '../api/backendContext'

const mocks = vi.hoisted(() => ({ auth: vi.fn() }))
vi.mock('../firebase/config', () => ({ getFirebaseAuth: mocks.auth }))
vi.mock('../api/client', () => ({ isMockEnabled: false }))

const accountId = '11111111-1111-4111-8111-100000000001'
const patientId = '22222222-2222-4222-8222-200000000002'
const tenantId = '33333333-3333-4333-8333-300000000003'

const context: BackendContext = {
  accountId,
  firebaseUid: 'uid-profile-page',
  roles: ['PATIENT'],
  patientId,
  personalTenantId: tenantId,
  displayName: 'Nguyễn Văn A',
  profileVersion: 1,
}

const fetchMock = vi.fn<typeof fetch>()

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })

function renderPage(ctx: BackendContext = context) {
  return render(
    <MemoryRouter>
      <BackendSessionContext.Provider
        value={{ state: { status: 'ready', context: ctx }, retry: vi.fn(), logout: vi.fn() }}
      >
        <ProfilePage />
      </BackendSessionContext.Provider>
    </MemoryRouter>,
  )
}

beforeEach(() => {
  vi.resetAllMocks()
  mocks.auth.mockReturnValue({
    currentUser: { uid: context.firebaseUid, getIdToken: async () => 'profile-jwt' },
  })
  vi.stubGlobal('fetch', fetchMock)
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('ProfilePage (TC-W6-M6-02)', () => {
  it('displays accountId separately from patientId and updates only displayName/phone with If-Match', async () => {
    fetchMock.mockResolvedValueOnce(
      json({
        accountId,
        displayName: 'Nguyễn Văn A',
        phone: '0901234567',
        patientId,
        personalTenantId: tenantId,
        profileVersion: 2,
        email: 'a@example.test',
      }),
    )

    renderPage()
    await screen.findByText(accountId)
    expect(screen.getByText(patientId)).toBeTruthy()
    expect(screen.getByText('v2')).toBeTruthy()

    fetchMock.mockResolvedValueOnce(
      json({
        accountId,
        displayName: 'Nguyễn Văn B',
        phone: '+84 912345678',
        patientId,
        personalTenantId: tenantId,
        profileVersion: 3,
        email: 'a@example.test',
      }),
    )

    fireEvent.change(screen.getByLabelText('Tên hiển thị *'), {
      target: { value: 'Nguyễn Văn B' },
    })
    fireEvent.change(screen.getByLabelText('Số điện thoại'), {
      target: { value: '+84 912345678' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Lưu thay đổi' }))

    await screen.findByText('Đã cập nhật hồ sơ (phiên bản v3).')
    const patchCall = fetchMock.mock.calls.find(([, init]) => init?.method === 'PATCH')
    expect(patchCall).toBeDefined()
    expect(patchCall?.[1]?.headers).toEqual(
      expect.objectContaining({
        'If-Match': '"2"',
      }),
    )
    const sent = JSON.parse(String(patchCall?.[1]?.body)) as Record<string, unknown>
    expect(sent).toEqual({ displayName: 'Nguyễn Văn B', phone: '+84 912345678' })
    expect(sent.roles).toBeUndefined()
    expect(sent.personalTenantId).toBeUndefined()
  })

  it('validates invalid phone and handles 409 conflict with reload recovery', async () => {
    fetchMock.mockResolvedValueOnce(
      json({
        accountId,
        displayName: 'Nguyễn Văn A',
        phone: null,
        patientId: null,
        personalTenantId: tenantId,
        profileVersion: 1,
        email: 'a@example.test',
      }),
    )

    renderPage()
    await screen.findByText('Chưa cấp')

    fireEvent.change(screen.getByLabelText('Số điện thoại'), {
      target: { value: 'invalid-phone!' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Lưu thay đổi' }))
    await screen.findByText(/Số điện thoại không hợp lệ/i)

    fireEvent.change(screen.getByLabelText('Số điện thoại'), {
      target: { value: '0901234567' },
    })
    fetchMock.mockResolvedValueOnce(
      json(
        {
          error: {
            code: 'ERR_PROFILE_VERSION_CONFLICT',
            message: 'Conflict',
            correlationId: 'c1',
            retryable: false,
          },
        },
        409,
      ),
    )
    fireEvent.click(screen.getByRole('button', { name: 'Lưu thay đổi' }))
    await screen.findByText(/409/i)

    fetchMock.mockResolvedValueOnce(
      json({
        accountId,
        displayName: 'Nguyễn Văn Mới',
        phone: '0901234567',
        patientId,
        personalTenantId: tenantId,
        profileVersion: 5,
        email: 'a@example.test',
      }),
    )
    fireEvent.click(screen.getByRole('button', { name: 'Tải lại hồ sơ mới nhất' }))
    await waitFor(() => expect(screen.getByText('v5')).toBeTruthy())
  })
})
