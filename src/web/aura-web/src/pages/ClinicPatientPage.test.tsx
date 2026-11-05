/// <reference types="node" />
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import ClinicPatientPage from './ClinicPatientPage'
import { BackendSessionContext } from '../auth/backendSession'
import type { BackendContext } from '../api/backendContext'

const m = vi.hoisted(() => ({ auth: vi.fn() }))
vi.mock('../firebase/config', () => ({ getFirebaseAuth: m.auth }))
vi.mock('../api/client', () => ({ isMockEnabled: false }))

const clinicAlphaId = '11111111-1111-4111-8111-200000000001'
const clinicBetaId = '11111111-1111-4111-8111-200000000002'

const doctorAccountId = '11111111-1111-4111-8111-400000000001'
const patientAlphaId = '11111111-1111-4111-8111-300000000001'
const patientBetaId = '11111111-1111-4111-8111-300000000002'

const doctorContext: BackendContext = {
  accountId: doctorAccountId,
  patientId: '11111111-1111-4111-8111-300000000099',
  personalTenantId: '11111111-1111-4111-8111-100000000001',
  firebaseUid: 'doctor-firebase-uid',
  roles: ['DOCTOR'],
  displayName: 'BS. Trần Văn B',
  profileVersion: 1,
}

const json = (data: unknown, status = 200) =>
  new Response(JSON.stringify(data), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })

