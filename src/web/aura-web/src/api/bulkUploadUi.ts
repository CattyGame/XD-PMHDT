/** SCRUM-356 — UI-only validation. Backend remains the authority for file content and patient rights. */
export const MAX_BULK_IMAGES = 100
export const MAX_BULK_FILE_BYTES = 10 * 1024 * 1024

export type InvalidBulkImage = Readonly<{ name: string; reason: string }>
export type BulkSelectionResult = Readonly<{
  accepted: readonly File[]
  rejected: readonly InvalidBulkImage[]
  tooMany: boolean
}>

export const bulkFileKey = (file: File) => JSON.stringify([file.name, file.size, file.lastModified])

export function matchesBulkImageFormat(file: File): boolean {
  const expectedMime = /\.png$/i.test(file.name) ? 'image/png' : /\.jpe?g$/i.test(file.name) ? 'image/jpeg' : null
  return expectedMime !== null && (!file.type || file.type === expectedMime)
}

export function validateBulkFiles(files: readonly File[]): BulkSelectionResult {
  if (files.length > MAX_BULK_IMAGES) {
    return { accepted: [], tooMany: true, rejected: [{
      name: 'Toàn bộ lượt chọn', reason: `Vượt giới hạn ${MAX_BULK_IMAGES} ảnh mỗi batch. Hãy chọn lại, không chia batch ngầm.`,
    }] }
  }
  const accepted: File[] = []
  const rejected: InvalidBulkImage[] = []
  const seen = new Set<string>()
  for (const file of files) {
    const key = bulkFileKey(file)
    const reason = !matchesBulkImageFormat(file)
      ? 'Chỉ chấp nhận .jpg, .jpeg hoặc .png với định dạng tương ứng.'
      : file.size === 0
        ? 'Ảnh trống (0 byte).'
        : file.size > MAX_BULK_FILE_BYTES
          ? 'Dung lượng vượt 10 MiB.'
          : seen.has(key) ? 'Tệp này đã có trong danh sách.' : ''
    if (reason) rejected.push({ name: file.name, reason })
    else {
      seen.add(key)
      accepted.push(file)
    }
  }
  return { accepted, rejected, tooMany: false }
}

const UUID_IN_NAME = /(?:^|[^0-9a-f])([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})(?=[^0-9a-f]|$)/gi
/** File names can only SELECT an existing authorised patient; never create new patients from file names. */
export function matchPatientIdInFilename(fileName: string, patients: readonly { patientId: string }[]): string {
  const allowed = new Map(patients.map(p => [p.patientId.toLowerCase(), p.patientId]))
  const candidates = [...fileName.matchAll(UUID_IN_NAME)]
    .map(match => match[1].toLowerCase())
    .filter(id => allowed.has(id))
  const unique = [...new Set(candidates)]
  return unique.length === 1 ? allowed.get(unique[0]) ?? '' : ''
}

/** A percentage of server-confirmed final outcomes, NOT the percentage of bytes uploaded. */
export function completedBatchCount(summary: Readonly<{
  total: number; succeeded: number; failed: number; duplicate: number
}>, admissionRejected = 0): number {
  return summary.succeeded + summary.failed + summary.duplicate + admissionRejected
}
export function calculateBatchPercent(summary: Readonly<{
  total: number; succeeded: number; failed: number; duplicate: number
}>, totalDeclared?: number, admissionRejected = 0): number {
  const total = totalDeclared ?? summary.total
  if (total === 0) return 0
  return Math.max(0, Math.min(100, Math.round(completedBatchCount(summary, admissionRejected) * 100 / total)))
}
