import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import AdminManagementPage from './AdminManagementPage'
import { BackendSessionContext } from '../auth/backendSession'
import type { BackendContext } from '../api/backendContext'

const mocks = vi.hoisted(() => ({ auth: vi.fn() }))
vi.mock('../firebase/config', () => ({ getFirebaseAuth: mocks.auth }))
vi.mock('../api/client', () => ({ isMockEnabled: false }))

const adminId = '99999999-9999-4999-8999-900000000009'
const targetAccountId = '11111111-1111-4111-8111-100000000001'
const clinicId = '22222222-2222-4222-8222-200000000002'
const tenantId = '33333333-3333-4333-8333-300000000003'

const adminContext: BackendContext = {
  accountId: adminId,
  firebaseUid: 'uid-admin',
  roles: ['SYSTEM_ADMIN'],
  patientId: '88888888-8888-4888-8888-800000000008',
  personalTenantId: '77777777-7777-4777-8777-700000000007',
  displayName: 'Admin',
  profileVersion: 1,
}

const patientContext: BackendContext = {
  ...adminContext,
  firebaseUid: 'uid-patient',
  roles: ['PATIENT'],
  displayName: 'Patient',
}

const fetchMock = vi.fn<typeof fetch>()

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })

function renderPage(ctx: BackendContext = adminContext) {
  return render(
    <MemoryRouter>
      <BackendSessionContext.Provider
        value={{ state: { status: 'ready', context: ctx }, retry: vi.fn(), logout: vi.fn() }}
      >
        <AdminManagementPage />
      </BackendSessionContext.Provider>
    </MemoryRouter>,
  )
}

const baseClinic = {
  clinicId,
  tenantId,
  name: 'Phòng khám Mắt Ánh Sáng',
  taxId: '0109998888',
  address: 'Quận 1, TP.HCM',
  representativeName: 'BS. Minh',
  phone: '02838221100',
  status: 'PENDING',
  auditReason: null,
  createdAt: '2026-11-18T00:00:00Z',
  updatedAt: '2026-11-18T00:00:00Z',
  version: 1,
}

const baseAccount = {
  accountId: targetAccountId,
  firebaseUid: 'fb-target-1',
  displayName: 'BS. Trần Văn C',
  status: 'ACTIVE',
  accountStatus: 'ACTIVE',
  roles: ['PATIENT', 'SYSTEM_ADMIN'],
  version: 2,
  profileProvisioningStatus: 'READY',
  createdAt: '2026-11-18T00:00:00Z',
  updatedAt: '2026-11-18T00:00:00Z',
}

