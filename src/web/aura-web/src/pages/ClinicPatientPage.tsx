import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { useBackendSession } from '../auth/backendSession'
import BackendSessionNotice from '../auth/BackendSessionNotice'
import type { BackendContext } from '../api/backendContext'
import {
  getActorClinics,
  getClinicPatients,
  getClinicPatientDetail,
  registerClinicPatient,
  type ClinicInfo,
  type ClinicPatient,
  type RegisterClinicPatientInput,
} from '../api/clinicManagement'
import { AnalysisResultError } from '../api/analysisResult'
import './ClinicPatientPage.css'

const emptyRegistration: RegisterClinicPatientInput = {
  fullName: '', dateOfBirth: '', gender: 'MALE', phone: '', healthInsuranceNumber: '',
  medicalHistory: { hasDiabetes: false, hasHypertension: false, notes: '' },
}

function ClinicPatientContent({ context }: { context: BackendContext }) {
  const [clinics, setClinics] = useState<readonly ClinicInfo[]>([])
  const [selectedClinicId, setSelectedClinicId] = useState<string>('')
  const [patients, setPatients] = useState<readonly ClinicPatient[]>([])
  const [totalCount, setTotalCount] = useState<number>(0)
  const [search, setSearch] = useState<string>('')
  const [selectedPatient, setSelectedPatient] = useState<ClinicPatient | null>(null)

  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string>('')
  const [successMsg, setSuccessMsg] = useState<string>('')
  const [reloadTrigger, setReloadTrigger] = useState(0)
  const scope = useRef(new AbortController())
  const detailRequest = useRef<AbortController | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    scope.current = controller
    setPatients([])
    setTotalCount(0)
    setSelectedPatient(null)
    setShowRegisterModal(false)
    setFormData(emptyRegistration)
    setError('')
    setSuccessMsg('')
    setSaving(false)
    return () => controller.abort()
  }, [context, selectedClinicId])

  // Modal register patient state
  const [showRegisterModal, setShowRegisterModal] = useState(false)
  const [saving, setSaving] = useState(false)
  const [formData, setFormData] = useState<RegisterClinicPatientInput>(emptyRegistration)

  // 1. Load actor's clinics
  useEffect(() => {
    const controller = new AbortController()
    getActorClinics(context, controller.signal)
      .then((items) => {
        if (controller.signal.aborted) return
        setClinics(items)
        if (items.length > 0) {
          setSelectedClinicId(items[0].clinicId)
        }
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          setError(
            err instanceof AnalysisResultError && err.status === 403
              ? 'Bạn không có quyền truy cập quản lý phòng khám này.'
              : 'Không thể tải danh sách phòng khám.',
          )
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false)
      })

    return () => controller.abort()
  }, [context])

  // 2. Load patients for selected clinic (AC2)
  useEffect(() => {
    if (!selectedClinicId) return
    const controller = new AbortController()

    getClinicPatients(context, selectedClinicId, { search, limit: 100 }, controller.signal)
      .then((res) => {
        if (!controller.signal.aborted) {
          setPatients(res.items)
          setTotalCount(res.totalCount)
        }
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          setPatients([])
          setError(
            err instanceof AnalysisResultError && err.status === 403
              ? 'Bạn không có quyền xem danh sách bệnh nhân tại phòng khám này.'
              : 'Không thể tải danh sách bệnh nhân.',
          )
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false)
      })

    return () => controller.abort()
  }, [context, selectedClinicId, search, reloadTrigger])

  // 3. View Patient Detail
  const handleSelectPatient = async (patient: ClinicPatient) => {
    if (!selectedClinicId) return
    detailRequest.current?.abort()
    detailRequest.current = new AbortController()
    const signal = AbortSignal.any([scope.current.signal, detailRequest.current.signal])
    setSelectedPatient(null)
    setError('')
    try {
      const detail = await getClinicPatientDetail(context, selectedClinicId, patient.patientId, signal)
      if (!signal.aborted) setSelectedPatient(detail)
    } catch (cause) {
      if (!signal.aborted) {
        if (cause instanceof AnalysisResultError && [401, 403, 404].includes(cause.status)) {
          setPatients([])
          setTotalCount(0)
        }
        setError('Không thể mở hồ sơ bệnh nhân. Quyền truy cập có thể đã thay đổi; hãy tải lại danh sách.')
      }
    }
  }

  // 4. Register new Patient
  const handleRegister = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!formData.fullName.trim() || !selectedClinicId) return
    setSaving(true)
    setError('')
    setSuccessMsg('')
    const signal = scope.current.signal

    try {
      const newPatient = await registerClinicPatient(context, selectedClinicId, {
        ...formData,
        fullName: formData.fullName.trim(),
        dateOfBirth: formData.dateOfBirth || null,
        phone: formData.phone?.trim() || null,
        healthInsuranceNumber: formData.healthInsuranceNumber?.trim() || null,
        medicalHistory: currentClinic?.role === 'STAFF' ? null : formData.medicalHistory,
      }, signal)

      if (signal.aborted) return

      setSuccessMsg(`Đã tiếp nhận hồ sơ bệnh nhân "${newPatient.fullName}" thành công!`)
      setShowRegisterModal(false)
      setFormData(emptyRegistration)
      setReloadTrigger((v) => v + 1)
      setSelectedPatient(newPatient)
    } catch (err) {
      if (signal.aborted) return
      if (err instanceof AnalysisResultError && err.status === 403) {
        setError('Bạn không có quyền tiếp nhận bệnh nhân tại phòng khám này.')
      } else {
        setError('Không thể đăng ký bệnh nhân. Vui lòng kiểm tra lại thông tin.')
      }
    } finally {
      if (!signal.aborted) setSaving(false)
    }
  }

  const currentClinic = clinics.find((c) => c.clinicId === selectedClinicId)
  const canRegister = currentClinic?.role === 'CLINIC_ADMIN' || currentClinic?.role === 'STAFF'

  return (
    <div className="clinic-patient-page">
      <header className="clinic-patient-header">
        <h1>Quản lý Danh bạ Bệnh nhân Phòng khám</h1>
        <nav className="clinic-patient-nav">
          <Link to="/">Trang chủ</Link>
          <Link to="/clinics/members">Quản lý Thành viên</Link>
          <Link to="/clinic-upload">Tải ảnh phòng khám</Link>
        </nav>
      </header>

      {error && <div className="status-alert error" role="alert">{error}</div>}
      {successMsg && <div className="status-alert success" role="status">{successMsg}</div>}

      <div className="patient-controls-bar">
        <div style={{ display: 'flex', gap: '12px', alignItems: 'center' }}>
          <label htmlFor="patient-clinic-select" style={{ fontWeight: 600 }}>Phòng khám:</label>
          <select
            id="patient-clinic-select"
            value={selectedClinicId}
            onChange={(e) => {
              setSelectedClinicId(e.target.value)
              setSelectedPatient(null)
            }}
            disabled={loading || clinics.length === 0}
            style={{ padding: '8px 12px', borderRadius: '6px', border: '1px solid #cbd5e1' }}
          >
            {clinics.map((c) => (
              <option key={c.clinicId} value={c.clinicId}>
                {c.clinicName}
              </option>
            ))}
          </select>
        </div>

        <div className="search-input-box">
          <input
            type="search"
            placeholder="Tìm kiếm bệnh nhân theo tên..."
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            aria-label="Tìm kiếm bệnh nhân"
          />
        </div>

        {canRegister && (
          <button
            type="button"
            className="btn-primary"
            onClick={() => {
              setShowRegisterModal(true)
              setError('')
              setSuccessMsg('')
            }}
          >
            + Đăng ký Bệnh nhân mới
          </button>
        )}
      </div>

      {/* Bảng danh sách bệnh nhân */}
      <div className="patient-table-container">
        <table className="patient-table" aria-label="Bảng danh sách bệnh nhân phòng khám">
          <thead>
            <tr>
              <th>Họ và Tên</th>
              <th>Ngày sinh / Giới tính</th>
              <th>Số điện thoại</th>
              <th>Số thẻ BHYT</th>
              <th>Tiền sử bệnh</th>
              <th>Trạng thái</th>
              <th>Hành động</th>
            </tr>
          </thead>
          <tbody>
            {loading && patients.length === 0 ? (
              <tr>
                <td colSpan={7} style={{ textAlign: 'center', padding: '24px' }}>
                  Đang tải danh bạ bệnh nhân...
                </td>
              </tr>
            ) : patients.length === 0 ? (
              <tr>
                <td colSpan={7} style={{ textAlign: 'center', padding: '24px', color: '#64748b' }}>
                  Không tìm thấy hồ sơ bệnh nhân nào trong phòng khám này.
                </td>
              </tr>
            ) : (
              patients.map((p) => (
                <tr
                  key={p.patientId}
                  style={{
                    backgroundColor: selectedPatient?.patientId === p.patientId ? '#eff6ff' : undefined,
                    cursor: 'pointer',
                  }}
                  onClick={() => handleSelectPatient(p)}
                >
                  <td>
                    <strong>{p.fullName}</strong>
                    <div style={{ fontSize: '11px', color: '#64748b' }}>Mã: {p.patientId.slice(0, 8)}...</div>
                  </td>
                  <td>
                    {p.dateOfBirth || '—'} /{' '}
                    {p.gender === 'MALE' ? 'Nam' : p.gender === 'FEMALE' ? 'Nữ' : 'Khác'}
                  </td>
                  <td>{p.phone || '—'}</td>
                  <td>{p.healthInsuranceNumber || '—'}</td>
                  <td>
                    {p.medicalHistory?.hasDiabetes && (
                      <span className="disease-tag diabetes">Đái tháo đường</span>
                    )}
                    {p.medicalHistory?.hasHypertension && (
                      <span className="disease-tag hypertension">Tăng huyết áp</span>
                    )}
                    {!p.medicalHistory?.hasDiabetes && !p.medicalHistory?.hasHypertension && (
                      <span style={{ color: '#94a3b8', fontSize: '12px' }}>Không ghi nhận</span>
                    )}
                  </td>
                  <td>
                    <span className="badge badge-active">{p.status === 'ACTIVE' ? 'Đang điều trị' : p.status}</span>
                  </td>
                  <td>
                    <button
                      type="button"
                      style={{
                        padding: '4px 8px',
                        border: '1px solid #cbd5e1',
                        borderRadius: '4px',
                        background: '#ffffff',
                        cursor: 'pointer',
                        fontSize: '12px',
                      }}
                      onClick={(e) => {
                        e.stopPropagation()
                        handleSelectPatient(p)
                      }}
                    >
                      Chi tiết
                    </button>
                  </td>
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>

      <div style={{ marginTop: '12px', fontSize: '13px', color: '#64748b', textAlign: 'right' }}>
        Tổng số hồ sơ: <strong>{totalCount}</strong> bệnh nhân thuộc phòng khám.
      </div>

      {/* Chi tiết bệnh nhân được chọn */}
      {selectedPatient && (
        <section className="patient-detail-card" aria-labelledby="patient-detail-title">
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '16px' }}>
            <h3 id="patient-detail-title">Hồ Sơ Bệnh Án: {selectedPatient.fullName}</h3>
            <div style={{ display: 'flex', gap: '8px' }}>
              <Link
                className="btn-primary"
                to={`/clinic-upload?clinicContextId=${selectedPatient.clinicId}&patientId=${selectedPatient.patientId}`}
                style={{ textDecoration: 'none', fontSize: '13px' }}
              >
                + Chụp / Gửi ảnh phân tích
              </Link>
              {currentClinic?.role !== 'STAFF' && <Link
                className="btn-secondary"
                to={`/clinics/patients/${selectedPatient.patientId}/history?clinicContextId=${selectedPatient.clinicId}`}
                style={{ textDecoration: 'none', fontSize: '13px' }}
              >
                Lịch sử đợt khám
              </Link>}
            </div>
          </div>

          <div className="patient-detail-grid">
            <div className="detail-item">
              <span className="label">Mã định danh bệnh nhân</span>
              <span className="value">{selectedPatient.patientId}</span>
            </div>
            <div className="detail-item">
              <span className="label">Ngày sinh & Giới tính</span>
              <span className="value">
                {selectedPatient.dateOfBirth || 'Chưa cập nhật'} (
                {selectedPatient.gender === 'MALE' ? 'Nam' : selectedPatient.gender === 'FEMALE' ? 'Nữ' : 'Khác'})
              </span>
            </div>
            <div className="detail-item">
              <span className="label">Số điện thoại liên hệ</span>
              <span className="value">{selectedPatient.phone || 'Chưa cập nhật'}</span>
            </div>
            <div className="detail-item">
              <span className="label">Mã thẻ BHYT</span>
              <span className="value">{selectedPatient.healthInsuranceNumber || 'Không có BHYT'}</span>
            </div>
          </div>

          <div style={{ marginTop: '12px', padding: '12px', background: '#f8fafc', borderRadius: '6px' }}>
            <strong style={{ fontSize: '13px', color: '#334155' }}>Tiền sử bệnh án & Ghi chú lâm sàng:</strong>
            <p style={{ margin: '6px 0 0 0', fontSize: '13px', color: '#475569' }}>
              {selectedPatient.medicalHistory?.notes ||
                'Chưa có ghi chú lâm sàng đặc biệt. Bác sĩ có thể bổ sung khi khám trực tiếp.'}
            </p>
          </div>
        </section>
      )}

      {/* Modal đăng ký bệnh nhân mới */}
      {showRegisterModal && (
        <div className="patient-modal-overlay">
          <div className="patient-modal">
            <h2>Tiếp Nhận Bệnh Nhân Phòng Khám Mới</h2>
            <form onSubmit={handleRegister}>
              <div className="form-group">
                <label htmlFor="reg-fullname">Họ và tên bệnh nhân (*):</label>
                <input
                  id="reg-fullname"
                  type="text"
                  required
                  placeholder="Ví dụ: Nguyễn Văn An"
                  value={formData.fullName}
                  onChange={(e) => setFormData({ ...formData, fullName: e.target.value })}
                />
              </div>

              <div className="form-row">
                <div className="form-group" style={{ flex: 1 }}>
                  <label htmlFor="reg-dob">Ngày sinh:</label>
                  <input
                    id="reg-dob"
                    type="date"
                    value={formData.dateOfBirth || ''}
                    onChange={(e) => setFormData({ ...formData, dateOfBirth: e.target.value })}
                  />
                </div>
                <div className="form-group" style={{ flex: 1 }}>
                  <label htmlFor="reg-gender">Giới tính:</label>
                  <select
                    id="reg-gender"
                    value={formData.gender || 'MALE'}
                    onChange={(e) => setFormData({ ...formData, gender: e.target.value })}
                  >
                    <option value="MALE">Nam</option>
                    <option value="FEMALE">Nữ</option>
                    <option value="OTHER">Khác</option>
                  </select>
                </div>
              </div>

              <div className="form-row">
                <div className="form-group" style={{ flex: 1 }}>
                  <label htmlFor="reg-phone">Số điện thoại:</label>
                  <input
                    id="reg-phone"
                    type="tel"
                    placeholder="0912345678"
                    value={formData.phone || ''}
                    onChange={(e) => setFormData({ ...formData, phone: e.target.value })}
                  />
                </div>
                <div className="form-group" style={{ flex: 1 }}>
                  <label htmlFor="reg-insurance">Số thẻ BHYT:</label>
                  <input
                    id="reg-insurance"
                    type="text"
                    placeholder="GD479..."
                    value={formData.healthInsuranceNumber || ''}
                    onChange={(e) => setFormData({ ...formData, healthInsuranceNumber: e.target.value })}
                  />
                </div>
              </div>

              {currentClinic?.role !== 'STAFF' && <>
              <div className="form-group">
                <label>Tiền sử bệnh lý:</label>
                <div className="checkbox-group">
                  <label className="checkbox-label">
                    <input
                      type="checkbox"
                      checked={formData.medicalHistory?.hasDiabetes === true}
                      onChange={(e) =>
                        setFormData({
                          ...formData,
                          medicalHistory: {
                            ...formData.medicalHistory,
                            hasDiabetes: e.target.checked,
                          },
                        })
                      }
                    />
                    Tiền sử Đái tháo đường
                  </label>
                  <label className="checkbox-label">
                    <input
                      type="checkbox"
                      checked={formData.medicalHistory?.hasHypertension === true}
                      onChange={(e) =>
                        setFormData({
                          ...formData,
                          medicalHistory: {
                            ...formData.medicalHistory,
                            hasHypertension: e.target.checked,
                          },
                        })
                      }
                    />
                    Tiền sử Tăng huyết áp
                  </label>
                </div>
              </div>

              <div className="form-group">
                <label htmlFor="reg-notes">Ghi chú lâm sàng:</label>
                <input
                  id="reg-notes"
                  type="text"
                  placeholder="Ghi chú triệu chứng ban đầu hoặc lý do khám..."
                  value={formData.medicalHistory?.notes || ''}
                  onChange={(e) =>
                    setFormData({
                      ...formData,
                      medicalHistory: {
                        ...formData.medicalHistory,
                        notes: e.target.value,
                      },
                    })
                  }
                />
              </div>

              </>}
              <div className="modal-actions">
                <button
                  type="button"
                  className="btn-secondary"
                  onClick={() => setShowRegisterModal(false)}
                  disabled={saving}
                >
                  Hủy bỏ
                </button>
                <button type="submit" className="btn-primary" disabled={saving}>
                  {saving ? 'Đang lưu...' : 'Lưu hồ sơ'}
                </button>
              </div>
            </form>
          </div>
        </div>
      )}
    </div>
  )
}

export default function ClinicPatientPage() {
  const { state } = useBackendSession()
  if (state.status !== 'ready') return <BackendSessionNotice />
  return <ClinicPatientContent key={`${state.context.accountId}:${state.context.firebaseUid}:${state.context.profileVersion}`} context={state.context} />
}
