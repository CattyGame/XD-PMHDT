import { useRef, useState, type PointerEvent } from 'react'
import styles from './doctor.module.css'

type Props = {
  originalUrl: string | null
  layers: Partial<Record<LayerKind, string>>
}

export const layerLabels = {
  overlay: 'Frangi · lớp phủ',
  mask: 'Frangi · mask',
  annotated: 'A/V · động mạch và tĩnh mạch',
  heatmap: 'Grad-CAM · ảnh cắt 224 × 224',
} as const
export type LayerKind = keyof typeof layerLabels

/** Chỉ hiển thị ảnh API cấp quyền; không dùng ảnh võng mạc mẫu trong quy trình thật. */
export default function InteractiveImageViewer({ originalUrl, layers }: Props) {
  const [zoom, setZoom] = useState(1)
  const [angle, setAngle] = useState(0)
  const [offset, setOffset] = useState({ x: 0, y: 0 })
  const [overlay, setOverlay] = useState(true)
  const [opacity, setOpacity] = useState(55)
  const [sideBySide, setSideBySide] = useState(false)
  const [selectedLayer, setSelectedLayer] = useState<LayerKind>()
  const pointer = useRef<{ id: number; x: number; y: number } | null>(null)
  const kinds = (Object.keys(layerLabels) as LayerKind[]).filter(kind => layers[kind])
  const kind = selectedLayer && layers[selectedLayer] ? selectedLayer : kinds[0]
  const activeUrl = kind ? layers[kind] : undefined
  const canCompare = Boolean(originalUrl && activeUrl)
  const isCrop = kind === 'heatmap'
  const separate = sideBySide || isCrop

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
  const vesselImage = kind && activeUrl ? <img src={activeUrl} alt={layerLabels[kind]} draggable={false}
    className={styles.retinaImage} referrerPolicy="no-referrer" /> : null

  return <section className={styles.viewer} aria-label="Khung xem ảnh tương tác">
    <div className={styles.sectionTitle}>
      <h2>Khung xem ảnh võng mạc</h2>
      <small>Ảnh riêng tư được cấp URL tạm thời từ API</small>
    </div>
    {kinds.length > 0 && <label className={styles.notesLabel}>Lớp ảnh
      <select value={kind} onChange={event => setSelectedLayer(event.target.value as LayerKind)}>
        {kinds.map(value => <option key={value} value={value}>{layerLabels[value]}</option>)}
      </select>
    </label>}
    <div className={styles.toolbar} aria-label="Công cụ ảnh">
      <button type="button" onClick={() => setZoom((old) => Math.min(4, +(old + 0.25).toFixed(2)))}
        disabled={!originalUrl || zoom >= 4} aria-label="Phóng to">＋</button>
      <button type="button" onClick={() => setZoom((old) => Math.max(0.5, +(old - 0.25).toFixed(2)))}
        disabled={!originalUrl || zoom <= 0.5} aria-label="Thu nhỏ">－</button>
      <span aria-label="Tỷ lệ phóng to">{Math.round(zoom * 100)}%</span>
      <button type="button" onClick={() => setAngle((old) => (old + 90) % 360)} disabled={!originalUrl}>Xoay 90°</button>
      <button type="button" onClick={reset} disabled={!originalUrl}>Đặt lại</button>
      <label className={styles.inlineControl}><input type="checkbox" checked={overlay && !isCrop} disabled={!canCompare || separate}
        onChange={(event) => setOverlay(event.target.checked)} /> Lớp phủ mạch máu</label>
      <label className={styles.inlineControl}>Độ trong suốt
        <input type="range" min="0" max="100" value={opacity} disabled={!canCompare || !overlay || separate}
          onChange={(event) => setOpacity(Number(event.target.value))} aria-label="Độ trong suốt lớp phủ" /> {opacity}%
      </label>
      <label className={styles.inlineControl}><input type="checkbox" checked={separate} disabled={!canCompare || isCrop}
        onChange={(event) => setSideBySide(event.target.checked)} /> So sánh song song</label>
    </div>
    {!originalUrl ? <p role="status" className={styles.placeholder}>Ảnh gốc chưa sẵn sàng hoặc chưa được cấp quyền xem.</p> :
      <div className={separate ? styles.compareGrid : styles.singleGrid}>
        <div>
          <p className={styles.imageLabel}>{separate ? 'Ảnh gốc' : 'Ảnh võng mạc'}</p>
          <div className={styles.viewport} onPointerDown={startDrag} onPointerMove={moveDrag}
            onPointerUp={endDrag} onPointerCancel={endDrag} aria-label="Kéo rê ảnh">
            <div className={styles.imageLayers} style={transformation}>
              {originalImage}
              {!separate && overlay && vesselImage && <div className={styles.overlayLayer}
                style={{ opacity: opacity / 100 }}>{vesselImage}</div>}
            </div>
          </div>
        </div>
        {separate && vesselImage && <div>
          <p className={styles.imageLabel}>{layerLabels[kind]}</p>
          <div className={styles.viewport} onPointerDown={startDrag} onPointerMove={moveDrag}
            onPointerUp={endDrag} onPointerCancel={endDrag} aria-label="Kéo rê ảnh mạch máu">
            <div className={styles.imageLayers} style={transformation}>{vesselImage}</div>
          </div>
        </div>}
      </div>}
    <p className={styles.hint}>Nhấn giữ chuột và kéo để di chuyển ảnh. Ảnh và kết luận AI chỉ dùng hỗ trợ bác sĩ, không thay thế chẩn đoán lâm sàng.</p>
    {isCrop && <p className={styles.hint}>Grad-CAM hiển thị trên ảnh cắt giữa sau khi thu cạnh ngắn về 256 px. Màu biểu thị vùng ảnh ảnh hưởng đến mô hình phân loại DR nhị phân.</p>}
    {!activeUrl && <p className={styles.hint}>Chưa có lớp ảnh để phủ hoặc so sánh.</p>}
  </section>
}