beforeEach(() => {
  vi.resetAllMocks()
  mocks.auth.mockReturnValue({
    currentUser: { uid: adminContext.firebaseUid, getIdToken: async () => 'admin-jwt' },
  })
  vi.stubGlobal('fetch', fetchMock)
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('AdminManagementPage (TC-W6-M6-02)', () => {
  it('blocks non-SYSTEM_ADMIN users with 403 and makes zero admin API calls', async () => {
    mocks.auth.mockReturnValue({
      currentUser: { uid: patientContext.firebaseUid, getIdToken: async () => 'patient-jwt' },
    })
    renderPage(patientContext)
    await screen.findByText(/403 — Không có quyền truy cập/i)
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('approves clinic with mandatory reason, handles 409 conflict, and never claims email was sent', async () => {
    fetchMock.mockImplementation(async (input, init) => {
      const url = String(input)
      if (url.startsWith('/api/v1/admin/clinics?')) {
        return json({ items: [baseClinic], nextCursor: null })
      }
      if (url === `/api/v1/admin/clinics/${clinicId}/status` && init?.method === 'PATCH') {
        return json({
          ...baseClinic,
          status: 'ACTIVE',
          auditReason: 'Hồ sơ hợp lệ',
          version: 2,
        })
      }
      return json({ items: [], nextCursor: null })
    })

    renderPage()
    await screen.findByText(baseClinic.name)
    expect(screen.getByText(/SuperAdmin/i)).toBeTruthy()
    expect(screen.getByText(/chưa cấu hình nhà cung cấp email/i)).toBeTruthy()

    fireEvent.click(screen.getByRole('button', { name: 'Duyệt (ACTIVE)' }))
    fireEvent.click(screen.getByRole('button', { name: 'Xác nhận' }))
    await screen.findByText(/Lý do thao tác là bắt buộc/i)

    fireEvent.change(screen.getByLabelText(/Lý do thực hiện/i), {
      target: { value: 'Hồ sơ hợp lệ' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Xác nhận' }))

    await screen.findByText(/Đã cập nhật trạng thái cơ sở "Phòng khám Mắt Ánh Sáng" thành ACTIVE/i)
    expect(screen.getByText(/chưa gửi email do chưa cấu hình nhà cung cấp email/i)).toBeTruthy()

    // Now test 409 version conflict on suspend
    fetchMock.mockResolvedValueOnce(
      json(
        {
          error: {
            code: 'ERR_VERSION_CONFLICT',
            message: 'Version conflict',
            correlationId: 'c-409',
            retryable: false,
          },
        },
        409,
      ),
    )
    fireEvent.click(screen.getByRole('button', { name: 'Tạm ngưng' }))
    fireEvent.change(screen.getByLabelText(/Lý do thực hiện/i), {
      target: { value: 'Cần rà soát lại' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Xác nhận' }))
    await screen.findByText(/409 Version Conflict/i)
  })

  it('displays SuperAdmin for SYSTEM_ADMIN, updates account status/roles/doctor-capability and loads audit events', async () => {
    fetchMock.mockImplementation(async (input, init) => {
      const url = String(input)
      if (url.startsWith('/api/v1/admin/clinics')) return json({ items: [], nextCursor: null })
      if (url.startsWith('/api/v1/admin/accounts?')) return json({ items: [baseAccount], nextCursor: null })
      if (url === `/api/v1/admin/accounts/${targetAccountId}/status` && init?.method === 'PATCH') {
        return json({ ...baseAccount, status: 'DISABLED', accountStatus: 'DISABLED', version: 3 })
      }
      if (url === `/api/v1/admin/accounts/${targetAccountId}/doctor-capability` && init?.method === 'PUT') {
        return new Response(null, { status: 204 })
      }
      if (url.startsWith('/api/v1/admin/audit-events')) {
        return json({
          items: [
            {
              eventId: '55555555-5555-4555-8555-500000000005',
              actorAccountId: adminId,
              action: 'ACCOUNT_STATUS_UPDATED',
              targetType: 'ACCOUNT',
              targetId: targetAccountId,
              reason: 'Kiểm tra định kỳ',
              correlationId: 'corr-audit',
              createdAt: '2026-11-18T12:00:00Z',
            },
          ],
          nextCursor: null,
        })
      }
      return json({ items: [], nextCursor: null })
    })

    renderPage()
    fireEvent.click(screen.getByRole('tab', { name: 'Quản lý Tài khoản' }))
    await screen.findByText(baseAccount.displayName)
    expect(screen.getByText('PATIENT, SuperAdmin')).toBeTruthy()

    fireEvent.click(screen.getByRole('button', { name: 'Tạm khóa' }))
    fireEvent.change(screen.getByLabelText(/Lý do thực hiện/i), {
      target: { value: 'Kiểm tra định kỳ' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Xác nhận' }))
    await screen.findByText(/Đã cập nhật trạng thái tài khoản BS\. Trần Văn C thành DISABLED/i)

    fireEvent.click(screen.getByRole('button', { name: 'Duyệt quyền Bác sĩ' }))
    fireEvent.change(screen.getByLabelText(/Lý do thực hiện/i), {
      target: { value: 'Xác nhận chứng chỉ' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Xác nhận' }))
    await waitFor(() =>
      expect(
        screen.getByText(/Đã phê duyệt năng lực bác sĩ cho BS\. Trần Văn C/i),
      ).toBeTruthy(),
    )

    fireEvent.click(screen.getByRole('tab', { name: 'Nhật ký kiểm toán' }))
    await screen.findByText('ACCOUNT_STATUS_UPDATED')
  })
})
