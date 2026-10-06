# AURA AI Service (Module M4)
> Microservice phân tích ảnh mạch máu võng mạc và tính toán chỉ số nguy cơ theo thuật toán Heuristic.

---

## 🚀 Hướng dẫn khởi chạy

### 1. Khởi chạy bằng Docker & Docker Compose (Khuyên dùng)
Từ thư mục gốc dự án:
```bash
docker compose up --build -d
```
Service sẽ lắng nghe tại cổng `http://localhost:8000`.

Kiểm tra trạng thái container:
```bash
docker compose ps
docker compose logs -f ai-service
```

### 2. Khởi chạy trực tiếp bằng Python
Yêu cầu Python 3.10+:
```bash
cd ai_service
python -m venv .venv

# Trên Windows
.venv\Scripts\activate

# Cài đặt thư viện
pip install -r requirements.txt

# Khởi chạy service
uvicorn app.main:app --host 0.0.0.0 --port 8000 --reload
```

---

## 📡 Tài liệu API & Swagger UI
Sau khi khởi chạy, truy cập trực tiếp bằng trình duyệt:
* **Interactive Swagger UI**: [http://localhost:8000/docs](http://localhost:8000/docs)
* **ReDoc Documentation**: [http://localhost:8000/redoc](http://localhost:8000/redoc)

---

## 🧪 Ví dụ gọi API (Curl)

### 1. Healthcheck
```bash
curl -X GET http://localhost:8000/health
```

### 2. Phân tích ảnh võng mạc
```bash
curl -X POST http://localhost:8000/api/v1/analyze \
  -H "Content-Type: application/json" \
  -d '{
    "request_id": "req_demo_001",
    "patient_id": "PAT-12345",
    "eye_side": "right",
    "mode": "retina_vessels",
    "image_base64": "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==",
    "include_mask": true,
    "include_overlay": true
  }'
```
