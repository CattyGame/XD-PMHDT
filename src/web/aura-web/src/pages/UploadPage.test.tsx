import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({ auth: vi.fn(), mock: false }))
vi.mock('../firebase/config', () => ({ getFirebaseAuth: mocks.auth }))
vi.mock('../api/client', () => ({ get isMockEnabled() { return mocks.mock } }))
import UploadPage from './UploadPage'
import { BackendSessionContext } from '../auth/backendSession'
import type { BackendContext } from '../api/backendContext'
import type { BackendSessionState } from '../auth/backendSessionStore'

const base: BackendContext = {
  firebaseUid: 'user-a', accountId: '10000000-0000-0000-0000-000000000001', roles: ['PATIENT'],
  patientId: '20000000-0000-0000-0000-000000000002',
  personalTenantId: '30000000-0000-0000-0000-000000000003', displayName: 'Hồ sơ A', profileVersion: 1,
}
const batchId = '40000000-0000-0000-0000-000000000004'
const analysisId = '50000000-0000-0000-0000-000000000005'
const accepted = {
  batchId, items: [{ analysisId, fileName: 'retina.png', processingStatus: 'QUEUED' }],
  createdAt: '2026-10-24T03:00:00Z', statusUrl: `/api/v1/batches/${batchId}`,
}
let context: BackendContext
const user = { uid: base.firebaseUid, getIdToken: vi.fn() }
const auth: { currentUser: typeof user | null } = { currentUser: user }
const fetchMock = vi.fn<typeof fetch>()
const closeBitmap = vi.fn()
const bitmap = vi.fn()
const createPreview = vi.fn()
const revokePreview = vi.fn()
function json(body: unknown, status = 202, headers = {}) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json', ...headers } })
}
function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason: unknown) => void
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}
function tree(state: BackendSessionState = { status: 'ready', context }) {
  return <BackendSessionContext.Provider value={{ state, retry: vi.fn(), logout: vi.fn() }}>
    <UploadPage />
  </BackendSessionContext.Provider>
}
function page() { return render(tree()) }
async function choose(files = [new File(['pixels-a'], 'retina.png', { type: 'image/png' })]) {
  fireEvent.change(screen.getByLabelText('Chọn ảnh võng mạc'), { target: { files } })
  await screen.findByText(`Đã chọn ${files.length} ảnh:`)
  return files
}
function submit() {
  fireEvent.click(screen.getByRole('button', { name: /^(Gửi ảnh phân tích|Thử lại cùng lượt gửi)$/ }))
}
const keyAt = (index: number) => new Headers(fetchMock.mock.calls[index][1]?.headers).get('Idempotency-Key')
const bodyAt = (index: number) => fetchMock.mock.calls[index][1]?.body as FormData

beforeEach(() => {
  vi.resetAllMocks()
  mocks.mock = false
  context = { ...base, roles: ['PATIENT'] }
  auth.currentUser = user
  mocks.auth.mockReturnValue(auth)
  user.uid = base.firebaseUid
  user.getIdToken.mockResolvedValue('fake-id-token')
  fetchMock.mockImplementation(async () => json(accepted))
  bitmap.mockImplementation(async () => ({ width: 1000, height: 1000, close: closeBitmap }))
  createPreview.mockReturnValue('blob:test-preview')
  const OriginalURL = URL
  vi.stubGlobal('URL', class extends OriginalURL {
    static createObjectURL = createPreview
    static revokeObjectURL = revokePreview
  })
  vi.stubGlobal('createImageBitmap', bitmap)
  vi.stubGlobal('fetch', fetchMock)
})
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks() })

