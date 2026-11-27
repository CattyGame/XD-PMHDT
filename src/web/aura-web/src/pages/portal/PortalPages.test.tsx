import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { AnalysisResultError } from '../../api/analysisResult'
import type { BackendContext } from '../../api/backendContext'
import { BackendSessionContext } from '../../auth/backendSession'
import AdminPortalPage from './AdminPortalPage'
import OwnerViewsPage from './OwnerViewsPage'

const request = vi.hoisted(() => vi.fn())
vi.mock('../../api/analysisResult', async importOriginal => ({
  ...await importOriginal<typeof import('../../api/analysisResult')>(), authorizedApiResponse: request,
}))
const id = (n: number) => `10000000-0000-4000-8000-${String(n).padStart(12, '0')}`
const accountId = id(1), clinicId = id(2)
const context: BackendContext = { roles: ['SYSTEM_ADMIN'], accountId, firebaseUid: 'fixture', displayName: 'Fixture',
  profileVersion: 1, patientId: id(3), personalTenantId: id(4) }
const time = '2026-11-16T10:00:00Z'
const range = { from: time, to: '2026-11-18T10:00:00Z' }
const json = (data: unknown) => new Response(JSON.stringify(data), { status: 200 })
const price = { packageId: 42, priceVersionId: id(5), displayName: 'Gói kiểm thử', amountMinor: 25000,
  currency: 'VND', credits: 4, status: 'ACTIVE', version: 7 }

function open(path: string, roles = context.roles) {
  render(<BackendSessionContext.Provider value={{ state: { status: 'ready', context: { ...context, roles } },
    retry: vi.fn(), logout: vi.fn() }}>
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/admin/:section" element={<AdminPortalPage />} />
        <Route path="/billing/history" element={<OwnerViewsPage />} />
        <Route path="/doctor/activity" element={<OwnerViewsPage mode="doctor" />} />
        <Route path="/403" element={<p>403 fixture</p>} />
      </Routes>
    </MemoryRouter>
  </BackendSessionContext.Provider>)
}

beforeEach(() => { request.mockReset() })
afterEach(cleanup)

