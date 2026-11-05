import type { BackendContext } from './backendContext'
import {
  createUploadAttempt, sendUploadAttempt, UploadRequestError,
  type EyeSide, type UploadAttempt, type UploadReceipt,
} from './analysisUpload'

export type UploadView = Readonly<{
  phase: 'editing' | 'validating' | 'sending' | 'error' | 'accepted'
  images: readonly File[]
  eyeSide: EyeSide
  attempt?: UploadAttempt
  receipt?: UploadReceipt
  error?: UploadRequestError
  validation?: string
  uncertain: boolean
}>

const empty = (): UploadView => Object.freeze({ phase: 'editing', images: Object.freeze([]), eyeSide: 'left', uncertain: false })

function createWorkspace(context: BackendContext) {
  let state = empty()
  const listeners = new Set<() => void>()
  let selection = 0
  let run = 0
  let controller: AbortController | undefined
  const publish = (next: UploadView) => {
    state = Object.freeze(next)
    for (const listener of listeners) listener()
  }
  const canReset = () => state.phase !== 'sending' && state.phase !== 'validating' && (
    state.phase === 'accepted' || !state.attempt ||
    (!state.uncertain && ['not-sent', 'rejected'].includes(state.error?.disposition ?? '')))

  async function selectImages(input: readonly File[]) {
    if (state.attempt || state.phase === 'sending') return
    const revision = ++selection
    const files = [...input]
    publish({ ...state, images: Object.freeze([]), phase: 'validating', validation: undefined, error: undefined })
    let problem: string | undefined
    if (files.length > 100) problem = 'Mỗi lần chỉ được chọn tối đa 100 ảnh.'
    else if (files.some((file) => !['image/jpeg', 'image/png'].includes(file.type))) {
      problem = 'Chỉ hỗ trợ ảnh JPG và PNG.'
    } else if (files.some((file) => file.size <= 0 || file.size > 10 * 1024 * 1024)) {
      problem = 'Mỗi ảnh phải có dữ liệu và không được vượt quá 10 MB.'
    }
    if (!problem) {
      for (const file of files) {
        try {
          const bitmap = await createImageBitmap(file)
          try {
            if (!Number.isSafeInteger(bitmap.width) || !Number.isSafeInteger(bitmap.height) ||
              bitmap.width <= 0 || bitmap.height <= 0 || bitmap.width * bitmap.height > 16_000_000) {
              problem = `Ảnh "${file.name}" vượt giới hạn 16 triệu pixel hoặc có kích thước không hợp lệ.`
            }
          } finally { bitmap.close() }
        } catch {
          problem = `Không thể đọc ảnh "${file.name}".`
        }
        if (revision !== selection) return
        if (problem) break
      }
    }
    if (revision !== selection) return
    publish({ ...state, phase: 'editing', images: Object.freeze(problem ? [] : files), validation: problem })
  }

  function setEyeSide(eyeSide: EyeSide) {
    if (state.attempt || state.phase !== 'editing' || !['left', 'right', 'unknown', 'corrupted', 'retry'].includes(eyeSide)) return
    publish({ ...state, eyeSide })
  }

  async function send() {
    if (state.phase === 'sending' || state.phase === 'validating' || state.phase === 'accepted' ||
      (state.phase === 'error' && !state.error?.canRetry)) return
    if (!state.images.length) {
      publish({ ...state, validation: 'Vui lòng chọn ít nhất một ảnh.' })
      return
    }
    let attempt: UploadAttempt
    try { attempt = state.attempt ?? createUploadAttempt(context, state.images, state.eyeSide) }
    catch {
      publish({ ...state, validation: 'Không tạo được lượt gửi cho hồ sơ hiện tại.' })
      return
    }
    const revision = ++run
    controller = new AbortController()
    const signal = controller.signal
    publish({ ...state, attempt, phase: 'sending', error: undefined, validation: undefined })
    try {
      const receipt = await sendUploadAttempt(attempt, signal)
      if (revision !== run) return
      publish({ ...state, phase: 'accepted', receipt, uncertain: false })
    } catch (error) {
      if (revision !== run) return
      const failure = error instanceof UploadRequestError ? error
        : new UploadRequestError(0, 'ERR_OUTCOME_UNKNOWN', 'uncertain', true)
      publish({ ...state, phase: 'error', error: failure,
        uncertain: state.uncertain || failure.disposition === 'uncertain' || failure.disposition === 'conflict' })
    } finally {
      if (revision === run) controller = undefined
    }
  }

  function release() {
    selection += 1
    run += 1
    controller?.abort()
    controller = undefined
    if (state.phase === 'sending') {
      publish({ ...state, phase: 'error', uncertain: true,
        error: new UploadRequestError(0, 'ERR_UPLOAD_INTERRUPTED', 'uncertain', true) })
    } else if (state.phase === 'validating') {
      publish({ ...state, phase: 'editing', images: Object.freeze([]) })
    }
  }

  return {
    getSnapshot: () => state,
    subscribe: (listener: () => void) => { listeners.add(listener); return () => { listeners.delete(listener) } },
    selectImages, setEyeSide, send, release, canReset,
    reset: () => { if (canReset()) { selection += 1; publish(empty()) } },
  }
}

// Route changes keep an unresolved attempt for the same context object in this tab.
// A new backend session gets a distinct workspace; WeakMap does not retain old sessions.
const workspaces = new WeakMap<BackendContext, ReturnType<typeof createWorkspace>>()
export function getPersonalUploadWorkspace(context: BackendContext) {
  let workspace = workspaces.get(context)
  if (!workspace) { workspace = createWorkspace(context); workspaces.set(context, workspace) }
  return workspace
}
