import { afterEach, expect, it, vi } from 'vitest'
import { getDoctorFileAccess } from './doctorReview'
import type { BackendContext } from './backendContext'

const mock = vi.hoisted(() => ({ auth: vi.fn() }))
vi.mock('../firebase/config', () => ({ getFirebaseAuth: mock.auth }))
vi.mock('./client', () => ({ isMockEnabled: false }))
afterEach(() => vi.unstubAllGlobals())

it('audit: Doctor accepts the relative authenticated content URL produced by FileAccessService', async () => {
  const id = '11111111-1111-4111-8111-111111111111'
  const context: BackendContext = { accountId: id, patientId: id, personalTenantId: id,
    firebaseUid: 'audit-doctor', roles: ['DOCTOR'], displayName: 'Synthetic', profileVersion: 1 }
  const user = { uid: context.firebaseUid, getIdToken: async () => 'fixture-bearer' }
  mock.auth.mockReturnValue({ currentUser: user })
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({
    url: `/api/v1/analyses/${id}/files/${id}/content?clinicContextId=${id}`,
    expiresAt: new Date(Date.now() + 120000).toISOString(),
  }), { status: 200, headers: { 'Content-Type': 'application/json' } })))
  await expect(getDoctorFileAccess(context, id, id, id)).resolves.toBeDefined()
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({
    url: `/api/v1/analyses/${id}/files/${id}/content?clinicContextId=${id}&clinicContextId=${id}`,
    expiresAt: new Date(Date.now() + 120000).toISOString(),
  }), { headers: { 'Content-Type': 'application/json' } }))
  await expect(getDoctorFileAccess(context, id, id, id)).rejects.toMatchObject({ code: 'ERR_INVALID_RESPONSE' })
})
