import { uploadErrorMessage } from '../api/analysisUpload'
import type { ItemTransferState, UploadInventory } from '../api/itemUpload'

export default function ItemUploadProgress({ inventory, transfers, busy, canRetry, onRetry }: {
  inventory: UploadInventory
  transfers: Readonly<Record<string, ItemTransferState>>
  busy: boolean
  canRetry: boolean
  onRetry: (itemId: string) => void
}) {
  const { items } = inventory
  const uploaded = items.filter(item => ['STAGED', 'ACCEPTED', 'UNKNOWN'].includes(item.uploadStatus)).length
  const canSend = canRetry && inventory.status !== 'FINALIZING' &&
    !items.some(item => item.uploadStatus === 'UNKNOWN')
  return <section aria-label="Tiến độ tiếp nhận ảnh">
    <p>Đã tải: {uploaded}/{items.length} ảnh</p>
    <progress className="bulk-upload-progress" aria-label="Tiến độ lưu ảnh lên máy chủ" max={items.length} value={uploaded} />
    <ul>
      {items.map(item => {
        const transfer = item.uploadStatus === 'DECLARED' ? transfers[item.itemId] : undefined
        const uploading = transfer?.phase === 'uploading'
        const failure = transfer?.phase === 'failed' ? transfer.error : undefined
        const saved = ['STAGED', 'ACCEPTED', 'UNKNOWN'].includes(item.uploadStatus)
        const label = saved ? 'Đã tải' : item.uploadStatus === 'REJECTED' || failure ? 'Lỗi tải' : uploading ? 'Đang tải' : 'Đang chờ tải'
        return <li key={item.itemId}>
          <strong>{item.fileName}</strong> · {label}
          {item.uploadStatus === 'UNKNOWN' && ' · Đang đối soát tiếp nhận'}
          <progress aria-label={`Tiến độ ${item.fileName}`} max={1} value={uploading ? undefined : saved ? 1 : 0} />
          {item.error?.message && <span> {item.error.message}</span>}
          {failure && <span> {uploadErrorMessage(failure)}</span>}
          {canSend && failure?.canRetry && <button type="button" disabled={busy} onClick={() => onRetry(item.itemId)}>
            Thử lại {item.fileName}
          </button>}
        </li>
      })}
    </ul>
  </section>
}
