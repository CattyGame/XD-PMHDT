/** Tiny, ephemeral thumbnails for the same browser tab only.
 * Batch GET currently has no thumbnail URL. Never store patient images in localStorage.
 * A refresh or new login intentionally falls back to a placeholder until M2 supplies an authorised image API.
 */
let currentKey = ''
let generation = 0
const previews = new Map<string, string>()
const listeners = new Set<() => void>()
const notify = () => { listeners.forEach(listener => listener()) }

export function clearBatchPreviews() {
  generation += 1
  currentKey = ''
  for (const url of previews.values()) URL.revokeObjectURL(url)
  previews.clear()
  notify()
}

export function subscribeBatchPreviews(listener: () => void): () => void {
  listeners.add(listener)
  return () => { listeners.delete(listener) }
}

export function getBatchPreview(batchId: string, firebaseUid: string, fileName: string | null): string | undefined {
  if (!fileName || currentKey !== `${firebaseUid}:${batchId.toLowerCase()}`) return undefined
  return previews.get(fileName)
}

export async function captureBatchPreviews(batchId: string, firebaseUid: string, files: readonly File[]): Promise<void> {
  const key = `${firebaseUid}:${batchId.toLowerCase()}`
  clearBatchPreviews()
  const run = generation
  currentKey = key
  if (typeof createImageBitmap !== 'function' || typeof URL.createObjectURL !== 'function') return
  const counts = new Map<string, number>()
  for (const file of files) counts.set(file.name, (counts.get(file.name) ?? 0) + 1)
  // Sequential decoding. At most one large Fundus bitmap exists at a time.
  for (const file of files) {
    if (run !== generation) return
    if (counts.get(file.name) !== 1) continue // No incorrect matching when two files share a name.
    let bitmap: ImageBitmap | undefined
    try {
      bitmap = await createImageBitmap(file)
      if (run !== generation) return
      if (!bitmap.width || !bitmap.height || bitmap.width * bitmap.height > 16_000_000) continue
      const canvas = document.createElement('canvas')
      const ratio = Math.min(96 / bitmap.width, 96 / bitmap.height, 1)
      canvas.width = Math.max(1, Math.round(bitmap.width * ratio))
      canvas.height = Math.max(1, Math.round(bitmap.height * ratio))
      const ctx = canvas.getContext('2d')
      if (!ctx) continue
      ctx.drawImage(bitmap, 0, 0, canvas.width, canvas.height)
      bitmap.close()
      bitmap = undefined
      const thumbnail = await new Promise<Blob | null>(resolve => canvas.toBlob(resolve, 'image/jpeg', 0.62))
      if (run !== generation) return
      if (!thumbnail) continue
      previews.set(file.name, URL.createObjectURL(thumbnail))
      notify()
    } catch { /* A corrupt file remains visible with a safe placeholder. */ }
    finally { bitmap?.close() }
    // Yield between images so a 50-100-image batch does not monopolise the main thread.
    await new Promise<void>(resolve => setTimeout(resolve, 0))
  }
}
