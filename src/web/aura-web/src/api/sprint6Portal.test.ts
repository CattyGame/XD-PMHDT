import { beforeEach, expect, it, vi } from 'vitest'
import { financial, ownerOrders, ownerUsage, retirePackage, savePrice, templates } from './sprint6Portal'
import type { BackendContext } from './backendContext'
const api = vi.hoisted(() => vi.fn())
vi.mock('./analysisResult', async original => ({ ...await original<typeof import('./analysisResult')>(), authorizedApiResponse: api }))
const id='11111111-1111-4111-8111-111111111111'
const context: BackendContext={accountId:id,patientId:id,personalTenantId:id,firebaseUid:'fixture',displayName:'Fixture',profileVersion:1,roles:['SYSTEM_ADMIN']}
const range={from:'2026-11-01T00:00:00Z',to:'2026-11-27T00:00:00Z'}
const signal=()=>new AbortController().signal
const json=(x:unknown)=>new Response(JSON.stringify(x))
beforeEach(()=>api.mockReset())
it('reads real backend order DTOs and maps personal owner to ACCOUNT',async()=>{
 api.mockResolvedValue(json({items:[{id,priceVersionId:id,packageName:'USD',amount:2.5,currency:'USD',credits:5,status:'COMPLETED',createdAt:range.from,updatedAt:range.to}],nextCursor:null,environment:'TEST'}))
 const result=await ownerOrders(context,{type:'PERSONAL',id},null,signal())
 expect(result.items[0]).toMatchObject({orderId:id,amount:2.5,currency:'USD',environment:'TEST'})
 expect(api.mock.calls[0][0]).toContain('ownerType=ACCOUNT')
})
it('reads ledger usage page without inventing missing balance aggregates',async()=>{
 api.mockResolvedValue(json({items:[{id,entryType:'RESERVE',balanceDelta:-5,balanceAfter:10,reservationUnits:5,orderId:null,reservationId:id,createdAt:range.from}],nextCursor:'next',environment:'TEST'}))
 const result=await ownerUsage(context,{type:'CLINIC',id},range,signal(),'previous')
 expect(result.items[0]).toMatchObject({entryType:'RESERVE',balanceDelta:-5,balanceAfter:10})
 expect(result.nextCursor).toBe('next')
 expect(api.mock.calls[0][0]).toContain('cursor=previous')
})
it('shows paid major-unit amounts by currency and environment',async()=>{
 api.mockResolvedValue(json({...range,groups:[{environment:'TEST',currency:'USD',paidOrders:1,paidAmount:2.5}]}))
 expect((await financial(context,range,signal())).groups).toEqual([{environment:'TEST',currency:'USD',orders:1,paidAmount:2.5}])
})
it('reads production template identity and event names',async()=>{
 api.mockResolvedValue(json({items:[{id,eventType:'ANALYSIS_COMPLETED',title:'AURA',body:'Đăng nhập để xem chi tiết.',enabled:true,version:1}]}))
 expect((await templates(context,signal()))[0]).toMatchObject({id,event:'ANALYSIS_COMPLETED'})
})
it('creates price with version zero and explicit stable command key',async()=>{
 api.mockImplementation(async()=>json({packageId:1,priceVersionId:id,displayName:'USD',amountMinor:250,currency:'USD',credits:5,status:'ACTIVE',version:1}))
 const input={displayName:'USD',amountMinor:250,currency:'USD',credits:5,reason:'Test'}
 await savePrice(context,input,signal(),undefined,id)
 await savePrice(context,input,signal(),undefined,id)
 expect(JSON.parse(api.mock.calls[0][4]).expectedVersion).toBe(0)
 expect(api.mock.calls[0][6]).toEqual({'Idempotency-Key':id})
 expect(api.mock.calls[1][6]).toEqual(api.mock.calls[0][6])
})
it('accepts retirement receipt at unchanged price version and sends key',async()=>{
 api.mockResolvedValue(json({packageId:1,priceVersionId:id,status:'RETIRED',version:2}))
 await retirePackage(context,1,{expectedVersion:2,reason:'Test'},signal(),id)
 expect(api.mock.calls[0][6]).toEqual({'Idempotency-Key':id})
})
it('rejects malformed money and clears no errors into fabricated zero',async()=>{
 api.mockResolvedValue(json({...range,groups:[{environment:'TEST',currency:'USD',paidOrders:1,paidAmount:null}]}))
 await expect(financial(context,range,signal())).rejects.toMatchObject({code:'ERR_INVALID_RESPONSE'})
})
