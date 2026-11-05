import { useCallback, useEffect, useMemo, useSyncExternalStore } from 'react'
import { useBackendSession } from '../auth/backendSession'
import BackendSessionNotice from '../auth/BackendSessionNotice'
import type { BackendContext } from '../api/backendContext'
import { canUploadPersonal, uploadErrorMessage, type EyeSide } from '../api/analysisUpload'
import { getPersonalUploadWorkspace } from '../api/personalUploadWorkspace'

function ImagePreview({ file }: { file: File }) {
  const attach = useCallback((image: HTMLImageElement | null) => {
    if (!image) return
    const url = URL.createObjectURL(file)
    image.src = url
    return () => URL.revokeObjectURL(url)
  }, [file])
  return <div style={{ marginBottom: 16 }}><p>{file.name}</p>
    <img ref={attach} alt={`Xem trước ${file.name}`}
      style={{ width: 200, height: 200, objectFit: 'contain', border: '1px solid #ddd', borderRadius: 8 }} />
  </div>
}

function PersonalUploadForm({ context }: { context: BackendContext }) {
  const workspace = useMemo(() => getPersonalUploadWorkspace(context), [context])
  const state = useSyncExternalStore(workspace.subscribe, workspace.getSnapshot)
  useEffect(() => {
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
  const locked = !!state.attempt || state.phase === 'validating'
  const sending = state.phase === 'sending'
  const canSend = state.phase === 'editing' || (state.phase === 'error' && state.error?.canRetry)
  const receipt = state.receipt

  return (
    <main style={{ maxWidth: 700, margin: '40px auto', padding: 24 }}>
      <h1>AURA — Upload ảnh võng mạc</h1>
      <p>Tải ảnh đáy mắt cho hồ sơ cá nhân đã được AURA xác nhận.</p>
      <p>Hồ sơ đang sử dụng: <strong>{context.displayName}</strong></p>
      <form onSubmit={(event) => { event.preventDefault(); void workspace.send() }}>
        <div style={{ marginBottom: 20 }}>
          <label htmlFor="eyeSide">Mắt được chụp</label>
          <select id="eyeSide" value={state.eyeSide} disabled={locked}
            onChange={(event) => workspace.setEyeSide(event.target.value as EyeSide)}
            style={{ display: 'block', width: '100%', padding: 10 }}>
            <option value="left">Mắt trái</option>
            <option value="right">Mắt phải</option>
            <option value="unknown">Chưa xác định</option>
          </select>
        </div>
        <div style={{ marginBottom: 20 }}>
          <label htmlFor="images">Chọn ảnh võng mạc</label>
          <input id="images" type="file" accept="image/jpeg,image/png" multiple disabled={locked}
            onChange={(event) => {
              const files = Array.from(event.currentTarget.files ?? [])
              event.currentTarget.value = ''
              void workspace.selectImages(files)
            }} style={{ display: 'block', marginTop: 10 }} />
          {state.images.length > 0 && <div style={{ marginTop: 16 }}>
            <p>Đã chọn {state.images.length} ảnh:</p>
            {state.images.map((image, index) => <ImagePreview key={index} file={image} />)}
          </div>}
        </div>
        <button type="submit" disabled={!canSend}>
          {sending ? 'Đang gửi ảnh...' : state.attempt ? 'Thử lại cùng lượt gửi' : 'Gửi ảnh phân tích'}
        </button>
      </form>
      {state.phase === 'validating' && <p role="status">Đang kiểm tra ảnh đã chọn...</p>}
      {state.validation && <p role="alert">{state.validation}</p>}
      {state.attempt && <p>Mã yêu cầu upload: <code>{state.attempt.key}</code></p>}
      {state.error && <div role="alert">
        <p>{uploadErrorMessage(state.error)}</p>
        {state.error.correlationId && <p>Mã đối chiếu: {state.error.correlationId}</p>}
      </div>}
      {state.uncertain && <p role="status">
        Kết quả lượt gửi trước chưa rõ. Giữ nguyên ảnh và mắt đã chọn để thử lại cùng lượt gửi.
        Nếu tải lại trang hoặc đổi phiên đăng nhập, hãy lưu mã yêu cầu và đối chiếu trước khi tạo lượt mới.
      </p>}
      {state.attempt && workspace.canReset() && <button type="button" onClick={workspace.reset}>
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
