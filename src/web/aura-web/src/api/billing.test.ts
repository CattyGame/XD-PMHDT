import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { BackendContext } from './backendContext'
import {
  BillingError, createBillingOrder, getBillingOrder, getBillingPackages,
  parseCatalog, paySandbox,
} from './billing'

const mocks = vi.hoisted(() => ({ auth: vi.fn() }))
vi.mock('../firebase/config', () => ({ getFirebaseAuth: mocks.auth }))
vi.mock('./client', () => ({ isMockEnabled: false }))

const accountId = '11111111-1111-4111-8111-111111111111'
const orderId = '22222222-2222-4222-8222-222222222222'
const key = '33333333-3333-4333-8333-333333333333'
const context: BackendContext = {
  accountId, firebaseUid: 'firebase-a', patientId: '44444444-4444-4444-8444-444444444444',
  personalTenantId: '55555555-5555-4555-8555-555555555555', roles: ['PATIENT'],
  displayName: 'Người kiểm thử', profileVersion: 1,
}
const catalog = { data: [{ id: 1, name: 'Gói thử nghiệm', price: 80000, credits: 5 }],
  source: 'database', cacheHit: false, ttlSeconds: 60, databaseReadCount: 1 }
const created = { orderId, status: 'PENDING', amount: 80000, currency: 'VND', credits: 5,
  packageName: 'Gói thử nghiệm', paymentUrl: 'http://localhost:5173/billing/sandbox-checkout',
  createdAt: '2026-11-04T10:00:00Z' }
const order = { id: orderId, billingOwnerType: 'ACCOUNT', billingOwnerId: accountId,
  packageNameSnapshot: 'Gói thử nghiệm', priceAmountSnapshot: 80000,
  currencyCodeSnapshot: 'VND', creditsSnapshot: 5, environment: 'TEST', status: 'PENDING' }
const payment = { orderId, transactionId: 'SANDBOX-TX-1', status: 'COMPLETED', message: 'OK',
  creditsAdded: 5, balanceAfter: 8, replayed: false }
const fetchMock = vi.fn<typeof fetch>()
const json = (value: unknown, status = 200) =>
  new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } })

beforeEach(() => {
  vi.resetAllMocks()
  mocks.auth.mockReturnValue({ currentUser: { uid: 'firebase-a', getIdToken: vi.fn(async () => 'firebase-token') } })
  fetchMock.mockResolvedValue(json(catalog))
  vi.stubGlobal('fetch', fetchMock)
})
afterEach(() => vi.unstubAllGlobals())

describe('SCRUM-355 Billing M3 API', () => {
  it('đọc catalog từ API, không tự tạo giá hoặc Credit', async () => {
    expect(await getBillingPackages()).toEqual(catalog)
    expect(fetchMock.mock.calls[0][0]).toBe('/api/v1/billing/packages')
    expect(fetchMock.mock.calls[0][1]?.headers).toMatchObject({ Accept: 'application/json' })
    expect(Object.keys(fetchMock.mock.calls[0][1]?.headers ?? {})).not.toContain('Authorization')
  })
  it('từ chối catalog thiếu dữ liệu, không hiển thị giá giả', () => {
    expect(() => parseCatalog({ data: [{ id: 1, name: 'Thiếu giá' }], source: 'db', cacheHit: false,
      ttlSeconds: 0, databaseReadCount: 1 })).toThrow(BillingError)
  })
  it('tạo đơn bằng Firebase Bearer và Idempotency-Key đúng backend', async () => {
    fetchMock.mockResolvedValueOnce(json(created, 201))
    const result = await createBillingOrder(context, { type: 'ACCOUNT', id: accountId }, 1, key)
    expect(result.orderId).toBe(orderId)
    const options = fetchMock.mock.calls[0][1]
    expect(options?.headers).toMatchObject({ Authorization: 'Bearer firebase-token', 'Idempotency-Key': key })
    expect(JSON.parse(String(options?.body))).toEqual({
      billingOwner: { type: 'ACCOUNT', id: accountId }, packageId: 1, paymentMethod: 'SANDBOX_MOCK',
    })
  })
  it('trả lại kết quả replay 200 và không tự tạo giao dịch mới', async () => {
    fetchMock.mockResolvedValueOnce(json(created, 200))
    expect((await createBillingOrder(context, { type: 'ACCOUNT', id: accountId }, 1, key)).orderId).toBe(orderId)
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })
  it('lấy trạng thái đơn PENDING từ backend để tránh POST thanh toán khi refresh', async () => {
    fetchMock.mockResolvedValueOnce(json(order))
    expect((await getBillingOrder(context, orderId)).status).toBe('PENDING')
    expect(fetchMock.mock.calls[0][0]).toBe(`/api/v1/billing/orders/${orderId}`)
  })
  it('xác nhận thành công và số dư theo response backend, không cộng frontend', async () => {
    fetchMock.mockResolvedValueOnce(json(payment))
    expect(await paySandbox(context, orderId)).toEqual(payment)
    expect(JSON.parse(String(fetchMock.mock.calls[0][1]?.body))).toEqual({ orderId, simulateFailure: false })
  })
  it('ghi nhận thất bại và không hiển thị Credit tự tính', async () => {
    fetchMock.mockResolvedValueOnce(json({ ...payment, status: 'CANCELLED', creditsAdded: 0, balanceAfter: 0 }))
    expect((await paySandbox(context, orderId, true)).status).toBe('CANCELLED')
    expect(JSON.parse(String(fetchMock.mock.calls[0][1]?.body))).toEqual({ orderId, simulateFailure: true })
  })
  it('chặn HTTP 409 idempotency conflict, HTTP 429 và 5xx', async () => {
    for (const status of [409, 429, 503]) {
      fetchMock.mockResolvedValueOnce(json({ error: { code: 'ERR_REQUEST_FAILED' } }, status))
      await expect(createBillingOrder(context, { type: 'ACCOUNT', id: accountId }, 1, key))
        .rejects.toMatchObject({ status })
    }
  })
  it('không gửi POST khi phiên Firebase thay đổi', async () => {
    mocks.auth.mockReturnValue({ currentUser: { uid: 'user-b', getIdToken: vi.fn() } })
    await expect(paySandbox(context, orderId)).rejects.toMatchObject({ status: 401 })
    expect(fetchMock).not.toHaveBeenCalled()
  })
})
