import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { getMyProfile, updateMyProfile, validateProfileDraft } from './profile'
import type { BackendContext } from './backendContext'

const mocks = vi.hoisted(() => ({ auth: vi.fn() }))
vi.mock('../firebase/config', () => ({ getFirebaseAuth: mocks.auth }))
vi.mock('./client', () => ({ isMockEnabled: false }))

const accountId = '11111111-1111-4111-8111-100000000001'
const patientId = '22222222-2222-4222-8222-200000000002'
const tenantId = '33333333-3333-4333-8333-300000000003'

const context: BackendContext = {
  accountId,
  firebaseUid: 'uid-profile-test',
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

beforeEach(() => {
  vi.resetAllMocks()
  mocks.auth.mockReturnValue({
    currentUser: { uid: context.firebaseUid, getIdToken: async () => 'fixture-jwt' },
  })
  vi.stubGlobal('fetch', fetchMock)
})

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('profile API client (W6-M6-02)', () => {
  it('loads /api/v1/profiles/me and distinguishes accountId from patientId', async () => {
    fetchMock.mockResolvedValue(
      json({
        accountId,
        displayName: 'Nguyễn Văn A',
        phone: '+84 901234567',
        patientId,
        personalTenantId: tenantId,
        profileVersion: 3,
        email: 'a@example.test',
      }),
    )

    const profile = await getMyProfile(context)
    expect(profile.accountId).toBe(accountId)
    expect(profile.patientId).toBe(patientId)
    expect(profile.accountId).not.toBe(profile.patientId)
    expect(profile.profileVersion).toBe(3)
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/v1/profiles/me',
      expect.objectContaining({
        method: 'GET',
        headers: expect.objectContaining({ Authorization: 'Bearer fixture-jwt' }),
      }),
    )
  })

  it('sends If-Match ETag and strictly omits role/tenantId/accountId/patientId from PATCH body', async () => {
    fetchMock.mockResolvedValue(
      json({
        accountId,
        displayName: 'Nguyễn Văn B',
        phone: '0909888777',
        patientId,
        personalTenantId: tenantId,
        profileVersion: 4,
        email: 'a@example.test',
      }),
    )

    const updated = await updateMyProfile(context, {
      displayName: '  Nguyễn Văn B  ',
      phone: ' 0909888777 ',
      expectedVersion: 3,
    })

    expect(updated.profileVersion).toBe(4)
    const [, init] = fetchMock.mock.calls[0]
    expect(init?.method).toBe('PATCH')
    expect(init?.headers).toEqual(
      expect.objectContaining({
        Authorization: 'Bearer fixture-jwt',
        'If-Match': '"3"',
        'Content-Type': 'application/json',
      }),
    )
    const sentBody = JSON.parse(String(init?.body)) as Record<string, unknown>
    expect(Object.keys(sentBody).sort()).toEqual(['displayName', 'phone'])
    expect(sentBody).toEqual({ displayName: 'Nguyễn Văn B', phone: '0909888777' })
  })

  it('throws 409 on version conflict and validates draft fields', async () => {
    expect(validateProfileDraft('   ', '')).toContain('Tên hiển thị')
    expect(validateProfileDraft('Hợp lệ', 'abc')).toContain('Số điện thoại')
    expect(validateProfileDraft('Hợp lệ', '+84 901234567')).toBeNull()

    fetchMock.mockResolvedValue(
      json(
        {
          error: {
            code: 'ERR_PROFILE_VERSION_CONFLICT',
            message: 'Conflict',
            correlationId: 'corr-1',
            retryable: false,
          },
        },
        409,
      ),
    )

    await expect(
      updateMyProfile(context, {
        displayName: 'Hợp lệ',
        phone: null,
        expectedVersion: 1,
      }),
    ).rejects.toMatchObject({ status: 409, code: 'ERR_PROFILE_VERSION_CONFLICT' })
  })
})
