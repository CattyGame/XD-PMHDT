# AURA - AI-Driven Retinal Analysis System
> Dự án Xây dựng Phần mềm Hướng Đối tượng (XD-PMHDT)

## 📌 Phân công trách nhiệm các Module
* **M1**: Kiến trúc hệ thống, API Gateway, Docker Compose, Redis Cache, CI/CD.
* **M2**: Nghiệp vụ hệ thống, Đặc tả Use Case Doctor/Patient, ERD Database, Analysis REST Contract.
* **M3**: Đặc tả SRS, Test Plan, Thiết kế Profile/Billing, Quản trị Backlog & Jira.
* **M4 (Chuyên trách AI & Computer Vision)**:
  * Phân vùng mạch máu võng mạc (Retina Vessel Segmentation).
  * Trích xuất đặc trưng hình thái (Vessel Density, Tortuosity, AV-Ratio).
  * Tính toán chỉ số nguy cơ theo thuật toán Heuristic.
  * Triển khai dịch vụ FastAPI đóng gói Docker (`ai-service:8000`).
  * Xây dựng pipeline tích hợp dữ liệu camera giả lập với Apache NiFi.
* **M5**: Giao diện người dùng (UI/UX), Ứng dụng Frontend React TypeScript.

---

## 📑 Hợp đồng API AI Service (Module M4)
* **OpenAPI 3.0 YAML**: [`contracts/ai_service_openapi.yaml`](contracts/ai_service_openapi.yaml)
* **OpenAPI 3.0 JSON**: [`contracts/ai_service_openapi.json`](contracts/ai_service_openapi.json)
* **Tài liệu đặc tả hợp đồng**: [`docs/api/ai_service_contract.md`](docs/api/ai_service_contract.md)
* **Mô hình Pydantic Schemas**: [`ai_service/app/schemas/`](ai_service/app/schemas/)

---

## 🧪 Kiểm thử Hợp đồng Schemas
```bash
python tests/test_schemas.py
```