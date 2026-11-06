/** Tiny, ephemeral thumbnails for the same browser tab only.
 * Batch GET currently has no thumbnail URL. Never store patient images in localStorage.
 * A refresh or new login intentionally falls back to a placeholder until M2 supplies an authorised image API.
 */
let currentKey = ''
const previews = new Map<string, string>()
const listeners = new Set<() => void>()
const notify = () => { listeners.forEach(listener => listener()) }

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
  currentKey = key
  previews.clear()
  notify()
  if (typeof createImageBitmap !== 'function') return
  const counts = new Map<string, number>()
  for (const file of files) counts.set(file.name, (counts.get(file.name) ?? 0) + 1)
  // Sequential decoding. At most one large Fundus bitmap exists at a time.
  for (const file of files) {
    if (currentKey !== key) return
    if (counts.get(file.name) !== 1) continue // No incorrect matching when two files share a name.
    let bitmap: ImageBitmap | undefined
    try {
      bitmap = await createImageBitmap(file)
      if (!bitmap.width || !bitmap.height) continue
      const canvas = document.createElement('canvas')
      const ratio = Math.min(96 / bitmap.width, 96 / bitmap.height, 1)
      canvas.width = Math.max(1, Math.round(bitmap.width * ratio))
      canvas.height = Math.max(1, Math.round(bitmap.height * ratio))
      const ctx = canvas.getContext('2d')
      if (!ctx) continue
      ctx.drawImage(bitmap, 0, 0, canvas.width, canvas.height)
      const thumbnail = canvas.toDataURL('image/jpeg', 0.62)
      if (currentKey !== key) return
      previews.set(file.name, thumbnail)
      notify()
    } catch { /* A corrupt file remains visible with a safe placeholder. */ }
    finally { bitmap?.close() }
    // Yield between images so a 50-100-image batch does not monopolise the main thread.
    await new Promise<void>(resolve => setTimeout(resolve, 0))
  }
}
