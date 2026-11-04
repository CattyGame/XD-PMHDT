import type { BackendContext } from './backendContext'
import {
  createUploadAttempt, UploadRequestError,
  type EyeSide, type UploadAttempt, type UploadReceipt,
} from './analysisUpload'
import { sendItemUploadAttempt, loadUploadInventory, resumeItemUpload, type UploadInventory } from './itemUpload'

export type UploadView = Readonly<{
  phase: 'editing' | 'validating' | 'restoring' | 'sending' | 'error' | 'accepted'
  images: readonly File[]
  eyeSide: EyeSide
  attempt?: UploadAttempt
  receipt?: UploadReceipt
  error?: UploadRequestError
  validation?: string
  uncertain: boolean
  inventory?: UploadInventory
  resumeBatchId?: string
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
    state.phase === 'accepted' || (!state.attempt && !state.resumeBatchId) ||
    (!state.uncertain && ['not-sent', 'rejected'].includes(state.error?.disposition ?? '')))

  function rememberBatch(batchId?: string) {
    const url = new URL(window.location.href)
    if (batchId) url.searchParams.set('uploadBatchId', batchId)
    else url.searchParams.delete('uploadBatchId')
    window.history.replaceState(window.history.state, '', url)
  }
  async function restoreFromUrl() {
    const batchId = new URL(window.location.href).searchParams.get('uploadBatchId')
    if (!batchId || state.attempt || state.receipt || state.phase === 'restoring' || state.resumeBatchId === batchId) return
    const revision = ++run
    controller = new AbortController()
    publish({ ...state, phase: 'restoring' })
    try {
      const inventory = await loadUploadInventory(context, batchId, { signal: controller.signal })
      if (revision === run) publish({ ...state, phase: 'editing', resumeBatchId: batchId, inventory, uncertain: false })
    } catch (cause) {
      if (revision !== run) return
      const error = cause instanceof UploadRequestError ? cause : new UploadRequestError(0, 'ERR_NETWORK', 'uncertain', true)
      publish({ ...empty(), phase: 'error', error, resumeBatchId: batchId, uncertain: error.code !== 'ERR_UPLOAD_EXPIRED' })
    }
  }

  async function selectImages(input: readonly File[]) {
    if (state.attempt || state.phase === 'sending' || state.phase === 'restoring') return
    const revision = ++selection
    const files = [...input]
    publish({ ...state, images: Object.freeze([]), phase: 'validating', validation: undefined, error: undefined })
    let problem: string | undefined
    if (files.length > 100) problem = 'Mỗi lần chỉ được chọn tối đa 100 ảnh.'
    else if (files.some((file) => !['image/jpeg', 'image/png'].includes(file.type) && !/\.(jpe?g|png)$/i.test(file.name))) {
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
          // Bỏ qua lỗi decode bitmap ở client để cho phép tải tệp hỏng (corrupted file)
          // lên pipeline kiểm định Quarantine của backend theo kịch bản demo (Scenario 3).
        }
        if (revision !== selection) return
        if (problem) break
      }
    }
    if (revision !== selection) return
    publish({ ...state, phase: 'editing', images: Object.freeze(problem ? [] : files), validation: problem })
  }

  function setEyeSide(eyeSide: EyeSide) {
    if (state.attempt || state.resumeBatchId || state.phase !== 'editing' || !['left', 'right', 'unknown'].includes(eyeSide)) return
    publish({ ...state, eyeSide })
  }

  async function send() {
    if (state.phase === 'sending' || state.phase === 'validating' || state.phase === 'accepted' ||
      (state.phase === 'error' && !state.error?.canRetry)) return
    if (!state.images.length && !state.resumeBatchId) {
      publish({ ...state, validation: 'Vui lòng chọn ít nhất một ảnh.' })
      return
    }
    let attempt: UploadAttempt | undefined
    try { attempt = state.attempt ?? (state.resumeBatchId ? undefined : createUploadAttempt(context, state.images, state.eyeSide)) }
    catch {
      publish({ ...state, validation: 'Không tạo được lượt gửi cho hồ sơ hiện tại.' })
      return
    }
    const revision = ++run
    controller = new AbortController()
    const signal = controller.signal
    publish({ ...state, attempt, phase: 'sending', error: undefined, validation: undefined })
    try {
      const onInventory = (inventory: UploadInventory) => {
        if (revision !== run) return
        rememberBatch(inventory.batchId)
        publish({ ...state, inventory, resumeBatchId: inventory.batchId })
      }
      const receipt = attempt
        ? await sendItemUploadAttempt(attempt, { signal, onInventory })
        : await resumeItemUpload(context, state.resumeBatchId!, state.images, { signal, onInventory })
      if (revision !== run) return
      publish({ ...state, phase: 'accepted', receipt, uncertain: false })
    } catch (error) {
      if (revision !== run) return
      const failure = error instanceof UploadRequestError ? error
        : new UploadRequestError(0, 'ERR_OUTCOME_UNKNOWN', 'uncertain', true)
      publish({ ...state, phase: 'error', error: failure,
        uncertain: failure.code !== 'ERR_UPLOAD_EXPIRED' && (state.uncertain || failure.disposition === 'uncertain' || failure.disposition === 'conflict') })
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
    } else if (state.phase === 'restoring') {
      publish({ ...state, phase: 'editing' })
    }
  }

  return {
    getSnapshot: () => state,
    subscribe: (listener: () => void) => { listeners.add(listener); return () => { listeners.delete(listener) } },
    selectImages, setEyeSide, send, release, canReset, restoreFromUrl,
    reset: () => { if (canReset()) { selection += 1; rememberBatch(); publish(empty()) } },
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
