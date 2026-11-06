/// <reference types="node" />
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import CampaignManagementPage from './CampaignManagementPage'
import { BackendSessionContext } from '../auth/backendSession'
import type { BackendContext } from '../api/backendContext'

const m = vi.hoisted(() => ({ auth: vi.fn() }))
vi.mock('../firebase/config', () => ({ getFirebaseAuth: m.auth }))
vi.mock('../api/client', () => ({ isMockEnabled: false }))

const clinicAlphaId = '11111111-1111-4111-8111-200000000001'
const clinicBetaId = '11111111-1111-4111-8111-200000000002'

const campaignAlphaId = '22222222-2222-4222-8222-200000000001'
const campaignBetaId = '22222222-2222-4222-8222-200000000002'
const batchId = '33333333-3333-4333-8333-300000000001'

const adminContext: BackendContext = {
  accountId: '11111111-1111-4111-8111-500000000001',
  patientId: '11111111-1111-4111-8111-300000000009',
  personalTenantId: '11111111-1111-4111-8111-100000000001',
  firebaseUid: 'admin-firebase-uid',
  roles: ['CLINIC'],
  displayName: 'Admin Phòng Khám Alpha',
  profileVersion: 1,
}

const json = (data: unknown, status = 200) =>
  new Response(JSON.stringify(data), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })

