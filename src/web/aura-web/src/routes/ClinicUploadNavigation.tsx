import { useMemo, useState } from 'react'
import { Outlet } from 'react-router-dom'
import { useBackendSession } from '../auth/backendSession'

type UploadDraft = { clinicId: string; patientId: string; files: File[] }

export type ClinicUploadHandoff = {
  draft: UploadDraft | null
  setDraft: (draft: UploadDraft | null) => void
}

function UploadOutlet() {
  const [draft, setDraft] = useState<UploadDraft | null>(null)
  const handoff = useMemo<ClinicUploadHandoff>(() => ({ draft, setDraft }), [draft])
  return <Outlet context={handoff} />
}

export default function ClinicUploadNavigation() {
  const { state } = useBackendSession()
  if (state.status !== 'ready') return null
  const { firebaseUid, accountId, profileVersion } = state.context
  // Files exist only in this protected route subtree, scoped to the current session.
  return <UploadOutlet key={`${firebaseUid}:${accountId}:${profileVersion}`} />
}
