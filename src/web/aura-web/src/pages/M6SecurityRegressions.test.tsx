import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import ClinicPatientPage from './ClinicPatientPage'
import CampaignManagementPage from './CampaignManagementPage'
import ClinicMemberPage from './ClinicMemberPage'
import { BackendSessionContext } from '../auth/backendSession'
import { inviteClinicMember } from '../api/clinicManagement'
import type { BackendContext } from '../api/backendContext'

const m=vi.hoisted(()=>({auth:vi.fn()}))
vi.mock('../firebase/config',()=>({getFirebaseAuth:m.auth}))
vi.mock('../api/client',()=>({isMockEnabled:false}))
const a='11111111-1111-4111-8111-200000000001'
const b='11111111-1111-4111-8111-200000000002'
const p='11111111-1111-4111-8111-300000000001'
const c='22222222-2222-4222-8222-200000000001'
const context:BackendContext={accountId:'11111111-1111-4111-8111-500000000001',patientId:p,personalTenantId:a,firebaseUid:'audit-fixture',roles:['PATIENT'],displayName:'Audit',profileVersion:1}
const json=(x:unknown,status=200)=>new Response(JSON.stringify(x),{status,headers:{'Content-Type':'application/json'}})
const patient={patientId:p,clinicId:a,tenantId:a,patientType:'CLINIC_MANAGED',fullName:'AUDIT PATIENT A',status:'ACTIVE',medicalHistory:{notes:'AUDIT_CACHED_HISTORY'}}
const campaign={id:c,tenantId:a,name:'AUDIT CAMPAIGN A',location:'test',status:'PLANNED',targetPatientCount:1,startDate:'2026-11-15',endDate:'2026-11-16'}
const clinics={items:[{clinicId:a,clinicName:'CLINIC A',role:'CLINIC_ADMIN',status:'ACTIVE'},{clinicId:b,clinicName:'CLINIC B',role:'CLINIC_ADMIN',status:'ACTIVE'}]}
const fetchMock=vi.fn<typeof fetch>()
function page(component:React.ReactNode){return render(<MemoryRouter><BackendSessionContext.Provider value={{state:{status:'ready',context},retry:vi.fn(),logout:vi.fn()}}>{component}</BackendSessionContext.Provider></MemoryRouter>)}
beforeEach(()=>{vi.resetAllMocks();m.auth.mockReturnValue({currentUser:{uid:context.firebaseUid,getIdToken:async()=>'fixture-jwt'}});vi.stubGlobal('fetch',fetchMock)})
afterEach(()=>{cleanup();vi.unstubAllGlobals();vi.restoreAllMocks()})
describe('M6 security regressions against real API contracts',()=>{
  it('does not display a delayed invitation token after switching clinic',async()=>{
    let finish:(x:Response)=>void=()=>{};let pending=false
    fetchMock.mockImplementation(async input=>{
      const url=String(input)
      if(url.split('?')[0]==='/api/v1/clinics')return json(clinics)
      if(url.endsWith('/invite')){pending=true;return new Promise<Response>(resolve=>{finish=resolve})}
      return json({items:[]})
    })
    page(<ClinicMemberPage/>);
    fireEvent.click(await screen.findByRole('button',{name:'+ Mời Bác sĩ / Nhân viên'}))
    fireEvent.change(screen.getByLabelText('Email tài khoản Google / Firebase:'),{target:{value:'invite@example.test'}})
    fireEvent.click(screen.getByRole('button',{name:'Gửi lời mời'}))
    await waitFor(()=>expect(pending).toBe(true))
    fireEvent.change(screen.getByLabelText(/Phòng khám:/i),{target:{value:b}})
    await act(async()=>finish(json({membershipId:p,invitedEmail:'invite@example.test',invitationToken:'private-A-token',role:'STAFF'})))
    expect(screen.queryByText('private-A-token')).toBeNull()
  })
  it('parses the actual invitation membership DTO',async()=>{
    fetchMock.mockResolvedValue(json({membershipId:p,clinicId:a,accountId:null,role:'STAFF',status:'INVITED',invitedEmail:'fixture@example.test',invitationToken:'fixture-only-one-use-token'},201))
    await expect(inviteClinicMember(context,a,'fixture@example.test','STAFF')).resolves.toMatchObject({email:'fixture@example.test',token:'fixture-only-one-use-token'})
  })
  it('never falls back to cached history when detail access is denied',async()=>{
    fetchMock.mockImplementation(async input=>{
      const path=String(input).split('?')[0]
      if(path==='/api/v1/clinics')return json(clinics)
      if(path===`/api/v1/clinics/${a}/patients`)return json({clinicId:a,totalCount:1,items:[patient]})
      return json({error:{code:'ERR_FORBIDDEN',message:'Revoked'}},403)
    })
    page(<ClinicPatientPage/>);await screen.findByText(patient.fullName)
    fireEvent.click(screen.getByRole('button',{name:'Chi tiết'}))
    await screen.findByRole('alert')
    expect(screen.queryByRole('heading',{name:'Hồ Sơ Bệnh Án: '+patient.fullName})).toBeNull()
    expect(screen.queryByText('AUDIT_CACHED_HISTORY')).toBeNull()
    expect(screen.queryByText(patient.fullName)).toBeNull()
  })
  it('ignores a late patient detail after changing clinic',async()=>{
    let finish:(x:Response)=>void=()=>{};let pending=false
    fetchMock.mockImplementation(async input=>{
      const path=String(input).split('?')[0]
      if(path==='/api/v1/clinics')return json(clinics)
      if(path===`/api/v1/clinics/${a}/patients`)return json({clinicId:a,totalCount:1,items:[patient]})
      if(path===`/api/v1/clinics/${b}/patients`)return json({clinicId:b,totalCount:0,items:[]})
      pending=true;return new Promise<Response>(resolve=>{finish=resolve})
    })
    page(<ClinicPatientPage/>);await screen.findByText(patient.fullName)
    fireEvent.click(screen.getByRole('button',{name:'Chi tiết'}));await waitFor(()=>expect(pending).toBe(true))
    fireEvent.change(screen.getByLabelText(/Phòng khám:/i),{target:{value:b}})
    await waitFor(()=>expect(screen.queryByText(patient.fullName)).toBeNull())
    await act(async()=>finish(json(patient)))
    expect(screen.queryByRole('heading',{name:'Hồ Sơ Bệnh Án: '+patient.fullName})).toBeNull()
    expect((screen.getByLabelText(/Phòng khám:/i) as HTMLSelectElement).value).toBe(b)
  })
  it('downloads CSV using a fresh bearer and releases the object URL',async()=>{
    const opened=vi.spyOn(window,'open').mockImplementation(()=>null)
    const clicked=vi.spyOn(HTMLAnchorElement.prototype,'click').mockImplementation(()=>{})
    const create=vi.fn().mockReturnValue('blob:test');const revoke=vi.fn()
    vi.stubGlobal('URL',Object.assign(URL,{createObjectURL:create,revokeObjectURL:revoke}))
    fetchMock.mockImplementation(async input=>String(input).includes('/export')?new Response('fixture,csv',{headers:{'Content-Type':'text/csv'}}):String(input).split('?')[0]==='/api/v1/clinics'?json(clinics):json([campaign]))
    page(<CampaignManagementPage/>);await screen.findByText(campaign.name)
    fireEvent.click(screen.getByRole('button',{name:'Xuất CSV'}))
    await waitFor(()=>expect(clicked).toHaveBeenCalledOnce())
    expect(opened).not.toHaveBeenCalled()
    expect(fetchMock).toHaveBeenCalledWith(`/api/v1/campaigns/${c}/export?clinicContextId=${a}`,expect.objectContaining({headers:{Authorization:'Bearer fixture-jwt'}}))
    expect(revoke).toHaveBeenCalledWith('blob:test')
  })
  it('clears campaign summary on clinic change',async()=>{
    fetchMock.mockImplementation(async input=>{
      const url=String(input)
      if(url.split('?')[0]==='/api/v1/clinics')return json(clinics)
      if(url.includes('/summary'))return json({campaignId:c,campaignName:campaign.name,tenantId:a,totalImages:1,completedImages:1})
      return json(url.includes(b)?[]:[campaign])
    })
    page(<CampaignManagementPage/>);await screen.findByText(campaign.name)
    fireEvent.click(screen.getByRole('button',{name:'Thống kê'}))
    await screen.findByRole('heading',{name:'Tiến Độ & Kết Quả Tầm Soát: '+campaign.name})
    fireEvent.change(screen.getByLabelText(/Phòng khám:/i),{target:{value:b}})
    await waitFor(()=>expect(screen.queryByRole('cell',{name:campaign.name})).toBeNull())
    expect(screen.queryByRole('heading',{name:'Tiến Độ & Kết Quả Tầm Soát: '+campaign.name})).toBeNull()
  })
  it('ignores a late campaign summary after changing clinic',async()=>{
    let finish:(x:Response)=>void=()=>{};let pending=false
    fetchMock.mockImplementation(async input=>{
      const url=String(input)
      if(url.split('?')[0]==='/api/v1/clinics')return json(clinics)
      if(url.includes('/summary')){pending=true;return new Promise<Response>(resolve=>{finish=resolve})}
      return json(url.includes(b)?[]:[campaign])
    })
    page(<CampaignManagementPage/>);await screen.findByText(campaign.name)
    fireEvent.click(screen.getByRole('button',{name:'Thống kê'}))
    await waitFor(()=>expect(pending).toBe(true))
    fireEvent.change(screen.getByLabelText(/Phòng khám:/i),{target:{value:b}})
    await act(async()=>finish(json({campaignId:c,campaignName:campaign.name,tenantId:a})))
    expect(screen.queryByRole('heading',{name:'Tiến Độ & Kết Quả Tầm Soát: '+campaign.name})).toBeNull()
  })
})
