
import { http, HttpResponse } from 'msw'

// Endpoint chỉ dùng cho demo SCRUM-296.
const demoEndpoint = '/api/v1/demo/fcm-token'

export const fcmHandlers = [
  http.post(demoEndpoint, async ({ request }) => {
    const scenario = new URL(request.url).searchParams.get('scenario')

    if (scenario === 'unauthorized') {
      return HttpResponse.json(
        { message: 'Phiên đăng nhập không hợp lệ.' },
        { status: 401 }
      )
    }

    if (scenario === 'forbidden') {
      return HttpResponse.json(
        { message: 'Không đủ quyền đăng ký thông báo.' },
        { status: 403 }
      )
    }

    if (scenario === 'server-error') {
      return HttpResponse.json(
        { message: 'Dịch vụ thông báo tạm thời không khả dụng.' },
        { status: 500 }
      )
    }

    // Không lưu hoặc trả lại FCM token trong demo.
    return HttpResponse.json(
      {
        registered: true,
        message: 'Đăng ký thông báo demo thành công.',
      },
      { status: 200 }
    )
  }),
]
