import { useCallback, useEffect, useRef, useState } from 'react'
import { bulkFileKey, matchPatientIdInFilename, validateBulkFiles, type InvalidBulkImage } from '../api/bulkUploadUi'
import { captureBatchPreviews } from '../api/batchLocalPreviews'
import { validBatchId } from '../api/batchProgress'
import './BulkUploadPage.css'
import { Link } from 'react-router-dom'
import { useBackendSession } from '../auth/backendSession'
import BackendSessionNotice from '../auth/BackendSessionNotice'
import type { BackendContext } from '../api/backendContext'
import { AnalysisResultError, analysisErrorMessage } from '../api/analysisResult'
import { getUploadClinics, getUploadPatients, type UploadClinic, type UploadPatient } from '../api/clinicUpload'
import { createClinicItemUploadAttempt, sendItemUploadAttempt, resumeItemUpload, loadUploadInventory, type ItemTransferState, type UploadInventory } from '../api/itemUpload'
import { UploadRequestError, uploadErrorMessage, type UploadAttempt, type UploadReceipt, type EyeSide } from '../api/analysisUpload'
import ItemUploadProgress from '../components/ItemUploadProgress'

type Selection = { file: File; patientId: string; eyeSide: EyeSide }

/** Giai phong URL khi anh bi xoa hoac component unmount. */
function FilePreview({ file }: { file: File }) {
  const attach = useCallback((image: HTMLImageElement | null) => {
    if (!image || typeof URL.createObjectURL !== 'function') return

    const url = URL.createObjectURL(file)
    image.src = url

    return () => URL.revokeObjectURL(url)
  }, [file])

  return (
    <img
      ref={attach}
      className="bulk-image-preview"
      loading="lazy"
      alt={`Xem trước ${file.name}`}
    />
  )
}
function ClinicUploadForm({ context }: { context: BackendContext }) {
  const [clinics, setClinics] = useState<readonly UploadClinic[]>([]), [clinicId, setClinicId] = useState('')
  const [patients, setPatients] = useState<readonly UploadPatient[]>([]), [search, setSearch] = useState('')
  const [patientNames, setPatientNames] = useState<Record<string, string>>({})
  const [selections, setSelections] = useState<Selection[]>([]), [attempt, setAttempt] = useState<UploadAttempt>()
  const [invalidFiles, setInvalidFiles] = useState<InvalidBulkImage[]>([])
  const [inventory, setInventory] = useState<UploadInventory>(), [receipt, setReceipt] = useState<UploadReceipt>()
  const [busy, setBusy] = useState(false), [error, setError] = useState(''), [uncertain, setUncertain] = useState(false)
  const [restoring, setRestoring] = useState(() => new URLSearchParams(window.location.search).has('uploadBatchId'))
  const [expired, setExpired] = useState(false)
  const [transfers, setTransfers] = useState<Record<string, ItemTransferState>>({})
  const [blocked, setBlocked] = useState(false)
  const active = useRef<AbortController | undefined>(undefined)
  const savedBatch = new URLSearchParams(window.location.search).get('uploadBatchId')
  const savedClinic = new URLSearchParams(window.location.search).get('clinicContextId')
  useEffect(() => {
    const controller = new AbortController()
    void getUploadClinics(context, controller.signal).then(items => {
      if (controller.signal.aborted) return
      setClinics(items)
      if (savedClinic && !items.some(x => x.clinicId === savedClinic)) throw new AnalysisResultError(403, 'ERR_FORBIDDEN')
      setClinicId(savedClinic || items[0]?.clinicId || '')
    }).catch(cause => { if (!controller.signal.aborted) {
      setRestoring(false)
      setError(analysisErrorMessage(cause instanceof AnalysisResultError ? cause : new AnalysisResultError(0, 'ERR_NETWORK')))
    } })
    return () => controller.abort()
  }, [context, savedClinic])
  useEffect(() => () => active.current?.abort(), [context])
  useEffect(() => {
    if (!clinicId) return
    const controller = new AbortController()
    const timer = setTimeout(() => {
      void getUploadPatients(context, clinicId, search, controller.signal).then(items => {
        if (!controller.signal.aborted) {
          setPatients(items)
          setPatientNames(previous => ({ ...previous, ...Object.fromEntries(items.map(x => [x.patientId, x.fullName])) }))
          // Tự nhận diện UUID trong tên file nếu bệnh nhân đã có trong danh sách hợp lệ.
          setSelections(previous => previous.map(x => x.patientId ? x : {
            ...x, patientId: matchPatientIdInFilename(x.file.name, items),
          }))
        }
      }).catch(cause => { if (!controller.signal.aborted) { setPatients([]); setPatientNames({}); setError(analysisErrorMessage(cause instanceof AnalysisResultError ? cause : new AnalysisResultError(0, 'ERR_NETWORK'))) } })
    }, 200)
    return () => { clearTimeout(timer); controller.abort() }
  }, [context, clinicId, search])
  useEffect(() => {
    if (attempt || receipt || blocked || !savedBatch || !clinicId || savedClinic !== clinicId) return
    const controller = new AbortController()
    void loadUploadInventory(context, savedBatch, { clinicContextId: clinicId, signal: controller.signal }).then(value => {
      if (!controller.signal.aborted) { setInventory(value); setRestoring(false) }
    }).catch(cause => { if (!controller.signal.aborted) {
      setRestoring(false)
      if (cause instanceof UploadRequestError) { setExpired(cause.code === 'ERR_UPLOAD_EXPIRED'); setError(uploadErrorMessage(cause)) }
      else setError(analysisErrorMessage(cause instanceof AnalysisResultError ? cause : new AnalysisResultError(0, 'ERR_NETWORK')))
    } })
    return () => controller.abort()
  }, [context, clinicId, savedBatch, savedClinic, attempt, receipt, blocked])

  const reconciling = inventory?.status === 'FINALIZING' || inventory?.items.some(item => item.uploadStatus === 'UNKNOWN')
  const locked = busy || restoring || expired || blocked || reconciling || !!attempt || !!receipt
  function choose(files: File[]) {
    if (locked) return
    const result = validateBulkFiles(files)
    setInvalidFiles([...result.rejected])
    if (!result.accepted.length) {
      setError(files.length === 0 ? 'Vui lòng chọn ảnh JPG/PNG.' : 'Không có ảnh hợp lệ để gửi. Hãy kiểm tra danh sách lỗi.')
      return
    }
    setError('')
    setSelections(result.accepted.map(file => ({
      file, patientId: matchPatientIdInFilename(file.name, patients), eyeSide: 'unknown',
    })))
  }
  async function send(retryItemId?: string) {
    if (active.current || blocked || receipt || !clinicId) return
    if (!inventory && (!selections.length || selections.some(x => !validBatchId(x.patientId)))) { setError('Nhập hoặc chọn mã bệnh nhân UUID hợp lệ cho từng ảnh trước khi gửi.'); return }
    const controller = new AbortController(); active.current = controller; setBusy(true); setError('')
    const onInventory = (value: UploadInventory) => {
      if (controller.signal.aborted) return
      setInventory(value)
      const url = new URL(window.location.href)
      if (url.searchParams.get('uploadBatchId') !== value.batchId || url.searchParams.get('clinicContextId') !== clinicId) {
        url.searchParams.set('uploadBatchId', value.batchId)
        url.searchParams.set('clinicContextId', clinicId)
        window.history.replaceState(window.history.state, '', url)
      }
    }
    try {
      const options = { clinicContextId: clinicId, signal: controller.signal, onInventory,
        onItemState: (id: string, state: ItemTransferState) => {
          if (!controller.signal.aborted) setTransfers(previous => ({ ...previous, [id]: state }))
        } }
      let result: UploadReceipt | undefined
      if (!attempt && inventory) {
        const files = selections.map(x => x.file)
        result = retryItemId
          ? await resumeItemUpload(context, inventory.batchId, files, { ...options, retryItemId })
          : await resumeItemUpload(context, inventory.batchId, files, options)
      }
      else {
        const current = attempt ?? createClinicItemUploadAttempt(context, clinicId, selections)
        setAttempt(current)
        result = retryItemId
          ? await sendItemUploadAttempt(current, { ...options, retryItemId })
          : await sendItemUploadAttempt(current, options)
      }
      if (!controller.signal.aborted && result) {
        // Xem trước tạm trong chính tab, không ghi ảnh bệnh nhân vào localStorage.
        void captureBatchPreviews(result.batchId, context.firebaseUid, selections.map(x => x.file))
        setReceipt(result); setUncertain(false); setSelections([]); setAttempt(undefined)
      }
    } catch (cause) {
      if (!controller.signal.aborted) {
        const failure = cause instanceof UploadRequestError ? cause : new UploadRequestError(0, 'ERR_NETWORK', 'uncertain', true)
        setError(uploadErrorMessage(failure)); setExpired(failure.code === 'ERR_UPLOAD_EXPIRED')
        if ([401, 403, 404].includes(failure.status)) {
          setBlocked(true); setSelections([]); setInventory(undefined); setTransfers({}); setAttempt(undefined)
          setPatients([]); setPatientNames({})
        }
        setUncertain(failure.disposition === 'uncertain' || failure.disposition === 'conflict')
      }
    } finally {
      if (active.current === controller) active.current = undefined
      if (!controller.signal.aborted) setBusy(false)
    }
  }
  return <main style={{ maxWidth: 1000, margin: '24px auto', padding: 20 }}>
    <nav><Link to="/">Trang chủ</Link> · <Link to="/batches">Theo dõi batch</Link></nav>
    <h1>Tải ảnh phòng khám</h1>
    <p>Chọn phòng khám và bệnh nhân tương ứng từng ảnh. Máy chủ kiểm tra quyền khi tiếp nhận và xử lý.</p>
    {error && <p role="alert">{error}</p>}
    {invalidFiles.length > 0 && <div role="alert" aria-label="Ảnh không hợp lệ">
      <strong>{invalidFiles.length} ảnh bị loại trước khi upload:</strong>
      <ul className="bulk-validation-list">{invalidFiles.map((item, index) =>
        <li key={`${item.name}:${index}`}><strong>{item.name}</strong>: {item.reason}</li>)}</ul>
      {selections.length > 0 && <p>Chỉ {selections.length} ảnh hợp lệ ở dưới sẽ được gửi khi bạn xác nhận.</p>}
    </div>}
    {expired && <button type="button" onClick={() => {
      const url = new URL(window.location.href); url.searchParams.delete('uploadBatchId')
      window.history.replaceState(window.history.state, '', url)
      setAttempt(undefined); setInventory(undefined); setSelections([]); setTransfers({}); setReceipt(undefined); setError(''); setExpired(false); setUncertain(false)
    }}>Tạo lượt tải mới</button>}
    <label htmlFor="upload-clinic">Phòng khám</label>
    <select id="upload-clinic" disabled={locked || !!inventory} value={clinicId} onChange={event => {
      setClinicId(event.target.value); setSelections([]); setInvalidFiles([]); setPatients([]); setPatientNames({}); setSearch(''); setError('')
    }}><option value="">Chọn phòng khám</option>{clinics.map(x => <option key={x.clinicId} value={x.clinicId}>{x.clinicName}</option>)}</select>
    {clinics.length === 0 && <p>Chưa có phòng khám đang hoạt động được cấp quyền cho tài khoản này.</p>}
    {clinicId && !blocked && <>
      <label htmlFor="upload-patient-search" style={{ display: 'block', marginTop: 16 }}>Tìm bệnh nhân theo tên</label>
      <input id="upload-patient-search" value={search} onChange={event => setSearch(event.target.value)} disabled={locked} />
      <div className="bulk-upload-drop" style={{ padding: 16, marginTop: 16 }}
        onDragOver={event => { if (!locked) event.preventDefault() }}
        onDrop={event => { event.preventDefault(); if (!locked) choose(Array.from(event.dataTransfer.files)) }}>
        <label htmlFor="clinic-images">Chọn ảnh phòng khám</label>
        <input id="clinic-images" type="file" accept="image/png,image/jpeg" multiple disabled={locked || restoring} onChange={event => {
          choose(Array.from(event.currentTarget.files ?? [])); event.currentTarget.value = ''
        }} />
        <p>Kéo thả tối đa 100 ảnh JPG/PNG, mỗi ảnh không quá 10 MiB. Ảnh lỗi bị loại tại trình duyệt.</p>
      </div>
      <div className="bulk-select-summary" aria-live="polite">
        <span>Ảnh hợp lệ đã chọn: <strong>{selections.length}/100</strong></span>
        <span>Đã gán bệnh nhân: <strong>{selections.filter(x => validBatchId(x.patientId)).length}/{selections.length}</strong></span>
      </div>
      {!receipt && (!inventory || !attempt) && selections.map((selection, index) => <fieldset key={bulkFileKey(selection.file)} disabled={locked} className="bulk-image-row" style={{ margin: '12px 0' }}>
        <legend>{selection.file.name}</legend>
        <FilePreview file={selection.file} />
        <span>{selection.file.size.toLocaleString('vi-VN')} byte</span>
        {!inventory && <span>{busy ? 'Đang chuẩn bị' : 'Đang chờ tải'}</span>}
        {!inventory && <><label>Mã bệnh nhân ảnh{index + 1}
          <input aria-label={`Mã bệnh nhân ảnh${index + 1}`} value={selection.patientId}
            placeholder="UUID bệnh nhân đã tồn tại" onChange={event =>
              setSelections(previous => previous.map((x, i) => i === index ? { ...x, patientId: event.target.value.trim() } : x))} />
        </label>
        <label>Bệnh nhân ảnh{index + 1}<select value={selection.patientId} onChange={event => setSelections(previous => previous.map((x, i) => i === index ? { ...x, patientId: event.target.value } : x))}>
          <option value="">Chọn bệnh nhân</option>{patients.map(patient => <option key={patient.patientId} value={patient.patientId}>{patient.fullName}</option>)}
          {selection.patientId && !patients.some(x => x.patientId === selection.patientId) && <option value={selection.patientId}>{patientNames[selection.patientId] || 'Bệnh nhân đã chọn'}</option>}
        </select></label>
        <label>Mắt ảnh{index + 1}<select value={selection.eyeSide} onChange={event => setSelections(previous => previous.map((x, i) => i === index ? { ...x, eyeSide: event.target.value as EyeSide } : x))}>
          <option value="left">OS – Mắt trái</option><option value="right">OD – Mắt phải</option><option value="unknown">Chưa xác định</option>
        </select></label></>}
        <button type="button" onClick={() => setSelections(previous => previous.filter(x => x.file !== selection.file))}>
          Xóa {selection.file.name}
        </button>
      </fieldset>)}
      {restoring && <p role="status">Đang khôi phục lượt tải trên máy chủ...</p>}
      {inventory && <ItemUploadProgress inventory={inventory} transfers={transfers} busy={busy}
        canRetry={!expired && !receipt && selections.length > 0} onRetry={id => void send(id)} />}
      {inventory && !attempt && !receipt && !reconciling && inventory.items.some(item => item.uploadStatus === 'DECLARED') &&
        <p>Chọn lại các tệp chưa tải. Nội dung được đối chiếu checksum trước khi tiếp tục.</p>}
      {!receipt && !expired && <button className="bulk-upload-submit" type="button" disabled={busy || restoring || (!inventory && selections.length === 0)}
        aria-label={reconciling ? 'Kiểm tra tiếp nhận' : inventory ? 'Tiếp tục lượt tải' : 'Gửi ảnh phòng khám'} onClick={() => void send()}>
        {busy ? 'Đang tải...' : reconciling ? 'Kiểm tra tiếp nhận' : inventory ? 'Tiếp tục lượt tải' : `Gửi ${selections.length} ảnh phòng khám`}
      </button>}
      {uncertain && <p role="status">Kết quả chưa rõ. Hệ thống sẽ đối chiếu máy chủ trước khi thử lại cùng lượt tải.</p>}
      {receipt && <section><h2>Đã tiếp nhận lượt tải phòng khám</h2><p>{receipt.items.length} ảnh, trạng thái riêng từng ảnh được lưu trên máy chủ.</p>
        <Link to={`/batches/${receipt.batchId}?clinicContextId=${clinicId}`}>Theo dõi tiến trình phòng khám</Link>
        <ul>{receipt.items.filter(x => x.processingStatus === 'REJECTED').map(x => <li key={x.analysisId}>{x.fileName}: {x.error?.message || 'Bị từ chối tiếp nhận'}</li>)}</ul>
      </section>}
    </>}
  </main>
}

export default function ClinicUploadPage() {
  const { state } = useBackendSession()
  return state.status === 'ready' ? <ClinicUploadForm key={`${state.context.firebaseUid}:${state.context.accountId}`} context={state.context} /> : <BackendSessionNotice />
}
