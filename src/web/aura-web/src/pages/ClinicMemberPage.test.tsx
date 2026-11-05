/// <reference types="node" />
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import ClinicMemberPage from './ClinicMemberPage'
import { BackendSessionContext } from '../auth/backendSession'
import type { BackendContext } from '../api/backendContext'

const m = vi.hoisted(() => ({ auth: vi.fn() }))
vi.mock('../firebase/config', () => ({ getFirebaseAuth: m.auth }))
vi.mock('../api/client', () => ({ isMockEnabled: false }))

const clinicId = '11111111-1111-4111-8111-200000000001'
const adminAccountId = '11111111-1111-4111-8111-500000000001'
const doctorAccountId = '11111111-1111-4111-8111-400000000001'

const context: BackendContext = {
  accountId: adminAccountId,
  patientId: '11111111-1111-4111-8111-300000000009',
  personalTenantId: '11111111-1111-4111-8111-100000000001',
  firebaseUid: 'admin-firebase-uid',
  roles: ['CLINIC'],
  displayName: 'Admin Alpha',
  profileVersion: 1,
}

const json = (data: unknown, status = 200) =>
  new Response(JSON.stringify(data), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })

describe('ClinicMemberPage Component (W5-M6-01 / AC1)', () => {
  const fetchMock = vi.fn<typeof fetch>()

  beforeEach(() => {
    vi.resetAllMocks()
    m.auth.mockReturnValue({
      currentUser: {
        uid: context.firebaseUid,
        getIdToken: async () => 'admin-jwt-token',
      },
    })
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
    vi.restoreAllMocks()
  })

  const renderPage = () =>
    render(
      <MemoryRouter>
        <BackendSessionContext.Provider
          value={{
            state: { status: 'ready', context },
            retry: vi.fn(),
            logout: vi.fn(),
          }}
        >
          <ClinicMemberPage />
        </BackendSessionContext.Provider>
      </MemoryRouter>,
    )

  it('hiển thị danh sách bác sĩ và nhân viên của phòng khám', async () => {
    fetchMock.mockImplementation(async (url) => {
      const path = String(url).split('?')[0]
      if (path === '/api/v1/clinics') {
        return json({
          items: [
            { clinicId, clinicName: 'Phòng khám Đa khoa Alpha', role: 'CLINIC_ADMIN', status: 'ACTIVE' },
          ],
        })
      }
      if (path === `/api/v1/clinics/${clinicId}/memberships`) {
        return json({
          clinicId,
          items: [
            {
              membershipId: '11111111-1111-4111-8111-700000000001',
              clinicId,
              accountId: doctorAccountId,
              displayName: 'BS. Lê Thị Mai',
              role: 'DOCTOR',
              status: 'ACTIVE',
              createdAt: '2026-11-01T00:00:00Z',
              updatedAt: '2026-11-01T00:00:00Z',
              invitedEmail: 'dr.mai@alpha.vn',
            },
          ],
        })
      }
      return json({})
    })

    renderPage()

    expect(await screen.findByText('BS. Lê Thị Mai')).toBeTruthy()
    expect(screen.getByText('Bác sĩ')).toBeTruthy()
    expect(screen.getAllByText('Hoạt động').length).toBeGreaterThanOrEqual(1)
  })

  it('AC1: Cho phép ClinicAdmin mời bác sĩ mới, nhận token kích hoạt và hiển thị trên giao diện', async () => {
    fetchMock.mockImplementation(async (url, init) => {
      const path = String(url).split('?')[0]
      if (path === '/api/v1/clinics') {
        return json({
          items: [
            { clinicId, clinicName: 'Phòng khám Đa khoa Alpha', role: 'CLINIC_ADMIN', status: 'ACTIVE' },
          ],
        })
      }
      if (path === `/api/v1/clinics/${clinicId}/memberships`) {
        return json({
          clinicId,
          items: [
            {
              membershipId: '11111111-1111-4111-8111-700000000001',
              clinicId,
              accountId: doctorAccountId,
              displayName: 'BS. Lê Thị Mai',
              role: 'DOCTOR',
              status: 'ACTIVE',
              createdAt: '2026-11-01T00:00:00Z',
              updatedAt: '2026-11-01T00:00:00Z',
            },
          ],
        })
      }
      if (path === `/api/v1/clinics/${clinicId}/memberships/invite` && init?.method === 'POST') {
        const body = JSON.parse(String(init.body))
        return json(
          {
            membershipId: '11111111-1111-4111-8111-700000000002',
            email: body.email,
            role: body.role,
            token: 'INV-TOKEN-ALPHA-999',
            expiresAt: '2026-11-08T00:00:00Z',
          },
          201,
        )
      }
      return json({})
    })

    renderPage()

    // 1. Mở modal mời bác sĩ
    const openInviteBtn = await screen.findByRole('button', { name: '+ Mời Bác sĩ / Nhân viên' })
    fireEvent.click(openInviteBtn)

    // 2. Điền email và gửi
    const emailInput = screen.getByLabelText('Email tài khoản Google / Firebase:')
    fireEvent.change(emailInput, { target: { value: 'dr.new@alpha.vn' } })

    const submitBtn = screen.getByRole('button', { name: 'Gửi lời mời' })
    fireEvent.click(submitBtn)

    // 3. Kiểm tra hiển thị thông báo thành công và Token kích hoạt
    expect(await screen.findByText('INV-TOKEN-ALPHA-999')).toBeTruthy()
    expect(screen.getByText(/Đã gửi lời mời thành công đến dr.new@alpha.vn!/)).toBeTruthy()
  })

  it('AC1/Revocation: Cho phép ClinicAdmin thu hồi quyền truy cập của bác sĩ tức thì', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true)

    fetchMock.mockImplementation(async (url, init) => {
      const path = String(url).split('?')[0]
      if (path === '/api/v1/clinics') {
        return json({
          items: [
            { clinicId, clinicName: 'Phòng khám Đa khoa Alpha', role: 'CLINIC_ADMIN', status: 'ACTIVE' },
          ],
        })
      }
      if (path === `/api/v1/clinics/${clinicId}/memberships`) {
        return json({
          clinicId,
          items: [
            {
              membershipId: '11111111-1111-4111-8111-700000000001',
              clinicId,
              accountId: doctorAccountId,
              displayName: 'BS. Bị Thu Hồi',
              role: 'DOCTOR',
              status: 'ACTIVE',
              createdAt: '2026-11-01T00:00:00Z',
              updatedAt: '2026-11-01T00:00:00Z',
            },
          ],
        })
      }
      if (path === `/api/v1/clinics/${clinicId}/memberships/${doctorAccountId}` && init?.method === 'DELETE') {
        return new Response(null, { status: 204 })
      }
      return json({})
    })

    renderPage()

    const revokeBtn = await screen.findByRole('button', { name: 'Thu hồi' })
    fireEvent.click(revokeBtn)

    expect(await screen.findByText('Đã thu hồi quyền truy cập thành công!')).toBeTruthy()
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining(`/api/v1/clinics/${clinicId}/memberships/${doctorAccountId}`),
      expect.objectContaining({ method: 'DELETE' }),
    )
  })
})
