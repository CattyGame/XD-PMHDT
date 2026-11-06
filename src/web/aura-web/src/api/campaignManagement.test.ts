/// <reference types="node" />
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import {
  getCampaigns,
  getCampaignById,
  createCampaign,
  linkBatchToCampaign,
  updateCampaignStatus,
  getCampaignSummary,
  getCampaignExportUrl,
} from './campaignManagement'
import type { BackendContext } from './backendContext'

const m = vi.hoisted(() => ({ auth: vi.fn() }))
vi.mock('../firebase/config', () => ({ getFirebaseAuth: m.auth }))
vi.mock('./client', () => ({ isMockEnabled: false }))

const clinicId = '11111111-1111-4111-8111-200000000001'
const campaignId = '22222222-2222-4222-8222-200000000002'
const batchId = '33333333-3333-4333-8333-300000000003'

const context: BackendContext = {
  accountId: '11111111-1111-4111-8111-500000000001',
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

describe('campaignManagement API Client', () => {
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

  afterEach(() => {
    vi.unstubAllGlobals()
    vi.restoreAllMocks()
  })

  it('getCampaigns: fetches campaign list for given clinic', async () => {
    fetchMock.mockResolvedValueOnce(
      json([
        {
          id: campaignId,
          tenantId: clinicId,
          name: 'Tầm soát đái tháo đường',
          location: 'Trạm Y tế P1',
          targetPatientCount: 100,
          status: 'PLANNED',
          startDate: '2026-11-01T00:00:00Z',
          endDate: '2026-11-10T00:00:00Z',
          createdAt: '2026-11-01T00:00:00Z',
          updatedAt: '2026-11-01T00:00:00Z',
        },
      ]),
    )

    const list = await getCampaigns(context, clinicId)
    expect(list).toHaveLength(1)
    expect(list[0].id).toBe(campaignId)
    expect(list[0].name).toBe('Tầm soát đái tháo đường')
    expect(fetchMock).toHaveBeenCalledWith(
      `/api/v1/campaigns?clinicContextId=${clinicId}`,
      expect.objectContaining({
        headers: expect.objectContaining({
          Authorization: 'Bearer mock-jwt-token',
        }),
      }),
    )
  })

  it('getCampaignById: fetches a single campaign', async () => {
    fetchMock.mockResolvedValueOnce(
      json({
        id: campaignId,
        tenantId: clinicId,
        name: 'Tầm soát đái tháo đường',
        location: 'Trạm Y tế P1',
        targetPatientCount: 100,
        status: 'IN_PROGRESS',
        startDate: '2026-11-01T00:00:00Z',
        endDate: '2026-11-10T00:00:00Z',
        createdAt: '2026-11-01T00:00:00Z',
        updatedAt: '2026-11-01T00:00:00Z',
      }),
    )

    const campaign = await getCampaignById(context, clinicId, campaignId)
    expect(campaign.id).toBe(campaignId)
    expect(campaign.status).toBe('IN_PROGRESS')
  })

  it('createCampaign: creates a new campaign with valid fields', async () => {
    fetchMock.mockResolvedValueOnce(
      json(
        {
          id: campaignId,
          tenantId: clinicId,
          name: 'Chiến dịch Khám Mắt THPT',
          location: 'Trường THPT Lê Hồng Phong',
          targetPatientCount: 200,
          status: 'PLANNED',
          startDate: '2026-11-05T00:00:00Z',
          endDate: '2026-11-12T00:00:00Z',
          createdAt: '2026-11-05T00:00:00Z',
          updatedAt: '2026-11-05T00:00:00Z',
        },
        201,
      ),
    )

    const created = await createCampaign(context, clinicId, {
      name: 'Chiến dịch Khám Mắt THPT',
      location: 'Trường THPT Lê Hồng Phong',
      targetPatientCount: 200,
      startDate: '2026-11-05T00:00:00Z',
      endDate: '2026-11-12T00:00:00Z',
    })

    expect(created.name).toBe('Chiến dịch Khám Mắt THPT')
    expect(created.targetPatientCount).toBe(200)
    expect(fetchMock).toHaveBeenCalledWith(
      `/api/v1/campaigns?clinicContextId=${clinicId}`,
      expect.objectContaining({
        method: 'POST',
        headers: expect.objectContaining({
          Authorization: 'Bearer mock-jwt-token',
          'Content-Type': 'application/json',
        }),
      }),
    )
  })

  it('linkBatchToCampaign: links an analysis batch to a campaign', async () => {
    fetchMock.mockResolvedValueOnce(
      json({ message: 'Liên kết batch vào chiến dịch tầm soát thành công.' }),
    )

    const res = await linkBatchToCampaign(context, clinicId, campaignId, batchId)
    expect(res.message).toContain('Liên kết batch vào chiến dịch tầm soát thành công')
    expect(fetchMock).toHaveBeenCalledWith(
      `/api/v1/campaigns/${campaignId}/batches?clinicContextId=${clinicId}`,
      expect.objectContaining({
        method: 'POST',
      }),
    )
  })

  it('updateCampaignStatus: updates campaign status', async () => {
    fetchMock.mockResolvedValueOnce(
      json({
        id: campaignId,
        tenantId: clinicId,
        name: 'Tầm soát đái tháo đường',
        location: 'Trạm Y tế P1',
        targetPatientCount: 100,
        status: 'CLOSED',
        startDate: '2026-11-01T00:00:00Z',
        endDate: '2026-11-10T00:00:00Z',
        createdAt: '2026-11-01T00:00:00Z',
        updatedAt: '2026-11-01T00:00:00Z',
      }),
    )

    const updated = await updateCampaignStatus(context, clinicId, campaignId, 'CLOSED')
    expect(updated.status).toBe('CLOSED')
    expect(fetchMock).toHaveBeenCalledWith(
      `/api/v1/campaigns/${campaignId}/status?clinicContextId=${clinicId}`,
      expect.objectContaining({
        method: 'PATCH',
      }),
    )
  })

  it('getCampaignSummary: fetches summary statistics', async () => {
    fetchMock.mockResolvedValueOnce(
      json({
        campaignId,
        tenantId: clinicId,
        campaignName: 'Tầm soát đái tháo đường',
        status: 'IN_PROGRESS',
        targetPatientCount: 100,
        totalBatches: 2,
        totalImages: 80,
        completedImages: 75,
        pendingImages: 5,
        failedImages: 0,
        completionRatePercent: 93.75,
        normalCases: 60,
        suspectedCases: 15,
        highRiskCases: 3,
        uniquePatients: 70,
      }),
    )

    const summary = await getCampaignSummary(context, clinicId, campaignId)
    expect(summary.totalBatches).toBe(2)
    expect(summary.completionRatePercent).toBe(93.75)
    expect(summary.normalCases).toBe(60)
    expect(summary.suspectedCases).toBe(15)
  })

  it('getCampaignExportUrl: generates correct export URL', () => {
    const url = getCampaignExportUrl(clinicId, campaignId)
    expect(url).toBe(`/api/v1/campaigns/${campaignId}/export?clinicContextId=${clinicId}`)
  })
})

