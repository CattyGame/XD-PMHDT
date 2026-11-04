import { getFirebaseAuth } from '../firebase/config'
import { isMockEnabled } from './client'
import type { BackendContext } from './backendContext'
import {
  UploadRequestError, parseUploadHttpError, parseUploadReceipt,
  type UploadAttempt, type UploadReceipt, type EyeSide,
} from './analysisUpload'

const BASE = '/api/v1/analysis-upload-batches'
export const MAX_IMAGE_BYTES = 10 * 1024 * 1024
export const DEFAULT_ITEM_CONCURRENCY = 4
export const MAX_ITEM_CONCURRENCY = 6
type UploadStatus = 'DECLARED' | 'STAGED' | 'REJECTED' | 'UNKNOWN' | 'ACCEPTED'
export type UploadInventoryItem = Readonly<{
  itemId: string; patientId: string; fileName: string; sha256: string
  modality: 'FUNDUS'; eyeSide: EyeSide; uploadStatus: UploadStatus
  analysisId?: string; error?: Readonly<{ code: string; message: string; retryable?: boolean }>
}>
export type UploadInventory = Readonly<{
  batchId: string; createdAt: string; expiresAt?: string; statusUrl: string
  status?: 'UPLOADING' | 'FINALIZING' | 'FINALIZED' | 'EXPIRED'; items: readonly UploadInventoryItem[]
  finalizedResponse?: unknown
}>
export type ItemUploadOptions = {
  clinicContextId?: string; concurrency?: number; signal?: AbortSignal
  onInventory?: (inventory: UploadInventory) => void
}
type Prepared = { items: readonly UploadInventoryItem[]; files: ReadonlyMap<string, File>; batchId?: string }
const prepared = new WeakMap<UploadAttempt, Promise<Prepared>>()
export type ClinicUploadSelection = Readonly<{ file: File; patientId: string; eyeSide: EyeSide }>
const clinicSelections = new WeakMap<UploadAttempt, { clinicId: string; selections: readonly ClinicUploadSelection[] }>()
const uuid = (x: unknown): x is string => typeof x === 'string' &&
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(x) && !/^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(x)
const object = (x: unknown): x is Record<string, unknown> => x !== null && typeof x === 'object' && !Array.isArray(x)
const invalid = (): never => { throw new UploadRequestError(0, 'ERR_INVALID_RESPONSE', 'uncertain', true) }
const eyes = ['left', 'right', 'unknown']

function inventory(payload: unknown, expectedId?: string): UploadInventory {
  if (!object(payload) || !uuid(payload.batchId) || (expectedId && payload.batchId !== expectedId) ||
    payload.statusUrl !== `/api/v1/batches/${payload.batchId}` || typeof payload.createdAt !== 'string' ||
    !Number.isFinite(Date.parse(payload.createdAt)) || !Array.isArray(payload.items) ||
    payload.items.length < 1 || payload.items.length > 100) return invalid()
  const seen = new Set<string>()
  for (const item of payload.items) {
    if (!object(item) || !uuid(item.itemId) || seen.has(item.itemId) || !uuid(item.patientId) ||
      typeof item.fileName !== 'string' || !item.fileName || typeof item.sha256 !== 'string' ||
      !/^[a-f0-9]{64}$/.test(item.sha256) || item.modality !== 'FUNDUS' || !eyes.includes(String(item.eyeSide)) ||
      !['DECLARED', 'STAGED', 'REJECTED', 'UNKNOWN', 'ACCEPTED'].includes(String(item.uploadStatus))) return invalid()
    seen.add(item.itemId)
  }
  return payload as unknown as UploadInventory
}

