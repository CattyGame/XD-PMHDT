import { useCallback, useEffect, useMemo, useState, useSyncExternalStore } from 'react'
import { useBackendSession } from '../auth/backendSession'
import BackendSessionNotice from '../auth/BackendSessionNotice'
import type { BackendContext } from '../api/backendContext'
import { canUploadPersonal, uploadErrorMessage, type EyeSide } from '../api/analysisUpload'
import { getPersonalUploadWorkspace } from '../api/personalUploadWorkspace'
import { bulkFileKey } from '../api/bulkUploadUi'
import ItemUploadProgress from '../components/ItemUploadProgress'

function ImagePreview({ file }: { file: File }) {
  const [loadFailed, setLoadFailed] = useState(false)
  const attach = useCallback((image: HTMLImageElement | null) => {
    if (!image) return
    const url = URL.createObjectURL(file)
    image.src = url
    return () => URL.revokeObjectURL(url)
  }, [file])
  return <div style={{ marginBottom: 16 }}>
    <p>{file.name} {loadFailed ? '(Tệp hỏng cấu trúc)' : ''}</p>
    {!loadFailed ? (
      <img ref={attach} loading="lazy" alt={`Xem trước ${file.name}`} onError={() => setLoadFailed(true)}
        style={{ width: 200, height: 200, objectFit: 'contain', border: '1px solid #ddd', borderRadius: 8 }} />
    ) : (
      <div style={{ width: 200, height: 100, display: 'flex', alignItems: 'center', justifyContent: 'center', background: '#fff3cd', color: '#856404', border: '1px solid #ffeeba', borderRadius: 8, padding: 8, fontSize: 13, textAlign: 'center' }}>
        Không thể xem trước ảnh này. Máy chủ sẽ kiểm tra và trả lý do nếu tệp không hợp lệ.
      </div>
    )}
  </div>
}

