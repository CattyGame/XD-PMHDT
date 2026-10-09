
const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? ''

export async function apiGet(path: string) {
  const response = await fetch(`${API_BASE_URL}${path}`, {
    signal: AbortSignal.timeout(3000),
  })

  if (!response.ok) {
    throw new Error(`HTTP ${response.status}`)
  }

  return response.json()
}
