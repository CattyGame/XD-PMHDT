import { useEffect, useRef, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { acceptClinicInvitation, getActorClinics, type ClinicInfo } from '../api/clinicManagement'
import type { BackendContext } from '../api/backendContext'
import { AnalysisResultError } from '../api/analysisResult'
import { useBackendSession } from '../auth/backendSession'
import BackendSessionNotice from '../auth/BackendSessionNotice'

function InvitationForm({ context, membershipId }: { context: BackendContext; membershipId: string }) {
  const [id, setId] = useState(membershipId)
  const [token, setToken] = useState('')
  const [busy, setBusy] = useState(false)
  const [accepted, setAccepted] = useState(false)
  const [error, setError] = useState('')
  const [clinics, setClinics] = useState<readonly ClinicInfo[]>([])
  const request = useRef<AbortController | null>(null)
  useEffect(() => () => request.current?.abort(), [])

  async function accept(event: React.FormEvent) {
    event.preventDefault()
    request.current?.abort()
    const controller = new AbortController()
    request.current = controller
    setBusy(true)
    setError('')
    try {
      await acceptClinicInvitation(context, id.trim(), token, controller.signal)
      if (controller.signal.aborted) return
      setAccepted(true)
      setToken('')
      const items = await getActorClinics(context, controller.signal)
      if (!controller.signal.aborted) setClinics(items)
    } catch (cause) {
      if (!controller.signal.aborted) setError(cause instanceof AnalysisResultError
        ? `Không thể hoàn tất: ${cause.code}. Kiểm tra email đã xác minh, mã lời mời và hạn 72 giờ.`
        : 'Không thể tải dữ liệu. Vui lòng thử lại.')
    } finally {
      if (!controller.signal.aborted) setBusy(false)
    }
  }

  return <main className="session-content">
    <h1>Chấp nhận lời mời phòng khám</h1>
    <p>Đăng nhập bằng đúng email đã được mời và đã xác minh. Lời mời dùng một lần, có hiệu lực 72 giờ.</p>
    {error && <p role="alert">{error}</p>}
    {accepted ? <>
      <p role="status">Đã chấp nhận lời mời.</p>
      <ul>{clinics.map(clinic => <li key={clinic.clinicId}>{clinic.clinicName}</li>)}</ul>
      <Link to="/clinics/patients">Mở danh bạ phòng khám</Link>
    </> : <form onSubmit={accept}>
      <p><label>Mã thành viên lời mời <input required value={id} onChange={event => setId(event.target.value)} disabled={busy} /></label></p>
      <p><label>Token lời mời <input required type="password" autoComplete="off" value={token} onChange={event => setToken(event.target.value)} disabled={busy} /></label></p>
      <button type="submit" disabled={busy}>{busy ? 'Đang xác nhận…' : 'Chấp nhận lời mời'}</button>
    </form>}
    <p><Link to="/">Về trang chủ</Link></p>
  </main>
}

export default function ClinicInvitationPage() {
  const { state } = useBackendSession()
  const { membershipId = '' } = useParams()
  if (state.status !== 'ready') return <BackendSessionNotice />
  return <InvitationForm key={`${state.context.accountId}:${state.context.firebaseUid}:${membershipId}`} context={state.context} membershipId={membershipId} />
}
