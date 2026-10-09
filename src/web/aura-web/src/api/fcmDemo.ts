
export type FcmDemoScenario =
  | 'success'
  | 'unauthorized'
  | 'forbidden'
  | 'server-error'

export async function registerFcmDemo(
  scenario: FcmDemoScenario
): Promise<string> {
  const response = await fetch(
    `/api/v1/demo/fcm-token?scenario=${scenario}`,
    {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({
        demo: true,
      }),
    }
  )

  if (response.status === 401) {
    return '401 - Phiên đăng nhập không hợp lệ.'
  }

  if (response.status === 403) {
    return '403 - Không đủ quyền đăng ký thông báo.'
  }

  if (!response.ok) {
    return `${response.status} - Không thể đăng ký thông báo.`
  }

  return '200 - Đăng ký thông báo demo thành công.'
}
