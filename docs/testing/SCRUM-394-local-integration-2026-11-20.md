# SCRUM-394 live integration evidence — 2026-11-20

Branch `fix/SCRUM-394-consultation-chat`, integration base `develop@a0e5997`, merge `7dc643f`. Re-fetched origin after live verification; develop still points to a0e5997.

## Contract integration

The deployed SCRUM-391 hub accepts `JoinConversation(Guid conversationId)`. Changed the client and Playwright SignalR fixture to invoke the method with one UUID argument, rather than an application-command object. The canonical hub schema now distinguishes application DTOs from positional SignalR method arguments for JoinConversation/SendMessage/MarkRead.

Retained develop's shared authorized request signature (PUT support and extraHeaders); consultation creation passes Idempotency-Key through that interface. Read receipts require status READ and a positive version.

## Local environment

Vite 127.0.0.1:5173, Gateway 5000, Notification 5003, Firebase development project, aura_dev PostgreSQL, Redis backplane. Updated Notification from the merged backend source. Applied existing Notification migrations 005, 017 and 020. Added the missing Notification service identity using only the canonical allowlist from infra/service-auth/clients.example.json. Upgraded Profile with the existing registration script to add analysis:binding and configured its Analysis URL. Existing signing keys and client secrets were preserved; issuer registry was backed up outside the repo. No secret, token or local environment file is committed.

Created a dedicated synthetic PATIENT account and explicitly linked it to the existing synthetic clinic patient. This does not grant access to any real patient. The other participant is the previously created local DOCTOR with an active clinic membership and assignment. Analysis 38900000-2026-4000-8000-000000000101 remains labelled mock.

## Executed live checks

- Patient eligible-doctor endpoint: HTTP 200, exact assigned doctor returned. Consultation listing: HTTP 200.
- Patient creates conversation through REST, then repeats the same Idempotency-Key: same conversation, replay HTTP 200.
- Doctor normal UI login; conversation badge shows 1 unread. Opens stored history and reaches “Đang nhận tin trực tiếp” via the real SignalR hub.
- Patient sends a second message over REST after the doctor joined; it appears in the open doctor UI and badge increments to 2. HTML-looking content renders as literal text; the chat list contains no img element.
- Doctor marks read in the UI; unread badge clears. Patient REST history reports both own messages READ.
- Doctor sends a reply from the UI. Patient REST history contains it, then advances its read cursor with a valid READ/version=1 receipt. The doctor UI updates that reply to “Đã đọc”.
- Replay of a persisted patient message with the same clientMessageId and text: HTTP 200, identical messageId. Same clientMessageId with changed text: HTTP 409.
- With the doctor thread open and an in-memory draft present, revoked only the synthetic doctor assignment. The live hub signalled access change; UI displayed 403 and removed history, composer and draft.
- A doctor REST send after revoke: HTTP 403 ERR_ACCESS_REVOKED. SQL confirmed exactly 3 persisted messages and 3 unique send intents; denied send did not persist.
- Restored the assignment after the negative check; authorized history works again with the same three messages.

Screenshots of the two-way read receipt and revoked state were saved in the task workspace, outside the repo.

## Automated verification

- Full frontend Vitest: 55 files / 534 tests passed, zero skipped (173.29 seconds).
- Playwright mock suite: 9 passed, including same-message retry after lost acknowledgement, reconnect gap paging beyond 20 messages, duplicate broadcast suppression, escaped HTML, read/unread cursor, offline draft, revoke cleanup, eligible-doctor selection and stable create key.
- Production build and lint passed; existing repository warnings remain. Notification publish/build passed with existing obsolete API warnings.
- Canonical hub schema passes Draft 2020-12 schema validation.

## Acceptance limits

SCRUM-391 and its authority dependency SCRUM-390 are In Review in Jira, despite code being available on develop. Live checks establish two-account persistence, real hub delivery/read events, replay conflict and revoke cleanup. Lost-ack/offline/reconnect gap and duplicate-listener scenarios above are automated mock evidence, not a fully fault-injected live transport campaign. Socket closure within 5 seconds was not instrumented as a timing assertion. Keep these limits explicit during team review; do not claim clinical/production acceptance or close dependent issues.