describe('SCRUM-393 protected portal', () => {
  it('keeps the price intent and idempotency key after a lost acknowledgement', async () => {
    let attempts = 0
    request.mockImplementation(async (_path, _context, _signal, method) => {
      if (method === 'POST') {
        if (++attempts === 1) throw new AnalysisResultError(503, 'ERR_DEPENDENCY_UNAVAILABLE')
        return json({ ...price, version: 8 })
      }
      return json({ nextCursor: null, items: [price] })
    })
    open('/admin/catalog')
    fireEvent.click(await screen.findByText('Quản lý giá'))
    const editor = screen.getByRole('group', { name: 'Tạo phiên bản giá' })
    fireEvent.change(within(editor).getByLabelText('Lý do'), { target: { value: 'Retry same intent' } })
    fireEvent.click(within(editor).getByRole('button', { name: 'Lưu thay đổi' }))
    await screen.findByRole('alert')
    expect(within(editor).getByLabelText('Lý do')).toHaveProperty('value', 'Retry same intent')
    fireEvent.click(within(editor).getByRole('button', { name: 'Lưu thay đổi' }))
    await screen.findByText('Đã lưu thay đổi.')
    const writes = request.mock.calls.filter(([, , , method]) => method === 'POST')
    expect(writes).toHaveLength(2)
    expect(writes[0][6]['Idempotency-Key']).toMatch(/^[0-9a-f-]{36}$/)
    expect(writes[1][6]).toEqual(writes[0][6])
    expect(writes[1][4]).toBe(writes[0][4])
  })
  it('blocks non-admin accounts before reading an admin endpoint', async () => {
    open('/admin/catalog', ['PATIENT'])
    expect(await screen.findByText('403 fixture')).toBeTruthy()
    expect(request).not.toHaveBeenCalled()
  })

  it('posts the active price version and discards a stale catalog after 409', async () => {
    request.mockImplementation(async (_path, _context, _signal, method) => {
      if (method === 'POST') throw new AnalysisResultError(409, 'ERR_CONCURRENT_UPDATE')
      return json({ nextCursor: null, items: [price] })
    })
    open('/admin/catalog')
    fireEvent.click(await screen.findByText('Quản lý giá'))
    const editor = screen.getByRole('group', { name: 'Tạo phiên bản giá' })
    fireEvent.change(within(editor).getByLabelText('Giá theo đơn vị nhỏ nhất'), { target: { value: '30000' } })
    fireEvent.change(within(editor).getByLabelText('Lý do'), { target: { value: 'Cập nhật giá thử nghiệm' } })
    fireEvent.click(within(editor).getByRole('button', { name: 'Lưu thay đổi' }))
    expect((await screen.findByRole('alert')).textContent).toMatch(/Phiên bản đã thay đổi/)
    const call = request.mock.calls.find(([path]) => path.includes('/price-versions'))!
    expect(call[0]).toBe('/api/v1/admin/billing/packages/42/price-versions')
    expect(JSON.parse(call[4])).toEqual({ expectedVersion: 7, displayName: price.displayName, amountMinor: 30000,
      currency: 'VND', credits: 4, reason: 'Cập nhật giá thử nghiệm' })
    expect(screen.queryByText('Gói kiểm thử')).toBeNull()
    fireEvent.click(screen.getByRole('button', { name: 'Tải lại' }))
    expect(await screen.findByText('Gói kiểm thử')).toBeTruthy()
  })

  it('activates an AI manifest with its current version and reloads after a valid response', async () => {
    const manifest = id(6), sha = 'a'.repeat(64)
    const active = { artifactManifestId: manifest, thresholdConfigSha256: sha, version: 2, activatedAt: time }
    let version = 2
    request.mockImplementation(async (_path, _context, _signal, method) => {
      if (method === 'PUT') version++
      return json({ ...active, version })
    })
    open('/admin/ai')
    fireEvent.change(await screen.findByLabelText('Lý do'), { target: { value: 'Activate verified artifact' } })
    fireEvent.click(screen.getByRole('button', { name: 'Lưu thay đổi' }))
    await screen.findByText(/Phiên bản 3/)
    const call = request.mock.calls.find(([, , , method]) => method === 'PUT')!
    expect(JSON.parse(call[4])).toEqual({ expectedVersion: 2, artifactManifestId: manifest,
      thresholdConfigSha256: sha, reason: 'Activate verified artifact' })
  })

  it('shows missing values separately from zero and keeps review risk separate from processing totals', async () => {
    request.mockImplementation(async (path: string) => path.includes('/analytics/') ? json({
      ...range, asOf: time, unit: 'UNIQUE_ANALYSIS', total: 3,
      processing: { active: 1, completed: 1, failed: 1, lowQuality: 0, rejected: 0, duplicate: 2 },
      riskSource: 'LATEST_CLINICIAN_REVIEW', reviewRisk: { low: 0, moderate: 0, high: 1, unavailable: 0, unreviewed: 2 },
      measurementAvailable: 0, measurementUnavailable: null,
    }) : json({ ...range, groups: [] }))
    open('/admin/dashboard')
    const panel = await screen.findByRole('region', { name: 'Tổng quan phân tích' })
    expect((await within(panel).findByText('Tổng ca phân tích duy nhất')).nextElementSibling?.textContent).toBe('3')
    expect(within(panel).getByText('Có số đo').nextElementSibling?.textContent).toBe('0')
    expect(within(panel).getByText('Ảnh trùng (ngoài tổng ca duy nhất)').nextElementSibling?.textContent).toBe('2')
    expect(within(panel).getByText('Thiếu số đo').nextElementSibling?.textContent).toBe('Chưa có dữ liệu')
    expect(await screen.findByText('Chưa có dữ liệu trong phạm vi đã chọn.')).toBeTruthy()
  })

  it('clears prior owner data before loading a clinic and renders denial without the old order', async () => {
    const orderId = id(7)
    request.mockImplementation(async (path: string) => {
      if (path === '/api/v1/clinics') return json({ items: [{ clinicId, clinicName: 'Phòng khám thử',
        role: 'CLINIC_ADMIN', status: 'ACTIVE' }] })
      if (path.includes('ownerType=CLINIC')) throw new AnalysisResultError(403, 'ERR_FORBIDDEN')
      if (path.includes('/orders?')) return json({ nextCursor: null, environment: 'TEST', items: [{ id: orderId, status: 'COMPLETED', priceVersionId: id(8),
        amount: 200, currency: 'VND', credits: 2, createdAt: time }] })
      return json({items: [], nextCursor: null, environment: 'TEST'})
    })
    open('/billing/history', ['PATIENT'])
    expect(await screen.findByRole('link', { name: orderId })).toBeTruthy()
    await screen.findByRole('option', { name: 'Phòng khám thử' })
    fireEvent.change(screen.getByLabelText('Chủ ví'), { target: { value: clinicId } })
    expect(screen.queryByText(orderId)).toBeNull()
    expect((await screen.findAllByRole('alert'))[0].textContent).toMatch(/không có quyền/)
    await waitFor(() => expect(request.mock.calls.some(([path]) => path.includes('ownerId=' + clinicId))).toBe(true))
  })

  it('uses the doctor clinic context from current membership for analytics', async () => {
    request.mockImplementation(async (path: string) => {
      if (path === '/api/v1/clinics') return json({ items: [{ clinicId, clinicName: 'Phòng khám bác sĩ',
        role: 'DOCTOR', status: 'ACTIVE' }] })
      return json({ ...range, asOf: time, unit: 'UNIQUE_ANALYSIS', total: 1,
        processing: { active: 0, completed: 1, failed: 0, lowQuality: 0, rejected: 0, duplicate: 0 },
        riskSource: 'LATEST_CLINICIAN_REVIEW', reviewRisk: { low: 0, moderate: 0, high: 0, unavailable: 0, unreviewed: 1 },
        measurementAvailable: 0, measurementUnavailable: 0, measurementUnknown: 1 })
    })
    open('/doctor/activity', ['DOCTOR'])
    await screen.findByRole('option', { name: 'Phòng khám bác sĩ' })
    expect(request).toHaveBeenCalledTimes(1)
    fireEvent.change(screen.getByLabelText('Phòng khám được cấp quyền'), { target: { value: clinicId } })
    const unknown = await screen.findByText('Chưa rõ trạng thái đo')
    expect(unknown.nextElementSibling?.textContent).toBe('1')
    expect(request.mock.calls[1][0]).toMatch(new RegExp('^/api/v1/doctors/me/summary\\?.*clinicContextId=' + clinicId))
  })
})

