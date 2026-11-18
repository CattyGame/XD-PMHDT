export const formatCount = (value: number | null) => value === null ? 'Chưa có dữ liệu' : value.toLocaleString('vi-VN')
export const formatDate = (value: string) => new Date(value).toLocaleString('vi-VN')
export const formatRange = (range: { from: string; to: string }) => formatDate(range.from) + ' – ' + formatDate(range.to)

export function defaultRange() {
  const now = new Date()
  return { from: new Date(now.valueOf() - 30 * 86400000).toISOString(), to: now.toISOString() }
}