async function bytes(file: File): Promise<ArrayBuffer> {
  if (typeof file.arrayBuffer === 'function') return file.arrayBuffer()
  return new Promise((resolve, reject) => {
    const reader = new FileReader()
    reader.onload = () => reader.result instanceof ArrayBuffer ? resolve(reader.result) : reject(new Error('File read failed'))
    reader.onerror = () => reject(new Error('File read failed'))
    reader.readAsArrayBuffer(file)
  })
}
export async function imageSha256(file: File): Promise<string> {
  return Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', await bytes(file))))
    .map(x => x.toString(16).padStart(2, '0')).join('')
}
async function operationId(value: string): Promise<string> {
  const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(value)))
  digest[6] = (digest[6] & 15) | 80
  digest[8] = (digest[8] & 63) | 128
  const hex = Array.from(digest.subarray(0, 16)).map(x => x.toString(16).padStart(2, '0')).join('')
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
}
function validateFile(file: File) {
  if (file.size < 1 || file.size > MAX_IMAGE_BYTES ||
    (!['image/jpeg', 'image/png'].includes(file.type) && !/\.(jpe?g|png)$/i.test(file.name))) {
    throw new UploadRequestError(0, 'ERR_INVALID_UPLOAD_DRAFT', 'not-sent', false)
  }
}
async function prepare(attempt: UploadAttempt): Promise<Prepared> {
  if (!uuid(attempt.key) || !uuid(attempt.context.patientId) || !eyes.includes(attempt.eyeSide) ||
    !attempt.images.length || attempt.images.length > 100) throw new UploadRequestError(0, 'ERR_INVALID_UPLOAD_DRAFT', 'not-sent', false)
  const items: UploadInventoryItem[] = [], files = new Map<string, File>()
  const selections = clinicSelections.get(attempt)?.selections ?? attempt.images.map(file => ({ file, patientId: attempt.context.patientId, eyeSide: attempt.eyeSide }))
  // Hash sequentially to bound memory; no image bytes or patient metadata are persisted in browser storage.
  for (let i = 0; i < attempt.images.length; i++) {
    const { file, patientId, eyeSide } = selections[i]
    if (!uuid(patientId) || !eyes.includes(eyeSide)) throw new UploadRequestError(0, 'ERR_INVALID_UPLOAD_DRAFT', 'not-sent', false)
    validateFile(file)
    const itemId = await operationId(`${attempt.key}:item:${i}`)
    items.push({ itemId, patientId, fileName: file.name,
      sha256: await imageSha256(file), modality: 'FUNDUS', eyeSide, uploadStatus: 'DECLARED' })
    files.set(itemId, file)
  }
  return { items, files }
}

/** Clinic UI supplies per-image patient/eye metadata; server resolves membership and tenant authority. */
export function createClinicItemUploadAttempt(context: BackendContext, clinicId: string, selections: readonly ClinicUploadSelection[]): UploadAttempt {
  if (!uuid(clinicId) || !uuid(context.accountId) || !context.firebaseUid || !selections.length || selections.length > 100 ||
    selections.some(x => !uuid(x.patientId) || !eyes.includes(x.eyeSide))) {
    throw new UploadRequestError(0, 'ERR_INVALID_UPLOAD_DRAFT', 'not-sent', false)
  }
  const attempt: UploadAttempt = Object.freeze({ key: crypto.randomUUID(), context, eyeSide: 'unknown', images: Object.freeze(selections.map(x => x.file)) })
  clinicSelections.set(attempt, { clinicId, selections: Object.freeze(selections.map(x => Object.freeze({ ...x }))) })
  return attempt
}