it('uses the merged clinic contract, sends expectedVersion and removes stale data on 409', async () => {
  request.mockImplementation(async (_path, _context, _signal, method) => {
    if (method === 'PATCH') throw new AnalysisResultError(409, 'ERR_CONFLICT')
    return json({ items: [{ clinicId, tenantId: id(4), name: 'Clinic lifecycle fixture',
      status: 'PENDING', version: 4 }], nextCursor: null })
  })
  open('/admin/clinics')
  await screen.findByText('Clinic lifecycle fixture')
  fireEvent.click(screen.getByText('Đổi trạng thái'))
  fireEvent.change(screen.getByLabelText('Trạng thái'), { target: { value: 'ACTIVE' } })
  fireEvent.change(screen.getByLabelText('Lý do'), { target: { value: 'Synthetic clinic test' } })
  fireEvent.click(screen.getByRole('button', { name: 'Lưu thay đổi' }))
  expect((await screen.findByRole('alert')).textContent).toMatch(/Phiên bản đã thay đổi/)
  expect(screen.queryByText('Clinic lifecycle fixture')).toBeNull()
  const call = request.mock.calls.find(([, , , method]) => method === 'PATCH')!
  expect(call[0]).toBe('/api/v1/admin/clinics/' + clinicId + '/status')
  expect(JSON.parse(call[4])).toEqual({ status: 'ACTIVE', expectedVersion: 4, reason: 'Synthetic clinic test' })
})

it('renders the canonical Profile audit DTO with only hashed resource identifiers', async () => {
  request.mockResolvedValue(json({ items: [{ eventId: id(9), sourceService: 'profile-api',
    action: 'CLINIC_STATUS_CHANGED', resourceType: 'CLINIC', resourceIdHash: 'a'.repeat(64),
    correlationId: 'synthetic-correlation', occurredAt: time, outcome: 'ALLOW' }], nextCursor: null }))
  open('/admin/audit')
  expect(await screen.findByText('CLINIC_STATUS_CHANGED')).toBeTruthy()
  expect(screen.getByText('a'.repeat(64))).toBeTruthy()
  expect(screen.getByText('ALLOW')).toBeTruthy()
  expect(screen.queryByText(clinicId)).toBeNull()
})

it('uses canonical account roles and clears current account data after revoked access', async () => {
  request.mockImplementation(async (_path, _context, _signal, method) => {
    if (method === 'PATCH') throw new AnalysisResultError(403, 'ERR_FORBIDDEN')
    return json({ items: [{ accountId, firebaseUid: 'synthetic-admin', displayName: 'Account fixture',
      roles: ['PATIENT', 'DOCTOR'], status: 'ACTIVE', version: 3 }], nextCursor: null })
  })
  open('/admin/accounts')
  expect(await screen.findByText('PATIENT, DOCTOR')).toBeTruthy()
  fireEvent.click(screen.getByText('Chỉnh sửa'))
  const editor = screen.getByRole('group', { name: 'Trạng thái tài khoản' })
  fireEvent.change(within(editor).getByLabelText('Lý do'), { target: { value: 'Synthetic status test' } })
  fireEvent.click(within(editor).getByRole('button', { name: 'Lưu thay đổi' }))
  expect((await screen.findAllByRole('alert'))[0].textContent).toMatch(/không có quyền/)
  expect(screen.queryByText('Account fixture')).toBeNull()
})
