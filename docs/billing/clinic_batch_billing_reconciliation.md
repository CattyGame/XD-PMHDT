# Clinic Batch Billing Reconciliation (SCRUM-370)

## Scope

Batch upload reserves one credit per accepted image through Analysis' `ICreditReservations` port. The Analysis client calls Billing's reservation endpoint; the reservation transaction decrements the wallet and writes a `RESERVE` ledger row atomically. A reservation rejected for insufficient funds does not create a reservation or debit.

The Worker owns AI execution and result persistence. Its project has no Billing API project reference or credit client. On a terminal outcome it commits the result and an `analysis.completed` or `analysis.failed` outbox event together. The durable outbox publisher delivers that event; Billing consumes it into its settlement inbox, resolves the terminal result through Analysis, and atomically applies `CAPTURE` or `RELEASE` with inbox completion. Worker code does not call a debit, capture, or refund API.

## Reconciliation rules

For one batch, count only images with an accepted reservation when comparing processing results to credits:

| Processing outcome | Reservation | Spendable balance | Ledger effect |
| --- | --- | --- | --- |
| Completed with valid result | `CAPTURED` | Already debited at reserve; no second debit | `CAPTURE` = 0 |
| Terminal failure / low quality | `RELEASED` | Reservation returned once | `RELEASE` = +1 |
| Retryable failure | `RESERVED` until a terminal event | Remains reserved | No terminal row yet |
| Reservation rejected | No reservation | Unchanged | No reservation ledger row |

The financial invariant is:

`successful analyzed images = captured reservations = net credits spent`

and, for each wallet:

`current balance = opening credits + sum(ledger amount)`

All observed `balance_after` values must be non-negative. A partial failure refunds each terminal failure exactly once. Idempotency and the reservation state transition guard prevent duplicate settlement and prevent one reservation from being both captured and released.

## Integration coverage

`BatchCreditReconciliationTests.Large_partial_failure_batch_never_overdraws_and_charges_only_successful_images` exercises the PostgreSQL Billing repository and settlement service with 100 concurrent reservation attempts against 80 available credits. It expects 80 accepted reservations, 20 insufficient-credit rejections, then 55 captures and 25 releases. It verifies that no balance or ledger balance is negative, that captures equal successful outcomes, and that the final wallet balance agrees with the ledger. The test creates and removes a uniquely identified wallet fixture.

The Worker boundary is also covered by the existing `JobProcessorTests`: terminal success/failure is committed through the Analysis repository with an outbox event. The durable RabbitMQ-to-Billing inbox and Analysis settlement resolver remain service-level integration points; this database test does not claim to launch the full multi-container message path.

## Run locally

Use an isolated PostgreSQL database with the Billing migrations applied. Set its connection string only for the current PowerShell session, then run:

```powershell
$env:AURA_SCRUM370_TEST_CONNECTION_STRING = "Host=localhost;Port=5433;Database=aura_dev;Username=aura_dev;Password=<your-local-password>"
dotnet test src/services/AURA.Billing.Api.Tests/AURA.Billing.Api.Tests.csproj --configuration Release --filter FullyQualifiedName~BatchCreditReconciliationTests
Remove-Item Env:AURA_SCRUM370_TEST_CONNECTION_STRING
```

Without the environment variable, this integration test is reported as skipped. Do not point it at a shared or production database: it inserts and deletes its own uniquely keyed TEST wallet and ledger rows.
