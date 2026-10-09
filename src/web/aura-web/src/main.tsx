
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'

async function startApp() {
  const enableMock =
    import.meta.env.DEV &&
    import.meta.env.VITE_ENABLE_MOCK === 'true'

  if (enableMock) {
    const { worker } = await import('./mocks/browser.ts')

    await worker.start({
  onUnhandledRequest: 'bypass',
  serviceWorker: {
    url: '/mockServiceWorker.js',
  },
})
  }

  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <>
        {enableMock && (
          <div
            role="status"
            style={{
              background: '#fff3cd',
              padding: '8px',
              textAlign: 'center',
              color: '#664d03',
            }}
          >
            DEMO MODE — Dữ liệu giả lập
          </div>
        )}
        <App />
      </>
    </StrictMode>,
  )
}

startApp()