function PersonalUploadForm({ context }: { context: BackendContext }) {
  const workspace = useMemo(() => getPersonalUploadWorkspace(context), [context])
  const state = useSyncExternalStore(workspace.subscribe, workspace.getSnapshot)
  useEffect(() => {
    void workspace.restoreFromUrl()
    function beforeUnload(event: BeforeUnloadEvent) {
      const current = workspace.getSnapshot()
      if (current.attempt && (current.phase === 'sending' || current.uncertain)) {
        event.preventDefault()
        event.returnValue = ''
      }
    }
    window.addEventListener('beforeunload', beforeUnload)
    return () => { window.removeEventListener('beforeunload', beforeUnload); workspace.release() }
  }, [workspace])
  const locked = !!state.attempt || !!state.receipt || state.phase === 'validating' || state.phase === 'restoring'
  const sending = state.phase === 'sending'
  const canSend = state.phase === 'editing' || (state.phase === 'error' && state.error?.canRetry === true)
  const receipt = state.receipt
  const reconciling = state.inventory?.status === 'FINALIZING' || state.inventory?.items.some(item => item.uploadStatus === 'UNKNOWN')

  return (
    <main style={{ maxWidth: 700, margin: '40px auto', padding: 24 }}>
      <h1>AURA — Upload ảnh võng mạc</h1>
      <p>Tải ảnh đáy mắt cho hồ sơ cá nhân đã được AURA xác nhận.</p>
      <p>Hồ sơ đang sử dụng: <strong>{context.displayName}</strong></p>
      <form onSubmit={(event) => { event.preventDefault(); void workspace.send() }}>
        <div style={{ marginBottom: 20 }}>
          <label htmlFor="eyeSide">Mắt được chụp</label>
          <select id="eyeSide" value={state.eyeSide} disabled={locked || !!state.resumeBatchId}
            onChange={(event) => workspace.setEyeSide(event.target.value as EyeSide)}
            style={{ display: 'block', width: '100%', padding: 10 }}>
            <option value="left">Mắt trái</option>
            <option value="right">Mắt phải</option>
            <option value="unknown">Chưa xác định</option>
          </select>
        </div>
        <div style={{ marginBottom: 20, padding: 12, border: '1px dashed #888' }}
          onDragOver={event => { if (!locked) event.preventDefault() }}
          onDrop={event => {
            event.preventDefault()
            if (!locked) void workspace.selectImages(Array.from(event.dataTransfer.files))
          }}>
          <label htmlFor="images">Chọn ảnh võng mạc</label>
          <p>Kéo thả ảnh JPG/PNG vào đây hoặc chọn tệp. Tối đa 100 ảnh, 10 MiB mỗi ảnh.</p>
          <input id="images" type="file" accept="image/jpeg,image/png" multiple disabled={locked}
            onChange={(event) => {
              const files = Array.from(event.currentTarget.files ?? [])
              event.currentTarget.value = ''
              void workspace.selectImages(files)
            }} style={{ display: 'block', marginTop: 10 }} />
          {state.images.length > 0 && <div style={{ marginTop: 16 }}>
            <p>Đã chọn {state.images.length} ảnh:</p>
            {state.images.map(image => <div key={bulkFileKey(image)}>
              <ImagePreview file={image} />
              <p>{image.size} byte</p>
              {!state.resumeBatchId && <label>Mắt của {image.name}
                <select aria-label={`Mắt của ${image.name}`} disabled={locked} value={state.eyesByFile[bulkFileKey(image)] ?? state.eyeSide}
                  onChange={event => workspace.setImageEye(image, event.target.value as EyeSide)}>
                  <option value="left">OS – Mắt trái</option>
                  <option value="right">OD – Mắt phải</option>
                  <option value="unknown">Chưa xác định</option>
                </select>
              </label>}
              <button type="button" disabled={locked} onClick={() => workspace.removeImage(image)}>Xóa {image.name}</button>
            </div>)}
          </div>}
        </div>
        <button type="submit" disabled={!canSend}>
          {receipt ? 'Đã tiếp nhận' : sending ? 'Đang gửi ảnh...' : reconciling ? 'Kiểm tra tiếp nhận' : state.attempt ? 'Thử lại cùng lượt gửi' : state.resumeBatchId ? 'Tiếp tục lượt tải đã lưu' : 'Gửi ảnh phân tích'}
        </button>
      </form>
      {state.phase === 'validating' && <p role="status">Đang kiểm tra ảnh đã chọn...</p>}
      {state.phase === 'restoring' && <p role="status">Đang khôi phục lượt tải từ máy chủ...</p>}
      {state.inventory && <ItemUploadProgress inventory={state.inventory} transfers={state.transfers} busy={sending}
        canRetry={workspace.canRetryItems() && state.images.length > 0} onRetry={id => void workspace.send(id)} />}
      {state.inventory?.items.some(item => item.uploadStatus === 'DECLARED') && !state.attempt &&
        <p>Chọn lại những ảnh chưa tải. Hệ thống đối chiếu nội dung bằng checksum.</p>}
      {state.validation && <p role="alert">{state.validation}</p>}
      {state.attempt && <p>Mã yêu cầu upload: <code>{state.attempt.key}</code></p>}
      {state.error && <div role="alert">
        <p>{uploadErrorMessage(state.error)}</p>
        {state.error.correlationId && <p>Mã đối chiếu: {state.error.correlationId}</p>}
      </div>}
      {state.uncertain && <p role="status">
        Kết quả lượt gửi trước chưa rõ. Giữ nguyên ảnh và mắt đã chọn để thử lại cùng lượt gửi.
        Khi tải lại trang, hệ thống đối chiếu lượt tải đã lưu trên máy chủ trước khi gửi lại ảnh.
      </p>}
      {(state.attempt || state.resumeBatchId || receipt) && workspace.canReset() && <button type="button" onClick={workspace.reset}>
        {receipt ? 'Tạo lượt tải ảnh mới' : 'Chọn lại ảnh sau khi bị từ chối'}
      </button>}
      {receipt && <section style={{ marginTop: 24 }}>
        <h2>Thông tin tiếp nhận</h2>
        <p role="status">Backend đã tiếp nhận lượt gửi; chưa phải kết quả phân tích AI.</p>
        {receipt.replayed && <p>Đây là phản hồi phát lại của cùng lượt gửi.</p>}
        <p><strong>Batch ID:</strong> {receipt.batchId}</p>
        <p><strong>Số ảnh:</strong> {receipt.items.length}</p>
        <p><strong>Thời gian:</strong> {receipt.createdAt}</p>
        <p><strong>Status URL:</strong> {receipt.statusUrl}</p>
        <p><a href={`/batches/${receipt.batchId}`}>Theo dõi tiến trình batch (GET, không gửi lại ảnh)</a></p>
        <ul>{receipt.items.map((item) => <li key={item.analysisId}>
          {item.fileName}: {item.processingStatus === 'DUPLICATE'
            ? `Ảnh trùng, tham chiếu phân tích ${item.existingAnalysisId}. Không tạo kết quả AI mới.`
            : item.processingStatus === 'QUARANTINE'
            ? 'Dữ liệu ảnh không hợp lệ (hỏng cấu trúc). Đã cách ly sang nhánh Quarantine trong < 1s (Không ghi rác vào DB).'
            : item.processingStatus === 'RETRY'
            ? 'Gặp sự cố tạm thời, hệ thống đang tự động xếp hàng thử lại (Bounded Retry).'
            : item.processingStatus === 'REJECTED'
            ? `Đã từ chối: ${item.error?.message ?? 'Ảnh không đủ điều kiện tiếp nhận.'}`
            : 'Đã xếp hàng, đang chờ xử lý.'}
        </li>)}</ul>
      </section>}
    </main>
  )
}

export default function UploadPage() {
  const { state } = useBackendSession()
  if (state.status !== 'ready') return state.status === 'anonymous'
    ? <p role="alert">Cần đăng nhập và xác nhận hồ sơ trước khi tải ảnh.</p>
    : <BackendSessionNotice />
  if (!canUploadPersonal(state.context)) return <p role="alert">Chức năng này dành cho hồ sơ cá nhân có vai trò bệnh nhân.</p>
  return <PersonalUploadForm context={state.context} />
}
