import { describe, expect, it } from 'vitest'
import { MAX_BULK_IMAGES, calculateBatchPercent, matchPatientIdInFilename, validateBulkFiles } from './bulkUploadUi'

const image = (name: string, size = 12, type = 'image/png') => new File([new Uint8Array(size)], name, { type })
const patientA = '11111111-1111-4111-8111-111111111111'
const patientB = '22222222-2222-4222-8222-222222222222'

describe('SCRUM-356 file validation', () => {
  it('allows a 50-file selection and the 100-file upper bound', () => {
    for (const count of [50, MAX_BULK_IMAGES]) {
      const result = validateBulkFiles(Array.from({ length: count }, (_, i) => image(`fundus-${i}.png`)))
      expect(result.accepted).toHaveLength(count)
      expect(result.rejected).toHaveLength(0)
    }
  })
  it('refuses 101 files instead of silently splitting a chargeable batch', () => {
    const result = validateBulkFiles(Array.from({ length: 101 }, (_, i) => image(`${i}.png`)))
    expect(result.tooMany).toBe(true)
    expect(result.accepted).toHaveLength(0)
  })
  it('returns per-file errors while retaining acceptable files', () => {
    const result = validateBulkFiles([
      image('ok.png'), image('wrong.gif', 3, 'image/gif'), image('fake.jpg', 3, 'image/png'),
      image('empty.png', 0), image('large.png', 10 * 1024 * 1024 + 1),
    ])
    expect(result.accepted.map(f => f.name)).toEqual(['ok.png'])
    expect(result.rejected).toHaveLength(4)
    expect(result.rejected.some(x => x.name === 'large.png' && x.reason.includes('10 MiB'))).toBe(true)
  })
  it('allows a 10 MiB file and missing browser MIME where extension is allowed', () => {
    const result = validateBulkFiles([image('edge.jpg', 10 * 1024 * 1024, 'image/jpeg'), image('unknown.jpeg', 1, '')])
    expect(result.accepted).toHaveLength(2)
  })
})

describe('SCRUM-356 mapping and progress', () => {
  it('only fills a patient ID from a file name when the ID is in the authorised patient list', () => {
    expect(matchPatientIdInFilename(`left_${patientA}.png`, [{ patientId: patientA }])).toBe(patientA)
    expect(matchPatientIdInFilename(`left_${patientB}.png`, [{ patientId: patientA }])).toBe('')
    expect(matchPatientIdInFilename(`left_${patientA}_${patientB}.png`, [{ patientId: patientA }, { patientId: patientB }])).toBe('')
  })
  it('does not label pending or in-progress items as completed', () => {
    expect(calculateBatchPercent({ total: 50, succeeded: 24, failed: 1, duplicate: 0 })).toBe(50)
    expect(calculateBatchPercent({ total: 100, succeeded: 0, failed: 0, duplicate: 0 })).toBe(0)
    expect(calculateBatchPercent({ total: 50, succeeded: 45, failed: 5, duplicate: 0 })).toBe(100)
  })
  it('includes rejected admission items only when the server supplies a declared denominator', () => {
    expect(calculateBatchPercent({ total: 49, succeeded: 25, failed: 0, duplicate: 0 }, 50, 1)).toBe(52)
    expect(calculateBatchPercent({ total: 0, succeeded: 0, failed: 0, duplicate: 0 }, 1, 0)).toBe(0)
  })
})