describe('CampaignManagementPage Component (W5-M6-02 / AC1 & AC2)', () => {
  const fetchMock = vi.fn<typeof fetch>()

  beforeEach(() => {
    vi.resetAllMocks()
    m.auth.mockReturnValue({
      currentUser: {
        uid: adminContext.firebaseUid,
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

  const renderPage = (context: BackendContext = adminContext) =>
    render(
      <MemoryRouter>
        <BackendSessionContext.Provider
          value={{
            state: { status: 'ready', context },
            retry: vi.fn(),
            logout: vi.fn(),
          }}
        >
          <CampaignManagementPage />
        </BackendSessionContext.Provider>
      </MemoryRouter>,
    )

  it('AC1: Quản trị viên Clinic tạo chiến dịch khám mắt cộng đồng, gán batch chụp ảnh thành công', async () => {
    fetchMock.mockImplementation(async (url, init) => {
      const urlStr = String(url)
      const path = urlStr.split('?')[0]
      const method = init?.method ?? 'GET'

      if (path === '/api/v1/clinics') {
        return json({
          items: [
            { clinicId: clinicAlphaId, clinicName: 'Phòng khám Đa khoa Alpha', role: 'CLINIC_ADMIN', status: 'ACTIVE' },
          ],
        })
      }

      if (path === '/api/v1/campaigns' && method === 'GET') {
        return json([
          {
            id: campaignAlphaId,
            tenantId: clinicAlphaId,
            name: 'Chiến dịch Khám Mắt Cộng Đồng Q5',
            location: 'Trung tâm Y tế Quận 5',
            targetPatientCount: 150,
            status: 'PLANNED',
            startDate: '2026-11-01T00:00:00Z',
            endDate: '2026-11-10T00:00:00Z',
            createdAt: '2026-11-01T00:00:00Z',
            updatedAt: '2026-11-01T00:00:00Z',
          },
        ])
      }

      if (path === `/api/v1/campaigns/${campaignAlphaId}/batches` && method === 'POST') {
        return json({ message: 'Liên kết batch vào chiến dịch tầm soát thành công.' })
      }

      return json({})
    })

    renderPage()

    // 1. Tải thành công chiến dịch
    expect(await screen.findByText('Chiến dịch Khám Mắt Cộng Đồng Q5')).toBeTruthy()
    expect(screen.getByText('Trung tâm Y tế Quận 5')).toBeTruthy()
    expect(screen.getByText('150 người')).toBeTruthy()

    // 2. Mở modal Gắn Batch
    const linkButtons = screen.getAllByRole('button', { name: 'Gắn Batch' })
    fireEvent.click(linkButtons[0])

    expect(await screen.findByText('Gắn Batch Ảnh Vào Chiến Dịch')).toBeTruthy()

    // 3. Nhập mã Batch ID và submit
    const batchInput = screen.getByLabelText(/Mã Batch/i)
    fireEvent.change(batchInput, { target: { value: batchId } })

    const submitBtn = screen.getByRole('button', { name: 'Xác nhận Gắn Batch' })
    fireEvent.click(submitBtn)

    // 4. Xác nhận thông báo thành công
    expect(await screen.findByText('Liên kết batch vào chiến dịch tầm soát thành công.')).toBeTruthy()
  })

  it('AC2: Dữ liệu chiến dịch hoàn toàn cô lập theo clinic_id (chặn rò rỉ chéo tenant)', async () => {
    fetchMock.mockImplementation(async (url) => {
      const urlStr = String(url)
      const path = urlStr.split('?')[0]

      if (path === '/api/v1/clinics') {
        return json({
          items: [
            { clinicId: clinicAlphaId, clinicName: 'Phòng khám Alpha', role: 'CLINIC_ADMIN', status: 'ACTIVE' },
            { clinicId: clinicBetaId, clinicName: 'Phòng khám Beta', role: 'CLINIC_ADMIN', status: 'ACTIVE' },
          ],
        })
      }

      // Khi truy vấn chiến dịch của Clinic Alpha
      if (path === '/api/v1/campaigns' && urlStr.includes(`clinicContextId=${clinicAlphaId}`)) {
        return json([
          {
            id: campaignAlphaId,
            tenantId: clinicAlphaId,
            name: 'Chiến dịch Riêng Của Alpha',
            location: 'Cơ sở Alpha',
            targetPatientCount: 100,
            status: 'IN_PROGRESS',
            startDate: '2026-11-01T00:00:00Z',
            endDate: '2026-11-07T00:00:00Z',
            createdAt: '2026-11-01T00:00:00Z',
            updatedAt: '2026-11-01T00:00:00Z',
          },
        ])
      }

      // Khi truy vấn chiến dịch của Clinic Beta
      if (path === '/api/v1/campaigns' && urlStr.includes(`clinicContextId=${clinicBetaId}`)) {
        return json([
          {
            id: campaignBetaId,
            tenantId: clinicBetaId,
            name: 'Chiến dịch Tuyệt Mật Beta',
            location: 'Cơ sở Beta',
            targetPatientCount: 50,
            status: 'PLANNED',
            startDate: '2026-11-02T00:00:00Z',
            endDate: '2026-11-08T00:00:00Z',
            createdAt: '2026-11-02T00:00:00Z',
            updatedAt: '2026-11-02T00:00:00Z',
          },
        ])
      }

      return json([])
    })

    renderPage()

    // 1. Mặc định chọn Clinic Alpha -> Thấy chiến dịch của Alpha
    expect(await screen.findByText('Chiến dịch Riêng Của Alpha')).toBeTruthy()
    // Tuyệt đối KHÔNG thấy chiến dịch của Clinic Beta
    expect(screen.queryByText('Chiến dịch Tuyệt Mật Beta')).toBeNull()

    // 2. Chuyển sang Clinic Beta qua selector
    const select = screen.getByLabelText(/Phòng khám:/i)
    fireEvent.change(select, { target: { value: clinicBetaId } })

    // 3. Bây giờ thấy chiến dịch của Beta và không còn thấy Alpha
    expect(await screen.findByText('Chiến dịch Tuyệt Mật Beta')).toBeTruthy()
    expect(screen.queryByText('Chiến dịch Riêng Của Alpha')).toBeNull()
  })

  it('Cho phép xem tiến độ & kết quả thống kê phân loại rủi ro (LOW vs MODERATE/HIGH)', async () => {
    fetchMock.mockImplementation(async (url) => {
      const urlStr = String(url)
      const path = urlStr.split('?')[0]

      if (path === '/api/v1/clinics') {
        return json({
          items: [{ clinicId: clinicAlphaId, clinicName: 'Phòng khám Alpha', role: 'CLINIC_ADMIN', status: 'ACTIVE' }],
        })
      }

      if (path === '/api/v1/campaigns') {
        return json([
          {
            id: campaignAlphaId,
            tenantId: clinicAlphaId,
            name: 'Khám Tầm Soát ĐTĐ 2026',
            location: 'Bệnh viện Quận',
            targetPatientCount: 100,
            status: 'IN_PROGRESS',
            startDate: '2026-11-01T00:00:00Z',
            endDate: '2026-11-10T00:00:00Z',
            createdAt: '2026-11-01T00:00:00Z',
            updatedAt: '2026-11-01T00:00:00Z',
          },
        ])
      }

      if (path === `/api/v1/campaigns/${campaignAlphaId}/summary`) {
        return json({
          campaignId: campaignAlphaId,
          tenantId: clinicAlphaId,
          campaignName: 'Khám Tầm Soát ĐTĐ 2026',
          status: 'IN_PROGRESS',
          targetPatientCount: 100,
          totalBatches: 3,
          totalImages: 100,
          completedImages: 95,
          pendingImages: 5,
          failedImages: 0,
          completionRatePercent: 95.0,
          normalCases: 80,
          suspectedCases: 15,
          highRiskCases: 4,
          uniquePatients: 88,
        })
      }

      return json({})
    })

    renderPage()

    expect(await screen.findByText('Khám Tầm Soát ĐTĐ 2026')).toBeTruthy()

    // Bấm nút "Thống kê"
    const summaryBtn = screen.getByRole('button', { name: 'Thống kê' })
    fireEvent.click(summaryBtn)

    // Xác minh dữ liệu hiển thị trên Modal
    expect(await screen.findByText(/Tiến Độ & Kết Quả Tầm Soát: Khám Tầm Soát ĐTĐ 2026/)).toBeTruthy()
    expect(screen.getByText('80')).toBeTruthy() // normalCases
    expect(screen.getByText(/15 \(4 ca nặng\)/)).toBeTruthy() // suspectedCases & highRiskCases
    expect(screen.getByText('88 / 100')).toBeTruthy() // uniquePatients
  })
})
