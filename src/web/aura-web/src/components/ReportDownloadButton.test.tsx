import { act, cleanup, render, screen } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import type { BackendContext } from '../api/backendContext'
import ReportDownloadButton from './ReportDownloadButton'

const mocks = vi.hoisted(() => ({ download: vi.fn() }))
vi.mock('../api/reportDownload', () => ({ downloadReport: mocks.download }))

const caseId = '50000000-0000-4000-8000-000000000005'
const context: BackendContext = {
  firebaseUid: 'user-a', accountId: '10000000-0000-4000-8000-000000000001',
  patientId: '20000000-0000-4000-8000-000000000002', personalTenantId: '30000000-0000-4000-8000-000000000003',
  roles: ['PATIENT'], displayName: 'Fixture', profileVersion: 1,
}

afterEach(() => { cleanup(); vi.resetAllMocks() })

it('starts one request for the selected case after rapid clicks and cancels on unmount', () => {
  mocks.download.mockReturnValue(new Promise(() => {}))
  const page = render(<ReportDownloadButton resource="analyses" resourceId={caseId} format="pdf" context={context} />)
  const button = screen.getByRole('button', { name: 'Tải PDF' })
  act(() => { button.click(); button.click() })
  expect(mocks.download).toHaveBeenCalledTimes(1)
  expect(mocks.download).toHaveBeenCalledWith('analyses', caseId, 'pdf', context, expect.any(AbortSignal))
  const signal = mocks.download.mock.calls[0][4] as AbortSignal
  expect(signal.aborted).toBe(false)
  page.unmount()
  expect(signal.aborted).toBe(true)
})
