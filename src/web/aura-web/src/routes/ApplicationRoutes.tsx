import { lazy, Suspense, type ComponentType, type ReactNode } from 'react'
import { Link, Route, Routes } from 'react-router-dom'
import LoginPage from '../pages/LoginPage'
import SessionHomePage from '../pages/SessionHomePage'
import ForbiddenPage from '../pages/ForbiddenPage'
import NotFoundPage from '../pages/NotFoundPage'
import ServerErrorPage from '../pages/ServerErrorPage'
import ProtectedRoute from './ProtectedRoute'
import ClinicUploadNavigation from './ClinicUploadNavigation'
import NotificationInboxPage from '../pages/NotificationInboxPage'
import CampaignManagementPage from '../pages/CampaignManagementPage'
import ClinicMemberPage from '../pages/ClinicMemberPage'
import ClinicPatientPage from '../pages/ClinicPatientPage'
import PatientHistoryPage from '../pages/PatientHistoryPage'
import ClinicInvitationPage from '../pages/ClinicInvitationPage'
import BillingOrderPage from '../pages/BillingOrderPage'
import FcmToastHost from '../components/FcmToastHost'
import AdminPortalPage from '../pages/portal/AdminPortalPage'
import OwnerViewsPage from '../pages/portal/OwnerViewsPage'
import ProfilePage from '../pages/ProfilePage'

function RouteLoadError() {
  return <main>
    <p role="alert">Không thể tải trang AURA. Kiểm tra kết nối rồi tải lại.</p>
    <button type="button" onClick={() => window.location.reload()}>Tải lại trang</button>
  </main>
}

function lazyPage(load: () => Promise<{ default: ComponentType }>) {
  return lazy(() => load().catch(() => ({ default: RouteLoadError })))
}

const App = lazyPage(() => import('../App'))
const AppRouter = lazyPage(() => import('./AppRouter'))
const UploadPage = lazyPage(() => import('../pages/UploadPage'))
const BatchProgressPage = lazyPage(() => import('../pages/BatchProgressPage'))
const AnalysisResultPage = lazyPage(() => import('../pages/AnalysisResultPage'))
const ClinicUploadPage = lazyPage(() => import('../pages/ClinicUploadPage'))
const DoctorWorkspacePage = lazyPage(() => import('../pages/doctor/DoctorWorkspacePage'))
const BillingPage = lazyPage(() => import('../pages/billing/BillingPage'))
const PaymentPage = lazyPage(() => import('../pages/billing/PaymentPage'))
const PaymentSuccessPage = lazyPage(() => import('../pages/billing/PaymentSuccessPage'))
const AdminManagementPage = lazyPage(() => import('../pages/AdminManagementPage'))

const ConsultationsPage = lazyPage(() => import('../pages/consultations/ConsultationsPage'))

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
    <>
    <FcmToastHost />
    <Suspense fallback={<p role="status">Đang tải trang AURA...</p>}>
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route path="/403" element={<ForbiddenPage />} />
      <Route path="/500" element={<ServerErrorPage />} />
      <Route path="/router-demo/*" element={<DemoScope><AppRouter /></DemoScope>} />
      <Route path="/demo/*" element={<ProtectedRoute><DemoScope><App /></DemoScope></ProtectedRoute>} />
      <Route path="/upload" element={<ProtectedRoute><UploadPage /></ProtectedRoute>} />
      <Route element={<ProtectedRoute><ClinicUploadNavigation /></ProtectedRoute>}>
        <Route path="/clinic-upload" element={<ClinicUploadPage />} />
        <Route path="/clinics/patients" element={<ClinicPatientPage />} />
      </Route>
      <Route path="/clinics/members" element={<ProtectedRoute><ClinicMemberPage /></ProtectedRoute>} />
      <Route path="/clinics/patients/:patientId/history" element={<ProtectedRoute><PatientHistoryPage /></ProtectedRoute>} />
      <Route path="/clinics/invitations/:membershipId?" element={<ProtectedRoute><ClinicInvitationPage /></ProtectedRoute>} />
      <Route path="/batches" element={<ProtectedRoute><BatchProgressPage /></ProtectedRoute>} />
      <Route path="/batches/:batchId" element={<ProtectedRoute><BatchProgressPage /></ProtectedRoute>} />
      <Route path="/" element={<ProtectedRoute><SessionHomePage /></ProtectedRoute>} />
      <Route path="*" element={<NotFoundPage />} />
      <Route path="/analyses/:analysisId" element={<ProtectedRoute><AnalysisResultPage /></ProtectedRoute>} />
      <Route path="/notifications" element={<ProtectedRoute><NotificationInboxPage /></ProtectedRoute>}/>
      <Route path="/consultations" element={<ProtectedRoute><ConsultationsPage /></ProtectedRoute>} />
      <Route path="/consultations/:conversationId" element={<ProtectedRoute><ConsultationsPage /></ProtectedRoute>} />
      <Route path="/billing/orders/:orderId" element={<ProtectedRoute><BillingOrderPage /></ProtectedRoute>}/>
      <Route path="/billing/packages" element={<ProtectedRoute><BillingPage /></ProtectedRoute>} />
      <Route path="/billing/history" element={<ProtectedRoute><OwnerViewsPage /></ProtectedRoute>} />
      <Route path="/clinics/analytics" element={<ProtectedRoute><OwnerViewsPage mode="clinic" /></ProtectedRoute>} />
      <Route path="/doctor/activity" element={<ProtectedRoute><OwnerViewsPage mode="doctor" /></ProtectedRoute>} />
      <Route path="/admin/:section" element={<ProtectedRoute><AdminPortalPage /></ProtectedRoute>} />
      <Route path="/billing/pay/:orderId" element={<ProtectedRoute><PaymentPage /></ProtectedRoute>} />
      <Route path="/billing/success/:orderId" element={<ProtectedRoute><PaymentSuccessPage /></ProtectedRoute>} />
      <Route path="/doctor/workspace" element={<ProtectedRoute><DoctorWorkspacePage /></ProtectedRoute>} />
      <Route path="/campaigns" element={<ProtectedRoute><CampaignManagementPage /></ProtectedRoute>}/>
      <Route path="/profile" element={<ProtectedRoute><ProfilePage /></ProtectedRoute>} />
      <Route path="/admin" element={<ProtectedRoute><AdminManagementPage /></ProtectedRoute>} />
    </Routes>
    </Suspense>
    </>
  )
}
