import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  formatGlobalRole,
  listAdminAccounts,
  listAdminAuditEvents,
  listAdminClinics,
  setDoctorCapability,
  updateAdminAccountRoles,
  updateAdminAccountStatus,
  updateAdminClinicStatus,
} from './adminManagement'
import type { BackendContext } from './backendContext'

const mocks = vi.hoisted(() => ({ auth: vi.fn() }))
vi.mock('../firebase/config', () => ({ getFirebaseAuth: mocks.auth }))
vi.mock('./client', () => ({ isMockEnabled: false }))

const accountId = '11111111-1111-4111-8111-100000000001'
const clinicId = '22222222-2222-4222-8222-200000000002'
const tenantId = '33333333-3333-4333-8333-300000000003'

const adminContext: BackendContext = {
  accountId: '99999999-9999-4999-8999-900000000009',
  firebaseUid: 'uid-superadmin',
  roles: ['SYSTEM_ADMIN'],
  patientId: '88888888-8888-4888-8888-800000000008',
  personalTenantId: '77777777-7777-4777-8777-700000000007',
  displayName: 'SuperAdmin User',
  profileVersion: 1,
}

const fetchMock = vi.fn<typeof fetch>()

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })

beforeEach(() => {
  vi.resetAllMocks()
  mocks.auth.mockReturnValue({
    currentUser: { uid: adminContext.firebaseUid, getIdToken: async () => 'admin-jwt' },
  })
  vi.stubGlobal('fetch', fetchMock)
})

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('adminManagement API client (W6-M6-02)', () => {
  it('formats SYSTEM_ADMIN canonically as SuperAdmin', () => {
    expect(formatGlobalRole('SYSTEM_ADMIN')).toBe('SuperAdmin')
    expect(formatGlobalRole('DOCTOR')).toBe('DOCTOR')
    expect(formatGlobalRole('PATIENT')).toBe('PATIENT')
  })

  it('lists and updates admin clinics with expectedVersion and reason', async () => {
    const clinicDto = {
      clinicId,
      tenantId,
      name: 'Phòng khám Mắt A',
      taxId: '0101234567',
      address: 'TP.HCM',
      representativeName: 'BS. Lan',
      phone: '0901112222',
      status: 'PENDING',
      auditReason: null,
      createdAt: '2026-11-18T00:00:00Z',
      updatedAt: '2026-11-18T00:00:00Z',
      version: 1,
    }
    fetchMock.mockResolvedValueOnce(json({ items: [clinicDto], nextCursor: null }))

    const page = await listAdminClinics(adminContext, { status: 'PENDING' })
    expect(page.items).toHaveLength(1)
    expect(page.items[0].status).toBe('PENDING')

    fetchMock.mockResolvedValueOnce(
      json({ ...clinicDto, status: 'ACTIVE', auditReason: 'Đủ giấy phép', version: 2 }),
    )
    const updated = await updateAdminClinicStatus(adminContext, clinicId, {
      status: 'ACTIVE',
      expectedVersion: 1,
      reason: 'Đủ giấy phép',
    })
    expect(updated.status).toBe('ACTIVE')
    expect(updated.version).toBe(2)
  })

  it('lists accounts, updates status/roles, sets doctor capability, and reads audit events', async () => {
    const accountDto = {
      accountId,
      firebaseUid: 'fb-1',
      displayName: 'Bác sĩ B',
      status: 'ACTIVE',
      accountStatus: 'ACTIVE',
      roles: ['PATIENT'],
      version: 2,
      profileProvisioningStatus: 'READY',
      createdAt: '2026-11-18T00:00:00Z',
      updatedAt: '2026-11-18T00:00:00Z',
    }
    fetchMock.mockResolvedValueOnce(json({ items: [accountDto], nextCursor: null }))
    const accounts = await listAdminAccounts(adminContext, { query: 'Bác sĩ' })
    expect(accounts.items[0].accountId).toBe(accountId)

    fetchMock.mockResolvedValueOnce(
      json({ ...accountDto, status: 'DISABLED', accountStatus: 'DISABLED', version: 3 }),
    )
    const suspended = await updateAdminAccountStatus(adminContext, accountId, {
      status: 'DISABLED',
      expectedVersion: 2,
      reason: 'Vi phạm quy chế',
    })
    expect(suspended.status).toBe('DISABLED')

    fetchMock.mockResolvedValueOnce(
      json({ ...accountDto, roles: ['PATIENT', 'DOCTOR'], version: 4 }),
    )
    const roleUpdated = await updateAdminAccountRoles(adminContext, accountId, {
      roles: ['PATIENT', 'DOCTOR'],
      expectedVersion: 3,
      reason: 'Bổ sung vai trò bác sĩ',
    })
    expect(roleUpdated.roles).toEqual(['PATIENT', 'DOCTOR'])

    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }))
    await setDoctorCapability(adminContext, accountId, {
      approved: true,
      reason: 'Xác minh chứng chỉ hành nghề',
    })
    expect(fetchMock).toHaveBeenLastCalledWith(
      `/api/v1/admin/accounts/${accountId}/doctor-capability`,
      expect.objectContaining({ method: 'PUT' }),
    )

    fetchMock.mockResolvedValueOnce(
      json({
        items: [
          {
            eventId: '44444444-4444-4444-8444-400000000004',
            actorAccountId: adminContext.accountId,
            action: 'CLINIC_STATUS_UPDATED',
            targetType: 'CLINIC',
            targetId: clinicId,
            reason: 'Đủ giấy phép',
            correlationId: 'corr-1',
            createdAt: '2026-11-18T00:00:00Z',
          },
        ],
        nextCursor: null,
      }),
    )
    const audit = await listAdminAuditEvents(adminContext)
    expect(audit.items).toHaveLength(1)
  })
})
