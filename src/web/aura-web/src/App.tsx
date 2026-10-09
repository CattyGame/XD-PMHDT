import { useMemo, useState } from 'react'
import './App.css'

type PingResponse = {
  service: string
  message: string
  timestamp: string
}

type Patient = {
  id: string
  name: string
  clinic: string
  date: string
  status: 'Hoàn thành' | 'Đang xử lý' | 'Cần xem xét'
  risk: 'Thấp' | 'Trung bình' | 'Cao'
}

const initialPatients: Patient[] = [
  {
    id: 'AUR-2026-001',
    name: 'Nguyễn Minh Anh',
    clinic: 'Phòng khám trung tâm',
    date: '09/10/2026',
    status: 'Hoàn thành',
    risk: 'Thấp',
  },
  {
    id: 'AUR-2026-002',
    name: 'Trần Quốc Bảo',
    clinic: 'Phòng khám trung tâm',
    date: '09/10/2026',
    status: 'Đang xử lý',
    risk: 'Trung bình',
  },
  {
    id: 'AUR-2026-003',
    name: 'Lê Thu Hà',
    clinic: 'Cơ sở y tế Quận 1',
    date: '08/10/2026',
    status: 'Cần xem xét',
    risk: 'Cao',
  },
  {
    id: 'AUR-2026-004',
    name: 'Phạm Gia Huy',
    clinic: 'Cơ sở y tế Quận 1',
    date: '08/10/2026',
    status: 'Hoàn thành',
    risk: 'Thấp',
  },
  {
    id: 'AUR-2026-005',
    name: 'Võ Ngọc Linh',
    clinic: 'Phòng khám phía Đông',
    date: '07/10/2026',
    status: 'Hoàn thành',
    risk: 'Trung bình',
  },
]

const navigation = [
  { group: 'TỔNG QUAN', items: [{ label: 'Dashboard', icon: '▦' }] },
  {
    group: 'SÀNG LỌC',
    items: [
      { label: 'Hồ sơ phân tích', icon: '◉' },
      { label: 'Tải dữ liệu lên', icon: '⇧' },
      { label: 'Lịch sử tải lên', icon: '◷' },
    ],
  },
  {
    group: 'QUẢN LÝ',
    items: [
      { label: 'Cơ sở y tế', icon: '⌂' },
      { label: 'Gói dịch vụ', icon: '▤' },
      { label: 'Người dùng', icon: '♙' },
    ],
  },
  {
    group: 'HỆ THỐNG',
    items: [
      { label: 'Cấu hình AI', icon: '⚙' },
      { label: 'Nhật ký hệ thống', icon: '☷' },
      { label: 'Kiểm tra kết nối', icon: '⌁' },
    ],
  },
]

