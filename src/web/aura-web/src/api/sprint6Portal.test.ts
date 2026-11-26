import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { BackendContext } from './backendContext'
import { financial, ownerOrders, ownerUsage, savePrice, saveTemplate, templates } from './sprint6Portal'

const authorized = vi.hoisted(() => vi.fn())
vi.mock('./analysisResult', async load => ({
  ...await load<typeof import('./analysisResult')>(),
  authorizedApiResponse: authorized,
}))

const uuid = (suffix: number) => `90000000-0000-4000-8000-${String(suffix).padStart(12, '0')}`
const context: BackendContext = {
  accountId: uuid(1), patientId: uuid(2), personalTenantId: uuid(3),
  firebaseUid: 'portal-test', displayName: 'Test account', profileVersion: 1, roles: ['SYSTEM_ADMIN'],
}
const personal = { type: 'PERSONAL', id: context.accountId } as const
const clinic = { type: 'CLINIC', id: uuid(4) } as const
const range = { from: '2026-11-20T00:00:00Z', to: '2026-11-21T00:00:00Z' }
const signal = new AbortController().signal
const response = (data: unknown) => Response.json(data)

// Synthetic values in the exact camelCase shapes written by BillingCatalogEndpoints,
// BillingHistoryRepository and PushTemplateEndpoints.
const order = {
  id: uuid(5), priceVersionId: uuid(6), packageName: 'Gói TEST', amount: 25000,
  currency: 'VND', credits: 3, status: 'COMPLETED', createdAt: range.from, updatedAt: range.to,
}
const ledger = {
  id: uuid(7), entryType: 'CAPTURE', balanceDelta: -2, balanceAfter: 0,
  reservationUnits: 2, orderId: null, reservationId: uuid(8), createdAt: range.from,
}
const template = {
  id: uuid(9), eventType: 'PAYMENT_SUCCESS', title: 'AURA',
  body: 'Có thông báo mới. Đăng nhập để xem chi tiết.', enabled: true, version: 1,
}
const price = { displayName: 'Gói TEST', amountMinor: 1250, currency: 'USD', credits: 3, reason: 'Kiểm thử' }

beforeEach(() => authorized.mockReset())

describe('SCRUM-393 backend response contracts', () => {
  it('maps PERSONAL to ACCOUNT using the session account ID and keeps CLINIC unchanged', async () => {
    authorized.mockImplementation(async () => response({ items: [], nextCursor: null, environment: 'TEST' }))
    await ownerOrders(context, personal, null, signal)
    await ownerOrders(context, clinic, null, signal)
    expect(authorized.mock.calls[0][0]).toContain(`ownerId=${context.accountId}&ownerType=ACCOUNT`)
    expect(authorized.mock.calls[1][0]).toContain(`ownerId=${clinic.id}&ownerType=CLINIC`)
    await expect(ownerOrders(context, { ...personal, id: context.patientId }, null, signal)).rejects.toHaveProperty('status', 403)
    expect(authorized).toHaveBeenCalledTimes(2)
  })

  it('reads a real-shaped order, then follows an opaque cursor into an empty page', async () => {
    authorized.mockResolvedValueOnce(response({ items: [order], nextCursor: 'cursor_A', environment: 'TEST' }))
      .mockResolvedValueOnce(response({ items: [], nextCursor: null, environment: 'TEST' }))
    const first = await ownerOrders(context, personal, null, signal)
    expect(first.items).toEqual([{ ...order, owner: personal, environment: 'TEST' }])
    expect(first.nextCursor).toBe('cursor_A')
    expect(await ownerOrders(context, personal, first.nextCursor, signal)).toEqual({
      items: [], nextCursor: null, environment: 'TEST',
    })
    expect(authorized.mock.calls[1][0]).toContain('cursor=cursor_A')
    authorized.mockResolvedValueOnce(response({
      items: [{ ...order, amount: '25000' }], nextCursor: null, environment: 'TEST',
    }))
    await expect(ownerOrders(context, personal, null, signal)).rejects.toHaveProperty('code', 'ERR_INVALID_RESPONSE')
  })

  it('reads a paged usage ledger with negative deltas, real zero and nullable references', async () => {
    authorized.mockResolvedValueOnce(response({ items: [ledger], nextCursor: 'ledger_B', environment: 'TEST' }))
    const result = await ownerUsage(context, clinic, range, signal, 'ledger_A')
    expect(result).toEqual({ items: [ledger], nextCursor: 'ledger_B', environment: 'TEST' })
    const query = new URL(authorized.mock.calls[0][0], 'https://aura.invalid').searchParams
    expect([...query.entries()]).toEqual(expect.arrayContaining([
      ['ownerType', 'CLINIC'], ['cursor', 'ledger_A'], ['from', range.from], ['to', range.to],
    ]))
  })

  it('keeps paid totals in major units and separate by environment and currency', async () => {
    const groups = [
      { environment: 'TEST', currency: 'VND', paidOrders: 0, paidAmount: 0 },
      { environment: 'LIVE', currency: 'USD', paidOrders: 2, paidAmount: 12.50 },
      { environment: 'LIVE', currency: 'EUR', paidOrders: 3, paidAmount: 19.95 },
    ]
    authorized.mockResolvedValueOnce(response({ ...range, groups }))
      .mockResolvedValueOnce(response({ ...range, groups: [{ ...groups[1], paidAmount: null }] }))
    expect(await financial(context, range, signal)).toEqual({ ...range, groups })
    await expect(financial(context, range, signal)).rejects.toHaveProperty('code', 'ERR_INVALID_RESPONSE')
  })

  it('reads a canonical template ID and checks the updated receipt', async () => {
    authorized.mockResolvedValueOnce(response({ items: [template] }))
      .mockResolvedValueOnce(response({ ...template, enabled: false, version: 2 }))
    const received = await templates(context, signal)
    expect(received[0]).toMatchObject({ id: template.id, event: 'PAYMENT_SUCCESS', enabled: true })
    await saveTemplate(context, template.id, {
      title: template.title, body: template.body, enabled: false, expectedVersion: 1, reason: 'Kiểm thử',
    }, signal)
    expect(authorized.mock.calls[1][0]).toBe(`/api/v1/admin/notification-templates/${template.id}`)
  })

  it('publishes with expectedVersion zero and the explicit key and accepts a canonical receipt without status', async () => {
    authorized.mockResolvedValueOnce(response({
      packageId: 42, priceVersionId: uuid(6), version: 1, displayName: price.displayName,
      price: 12.50, amountMinor: 1250, currency: 'USD', credits: 3,
    }))
    await savePrice(context, price, signal, undefined, uuid(10))
    expect(authorized).toHaveBeenCalledWith('/api/v1/admin/billing/packages', context, signal, 'POST',
      expect.any(String), undefined, { 'Idempotency-Key': uuid(10) })
    expect(JSON.parse(authorized.mock.calls[0][4])).toEqual({ ...price, expectedVersion: 0 })
  })
})