describe('ClinicPatientPage Component (W5-M6-01 / AC2)', () => {
  const fetchMock = vi.fn<typeof fetch>()

  beforeEach(() => {
    vi.resetAllMocks()
    m.auth.mockReturnValue({
      currentUser: {
        uid: doctorContext.firebaseUid,
        getIdToken: async () => 'doctor-jwt-token',
      },
    })
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
    vi.restoreAllMocks()
  })

  const renderPage = (context: BackendContext = doctorContext) =>
    render(
      <MemoryRouter>
        <BackendSessionContext.Provider
          value={{
            state: { status: 'ready', context },
            retry: vi.fn(),
            logout: vi.fn(),
          }}
        >
          <ClinicPatientPage />
        </BackendSessionContext.Provider>
      </MemoryRouter>,
    )

  it('AC2: Bác sĩ chỉ nhìn thấy danh sách bệnh nhân thuộc phòng khám của mình, tuyệt đối không thấy bệnh nhân của clinic khác', async () => {
    fetchMock.mockImplementation(async (url) => {
      const urlStr = String(url)
      const path = urlStr.split('?')[0]
      if (path === '/api/v1/clinics') {
        return json({
          items: [
            { clinicId: clinicAlphaId, clinicName: 'Phòng khám Alpha', role: 'DOCTOR', status: 'ACTIVE' },
          ],
        })
      }
      if (path === `/api/v1/clinics/${clinicAlphaId}/patients`) {
        return json({
          clinicId: clinicAlphaId,
          totalCount: 1,
          items: [
            {
              patientId: patientAlphaId,
              clinicId: clinicAlphaId,
              tenantId: '11111111-1111-4111-8111-200000000001',
              patientType: 'CLINIC_MANAGED',
              fullName: 'Bệnh Nhân Alpha',
              dateOfBirth: '1980-01-01',
              gender: 'MALE',
              phone: '0911223344',
              healthInsuranceNumber: 'DN4012345678',
              medicalHistory: { hasDiabetes: true, hasHypertension: false, notes: 'Theo dõi võng mạc tiểu đường' },
              status: 'ACTIVE',
              createdAt: '2026-11-01T00:00:00Z',
              updatedAt: '2026-11-01T00:00:00Z',
            },
          ],
        })
      }
      if (path === `/api/v1/clinics/${clinicBetaId}/patients`) {
        return json({
          clinicId: clinicBetaId,
          totalCount: 1,
          items: [
            {
              patientId: patientBetaId,
              clinicId: clinicBetaId,
              tenantId: '11111111-1111-4111-8111-200000000002',
              patientType: 'CLINIC_MANAGED',
              fullName: 'Bệnh Nhân Ngoại Lai Beta',
              dateOfBirth: '1975-02-02',
              gender: 'FEMALE',
              status: 'ACTIVE',
            },
          ],
        })
      }
      return json({})
    })

    renderPage()

    // Bác sĩ thấy Bệnh Nhân Alpha
    expect(await screen.findByText('Bệnh Nhân Alpha')).toBeTruthy()
    expect(screen.getByText('Đái tháo đường')).toBeTruthy()

    // Tuyệt đối KHÔNG thấy bệnh nhân của Clinic Beta
    expect(screen.queryByText('Bệnh Nhân Ngoại Lai Beta')).toBeNull()
  })

  it('Cho phép tìm kiếm bệnh nhân và xem chi tiết hồ sơ bệnh án', async () => {
    fetchMock.mockImplementation(async (url) => {
      const urlStr = String(url)
      const path = urlStr.split('?')[0]
      if (path === '/api/v1/clinics') {
        return json({
          items: [
            { clinicId: clinicAlphaId, clinicName: 'Phòng khám Alpha', role: 'DOCTOR', status: 'ACTIVE' },
          ],
        })
      }
      if (path === `/api/v1/clinics/${clinicAlphaId}/patients`) {
        return json({
          clinicId: clinicAlphaId,
          totalCount: 1,
          items: [
            {
              patientId: patientAlphaId,
              clinicId: clinicAlphaId,
              tenantId: '11111111-1111-4111-8111-200000000001',
              patientType: 'CLINIC_MANAGED',
              fullName: 'Nguyễn Thị Hoa',
              dateOfBirth: '1992-06-15',
              gender: 'FEMALE',
              phone: '0987654321',
              healthInsuranceNumber: 'DN479998877',
              medicalHistory: { hasDiabetes: false, hasHypertension: true, notes: 'Mạch máu co thắt do huyết áp' },
              status: 'ACTIVE',
              createdAt: '2026-11-01T00:00:00Z',
              updatedAt: '2026-11-01T00:00:00Z',
            },
          ],
        })
      }
      if (path === `/api/v1/clinics/${clinicAlphaId}/patients/${patientAlphaId}`) {
        return json({
          patientId: patientAlphaId,
          clinicId: clinicAlphaId,
          tenantId: '11111111-1111-4111-8111-200000000001',
          patientType: 'CLINIC_MANAGED',
          fullName: 'Nguyễn Thị Hoa',
          dateOfBirth: '1992-06-15',
          gender: 'FEMALE',
          phone: '0987654321',
          healthInsuranceNumber: 'DN479998877',
          medicalHistory: { hasDiabetes: false, hasHypertension: true, notes: 'Mạch máu co thắt do huyết áp' },
          status: 'ACTIVE',
        })
      }
      return json({})
    })

    renderPage()

    expect(await screen.findByText('Nguyễn Thị Hoa')).toBeTruthy()

    // Bấm xem chi tiết
    const detailBtn = screen.getByRole('button', { name: 'Chi tiết' })
    fireEvent.click(detailBtn)

    // Xác nhận mở card chi tiết
    expect(await screen.findByRole('heading', { name: 'Hồ Sơ Bệnh Án: Nguyễn Thị Hoa' })).toBeTruthy()
    expect(screen.getByText('Mạch máu co thắt do huyết áp')).toBeTruthy()
  })

  it('Cho phép ClinicAdmin / Staff đăng ký tiếp nhận hồ sơ bệnh nhân mới', async () => {
    const adminContext: BackendContext = {
      ...doctorContext,
      roles: ['CLINIC'],
    }

    fetchMock.mockImplementation(async (url, init) => {
      const urlStr = String(url)
      const path = urlStr.split('?')[0]
      if (path === '/api/v1/clinics') {
        return json({
          items: [
            { clinicId: clinicAlphaId, clinicName: 'Phòng khám Alpha', role: 'CLINIC_ADMIN', status: 'ACTIVE' },
          ],
        })
      }
      if (path === `/api/v1/clinics/${clinicAlphaId}/patients` && init?.method === 'POST') {
        const body = JSON.parse(String(init.body))
        return json(
          {
            patientId: '11111111-1111-4111-8111-300000000099',
            clinicId: clinicAlphaId,
            tenantId: '11111111-1111-4111-8111-200000000001',
            patientType: 'CLINIC_MANAGED',
            fullName: body.fullName,
            dateOfBirth: body.dateOfBirth,
            gender: body.gender,
            phone: body.phone,
            healthInsuranceNumber: body.healthInsuranceNumber,
            medicalHistory: body.medicalHistory,
            status: 'ACTIVE',
            createdAt: '2026-11-05T00:00:00Z',
            updatedAt: '2026-11-05T00:00:00Z',
          },
          201,
        )
      }
      if (path === `/api/v1/clinics/${clinicAlphaId}/patients`) {
        return json({ clinicId: clinicAlphaId, totalCount: 0, items: [] })
      }
      return json({})
    })

    renderPage(adminContext)

    // Mở modal tiếp nhận
    const openBtn = await screen.findByRole('button', { name: '+ Đăng ký Bệnh nhân mới' })
    fireEvent.click(openBtn)

    // Điền form
    fireEvent.change(screen.getByLabelText('Họ và tên bệnh nhân (*):'), { target: { value: 'Phạm Văn Nam' } })
    fireEvent.change(screen.getByLabelText('Số điện thoại:'), { target: { value: '0933221100' } })

    // Submit
    fireEvent.click(screen.getByRole('button', { name: 'Lưu hồ sơ' }))

    // Kiểm tra thông báo tiếp nhận thành công
    expect(await screen.findByText('Đã tiếp nhận hồ sơ bệnh nhân "Phạm Văn Nam" thành công!')).toBeTruthy()
  })
})

