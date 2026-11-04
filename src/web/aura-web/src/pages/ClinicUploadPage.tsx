import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { useBackendSession } from '../auth/backendSession'
import BackendSessionNotice from '../auth/BackendSessionNotice'
import type { BackendContext } from '../api/backendContext'
import { AnalysisResultError, analysisErrorMessage } from '../api/analysisResult'
import { getUploadClinics, getUploadPatients, type UploadClinic, type UploadPatient } from '../api/clinicUpload'
import { createClinicItemUploadAttempt, sendItemUploadAttempt, resumeItemUpload, loadUploadInventory, type UploadInventory } from '../api/itemUpload'
import { UploadRequestError, uploadErrorMessage, type UploadAttempt, type UploadReceipt, type EyeSide } from '../api/analysisUpload'

type Selection = { file: File; patientId: string; eyeSide: EyeSide }
function ClinicUploadForm({ context }: { context: BackendContext }) {
  const [clinics, setClinics] = useState<readonly UploadClinic[]>([]), [clinicId, setClinicId] = useState('')
  const [patients, setPatients] = useState<readonly UploadPatient[]>([]), [search, setSearch] = useState('')
  const [patientNames, setPatientNames] = useState<Record<string, string>>({})
  const [selections, setSelections] = useState<Selection[]>([]), [attempt, setAttempt] = useState<UploadAttempt>()
  const [inventory, setInventory] = useState<UploadInventory>(), [receipt, setReceipt] = useState<UploadReceipt>()
  const [busy, setBusy] = useState(false), [error, setError] = useState(''), [uncertain, setUncertain] = useState(false)
  const [restoring, setRestoring] = useState(() => new URLSearchParams(window.location.search).has('uploadBatchId'))
  const [expired, setExpired] = useState(false)
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
    }).catch(cause => { if (!controller.signal.aborted) setError(analysisErrorMessage(cause instanceof AnalysisResultError ? cause : new AnalysisResultError(0, 'ERR_NETWORK'))) })
    return () => { controller.abort(); active.current?.abort() }
  }, [context, savedClinic])
  useEffect(() => {
    if (!clinicId) return
    const controller = new AbortController()
    const timer = setTimeout(() => {
      void getUploadPatients(context, clinicId, search, controller.signal).then(items => {
        if (!controller.signal.aborted) {
          setPatients(items)
          setPatientNames(previous => ({ ...previous, ...Object.fromEntries(items.map(x => [x.patientId, x.fullName])) }))
        }
      }).catch(cause => { if (!controller.signal.aborted) { setPatients([]); setPatientNames({}); setError(analysisErrorMessage(cause instanceof AnalysisResultError ? cause : new AnalysisResultError(0, 'ERR_NETWORK'))) } })
    }, 200)
    return () => { clearTimeout(timer); controller.abort() }
  }, [context, clinicId, search])
  useEffect(() => {
    if (attempt || !savedBatch || !clinicId || savedClinic !== clinicId) return
    const controller = new AbortController()
    void loadUploadInventory(context, savedBatch, { clinicContextId: clinicId, signal: controller.signal }).then(value => {
      if (!controller.signal.aborted) { setInventory(value); setRestoring(false) }
    }).catch(cause => { if (!controller.signal.aborted) {
      setRestoring(false)
      if (cause instanceof UploadRequestError) { setExpired(cause.code === 'ERR_UPLOAD_EXPIRED'); setError(uploadErrorMessage(cause)) }
      else setError(analysisErrorMessage(cause instanceof AnalysisResultError ? cause : new AnalysisResultError(0, 'ERR_NETWORK')))
    } })
    return () => controller.abort()
  }, [context, clinicId, savedBatch, savedClinic, attempt])

  const locked = busy || restoring || expired || !!attempt || !!receipt
  function choose(files: File[]) {
    if (locked) return
    if (files.length < 1 || files.length > 100 || files.some(file => !file.size || file.size > 10 * 1024 * 1024 ||
      (!['image/png', 'image/jpeg'].includes(file.type) && !/\.(jpe?g|png)$/i.test(file.name)))) {
      setError('Chọn tối đa 100 ảnh JPG/PNG, mỗi ảnh có dữ liệu và không quá 10 MiB.'); return
    }
    setError(''); setSelections(files.map(file => ({ file, patientId: '', eyeSide: 'unknown' })))
  }
  async function send() {
    if (busy || receipt || !clinicId) return
    if (!inventory && (!selections.length || selections.some(x => !x.patientId))) { setError('Chọn bệnh nhân cho từng ảnh trước khi gửi.'); return }
    const controller = new AbortController(); active.current = controller; setBusy(true); setError('')
    const onInventory = (value: UploadInventory) => {
      if (controller.signal.aborted) return
      setInventory(value)
      const url = new URL(window.location.href)
      url.searchParams.set('uploadBatchId', value.batchId); url.searchParams.set('clinicContextId', clinicId)
      window.history.replaceState(window.history.state, '', url)
    }
    try {
      const options = { clinicContextId: clinicId, signal: controller.signal, onInventory }
      let result: UploadReceipt
      if (!attempt && inventory) result = await resumeItemUpload(context, inventory.batchId, selections.map(x => x.file), options)
      else {
        const current = attempt ?? createClinicItemUploadAttempt(context, clinicId, selections)
        setAttempt(current)
        result = await sendItemUploadAttempt(current, options)
      }
      if (!controller.signal.aborted) { setReceipt(result); setUncertain(false) }
    } catch (cause) {
      if (!controller.signal.aborted) {
        const failure = cause instanceof UploadRequestError ? cause : new UploadRequestError(0, 'ERR_NETWORK', 'uncertain', true)
        setError(uploadErrorMessage(failure)); setExpired(failure.code === 'ERR_UPLOAD_EXPIRED')
        setUncertain(failure.disposition === 'uncertain' || failure.disposition === 'conflict')
      }
    } finally { if (!controller.signal.aborted) setBusy(false) }
  }
  return <main style={{ maxWidth: 1000, margin: '24px auto', padding: 20 }}>
    <nav><Link to="/">Trang chủ</Link> · <Link to="/batches">Theo dõi batch</Link></nav>
    <h1>Tải ảnh phòng khám</h1>
    <p>Chọn phòng khám và bệnh nhân tương ứng từng ảnh. Máy chủ kiểm tra quyền khi tiếp nhận và xử lý.</p>
    {error && <p role="alert">{error}</p>}
    {expired && <button type="button" onClick={() => {
      const url = new URL(window.location.href); url.searchParams.delete('uploadBatchId')
      window.history.replaceState(window.history.state, '', url)
      setAttempt(undefined); setInventory(undefined); setSelections([]); setReceipt(undefined); setError(''); setExpired(false); setUncertain(false)
    }}>Tạo lượt tải mới</button>}
    <label htmlFor="upload-clinic">Phòng khám</label>
    <select id="upload-clinic" disabled={locked || !!inventory} value={clinicId} onChange={event => {
      setClinicId(event.target.value); setSelections([]); setPatients([]); setPatientNames({}); setSearch(''); setError('')
    }}><option value="">Chọn phòng khám</option>{clinics.map(x => <option key={x.clinicId} value={x.clinicId}>{x.clinicName}</option>)}</select>
    {clinics.length === 0 && <p>Chưa có phòng khám đang hoạt động được cấp quyền cho tài khoản này.</p>}
    {clinicId && <>
      <label htmlFor="upload-patient-search" style={{ display: 'block', marginTop: 16 }}>Tìm bệnh nhân theo tên</label>
      <input id="upload-patient-search" value={search} onChange={event => setSearch(event.target.value)} disabled={locked} />
      <div style={{ border: '1px dashed #888', padding: 16, marginTop: 16 }} onDragOver={event => event.preventDefault()}
        onDrop={event => { event.preventDefault(); choose(Array.from(event.dataTransfer.files)) }}>
        <label htmlFor="clinic-images">Chọn ảnh phòng khám</label>
        <input id="clinic-images" type="file" accept="image/png,image/jpeg" multiple disabled={locked || restoring} onChange={event => {
          choose(Array.from(event.currentTarget.files ?? [])); event.currentTarget.value = ''
        }} />
        <p>Kéo thả tối đa 100 ảnh, 10 MiB mỗi ảnh.</p>
      </div>
      {!inventory && selections.map((selection, index) => <fieldset key={index} disabled={locked} style={{ margin: '12px 0', display: 'flex', flexWrap: 'wrap', gap: 12 }}>
        <legend>{selection.file.name}</legend>
        <label>Bệnh nhân ảnh{index + 1}<select value={selection.patientId} onChange={event => setSelections(previous => previous.map((x, i) => i === index ? { ...x, patientId: event.target.value } : x))}>
          <option value="">Chọn bệnh nhân</option>{patients.map(patient => <option key={patient.patientId} value={patient.patientId}>{patient.fullName}</option>)}
          {selection.patientId && !patients.some(x => x.patientId === selection.patientId) && <option value={selection.patientId}>{patientNames[selection.patientId] || 'Bệnh nhân đã chọn'}</option>}
        </select></label>
        <label>Mắt ảnh{index + 1}<select value={selection.eyeSide} onChange={event => setSelections(previous => previous.map((x, i) => i === index ? { ...x, eyeSide: event.target.value as EyeSide } : x))}>
          <option value="left">Trái</option><option value="right">Phải</option><option value="unknown">Chưa xác định</option>
        </select></label>
      </fieldset>)}
      {restoring && <p role="status">Đang khôi phục lượt tải trên máy chủ...</p>}
      {inventory && <section aria-label="Tiến độ tải phòng khám">
        <p>Đã lưu: {inventory.items.filter(x => x.uploadStatus !== 'DECLARED').length}/{inventory.items.length} ảnh.</p>
        {!attempt && <p>Chọn lại các tệp chưa tải. Nội dung được đối chiếu checksum trước khi tiếp tục.</p>}
      </section>}
      {!receipt && !expired && <button type="button" disabled={busy || restoring} onClick={() => void send()}>{busy ? 'Đang tải...' : inventory ? 'Tiếp tục lượt tải' : 'Gửi ảnh phòng khám'}</button>}
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
