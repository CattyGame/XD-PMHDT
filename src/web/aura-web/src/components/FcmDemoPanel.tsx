
import { useState } from 'react'
import {
  registerFcmDemo,
  type FcmDemoScenario,
} from '../api/fcmDemo'

export default function FcmDemoPanel() {
  const [scenario, setScenario] =
    useState<FcmDemoScenario>('success')

  const [result, setResult] = useState('')
  const [loading, setLoading] = useState(false)

  async function handleTest() {
    setLoading(true)
    setResult('')

    try {
      const message = await registerFcmDemo(scenario)
      setResult(message)
    } catch {
      setResult('Không thể kết nối API demo. Kiểm tra MSW.')
    } finally {
      setLoading(false)
    }
  }

  return (
    <section className="panel content-panel" style={{ marginTop: 20 }}>
      <h3>FCM API Demo - SCRUM-296</h3>

      <p className="section-description">
        Mô phỏng phản hồi API bằng MSW.
        Không gửi push hoặc FCM token thật.
      </p>

      <label htmlFor="fcm-scenario">Chọn tình huống kiểm thử</label>

      <select
        id="fcm-scenario"
        value={scenario}
        onChange={(event) =>
          setScenario(event.target.value as FcmDemoScenario)
        }
        style={{ display: 'block', margin: '12px 0', padding: 10 }}
      >
        <option value="success">200 - Thành công</option>
        <option value="unauthorized">401 - Chưa xác thực</option>
        <option value="forbidden">403 - Không đủ quyền</option>
        <option value="server-error">500 - Lỗi server</option>
      </select>

      <button
        type="button"
        className="button button-primary"
        onClick={handleTest}
        disabled={loading}
      >
        {loading ? 'Đang kiểm thử...' : 'Chạy demo'}
      </button>

      {result && (
        <p role="status" style={{ marginTop: 12 }}>
          {result}
        </p>
      )}
    </section>
  )
}
