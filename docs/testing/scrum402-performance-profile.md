# SCRUM-402 query and pagination profile

## Run setup

- PostgreSQL 17.11 in a disposable local container.
- Synthetic dataset: 10,000 analysis requests and images, 2,000 distinct requester accounts, 20 clinics.
- Read load: 20 concurrent readers for 5 rounds (100 measurements per operation).
- Dates: 2026-11-01 through 2026-11-23 UTC.
- `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` captured for clinic summary, patient trends, and patient history with a stable `(created_at, id)` cursor.
- The run used seeded synthetic data and warm local caches; it is a repeatable acceptance profile, not a production-capacity guarantee.

## Observed results

| Operation | EXPLAIN execution time | Query/service p95 |
| --- | ---: | ---: |
| Clinic summary | 9.12 ms | Query 85.42 ms; service 25.90 ms |
| Patient trends (maximum 100 rows) | 0.27 ms | Query 16.42 ms; service 5.63 ms |
| Patient history (cursor, page size 3) | 0.13 ms | Query 4.93 ms |

Every measured query and service p95 was below the 3-second target. History pagination returned two non-overlapping pages in descending `(created_at, id)` order. Existing scope predicates keep tenant, clinic/owner, and patient identifiers in the database query. The analytics result is bounded to 100 rows; the summary returns aggregate counts rather than an unbounded row list.

The measured plan used existing indexes, including `ix_analysis_requests_owner`, `ix_analysis_requests_patient`, `ix_images_request`, `ix_results_image_version`, and `ix_reviews_image`. No additional index migration was justified by this profile.

## Service and UI timing boundary

Service p95 includes the Analysis service call and a local in-memory Profile access-authority fixture. It does not include network latency to a running Profile service. This checkout has no web client call to the Analytics routes (`/api/v1/analytics/clinics/{clinicId}/summary`, `/api/v1/admin/analytics/summary`, `/api/v1/doctors/me/summary`, or `/api/v1/analyses/patients/{patientId}/trends`), so an end-user UI load time for these routes cannot be measured here. UI timing is therefore not reported as a passing result.

## Reproduce

Set `AURA_SCRUM402_TEST_CONNECTION_STRING` to a dedicated disposable database initialized with the repository development migrations, then run:

```powershell
dotnet test src/services/AURA.Analysis.Api.Tests/AURA.Analysis.Api.Tests.csproj --no-restore --filter FullyQualifiedName~Scrum402AnalyticsPerformanceTests --logger 'console;verbosity=detailed'
```

The performance fixture creates and deletes its own tenant data. Do not point it at a shared or production database.
