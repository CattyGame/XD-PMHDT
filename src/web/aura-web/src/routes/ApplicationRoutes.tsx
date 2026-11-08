import type { ReactNode } from 'react'
import { Link, Route, Routes } from 'react-router-dom'
import App from '../App'
import LoginPage from '../pages/LoginPage'
import UploadPage from '../pages/UploadPage'
import BatchProgressPage from '../pages/BatchProgressPage'
import SessionHomePage from '../pages/SessionHomePage'
import ForbiddenPage from '../pages/ForbiddenPage'
import NotFoundPage from '../pages/NotFoundPage'
import ServerErrorPage from '../pages/ServerErrorPage'
import ProtectedRoute from './ProtectedRoute'
import AppRouter from './AppRouter'
import AnalysisResultPage from '../pages/AnalysisResultPage'
import NotificationInboxPage from '../pages/NotificationInboxPage'
import CampaignManagementPage from '../pages/CampaignManagementPage'
import ClinicUploadPage from '../pages/ClinicUploadPage'
import DoctorWorkspacePage from '../pages/doctor/DoctorWorkspacePage'
import ClinicMemberPage from '../pages/ClinicMemberPage'
import ClinicPatientPage from '../pages/ClinicPatientPage'
import BillingOrderPage from '../pages/BillingOrderPage'

function DemoScope({ children }: { children: ReactNode }) {
  return <>
    <aside role="note" aria-label="Giao diện minh họa" style={{ position: 'relative', zIndex: 1000, padding: 16, background: '#fff3cd', color: '#5d4800' }}>
      <strong>Giao diện minh họa.</strong> Vai trò và dữ liệu trong khu vực này là giả lập.{' '}
      <Link to="/">Về trang tài khoản thật</Link>
    </aside>
    {children}
  </>
}

export default function ApplicationRoutes() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route path="/403" element={<ForbiddenPage />} />
      <Route path="/500" element={<ServerErrorPage />} />
      <Route path="/router-demo/*" element={<DemoScope><AppRouter /></DemoScope>} />
      <Route path="/demo/*" element={<ProtectedRoute><DemoScope><App /></DemoScope></ProtectedRoute>} />
      <Route path="/upload" element={<ProtectedRoute><UploadPage /></ProtectedRoute>} />
      <Route path="/clinic-upload" element={<ProtectedRoute><ClinicUploadPage /></ProtectedRoute>} />
      <Route path="/clinics/members" element={<ProtectedRoute><ClinicMemberPage /></ProtectedRoute>} />
      <Route path="/clinics/patients" element={<ProtectedRoute><ClinicPatientPage /></ProtectedRoute>} />
      <Route path="/batches" element={<ProtectedRoute><BatchProgressPage /></ProtectedRoute>} />
      <Route path="/batches/:batchId" element={<ProtectedRoute><BatchProgressPage /></ProtectedRoute>} />
      <Route path="/" element={<ProtectedRoute><SessionHomePage /></ProtectedRoute>} />
      <Route path="*" element={<NotFoundPage />} />
      <Route path="/analyses/:analysisId" element={<ProtectedRoute><AnalysisResultPage /></ProtectedRoute>} />
      <Route path="/notifications" element={<ProtectedRoute><NotificationInboxPage /></ProtectedRoute>}/>
      <Route path="/billing/orders/:orderId" element={<ProtectedRoute><BillingOrderPage /></ProtectedRoute>}/>
      <Route path="/doctor/workspace" element={<ProtectedRoute><DoctorWorkspacePage /></ProtectedRoute>} />
      <Route path="/campaigns" element={<ProtectedRoute><CampaignManagementPage /></ProtectedRoute>}/>
    </Routes>
  )
}