function App() {
  const [activePage, setActivePage] = useState('Dashboard')
  const [search, setSearch] = useState('')
  const [statusFilter, setStatusFilter] = useState('Tất cả trạng thái')
  const [patients] = useState(initialPatients)
  const [selectedPatient, setSelectedPatient] = useState<Patient | null>(null)
  const [showUpload, setShowUpload] = useState(false)
  const [uploadName, setUploadName] = useState('')
  const [uploadMessage, setUploadMessage] = useState('')
  const [result, setResult] = useState<PingResponse | null>(null)
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(false)
  const [sidebarOpen, setSidebarOpen] = useState(false)

  const filteredPatients = useMemo(() => {
    const keyword = search.trim().toLowerCase()

    return patients.filter((patient) => {
      const matchesSearch =
        patient.name.toLowerCase().includes(keyword) ||
        patient.id.toLowerCase().includes(keyword) ||
        patient.clinic.toLowerCase().includes(keyword)

      const matchesStatus =
        statusFilter === 'Tất cả trạng thái' ||
        patient.status === statusFilter

      return matchesSearch && matchesStatus
    })
  }, [patients, search, statusFilter])

  async function checkConnection() {
    setLoading(true)
    setError('')
    setResult(null)

    try {
      const response = await fetch('/api/v1/platform/ping')

      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`)
      }

      const data: PingResponse = await response.json()
      setResult(data)
    } catch {
      setError('Không thể kết nối tới hệ thống.')
    } finally {
      setLoading(false)
    }
  }

  function choosePage(page: string) {
    setActivePage(page)
    setSidebarOpen(false)
    setSelectedPatient(null)
    setUploadMessage('')
  }

  function handleUpload() {
    if (!uploadName) {
      setUploadMessage('Vui lòng chọn một tệp trước khi tiếp tục.')
      return
    }

    setUploadMessage(
      `Đã chọn tệp "${uploadName}". Đây là giao diện demo; chưa tải tệp lên máy chủ.`,
    )
  }

  function renderPatientTable() {
    return (
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Mã hồ sơ</th>
              <th>Họ và tên</th>
              <th>Cơ sở y tế</th>
              <th>Ngày tạo</th>
              <th>Trạng thái</th>
              <th>Mức nguy cơ</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {filteredPatients.map((patient) => (
              <tr key={patient.id}>
                <td className="patient-id">{patient.id}</td>
                <td className="patient-name">{patient.name}</td>
                <td>{patient.clinic}</td>
                <td>{patient.date}</td>
                <td>
                  <span
                    className={`status status-${patient.status === 'Hoàn thành' ? 'done' : patient.status === 'Đang xử lý' ? 'processing' : 'review'}`}
                  >
                    {patient.status}
                  </span>
                </td>
                <td>
                  <span
                    className={`risk risk-${patient.risk === 'Thấp' ? 'low' : patient.risk === 'Trung bình' ? 'medium' : 'high'}`}
                  >
                    <span className="risk-dot" />
                    {patient.risk}
                  </span>
                </td>
                <td>
                  <button
                    className="text-button"
                    onClick={() => setSelectedPatient(patient)}
                  >
                    Chi tiết
                  </button>
                </td>
              </tr>
            ))}
            {filteredPatients.length === 0 && (
              <tr>
                <td colSpan={7} className="empty-state">
                  Không tìm thấy hồ sơ phù hợp.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
    )
  }

  function renderDashboard() {
    return (
      <>
        <section className="welcome-banner">
          <div>
            <span className="eyebrow">TỔNG QUAN HỆ THỐNG</span>
            <h2>Xin chào, Quản trị viên 👋</h2>
            <p>
              Theo dõi hoạt động sàng lọc và tình trạng xử lý hồ sơ tại AURA.
            </p>
          </div>
          <div className="banner-mark" aria-hidden="true">
            <span>✳</span>
            <span className="banner-mark-small">AURA</span>
          </div>
        </section>

        <section className="stats-grid">
          <article className="stat-card">
            <div className="stat-top">
              <span>Tổng hồ sơ</span>
              <span className="stat-icon icon-blue">▤</span>
            </div>
            <strong>{patients.length.toLocaleString('vi-VN')}</strong>
            <span className="stat-note">Hồ sơ demo hiện có</span>
          </article>
          <article className="stat-card">
            <div className="stat-top">
              <span>Hoàn thành</span>
              <span className="stat-icon icon-green">✓</span>
            </div>
            <strong>
              {patients.filter((p) => p.status === 'Hoàn thành').length}
            </strong>
            <span className="stat-note">Đã xử lý xong</span>
          </article>
          <article className="stat-card">
            <div className="stat-top">
              <span>Đang xử lý</span>
              <span className="stat-icon icon-orange">◷</span>
            </div>
            <strong>
              {patients.filter((p) => p.status === 'Đang xử lý').length}
            </strong>
            <span className="stat-note">Đang chờ kết quả</span>
          </article>
          <article className="stat-card">
            <div className="stat-top">
              <span>Cần xem xét</span>
              <span className="stat-icon icon-red">!</span>
            </div>
            <strong>
              {patients.filter((p) => p.status === 'Cần xem xét').length}
            </strong>
            <span className="stat-note">Cần kiểm tra thêm</span>
          </article>
        </section>

        <section className="panel">
          <div className="panel-heading">
            <div>
              <h3>Hồ sơ gần đây</h3>
              <p>Theo dõi các hồ sơ sàng lọc mới nhất</p>
            </div>
            <button
              className="button button-primary"
              onClick={() => choosePage('Hồ sơ phân tích')}
            >
              Xem tất cả <span>→</span>
            </button>
          </div>
          {renderPatientTable()}
        </section>

        <section className="bottom-grid">
          <article className="panel info-panel">
            <div className="panel-heading">
              <div>
                <h3>Trạng thái hệ thống</h3>
                <p>Kiểm tra kết nối dịch vụ</p>
              </div>
              <span className="service-icon">⌁</span>
            </div>
            <div className="connection-line">
              <span className={`connection-dot ${result ? 'online' : ''}`} />
              <div>
                <strong>{result ? 'Đã nhận phản hồi API' : 'Chưa kiểm tra'}</strong>
                <p>
                  {result
                    ? result.service
                    : 'Nhấn nút bên dưới để kiểm tra kết nối.'}
                </p>
              </div>
            </div>
            {error && (
  <p className="error-message" role="alert">
    {error}
  </p>
)}
            {result && <p className="success-message">{result.message}</p>}
            <button
              className="button button-outline"
              onClick={checkConnection}
              disabled={loading}
            >
              {loading ? 'Đang kiểm tra...' : 'Kiểm tra kết nối'}
            </button>
          </article>

          <article className="panel info-panel">
            <div className="panel-heading">
              <div>
                <h3>Thao tác nhanh</h3>
                <p>Truy cập các chức năng thường dùng</p>
              </div>
            </div>
            <button
              className="quick-action"
              onClick={() => setShowUpload(true)}
            >
              <span className="quick-icon">⇧</span>
              <span>
                <strong>Tải dữ liệu lên</strong>
                <small>Chọn tệp dữ liệu cần xử lý</small>
              </span>
              <span className="quick-arrow">→</span>
            </button>
            <button
              className="quick-action"
              onClick={() => choosePage('Hồ sơ phân tích')}
            >
              <span className="quick-icon">◉</span>
              <span>
                <strong>Hồ sơ phân tích</strong>
                <small>Xem và lọc hồ sơ sàng lọc</small>
              </span>
              <span className="quick-arrow">→</span>
            </button>
          </article>
        </section>
      </>
    )
  }

  function renderPageContent() {
    if (selectedPatient) {
      return (
        <section className="panel detail-panel">
          <button
            className="back-button"
            onClick={() => setSelectedPatient(null)}
          >
            ← Quay lại danh sách
          </button>
          <span className="eyebrow">CHI TIẾT HỒ SƠ</span>
          <h2>{selectedPatient.name}</h2>
          <p className="detail-description">
            Thông tin hồ sơ minh họa trong môi trường frontend demo.
          </p>
          <div className="detail-grid">
            <div><span>Mã hồ sơ</span><strong>{selectedPatient.id}</strong></div>
            <div><span>Cơ sở y tế</span><strong>{selectedPatient.clinic}</strong></div>
            <div><span>Ngày tạo</span><strong>{selectedPatient.date}</strong></div>
            <div><span>Trạng thái</span><strong>{selectedPatient.status}</strong></div>
            <div><span>Mức nguy cơ minh họa</span><strong>{selectedPatient.risk}</strong></div>
          </div>
          <div className="notice">
            Đây là dữ liệu mẫu, không phải kết quả chẩn đoán y khoa thực tế.
          </div>
        </section>
      )
    }

    if (activePage === 'Dashboard') return renderDashboard()

    if (activePage === 'Hồ sơ phân tích') {
      return (
        <section className="panel">
          <div className="panel-heading">
            <div>
              <h3>Hồ sơ phân tích</h3>
              <p>Tra cứu và theo dõi trạng thái hồ sơ sàng lọc</p>
            </div>
            <button
              className="button button-primary"
              onClick={() => setShowUpload(true)}
            >
              + Tạo hồ sơ demo
            </button>
          </div>
          <div className="filters">
            <input
              value={search}
              onChange={(event) => setSearch(event.target.value)}
              placeholder="Tìm theo tên, mã hồ sơ, cơ sở..."
              aria-label="Tìm kiếm hồ sơ"
            />
            <select
              value={statusFilter}
              onChange={(event) => setStatusFilter(event.target.value)}
              aria-label="Lọc theo trạng thái"
            >
              <option>Tất cả trạng thái</option>
              <option>Hoàn thành</option>
              <option>Đang xử lý</option>
              <option>Cần xem xét</option>
            </select>
          </div>
          {renderPatientTable()}
        </section>
      )
    }

    if (activePage === 'Tải dữ liệu lên') {
      return (
        <section className="panel content-panel">
          <h3>Tải dữ liệu lên</h3>
          <p className="section-description">
            Chọn tệp để xem trước luồng tải dữ liệu. Chức năng tải lên máy chủ
            sẽ được tích hợp ở bước kết nối API.
          </p>
          <div className="upload-zone">
            <span className="upload-symbol">⇧</span>
            <h4>Chọn tệp dữ liệu</h4>
            <p>Chọn tệp từ máy tính để tiếp tục</p>
            <label className="button button-primary file-label">
              Chọn tệp
              <input
                type="file"
                accept=".csv,.xlsx,.xls,.json,.zip"
                onChange={(event) => {
                  const file = event.target.files?.[0]
                  setUploadName(file?.name ?? '')
                  setUploadMessage('')
                }}
              />
            </label>
            {uploadName && <p className="selected-file">Đã chọn: {uploadName}</p>}
            <button className="button button-outline" onClick={handleUpload}>
              Tiếp tục
            </button>
            {uploadMessage && <p className="form-message">{uploadMessage}</p>}
          </div>
        </section>
      )
    }

    if (activePage === 'Kiểm tra kết nối') {
      return (
        <section className="panel content-panel">
          <h3>Kiểm tra kết nối</h3>
          <p className="section-description">
            Kiểm tra phản hồi từ API Gateway của hệ thống.
          </p>
          <button
            className="button button-primary"
            onClick={checkConnection}
            disabled={loading}
          >
            {loading ? 'Đang kiểm tra...' : 'Kiểm tra kết nối'}
          </button>
          {error && (
  <p className="error-message" role="alert">
    {error}
  </p>
)}
          {result && (
            <div className="connection-result">
              <h4>Kết nối thành công</h4>
              <p>Dịch vụ: {result.service}</p>
              <p>Thông báo: {result.message}</p>
              <p>Thời gian: {result.timestamp}</p>
            </div>
          )}
        </section>
      )
    }

    const descriptions: Record<string, string> = {
      'Lịch sử tải lên': 'Theo dõi các lần tải dữ liệu và trạng thái xử lý.',
      'Cơ sở y tế': 'Khu vực quản lý thông tin các cơ sở y tế.',
      'Gói dịch vụ': 'Khu vực quản lý danh mục và thông tin gói dịch vụ.',
      'Người dùng': 'Khu vực quản lý tài khoản và quyền truy cập.',
      'Cấu hình AI': 'Khu vực cấu hình dịch vụ phân tích AI.',
      'Nhật ký hệ thống': 'Theo dõi hoạt động và sự kiện của hệ thống.',
    }

    return (
      <section className="panel content-panel">
        <span className="eyebrow">AURA MANAGEMENT</span>
        <h3>{activePage}</h3>
        <p className="section-description">
          {descriptions[activePage] ??
            'Khu vực chức năng của hệ thống AURA.'}
        </p>
        <div className="coming-soon">
          <span>◷</span>
          <strong>Giao diện đang được xây dựng</strong>
          <p>
            Khung điều hướng đã hoạt động. Nội dung chi tiết sẽ được phát
            triển ở các bước tiếp theo.
          </p>
        </div>
      </section>
    )
  }

  return (
    <div className="app-shell">
      {sidebarOpen && (
        <button
          className="mobile-overlay"
          aria-label="Đóng menu"
          onClick={() => setSidebarOpen(false)}
        />
      )}

      <aside className={`sidebar ${sidebarOpen ? 'sidebar-open' : ''}`}>
        <button
          className="brand"
          onClick={() => choosePage('Dashboard')}
          aria-label="Về Dashboard AURA"
        >
          <span className="brand-symbol">✳</span>
          <span className="brand-copy">
            <strong>AURA</strong>
            <small>RETINAL HEALTH</small>
          </span>
        </button>

        <div className="workspace-label">WORKSPACE</div>

        <nav className="side-navigation">
          {navigation.map((section) => (
            <div className="nav-section" key={section.group}>
              <div className="nav-group-label">{section.group}</div>
              {section.items.map((item) => (
                <button
                  key={item.label}
                  className={`nav-item ${activePage === item.label ? 'active' : ''}`}
                  onClick={() => choosePage(item.label)}
                >
                  <span className="nav-icon">{item.icon}</span>
                  <span>{item.label}</span>
                  {item.label === 'Hồ sơ phân tích' && (
                    <span className="nav-count">{patients.length}</span>
                  )}
                </button>
              ))}
            </div>
          ))}
        </nav>

        <div className="sidebar-bottom">
          <div className="help-card">
            <span className="help-icon">?</span>
            <strong>Cần hỗ trợ?</strong>
            <p>Liên hệ quản trị viên nếu bạn gặp vấn đề.</p>
          </div>
          <div className="sidebar-version">
            <span className="version-dot" />
            AURA Web · Demo v0.1
          </div>
        </div>
      </aside>

      <div className="main-column">
        <header className="topbar">
          <div className="topbar-left">
            <button
              className="menu-toggle"
              onClick={() => setSidebarOpen(!sidebarOpen)}
              aria-label="Mở hoặc đóng menu"
            >
              ☰
            </button>
            <div className="breadcrumb">
              <span>Workspace</span>
              <span className="breadcrumb-separator">/</span>
              <strong>{selectedPatient ? 'Chi tiết hồ sơ' : activePage}</strong>
            </div>
          </div>
          <div className="topbar-right">
            <span className="environment-badge">
              <span /> DEMO
            </span>
            <div className="topbar-divider" />
            <div className="user-profile">
              <div className="avatar">AD</div>
              <div className="user-copy">
                <strong>Quản trị viên</strong>
                <small>System Admin</small>
              </div>
              <span className="user-chevron">⌄</span>
            </div>
          </div>
        </header>

        <main className="page-content">
          <div className="page-title-row">
            <div>
              <h1>{selectedPatient ? 'Chi tiết hồ sơ' : activePage}</h1>
              <p>
                {selectedPatient
                  ? 'Thông tin chi tiết của hồ sơ được chọn.'
                  : 'Hệ thống sàng lọc sức khỏe mạch máu võng mạc.'}
              </p>
            </div>
            <div className="current-date">
              <span>◷</span> 09 tháng 10, 2026
            </div>
          </div>

          {renderPageContent()}

          <footer className="page-footer">
            <span>© 2026 AURA · Retinal Health Screening System</span>
            <span>Dữ liệu demo · Không dùng để chẩn đoán y khoa</span>
          </footer>
        </main>
      </div>

      {showUpload && (
        <div
          className="modal-backdrop"
          onMouseDown={(event) => {
            if (event.target === event.currentTarget) setShowUpload(false)
          }}
        >
          <section
            className="modal"
            role="dialog"
            aria-modal="true"
            aria-labelledby="upload-modal-title"
          >
            <div className="modal-heading">
              <div>
                <span className="eyebrow">AURA WORKSPACE</span>
                <h3 id="upload-modal-title">Tải dữ liệu lên</h3>
              </div>
              <button
                className="modal-close"
                onClick={() => setShowUpload(false)}
                aria-label="Đóng cửa sổ"
              >
                ×
              </button>
            </div>
            <p>
              Chọn tệp để minh họa quy trình tiếp nhận dữ liệu sàng lọc.
            </p>
            <label className="button button-outline file-label modal-file">
              ⇧ &nbsp; Chọn tệp từ máy tính
              <input
                type="file"
                accept=".csv,.xlsx,.xls,.json,.zip"
                onChange={(event) => {
                  setUploadName(event.target.files?.[0]?.name ?? '')
                  setUploadMessage('')
                }}
              />
            </label>
            {uploadName && <p className="selected-file">{uploadName}</p>}
            {uploadMessage && <p className="form-message">{uploadMessage}</p>}
            <div className="modal-actions">
              <button
                className="button button-outline"
                onClick={() => setShowUpload(false)}
              >
                Hủy
              </button>
              <button className="button button-primary" onClick={handleUpload}>
                Tiếp tục
              </button>
            </div>
          </section>
        </div>
      )}
    </div>
  )
}

export default App
