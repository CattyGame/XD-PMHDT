
import { http, HttpResponse, delay } from 'msw'

import queued from './fixtures/queued.json'
import processing from './fixtures/processing.json'
import completed from './fixtures/completed.json'
import failed from './fixtures/failed.json'
import qualityReject from './fixtures/quality-reject.json'
import error400 from './fixtures/error-400.json'
import error401 from './fixtures/error-401.json'
import error403 from './fixtures/error-403.json'

// Chọn kịch bản demo bằng query parameter.
// Ví dụ: /api/v1/analysis/123?scenario=failed

export const handlers = [
  // Tạo yêu cầu phân tích
  
http.post('/api/v1/analysis', async ({ request }) => {
  const url = new URL(request.url)
  const scenario = url.searchParams.get('scenario')

  if (scenario === 'insufficient-credit') {
    return HttpResponse.json(
      {
        code: 'INSUFFICIENT_CREDIT',
        message: 'Not enough credit to create analysis',
      },
      {
        status: 409,
      }
    )
  }

  await delay(500)

  return HttpResponse.json(queued, {
    status: 202,
  })
}),


  // Lấy trạng thái phân tích
  http.get('/api/v1/analysis/:analysisId', async ({ request }) => {
    const url = new URL(request.url)
    const scenario = url.searchParams.get('scenario')
    
    
if (scenario === '401') {
  return HttpResponse.json(error401, {
    status: 401,
  })
}

if (scenario === '403') {
  return HttpResponse.json(error403, {
    status: 403,
  })
}

if (scenario === 'validation') {
  return HttpResponse.json(error400, {
    status: 400,
  })
}



    await delay(500)

    switch (scenario) {
      case 'processing':
        return HttpResponse.json(processing)

      case 'completed':
        return HttpResponse.json(completed)

      case 'failed':
        return HttpResponse.json(failed)

      case 'quality-reject':
        return HttpResponse.json(qualityReject)

      case 'timeout':
        await delay(10000)
        return HttpResponse.json(processing)

      default:
        return HttpResponse.json(processing)
    }
  }),

  // Demo danh sách trống
  // Chỉ dùng để kiểm thử, chưa phải endpoint contract chính thức
  http.get('/api/v1/demo/empty-list', () => {
    return HttpResponse.json([], {
      status: 200,
    })
  }),
]
