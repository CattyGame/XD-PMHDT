/// <reference types="node" />
import { describe, it, expect, vi, beforeEach } from 'vitest'
import {
  getActorClinics,
  getClinicMembers,
  inviteClinicMember,
  revokeClinicMember,
  getClinicPatients,
  registerClinicPatient,
} from './clinicManagement'
import type { BackendContext } from './backendContext'

const m = vi.hoisted(() => ({ auth: vi.fn() }))
vi.mock('../firebase/config', () => ({ getFirebaseAuth: m.auth }))
vi.mock('./client', () => ({ isMockEnabled: false }))

const clinicId = '11111111-1111-4111-8111-200000000001'
const doctorAccountId = '11111111-1111-4111-8111-400000000001'
const patientId = '11111111-1111-4111-8111-300000000001'

const context: BackendContext = {
  accountId: '11111111-1111-4111-8111-500000000001',
  patientId: '11111111-1111-4111-8111-300000000009',
  personalTenantId: '11111111-1111-4111-8111-100000000001',
  firebaseUid: 'admin-uid',
  roles: ['CLINIC'],
  displayName: 'Admin Alpha',
  profileVersion: 1,
}

const json = (data: unknown, status = 200) =>
  new Response(JSON.stringify(data), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })

describe('clinicManagement API client', () => {
  const fetchMock = vi.fn<typeof fetch>()

  beforeEach(() => {
    vi.resetAllMocks()
    m.auth.mockReturnValue({
      currentUser: {
        uid: context.firebaseUid,
        getIdToken: async () => 'mock-jwt-token',
      },
    })
    vi.stubGlobal('fetch', fetchMock)
  })

  it('getActorClinics trả về danh sách clinic hợp lệ', async () => {
    fetchMock.mockResolvedValueOnce(
      json({
        items: [
          {
            clinicId,
            clinicName: 'Phòng khám Alpha',
            role: 'CLINIC_ADMIN',
            status: 'ACTIVE',
          },
        ],
      }),
    )

    const result = await getActorClinics(context)
    expect(result).toHaveLength(1)
    expect(result[0].clinicName).toBe('Phòng khám Alpha')
    expect(result[0].role).toBe('CLINIC_ADMIN')
  })

  it('getClinicMembers lấy danh sách thành viên bác sĩ và staff', async () => {
    fetchMock.mockResolvedValueOnce(
      json({
        clinicId,
        items: [
          {
            membershipId: '11111111-1111-4111-8111-700000000001',
            clinicId,
            accountId: doctorAccountId,
            displayName: 'BS. Nguyễn Văn A',
            role: 'DOCTOR',
            status: 'ACTIVE',
            createdAt: '2026-11-01T00:00:00Z',
            updatedAt: '2026-11-01T00:00:00Z',
          },
        ],
      }),
    )

    const result = await getClinicMembers(context, clinicId)
    expect(result).toHaveLength(1)
    expect(result[0].displayName).toBe('BS. Nguyễn Văn A')
    expect(result[0].role).toBe('DOCTOR')
    expect(result[0].status).toBe('ACTIVE')
  })

  it('inviteClinicMember gửi lời mời bác sĩ thành công và nhận token (AC1)', async () => {
    fetchMock.mockResolvedValueOnce(
      json({
        membershipId: '11111111-1111-4111-8111-700000000002',
        email: 'doctor.b@clinic-alpha.vn',
        role: 'DOCTOR',
        token: 'INV-TOKEN-123456',
        expiresAt: '2026-11-08T00:00:00Z',
      }),
    )

    const result = await inviteClinicMember(context, clinicId, 'doctor.b@clinic-alpha.vn', 'DOCTOR')
    expect(result.email).toBe('doctor.b@clinic-alpha.vn')
    expect(result.token).toBe('INV-TOKEN-123456')
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining(`/api/v1/clinics/${clinicId}/memberships/invite`),
      expect.objectContaining({
        method: 'POST',
        body: JSON.stringify({ email: 'doctor.b@clinic-alpha.vn', role: 'DOCTOR' }),
      }),
    )
  })

  it('revokeClinicMember gọi DELETE để thu hồi quyền truy cập tức thì (AC1 / Revocation)', async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }))

    await expect(
      revokeClinicMember(context, clinicId, doctorAccountId),
    ).resolves.toBeUndefined()

    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining(`/api/v1/clinics/${clinicId}/memberships/${doctorAccountId}`),
      expect.objectContaining({ method: 'DELETE' }),
    )
  })

  it('getClinicPatients lấy danh sách bệnh nhân thuộc phòng khám và hỗ trợ tìm kiếm (AC2)', async () => {
    fetchMock.mockResolvedValueOnce(
      json({
        clinicId,
        totalCount: 1,
        items: [
          {
            patientId,
            clinicId,
            tenantId: '11111111-1111-4111-8111-200000000001',
            patientType: 'CLINIC_MANAGED',
            fullName: 'Trần Thị Bệnh Nhân',
            dateOfBirth: '1985-05-20',
            gender: 'FEMALE',
            phone: '0901234567',
            healthInsuranceNumber: 'DN4791234567890',
            medicalHistory: {
              hasDiabetes: true,
              hasHypertension: false,
              notes: 'Tái khám định kỳ võng mạc',
            },
            status: 'ACTIVE',
            createdAt: '2026-11-01T00:00:00Z',
            updatedAt: '2026-11-01T00:00:00Z',
          },
        ],
      }),
    )

    const result = await getClinicPatients(context, clinicId, { search: 'Trần Thị' })
    expect(result.items).toHaveLength(1)
    expect(result.items[0].fullName).toBe('Trần Thị Bệnh Nhân')
    expect(result.items[0].medicalHistory?.hasDiabetes).toBe(true)
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining(`search=Tr%E1%BA%A7n+Th%E1%BB%8B`),
      expect.anything(),
    )
  })

  it('registerClinicPatient đăng ký thành công hồ sơ bệnh nhân mới', async () => {
    fetchMock.mockResolvedValueOnce(
      json({
        patientId,
        clinicId,
        tenantId: '11111111-1111-4111-8111-200000000001',
        patientType: 'CLINIC_MANAGED',
        fullName: 'Lê Văn Mới',
        dateOfBirth: '1990-10-10',
        gender: 'MALE',
        phone: '0988776655',
        healthInsuranceNumber: null,
        medicalHistory: { hasDiabetes: false, hasHypertension: true },
        status: 'ACTIVE',
        createdAt: '2026-11-05T00:00:00Z',
        updatedAt: '2026-11-05T00:00:00Z',
      }),
    )

    const patient = await registerClinicPatient(context, clinicId, {
      fullName: 'Lê Văn Mới',
      dateOfBirth: '1990-10-10',
      gender: 'MALE',
      phone: '0988776655',
      medicalHistory: { hasDiabetes: false, hasHypertension: true },
    })

    expect(patient.fullName).toBe('Lê Văn Mới')
    expect(patient.gender).toBe('MALE')
  })
})