function transport(context: BackendContext, options: ItemUploadOptions) {
  if (isMockEnabled) throw new UploadRequestError(0, 'ERR_MOCK_ENABLED', 'not-sent', false)
  const auth = getFirebaseAuth(), user = auth?.currentUser
  if (!user || user.uid !== context.firebaseUid) throw new UploadRequestError(401, 'ERR_SESSION_CHANGED', 'not-sent', false)
  const signal = options.signal ? AbortSignal.any([options.signal, AbortSignal.timeout(600_000)]) : AbortSignal.timeout(600_000)
  const query = options.clinicContextId ? `?clinicContextId=${encodeURIComponent(options.clinicContextId)}` : ''
  if (options.clinicContextId && !uuid(options.clinicContextId)) throw new UploadRequestError(0, 'ERR_INVALID_UPLOAD_DRAFT', 'not-sent', false)
  const check = () => {
    if (signal.aborted) throw new UploadRequestError(0, 'ERR_UPLOAD_INTERRUPTED', 'uncertain', true)
    if (auth.currentUser !== user || user.uid !== context.firebaseUid) throw new UploadRequestError(401, 'ERR_SESSION_CHANGED', 'uncertain', false)
  }
  const bounded = <T>(promise: Promise<T>, requestSignal: AbortSignal): Promise<T> => new Promise((resolve, reject) => {
    const abort = () => reject(new UploadRequestError(0, 'ERR_UPLOAD_INTERRUPTED', 'uncertain', true))
    if (requestSignal.aborted) { void promise.catch(() => {}); abort(); return }
    requestSignal.addEventListener('abort', abort, { once: true })
    promise.then(resolve, reject).finally(() => requestSignal.removeEventListener('abort', abort)).catch(() => {})
  })
  const request = async (path: string, method: string, body?: BodyInit, key?: string): Promise<unknown> => {
    check()
    let sent = false
    try {
      const requestSignal = AbortSignal.any([signal, AbortSignal.timeout(60_000)])
      const token = await bounded(user.getIdToken(), requestSignal)
      check()
      if (!token || /\s/.test(token)) throw new UploadRequestError(401, 'ERR_TOKEN_UNAVAILABLE', 'not-sent', false)
      const headers: Record<string, string> = { Authorization: `Bearer ${token}` }
      if (key) headers['Idempotency-Key'] = key
      if (typeof body === 'string') headers['Content-Type'] = 'application/json'
      sent = true
      const response = await bounded(fetch(path + query, { method, body, headers, signal: requestSignal,
        credentials: 'omit', redirect: 'error', cache: 'no-store' }), requestSignal)
      check()
      const value: unknown = await bounded(response.json().catch(() => null), requestSignal)
      check()
      if (!response.ok) throw parseUploadHttpError(response.status, value)
      return value
    } catch (cause) {
      if (cause instanceof UploadRequestError) throw cause
      throw new UploadRequestError(0, signal.aborted ? 'ERR_UPLOAD_INTERRUPTED' : sent ? 'ERR_NETWORK' : 'ERR_TOKEN_UNAVAILABLE',
        sent ? 'uncertain' : 'not-sent', true)
    }
  }
  return { request, check }
}

function verifyManifest(actual: UploadInventory, expected: readonly UploadInventoryItem[]) {
  if (actual.items.length !== expected.length) return invalid()
  const byId = new Map(expected.map(x => [x.itemId, x]))
  for (const item of actual.items) {
    const match = byId.get(item.itemId)
    if (!match || ['sha256', 'patientId', 'fileName', 'modality', 'eyeSide'].some(k =>
      item[k as keyof UploadInventoryItem] !== match[k as keyof UploadInventoryItem])) return invalid()
  }
}

