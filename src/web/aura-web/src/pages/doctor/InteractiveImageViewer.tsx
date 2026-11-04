import { useRef, useState, type PointerEvent } from 'react'
import styles from './doctor.module.css'

type Props = {
  originalUrl: string | null
  vesselUrl: string | null
  vesselKind: 'mask' | 'overlay' | null
}

/** Chỉ hiển thị ảnh API cấp quyền; không dùng ảnh võng mạc mẫu trong quy trình thật. */
export default function InteractiveImageViewer({ originalUrl, vesselUrl, vesselKind }: Props) {
  const [zoom, setZoom] = useState(1)
  const [angle, setAngle] = useState(0)
  const [offset, setOffset] = useState({ x: 0, y: 0 })
  const [overlay, setOverlay] = useState(true)
  const [opacity, setOpacity] = useState(55)
  const [sideBySide, setSideBySide] = useState(false)
  const pointer = useRef<{ id: number; x: number; y: number } | null>(null)
  const canCompare = Boolean(originalUrl && vesselUrl)

  function startDrag(event: PointerEvent<HTMLDivElement>) {
    if (event.button !== 0 || !originalUrl) return
    pointer.current = { id: event.pointerId, x: event.clientX, y: event.clientY }
    event.currentTarget.setPointerCapture?.(event.pointerId)
  }
  function moveDrag(event: PointerEvent<HTMLDivElement>) {
    const start = pointer.current
    if (!start || start.id !== event.pointerId) return
    setOffset((old) => ({ x: old.x + event.clientX - start.x, y: old.y + event.clientY - start.y }))
    pointer.current = { id: start.id, x: event.clientX, y: event.clientY }
  }
  function endDrag(event: PointerEvent<HTMLDivElement>) {
    if (pointer.current?.id === event.pointerId) pointer.current = null
  }
  function reset() { setZoom(1); setAngle(0); setOffset({ x: 0, y: 0 }) }

  const transformation = { transform: `translate(${offset.x}px, ${offset.y}px) scale(${zoom}) rotate(${angle}deg)` }
  const originalImage = <img src={originalUrl ?? undefined} alt="Ảnh võng mạc gốc" draggable={false}
    className={styles.retinaImage} referrerPolicy="no-referrer" />
  const vesselImage = vesselUrl ? <img src={vesselUrl} alt="Ảnh mạch máu võng mạc" draggable={false}
    className={styles.retinaImage} referrerPolicy="no-referrer" /> : null

  return <section className={styles.viewer} aria-label="Khung xem ảnh tương tác">
    <div className={styles.sectionTitle}>
      <h2>Khung xem ảnh võng mạc</h2>
      <small>Ảnh riêng tư được cấp URL tạm thời từ API</small>
    </div>
    <div className={styles.toolbar} aria-label="Công cụ ảnh">
      <button type="button" onClick={() => setZoom((old) => Math.min(4, +(old + 0.25).toFixed(2)))}
        disabled={!originalUrl || zoom >= 4} aria-label="Phóng to">＋</button>
      <button type="button" onClick={() => setZoom((old) => Math.max(0.5, +(old - 0.25).toFixed(2)))}
        disabled={!originalUrl || zoom <= 0.5} aria-label="Thu nhỏ">－</button>
      <span aria-label="Tỷ lệ phóng to">{Math.round(zoom * 100)}%</span>
      <button type="button" onClick={() => setAngle((old) => (old + 90) % 360)} disabled={!originalUrl}>Xoay 90°</button>
      <button type="button" onClick={reset} disabled={!originalUrl}>Đặt lại</button>
      <label className={styles.inlineControl}><input type="checkbox" checked={overlay} disabled={!canCompare || sideBySide}
        onChange={(event) => setOverlay(event.target.checked)} /> Lớp phủ mạch máu</label>
      <label className={styles.inlineControl}>Độ trong suốt
        <input type="range" min="0" max="100" value={opacity} disabled={!canCompare || !overlay || sideBySide}
          onChange={(event) => setOpacity(Number(event.target.value))} aria-label="Độ trong suốt lớp phủ" /> {opacity}%
      </label>
      <label className={styles.inlineControl}><input type="checkbox" checked={sideBySide} disabled={!canCompare}
        onChange={(event) => setSideBySide(event.target.checked)} /> So sánh song song</label>
    </div>
    {!originalUrl ? <p role="status" className={styles.placeholder}>Ảnh gốc chưa sẵn sàng hoặc chưa được cấp quyền xem.</p> :
      <div className={sideBySide ? styles.compareGrid : styles.singleGrid}>
        <div>
          <p className={styles.imageLabel}>{sideBySide ? 'Ảnh gốc' : 'Ảnh võng mạc'}</p>
          <div className={styles.viewport} onPointerDown={startDrag} onPointerMove={moveDrag}
            onPointerUp={endDrag} onPointerCancel={endDrag} aria-label="Kéo rê ảnh">
            <div className={styles.imageLayers} style={transformation}>
              {originalImage}
              {!sideBySide && overlay && vesselImage && <div className={styles.overlayLayer}
                style={{ opacity: opacity / 100 }}>{vesselImage}</div>}
            </div>
          </div>
        </div>
        {sideBySide && vesselImage && <div>
          <p className={styles.imageLabel}>Ảnh mạch máu ({vesselKind === 'overlay' ? 'overlay' : 'mask'})</p>
          <div className={styles.viewport} onPointerDown={startDrag} onPointerMove={moveDrag}
            onPointerUp={endDrag} onPointerCancel={endDrag} aria-label="Kéo rê ảnh mạch máu">
            <div className={styles.imageLayers} style={transformation}>{vesselImage}</div>
          </div>
        </div>}
      </div>}
    <p className={styles.hint}>Nhấn giữ chuột và kéo để di chuyển ảnh. Ảnh và kết luận AI chỉ dùng hỗ trợ bác sĩ, không thay thế chẩn đoán lâm sàng.</p>
    {!vesselUrl && <p className={styles.hint}>API chưa trả mask/overlay mạch máu. Công cụ lớp phủ và so sánh tạm thời bị khóa.</p>}
  </section>
}
