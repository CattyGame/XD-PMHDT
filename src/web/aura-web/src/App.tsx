import { useState } from 'react'

type PingResponse = {
  service: string
  message: string
  timestamp: string
}

function App() {
  const [result, setResult] = useState<PingResponse | null>(null)
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(false)

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

  return (
    <main>
      <h1>AURA</h1>
      <p>Hệ thống sàng lọc sức khỏe mạch máu võng mạc</p>

      <button
        type="button"
        onClick={checkConnection}
        disabled={loading}
      >
        {loading ? 'Đang kiểm tra...' : 'Kiểm tra kết nối'}
      </button>

      {error && <p role="alert">{error}</p>}

      {result && (
        <section aria-label="Kết quả kết nối">
          <h2>Kết nối thành công</h2>
          <p>{result.service}</p>
          <p>{result.message}</p>
          <p>{result.timestamp}</p>
        </section>
      )}
    </main>
  )
}

export default App