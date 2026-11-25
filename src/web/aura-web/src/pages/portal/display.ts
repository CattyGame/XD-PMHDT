export const formatCount = (value: number | null) => value === null ? 'Chưa có dữ liệu' : value.toLocaleString('vi-VN')
export const formatDate = (value: string) => new Date(value).toLocaleString('vi-VN')
export const formatRange = (range: { from: string; to: string }) => formatDate(range.from) + ' – ' + formatDate(range.to)

export function defaultRange() {
  const from = new Date(), to = new Date()
  from.setHours(0, 0, 0, 0)
  from.setDate(from.getDate() - 30)
  to.setHours(24, 0, 0, 0)
  return { from: from.toISOString(), to: to.toISOString() }
}