describe('Personal upload using backend context and a stable request', () => {
  it('shows the verified profile without a patientId input or manual override', () => {
    page()
    expect(screen.getByText('Hồ sơ A')).toBeTruthy()
    expect(screen.queryByLabelText('Patient ID (UUID)')).toBeNull()
    expect(document.querySelector('input[name="patientId"]')).toBeNull()
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it.each<BackendSessionState>([
    { status: 'anonymous' }, { status: 'loading', firebaseUid: base.firebaseUid },
  ])('does not expose the upload form before the session is ready: %j', (state) => {
    render(tree(state))
    expect(screen.queryByLabelText('Chọn ảnh võng mạc')).toBeNull()
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it.each<[BackendContext['roles']]>([[['SYSTEM_ADMIN', 'PATIENT']], [['DOCTOR']], [['CLINIC']]])('blocks roles outside this personal upload flow: %j', (roles) => {
    context = { ...context, roles }
    page()
    expect(screen.getByRole('alert').textContent).toContain('vai trò bệnh nhân')
    expect(screen.queryByLabelText('Chọn ảnh võng mạc')).toBeNull()
  })

  it('requires at least one validated image', async () => {
    page()
    submit()
    expect((await screen.findByRole('alert')).textContent).toContain('ít nhất một ảnh')
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it.each<[string, File[], string]>([
    ['PDF', [new File(['pdf'], 'file.pdf', { type: 'application/pdf' })], 'JPG và PNG'],
    ['empty', [new File([], 'empty.png', { type: 'image/png' })], 'phải có dữ liệu'],
    ['oversize', [new File([new Uint8Array(10 * 1024 * 1024 + 1)], 'big.png', { type: 'image/png' })], '10 MB'],
    ['too many', Array.from({ length: 101 }, (_, n) => new File(['x'], `${n}.png`, { type: 'image/png' })), '100 ảnh'],
  ])('rejects %s before decoding or sending', async (_label, files, text) => {
    page()
    fireEvent.change(screen.getByLabelText('Chọn ảnh võng mạc'), { target: { files } })
    expect((await screen.findByRole('alert')).textContent).toContain(text as string)
    expect(bitmap).not.toHaveBeenCalled()
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('rejects excessive pixels and closes the decoded bitmap', async () => {
    bitmap.mockResolvedValue({ width: 5000, height: 4000, close: closeBitmap })
    page()
    fireEvent.change(screen.getByLabelText('Chọn ảnh võng mạc'), {
      target: { files: [new File(['x'], 'large.png', { type: 'image/png' })] },
    })
    expect((await screen.findByRole('alert')).textContent).toContain('16 triệu pixel')
    expect(closeBitmap).toHaveBeenCalledTimes(1)
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('accepts corrupted file or decoder failure so backend quarantine pipeline can isolate it', async () => {
    bitmap.mockRejectedValue(new Error('private-decoder-details'))
    page()
    fireEvent.change(screen.getByLabelText('Chọn ảnh võng mạc'), {
      target: { files: [new File(['bad'], 'bad.png', { type: 'image/png' })] },
    })
    expect(screen.queryByRole('alert')).toBeNull()
    expect(await screen.findByText('Đã chọn 1 ảnh:')).toBeTruthy()
  })

  it('renders quarantine notice when backend isolates corrupted payload', async () => {
    fetchMock.mockResolvedValueOnce(json({ ...accepted, items: [{
      ...accepted.items[0], processingStatus: 'QUARANTINE',
      error: { code: 'ERR_CORRUPTED_PAYLOAD', message: 'Tệp hỏng', retryable: false },
    }] }))
    page()
    await choose([new File(['bad-bytes'], 'bad_retina.jpg', { type: 'image/jpeg' })])
    submit()
    expect(await screen.findByText(/Dữ liệu ảnh không hợp lệ \(hỏng cấu trúc\)\. Đã cách ly sang nhánh Quarantine/)).toBeTruthy()
  })

  it('uses the backend patientId and preserves selected file order and eye side', async () => {
    fetchMock.mockResolvedValueOnce(json({ ...accepted, items: [accepted.items[0], {
      analysisId: context.accountId, fileName: 'second.jpg', processingStatus: 'QUEUED',
    }] }))
    page()
    fireEvent.change(screen.getByLabelText('Mắt được chụp'), { target: { value: 'right' } })
    const images = await choose([
      new File(['first-bytes'], 'retina.png', { type: 'image/png' }),
      new File(['second-bytes'], 'second.jpg', { type: 'image/jpeg' }),
    ])
    submit()
    await screen.findByText('Thông tin tiếp nhận')
    expect(bodyAt(0).get('patientId')).toBe(context.patientId)
    expect(bodyAt(0).get('eyeSide')).toBe('right')
    expect(bodyAt(0).get('modality')).toBe('FUNDUS')
    expect(bodyAt(0).getAll('images')).toEqual(images)
    expect(Array.from(bodyAt(0).keys())).not.toContain('tenantId')
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it('keeps the key, files and metadata after a lost response, then accepts a replay', async () => {
    fetchMock.mockRejectedValueOnce(new TypeError('lost-response')).mockResolvedValueOnce(json(accepted, 202, { 'Idempotent-Replayed': 'true' }))
    page()
    fireEvent.change(screen.getByLabelText('Mắt được chụp'), { target: { value: 'left' } })
    const images = await choose()
    submit()
    await screen.findByRole('alert')
    expect((screen.getByLabelText('Chọn ảnh võng mạc') as HTMLInputElement).disabled).toBe(true)
    expect((screen.getByLabelText('Mắt được chụp') as HTMLSelectElement).disabled).toBe(true)
    expect(screen.queryByRole('button', { name: /Tạo lượt|Chọn lại ảnh/ })).toBeNull()
    fireEvent.change(screen.getByLabelText('Mắt được chụp'), { target: { value: 'right' } })
    user.getIdToken.mockResolvedValue('fresh-retry-token')
    submit()
    expect(await screen.findByText('Đây là phản hồi phát lại của cùng lượt gửi.')).toBeTruthy()
    expect(keyAt(1)).toBe(keyAt(0))
    expect(bodyAt(1).get('eyeSide')).toBe('left')
    expect(bodyAt(1).getAll('images')).toEqual(images)
    expect(new Headers(fetchMock.mock.calls[1][1]?.headers).get('Authorization')).toBe('Bearer fresh-retry-token')
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })

  it.each([
    [402, 'ERR_INSUFFICIENT_CREDIT'], [409, 'ERR_REQUEST_IN_PROGRESS'],
    [429, 'ERR_TOO_MANY_REQUESTS'], [500, 'ERR_DEPENDENCY'], [503, 'ERR_DEPENDENCY'],
  ])('retries %i/%s with the original key only on a user click', async (status, code) => {
    fetchMock.mockResolvedValueOnce(json({ error: { code, retryable: true } }, status)).mockResolvedValueOnce(json(accepted))
    page()
    await choose()
    submit()
    await screen.findByRole('alert')
    expect(fetchMock).toHaveBeenCalledTimes(1)
    submit()
    await screen.findByText('Thông tin tiếp nhận')
    expect(keyAt(0)).toBe(keyAt(1))
  })

  it('retains ambiguity even when a later replay is rejected with a valid 400', async () => {
    fetchMock.mockRejectedValueOnce(new TypeError('lost')).mockResolvedValueOnce(json({
      error: { code: 'ERR_VALIDATION', retryable: false },
    }, 400))
    page()
    await choose()
    submit()
    await screen.findByRole('alert')
    submit()
    await waitFor(() => expect(screen.getByRole('alert').textContent).toContain('không hợp lệ'))
    expect(keyAt(1)).toBe(keyAt(0))
    expect(screen.queryByRole('button', { name: /Tạo lượt|Chọn lại ảnh/ })).toBeNull()
    expect(screen.getByRole('status').textContent).toContain('chưa rõ')
  })

  it('permits an explicit new draft after a first, verified validation rejection', async () => {
    fetchMock.mockResolvedValueOnce(json({ error: { code: 'ERR_VALIDATION', retryable: false } }, 400)).mockResolvedValueOnce(json(accepted))
    page()
    await choose()
    submit()
    fireEvent.click(await screen.findByRole('button', { name: 'Chọn lại ảnh sau khi bị từ chối' }))
    await choose()
    submit()
    await screen.findByText('Thông tin tiếp nhận')
    expect(keyAt(1)).not.toBe(keyAt(0))
  })

  it('never auto-rotates a reused-key conflict', async () => {
    fetchMock.mockResolvedValueOnce(json({ error: { code: 'ERR_IDEMPOTENCY_KEY_REUSED', retryable: false } }, 409))
    page()
    await choose()
    submit()
    expect((await screen.findByRole('alert')).textContent).toContain('đối chiếu')
    expect((screen.getByRole('button', { name: 'Thử lại cùng lượt gửi' }) as HTMLButtonElement).disabled).toBe(true)
    expect(screen.queryByRole('button', { name: /Tạo lượt|Chọn lại ảnh/ })).toBeNull()
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it('blocks duplicate submits while the response is pending', async () => {
    const pending = deferred<Response>()
    fetchMock.mockReturnValueOnce(pending.promise)
    page()
    await choose()
    const button = screen.getByRole('button', { name: 'Gửi ảnh phân tích' })
    fireEvent.click(button)
    fireEvent.click(button)
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1))
    expect((screen.getByRole('button', { name: 'Đang gửi ảnh...' }) as HTMLButtonElement).disabled).toBe(true)
    await act(async () => pending.resolve(json(accepted)))
    await screen.findByText('Thông tin tiếp nhận')
  })

  it('does not allow a second POST after acceptance until the user explicitly creates another draft', async () => {
    page()
    await choose()
    submit()
    fireEvent.click(await screen.findByRole('button', { name: 'Thử lại cùng lượt gửi' }))
    expect(fetchMock).toHaveBeenCalledTimes(1)
    fireEvent.click(screen.getByRole('button', { name: 'Tạo lượt tải ảnh mới' }))
    expect(screen.queryByText('Thông tin tiếp nhận')).toBeNull()
    expect((screen.getByLabelText('Chọn ảnh võng mạc') as HTMLInputElement).disabled).toBe(false)
    await choose()
    submit()
    await screen.findByText('Thông tin tiếp nhận')
    expect(keyAt(1)).not.toBe(keyAt(0))
  })

  it('does not report malformed 202 as accepted and replays with the same key', async () => {
    fetchMock.mockResolvedValueOnce(json({ ...accepted, statusUrl: 'https://external.example' })).mockResolvedValueOnce(json(accepted))
    page()
    await choose()
    submit()
    expect((await screen.findByRole('alert')).textContent).toContain('chưa hợp lệ')
    expect(screen.queryByText('Thông tin tiếp nhận')).toBeNull()
    submit()
    await screen.findByText('Thông tin tiếp nhận')
    expect(keyAt(1)).toBe(keyAt(0))
  })

  it('renders duplicate as a reference and performs no status/AI follow-up request', async () => {
    fetchMock.mockResolvedValueOnce(json({ ...accepted, items: [{
      ...accepted.items[0], processingStatus: 'DUPLICATE', existingAnalysisId: context.accountId,
      error: { code: 'ERR_DUPLICATE_REQUEST', message: 'duplicate', retryable: false },
    }] }))
    page()
    await choose()
    submit()
    expect(await screen.findByText(/Không tạo kết quả AI mới/)).toBeTruthy()
    expect(screen.getByRole('status').textContent).toContain('chưa phải kết quả phân tích AI')
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it('keeps an unresolved key on route unmount/remount, aborting the in-flight request', async () => {
    const old = deferred<Response>()
    fetchMock.mockReturnValueOnce(old.promise).mockResolvedValueOnce(json(accepted))
    const view = page()
    await choose()
    submit()
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1))
    const signal = fetchMock.mock.calls[0][1]?.signal
    view.unmount()
    expect(signal?.aborted).toBe(true)
    page()
    expect(screen.getByRole('status').textContent).toContain('chưa rõ')
    await act(async () => old.resolve(json(accepted)))
    expect(screen.queryByText('Thông tin tiếp nhận')).toBeNull()
    submit()
    await screen.findByText('Thông tin tiếp nhận')
    expect(keyAt(1)).toBe(keyAt(0))
  })

  it('does not reuse a prior account draft or send its images after context changes', async () => {
    const token = deferred<string>()
    user.getIdToken.mockReturnValueOnce(token.promise)
    const view = page()
    await choose()
    submit()
    const next = { ...context, firebaseUid: 'user-b', displayName: 'Hồ sơ B' }
    auth.currentUser = { ...user, uid: next.firebaseUid }
    view.rerender(tree({ status: 'ready', context: next }))
    expect(screen.getByText('Hồ sơ B')).toBeTruthy()
    expect(screen.queryByText('Đã chọn 1 ảnh:')).toBeNull()
    expect(screen.queryByText(/Mã yêu cầu upload/)).toBeNull()
    await act(async () => token.resolve('stale-a-token'))
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('does not send without a matching Firebase session even when the UI context is ready', async () => {
    auth.currentUser = null
    page()
    await choose()
    submit()
    expect((await screen.findByRole('alert')).textContent).toContain('Phiên đăng nhập')
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('warns before reload while the result is uncertain and removes that guard after acceptance', async () => {
    fetchMock.mockRejectedValueOnce(new TypeError('lost')).mockResolvedValueOnce(json(accepted))
    page()
    await choose()
    submit()
    await screen.findByRole('alert')
    const before = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(before)
    expect(before.defaultPrevented).toBe(true)
    submit()
    await screen.findByText('Thông tin tiếp nhận')
    const after = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(after)
    expect(after.defaultPrevented).toBe(false)
  })

  it('releases preview object URLs when the image selection is replaced or the view unmounts', async () => {
    const view = page()
    await choose()
    expect(createPreview).toHaveBeenCalledTimes(1)
    await choose([new File(['new'], 'new.png', { type: 'image/png' })])
    expect(revokePreview).toHaveBeenCalledTimes(1)
    expect(createPreview).toHaveBeenCalledTimes(2)
    view.unmount()
    expect(revokePreview).toHaveBeenCalledTimes(2)
    expect(closeBitmap).toHaveBeenCalledTimes(2)
  })

  it('blocks mock mode without any real upload request', async () => {
    mocks.mock = true
    page()
    await choose()
    submit()
    expect((await screen.findByRole('alert')).textContent).toContain('chế độ demo')
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('closes a bitmap returned after the view unmounts without accepting a stale selection', async () => {
    const pending = deferred<{ width: number; height: number; close: () => void }>()
    bitmap.mockReturnValueOnce(pending.promise)
    const view = page()
    fireEvent.change(screen.getByLabelText('Chọn ảnh võng mạc'), {
      target: { files: [new File(['old'], 'old.png', { type: 'image/png' })] },
    })
    view.unmount()
    await act(async () => pending.resolve({ width: 1000, height: 1000, close: closeBitmap }))
    page()
    expect(closeBitmap).toHaveBeenCalledTimes(1)
    expect(createPreview).not.toHaveBeenCalled()
    expect(screen.queryByText('Đã chọn 1 ảnh:')).toBeNull()
    expect(fetchMock).not.toHaveBeenCalled()
  })
})