async function finish(state: Prepared, initial: UploadInventory, context: BackendContext, options: ItemUploadOptions): Promise<UploadReceipt> {
  const { request, check } = transport(context, options)
  let current = initial
  if (current.status === 'EXPIRED') throw new UploadRequestError(410, 'ERR_UPLOAD_EXPIRED', 'rejected', false)
  verifyManifest(current, state.items)
  const publish = (next: UploadInventory) => { current = next; options.onInventory?.(next) }
  publish(current)
  const finalizeKey = await operationId(`${current.batchId}:finalize`)
  const template: UploadAttempt = { key: finalizeKey, context, eyeSide: 'unknown', images: current.items.map(() => ({} as File)) }
  if (current.finalizedResponse) return parseUploadReceipt(current.finalizedResponse, template, true)
  const pending = current.items.filter(x => x.uploadStatus === 'DECLARED')
  let cursor = 0, firstError: unknown
  const requested = options.concurrency ?? DEFAULT_ITEM_CONCURRENCY
  const limit = Number.isInteger(requested) && requested > 0 ? Math.min(requested, MAX_ITEM_CONCURRENCY) : DEFAULT_ITEM_CONCURRENCY
  const worker = async () => {
    while (!firstError && cursor < pending.length) {
      const item = pending[cursor++]
      try {
        check()
        const file = state.files.get(item.itemId)
        if (!file) throw new UploadRequestError(0, 'ERR_UPLOAD_FILES_REQUIRED', 'not-sent', true)
        const form = new FormData(); form.append('image', file)
        const response = await request(`${BASE}/${current.batchId}/items/${item.itemId}`, 'PUT', form, item.itemId)
        if (!object(response) || response.itemId !== item.itemId) return invalid()
        const updated = inventory({ ...current, items: current.items.map(x => x.itemId === item.itemId ? response : x) }, current.batchId)
        verifyManifest(updated, state.items)
        publish(updated)
      } catch (cause) { firstError ??= cause }
    }
  }
  // Wait for every in-flight request. No sibling upload continues after this function returns.
  await Promise.all(Array.from({ length: Math.min(limit, pending.length) }, worker))
  check()
  const reconciled = inventory(await request(`${BASE}/${current.batchId}`, 'GET'), current.batchId)
  verifyManifest(reconciled, state.items)
  publish(reconciled)
  if (reconciled.finalizedResponse) return parseUploadReceipt(reconciled.finalizedResponse, template, true)
  if (reconciled.items.some(x => x.uploadStatus === 'DECLARED')) {
    throw firstError ?? new UploadRequestError(0, 'ERR_UPLOAD_FILES_REQUIRED', 'not-sent', true)
  }
  return parseUploadReceipt(await request(`${BASE}/${current.batchId}/finalize`, 'POST', '{}', finalizeKey), template, false)
}

/** Versioned one-batch/item protocol. Retries reconcile persisted inventory before sending any item again. */
export async function sendItemUploadAttempt(attempt: UploadAttempt, options: ItemUploadOptions = {}): Promise<UploadReceipt> {
  const clinic = clinicSelections.get(attempt)?.clinicId
  if (clinic) {
    if (options.clinicContextId && options.clinicContextId !== clinic) throw new UploadRequestError(0, 'ERR_INVALID_UPLOAD_DRAFT', 'not-sent', false)
    options = { ...options, clinicContextId: clinic }
  }
  const { request, check } = transport(attempt.context, options)
  let promise = prepared.get(attempt)
  if (!promise) { promise = prepare(attempt); prepared.set(attempt, promise) }
  const state = await promise
  check()
  let current: UploadInventory
  if (state.batchId) current = inventory(await request(`${BASE}/${state.batchId}`, 'GET'), state.batchId)
  else {
    current = inventory(await request(BASE, 'POST', JSON.stringify({ items: state.items.map(item => ({
      itemId: item.itemId, patientId: item.patientId, fileName: item.fileName, sha256: item.sha256,
      modality: item.modality, eyeSide: item.eyeSide,
    })) }), attempt.key))
    state.batchId = current.batchId
  }
  return finish(state, current, attempt.context, options)
}

export async function loadUploadInventory(context: BackendContext, batchId: string, options: ItemUploadOptions = {}): Promise<UploadInventory> {
  if (!uuid(batchId)) throw new UploadRequestError(0, 'ERR_INVALID_UPLOAD_DRAFT', 'not-sent', false)
  const { request } = transport(context, options)
  const value = inventory(await request(`${BASE}/${batchId}`, 'GET'), batchId)
  if (value.status === 'EXPIRED') throw new UploadRequestError(410, 'ERR_UPLOAD_EXPIRED', 'rejected', false)
  return value
}

/** F5 recovery: metadata stays server-side; reselected bytes are matched by checksum, never filename alone. */
export async function resumeItemUpload(context: BackendContext, batchId: string, selectedFiles: readonly File[], options: ItemUploadOptions = {}): Promise<UploadReceipt> {
  const current = await loadUploadInventory(context, batchId, options)
  const files = new Map<string, File>(), hashes = new Map<string, File>()
  for (const file of selectedFiles) { validateFile(file); hashes.set(await imageSha256(file), file) }
  for (const item of current.items) { const file = hashes.get(item.sha256); if (file) files.set(item.itemId, file) }
  return finish({ items: current.items, files, batchId }, current, context, options)
}
