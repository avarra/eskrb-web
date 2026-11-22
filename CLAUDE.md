# eSkrb

Backend API and staff ("doctor") dashboard for a post-discharge recovery health app, built for **Ada Hack 2026** (hackathon: favor speed and a working demo over polish). The patient-facing mobile app (Expo, TypeScript) lives elsewhere; we only owe it the REST contract.

**Mobile-facing upload guide: `docs/uploads.md` (keep in sync with the upload endpoints).** **Product spec and shared contract: `PLAN-backend.md`.** Read it before changing entities, endpoints, the protocol JSON or the triage rules. Do not duplicate it here; if code and plan disagree, flag it rather than silently picking one.

**Amendments that override `PLAN-backend.md`** (agreed with the mobile dev; the plan file still shows the old voice/transcript shape):
- **No audio, ever.** The backend must not accept, store or forward audio, and never calls Soniox with audio. Speech becomes text on the phone; the patient reviews/edits it and only the confirmed text is sent. The dashboard shows text and photos only.
- **Log shape:** `id, episodeId, type ("text" | "photo"), source ("typed" | "dictated"), text, mediaUrl (photos only), extracted{}, authoredBy, reviewedAt`. Dropped: type `voice` and the `transcript` field.
- **`POST /episodes/:id/logs`:** JSON `{ type: "text", source, text }` for text logs; **multipart only for a photo** (`image/*` only, reject everything else). Returns saved log + `flags[]` + `patientAction`. LLM extraction and the rules run on `text`; the flag `quote` is the patient's own confirmed words.
- Check-ins, timeline, messages, triage and review are unchanged, except that photos can be a check-in question type (below).

## Deployment reality
**The dev instance IS the deployment** (the hackathon demo, reached by real phones and the dashboard), and it is **open to the world**: `launchSettings.json` binds `0.0.0.0:5000`, and the Vite dev server serves `eskrb.home.sync.si`. There is no separate staging/prod. So:
- Treat every endpoint as publicly reachable. All data is fake/seeded (never put real patient data in), but it is still health-themed and the LLM key behind it costs money.
- Everything except `/auth/*` and the Swagger UI/OpenAPI doc requires a bearer token. No unauthenticated data endpoints, no public static media.
- Pre-seeded tokens must be **high-entropy random values supplied via env/user-secrets**, never committed to git and never guessable (no `token-patient-1`). Share them with the mobile dev out of band. The seeder reads them from config; if unset, generate and log once.
- `POST /auth/demo-login` is public by nature (it must be, for the demo). It may only issue tokens for seeded demo users, and never for staff unless explicitly intended. Keep the `Auth:DemoLoginEnabled` kill switch.
- Protect the paid/abusable paths (LLM extraction, summary, uploads): enforce request size limits on uploads, image-only media types (`image/*`), and basic rate limiting (ASP.NET `AddRateLimiter`) so strangers can't burn the LLM budget.
- OpenAPI `servers` is the relative `"/"` (Program.cs), so Swagger "Try it out" follows the page origin (https behind a TLS proxy). Swagger UI is served at `/` (redirects to `/index.html`) and the OpenAPI JSON at `/openapi/v1.json`, in all environments and **unauthenticated**, by design (the mobile dev uses it as the contract). It documents the bearer scheme, so use Authorize with a token to try endpoints. There is no other `/` endpoint.
- CORS: allow only the dashboard origin(s), not `*`.
- Back up nothing important: the DB and media are disposable and reseedable, but a redeploy must not silently wipe the demo mid-pitch (use a persistent volume/dir for SQLite and media, and avoid reseeding over existing data).

## Goal (v0)
One loop, one scenario (knee replacement): patient logs on phone -> backend flags it -> staff see it in the triage queue and respond -> patient sees the response. Anything outside that loop is "next round" (see the end of the plan).

## Layout
- `api/` - ASP.NET Core Web API, .NET 10, controllers (not minimal APIs). Namespace `Eskrb.Api`.
- `web/` - Vue 3 + TypeScript + Pinia + Vue Router, Vite. Alias `@` -> `web/src`.
- `Eskrb.slnx` - solution (api only).

## Commands
- API: `dotnet run --project api` (http://0.0.0.0:5000, Development)
- Web: `cd web && npm run dev`
- Web checks: `npm run type-check`, `npm run lint`, `npm run format`, `npm run build`
- API tests: `dotnet test` (xUnit, `tests/Eskrb.Api.Tests`): unit tests (rules, validator, quotes, image sniffer, episode status) plus in-process integration tests (`ApiTests`: real app + temp DB + fake extractor; classes share `[Collection("api")]` because the fixture sets process env vars)
- Requires Node `^22.18 || >=24.12`.

## Architecture (API)
Single project, layered by folder (no multi-project Clean Architecture):
- `Controllers/` - thin: HTTP + DTOs only.
- `Domain/` - entities and enums (User, AuthToken, Protocol, DoctorGroup, DoctorGroupMember, Episode, CheckIn, Flag, Log, Message).
- `Rules/` - `RulesEngine`: **pure function** (answers + protocol rules -> flags). No I/O, no dependencies. Unit-test it.
- `Services/` - CheckIn, Log (pipeline orchestration), Triage, Summary.
- `Integrations/` - `IExtractor` (LLM extraction: `OpenAiExtractor` when `OpenAI:ApiKey` is configured, else `NullExtractor`, so text notes are stored but raise no flags) and `ISttKeyIssuer` (Soniox temp keys). Always behind interfaces so they can be faked in tests and as a demo fallback. No audio handling anywhere.
- `Data/` - EF Core DbContext, migrations, idempotent seeder.
- `Auth/` - token issuing and validation.

Pipeline for a log: (photo: validate `image/*` + save via `IMediaStore`) -> for text logs, LLM extracts `{questionId: value}` plus exact quote from `text` -> RulesEngine -> persist flags -> return `patientAction`. Photos carry no text, so they produce no extraction or flags in v0. Runs synchronously so flags come back in the response.

### Hard rules
- **The LLM never decides flags.** It only fills in field values and quotes; `RulesEngine` decides. Do not add LLM-based flagging.
- `patientAction` is derived from max flag severity (`urgent` -> `call_112`, `attention` -> `contact_team`, else `none`). Mobile picks its result screen from this field alone.
- Protocols (questions + rules) are JSON data in the DB, not code.
- `authoredBy` goes on every check-in and log (future caretakers, no migration).
- Patient-facing copy (question text, etc.) is **Slovenian**. Code, field names and the dashboard are English.
- **No audio on the server**, see the amendments above. dictation streams phone -> Soniox with a temp key from us.

### Soniox temporary keys (implemented; needs the key configured)
The backend's only STT job is issuing short-lived keys; audio streams phone -> Soniox EU and never touches us.
- `POST /stt/temporary-key` (patient, active episode else 404; rate limited 6/min per patient, then 429; no body) -> `SonioxKeyIssuer` calls `POST {Soniox:BaseUrl}/v1/auth/temporary-api-key` (default `https://api.soniox.com`, non-EU; set `Soniox:BaseUrl=https://api.eu.soniox.com` for an EU key) with `Authorization: Bearer <Soniox:ApiKey>` and `{ usage_type: "transcribe_websocket", expires_in_seconds: 900, single_use: true, max_session_duration_seconds: 900 (both = `Soniox:KeyTtlSeconds`, default 900, max 3600), client_reference_id: "<episodeId>" }` (never a name) -> returns `{ apiKey, expiresAt }`. 503 if no key is configured, 502 if Soniox fails.
- **The long-lived key goes in user-secrets, NOT in `reseed.sh`** (it's a paid key; reseed.sh is committed): `dotnet user-secrets set Soniox:ApiKey <key> --project api` (or env `Soniox__ApiKey`), then restart the API. Reseed never touches it. The key needs the "Temporary API keys" + `transcribe_websocket` permissions in the Soniox Console.
- Never log keys or bodies: only episode id, time and success/failure. Do **not** use Soniox's async/file API (it stores audio and transcripts for 30 days).
- Before go-live: EU data residency approved in the Soniox Console and a signed DPA (transcripts are health data under GDPR).
- Verified against the real API (2026-11-21): response is `{ api_key, expires_at }`. The configured key is a non-EU key, so the default is non-EU. Region = data residency (health data): before go-live move to an EU key + `Soniox:BaseUrl` override; the phone must open its websocket in the same region as the key.

### Persistence: swappable by design
- **DB: SQLite + EF Core** for v0. Use JSON columns for protocol, `answers`, `extracted`. **Avoid SQLite-specific SQL/features** so swapping to Postgres "one day" is a provider change + regenerated migrations. Seed idempotently on startup (demo data is the seed: 4 patients with different histories, 1 staff user). Reseeding = delete the DB file.
- **Media: local directory** (git-ignored, configured path) behind `IMediaStore` (`Save` / `Open` / `GetUrl`). The DB stores a storage **key**, never a filesystem path. Files are served through an **authorized** endpoint (e.g. `GET /media/{id}`), never as public static files (wound photos). `mediaUrl` in the contract is an opaque URL clients just fetch, so a later S3 implementation (presigned PUT/GET) is non-breaking. Don't leak disk assumptions outside the `IMediaStore` implementation.
- Never reference concrete DB/storage providers from controllers or domain code.

### Auth: demo now, real later
- Standard ASP.NET bearer authentication + `[Authorize]` with role policies (`patient`, `staff`, `admin`, later `caretaker`). Controllers don't know how a token was issued.
- Tokens are **opaque random strings, stored hashed** in an `AuthToken` table (`userId`, `tokenHash`, `createdAt`, `expiresAt`, `revokedAt`, `label`). Long-lived and revocable; supports adding refresh/rotation later without client changes.
- `POST /auth/demo-login` issues a token for a seeded user id; gated by config flag `Auth:DemoLoginEnabled`.
- The seeder creates **pre-seeded fixed tokens** per seeded user (from env/user-secrets config, never committed) so mobile/dashboard devs can hardcode them. Real login (SI-PASS/passwords) later = another endpoint issuing the same token type.

### Ownership model (doctor groups)
- **Protocols are global; episodes are local.** `Episode` = `patientId` + `protocolId` + `doctorGroupId` (+ start/end dates). A `DoctorGroup` is a hospital/wing-local team that owns episodes.
- `DoctorGroupMember` links staff to a group dynamically (add/remove rows, no episode changes). `Level` drives escalation (1 = first responder, higher = escalated to); `Title` is display text; `ActiveFrom`/`ActiveUntil` (null = open) make a membership time-boxed, which also covers temporary substitutes. A user can be in several groups.
- Staff access rule (to enforce when staff endpoints exist): a staff user sees/acts on an episode only if they hold an **active** membership in its `doctorGroupId`. Escalation = notify the lowest active level with unresolved flags, then step up. Not implemented yet.
- Protocols are seeded from `api/Data/Seed/*.json` and **overwritten on every start** (edit file + restart). Groups, memberships and episodes are created only if missing, never overwritten. Seeded: Wing A (ana, marko) and Wing B (nina), doctor is level 1 in both; episode start dates are relative to the seeding day (days 20, 10, 30). Only one staff user exists, so escalation levels are modelled but not exercised.
- Schema: `EnsureCreated` builds a fresh DB; on every start `Seeder.AddMissingTablesAsync` also creates any table (and its indexes) the model has but the DB lacks, so **new entities need no reseed and no data is lost**. It does NOT add columns to existing tables or change them: after such a change run `./reseed.sh` (until EF migrations exist; see `APITODO.md`).
- `GET /me/episode` (patient only) returns the ACTIVE episode (404 if none; ended/upcoming episodes are not returned), full protocol and `dayNumber` (computed in Europe/Ljubljana via `Services/AppTime`). `checkInDoneToday` comes with check-ins.

### List endpoints and visibility
- `Services/Visibility.cs` is the single place for scoping. **Admin sees everything; staff see only groups where they hold an active membership, and the episodes/patients in those groups.** Reuse `VisibleGroups` / `VisibleEpisodes` for every new staff-facing query; never query `Episodes` directly in a staff endpoint.
- Read-only, staff+admin (`DirectoryController`): `GET /doctor-groups` (with members), `/protocols` (global; episode counts are scoped), `/protocols/{id}` (full questions + rules + the visible episodes on it), `/patients`, `/episodes?protocolId=&groupId=&employeeId=&patientId=` (one filtered list covers by-protocol, by-group, by-employee, by-patient; `employeeId` = episodes of that employee's active groups; staff may only pass their own id, else 403). Admin only: `GET /staff` (employees + memberships, feeds the employee filter) and `UsersController`: `GET /users[?role=]` and `GET /users/{id}` (per-user admin view: patients get their episodes/protocols, staff/admin get their group memberships).
- Dashboard views for these live in `web/src/views/{Episodes,Patients,Groups,Protocols,ProtocolDetail,Users,UserDetail}View.vue` (Users/UserDetail are admin-only) (plain tables; prototype tier).
- GUIDs are stored as **uppercase** text in SQLite; when poking the DB with `sqlite3`, compare with `upper(...)`.

### Write endpoints (admin = all; staff = scoped via `Visibility`)
- Episode lists carry `status` = `upcoming | active | ended` (computed server-side by `Services/EpisodeStatus`; active = start..end inclusive).
- Episodes (`EpisodesController`): `POST /episodes` (admin; end = start + protocol duration; 409 if it overlaps another episode of the same patient), `PATCH /episodes/{id}` (start/end dates), `POST /episodes/{id}/end` (end date = yesterday, clamped to the start date, so it is inactive immediately), `PUT /episodes/{id}/group` (switch doctor group; staff may only target groups they are active in). Staff get 404 for episodes outside their groups.
- Groups (`GroupsController`, admin): `POST /doctor-groups`, `DELETE /doctor-groups/{id}` (409 while episodes remain; deletes memberships), `POST /doctor-groups/{id}/members` (staff users only; one membership per user per group, 409 on duplicate), `DELETE /doctor-groups/{id}/members/{userId}`.
- Errors are RFC 9457 problem details with a human-readable `detail` and a `traceId`, for EVERY failure: controller errors, model-binding errors (flattened into `detail`), and bodyless 401/403/404/415 and unhandled 500s (`AddProblemDetails` + `UseStatusCodePages` + `UseExceptionHandler` in `Program.cs`). Use `Problem(detail: ...)` in controllers; the web `errorMessage()` helper unwraps `detail`.
- Remaining planned write actions live in `APITODO.md`. **Update it when you finish or add items.**

### Check-ins
- `POST /episodes/{id}/checkins` (patient, own episode only; 404 otherwise). Body `{ answers: { <questionId>: value } }`. `AnswerValidator` checks answers against the protocol (unknown ids, types, scale bounds, choice options; unanswered questions are allowed, empty is not). `RulesEngine` (pure, unit-tested) raises `Flag`s; the response is `{ id, date, answers, flags[], patientAction }`.
- **Photo questions:** a protocol question may have `type: "photo"` (and any question may set `required: true`; a check-in missing a required answer is 400). Photo answers can't go in JSON: send the check-in as multipart with a text field `answers` (JSON object of the other answers) and one image file part per photo question, part name = question id (one file each, max 10 MB, sniffed like log photos, validated before anything is stored). Each photo is stored as a photo Log; the check-in's `answers[questionId]` is its `/media/<logId>` URL (fetch with the bearer token). The timeline shows such photos inside the check-in, not as loose notes. A photo-only check-in is allowed. Seeded `knee-replacement` has an optional `woundPhoto`.
- Decisions: several check-ins per day are allowed (so the demo can repeat); `date` is set by the server (Europe/Ljubljana), never the client; the episode must be active today (else 409); flag `quote` for a check-in is `"<questionId>: <answer>"`.
- `GET /me/episode` now includes `checkInDoneToday`.

### Notes (logs), replies (messages) and the timeline
- **No audio, ever.** `POST /episodes/{id}/logs` (patient, own active episode, else 404/409): JSON `{type:"text", source:"typed"|"dictated", text}` (max 4000 chars) or multipart with one or more file parts (any part name, max 5 per request, all-or-nothing; each becomes its own photo log; response has `log` = first and `logs[]` = all). Photos are identified by their first bytes (JPEG/PNG/WebP/HEIC/AVIF), never by the client's Content-Type; max 10 MB; anything else (incl. audio) is 400.
- **LLM provider: OpenAI, plain text-based extraction (no Decisions API; decided).** `Integrations/OpenAiExtractor` calls the Responses API (`POST {OpenAI:BaseUrl}/responses`, raw HttpClient) with strict JSON-schema output: one nullable `{value, quote}` object per non-photo question, `store: false` (OpenAI must not keep patient text), `reasoning.effort` low. Config (user-secrets / env, NEVER reseed.sh or git): `OpenAI:ApiKey` (unset = NullExtractor), `OpenAI:Model` (default `gpt-6-luna`, the cheap tier; whether it supports json_schema is unverified: if requests fail, try `gpt-6.1-terra` or `gpt-6.2-sol`), `OpenAI:Effort`, `OpenAI:BaseUrl` (default `https://api.openai.com/v1`; EU residency = an EU-region project key + `https://eu.api.openai.com/v1`, needs eligibility via OpenAI sales, +10% price). Prompt tells the model to fill only what is clearly stated, copy quotes verbatim and treat the note as untrusted. The text-log endpoint is rate limited per patient (`llm` policy, 20/min). Verified live against the real API (gpt-6-luna, ~5 s per note). Sandbox: `POST /demo/extract` `{text, protocolId?}` (staff/admin, `llm` rate limit, stores nothing) returns extracted values, quotes, flags and `patientAction`. Still no Slovenian eval set.
- Text logs: `IExtractor` proposes `{questionId: value + quote}`; each value is validated by `AnswerValidator.CheckAnswer` (invalid/unknown ones are dropped), `RulesEngine` raises flags, and the flag `quote` must be a substring of the patient's text (`Rules/Quotes.Resolve`, else the whole text). If the extractor throws, the note is still saved with `extractionFailed: true` and no flags. Photos are stored and shown, never analysed.
- Photos live behind `IMediaStore` (`DiskMediaStore`, dir `Media:Path`, default `data/media` next to the DB; the DB stores only an opaque key). `GET /media/{logId}` is authorized (owner, or staff/admin who can see the episode; else 404) and `nosniff`. `mediaUrl` is `/media/{logId}`; the web app fetches it as a blob with the bearer token (`AuthImage.vue`).
- `POST /episodes/{id}/messages` `{body}` (max 2000): staff/admin on episodes they can see, or the patient on their own episode while it is active (409 otherwise). Staff can message on ended episodes.
- `GET /episodes/{id}/timeline?limit=` (patient: own; staff/admin: visible): check-ins, logs and messages merged, newest first. **Staff additionally get `flags` and `extracted`; patients get `null` for both.**
- Dashboard: `EpisodeDetailView` (`/episodes/:id`, click a patient name) shows the timeline (polls every 10 s) and a reply box.
- **Triage + review (`StaffController`, staff/admin, scoped by `Visibility`):** `GET /staff/triage` returns `{needsAttention, missedCheckIn, awaitingPatientReply, stable, generatedAt}` (every ACTIVE visible episode in exactly one column, first match wins; pure logic in `Rules/Triage.cs`; missed = no check-in today and (after `Triage:MissedCutoffHour`, default 20, Europe/Ljubljana, or yesterday also missed)). Cards: patient, group, protocol, dayNumber, `topFlag`, `openFlags`, `missedCheckIn`, `lastCheckInDate`, `lastMessageAt/FromStaff`. `POST /staff/episodes/{id}/review` marks all unreviewed check-ins/logs reviewed and resolves open flags (404 outside the caller's groups); the timeline's `reviewed` reflects it.
- **Patient unread:** `/me/episode` has `unreadMessages` (staff messages newer than the patient's last read); `POST /episodes/{id}/read` (patient) marks them read. State lives in the new `EpisodeReadState` table (created automatically). No push yet: the phone polls `/me/episode`.
- **Seeded demo history** (only for episodes created by the seeder, so it appears after `./reseed.sh` or for new seed episodes): ana stable, marko urgent unresolved flags + note, nina stopped checking in 3 days ago, luka (4th patient, Wing B) awaiting reply to a staff message. Flags come from the real rules engine. Notifications (push) not built, see `APITODO.md`.

### Episode notes and escalation
- **Notes** (`NotesController`): `GET/POST /episodes/{id}/notes`, `PATCH/DELETE .../{noteId}` (writes staff/admin on visible episodes). Kinds: `medication` (instructions; patient-visible), `advice` (patient-visible), `private` (staff only: the patient endpoints never return it, tested). `/me/episode` carries the patient-visible `notes`. **Medication notes sync into the check-in:** `Services/EpisodeProtocol.WithMedication` adds a boolean question `medsTaken` ("Ali ste danes vzeli zdravila...?", `skipExtraction`) and a rule `meds-missed` (false -> attention flag) to the protocol as seen by that episode (a copy; stored protocol untouched). Used by `/me/episode` and check-in validation/rules. Dashboard: `EpisodeNotes.vue` on the episode page, quick "Add note" on triage cards (`NoteForm.vue`).
- **Escalation** (`EscalationsController`): `POST /episodes/{id}/escalations {sourceType: checkin|log|message, sourceId, comment?}` hands an item to the lowest ACTIVE group level above the caller's (409 if nobody above or caller has no membership, e.g. admin). Open escalations put the episode in "needs attention" (card has `escalation`) for staff at that level or higher (and admin), not for the sender. `GET /episodes/{id}/escalations`, `POST .../{escalationId}/resolve` (level >= target or admin); `POST /staff/episodes/{id}/review` also resolves those addressed to the reviewer. Seeded `senior` (Dr. Peter Zorko) is level 2 in both wings; `doctor` is level 1.

### Auth: current state
- Implemented: `User` + `AuthToken` entities, `BearerTokenHandler` (hash lookup, revoked/expired checks), `GET /me`, idempotent `Seeder` (`EnsureCreated`, no migrations yet; switch to migrations before any schema needs to survive a deploy).
- Seeded users (fixed ids): patients `ana`, `marko`, `nina`, `luka`; staff `doctor` (Dr. Maja Zupan, level 1) and `senior` (Dr. Peter Zorko, level 2); `admin` (role `admin`, member of no group). Token plaintexts come from config `Seed:Tokens:<key>` (env `Seed__Tokens__doctor=...` or user-secrets). Unset -> generated and logged once at first creation (only the hash is stored).
- Not yet: `POST /auth/demo-login`, rate limiting. Role claims are lowercase (`[Authorize(Roles = "staff,admin")]`).
- SQLite file defaults to `api/data/eskrb.db` (git-ignored); override with `ConnectionStrings__Default`.
- When testing locally, the live instance owns :5000 and `launchSettings.json` overrides `ASPNETCORE_URLS`: run with `--no-launch-profile` on another port and a throwaway DB, and never `pkill` by process name.

## Dashboard (web)
- **Design language:** the mobile one adapted to a dense desktop tool (tokens in `web/src/assets/base.css`): blue `#1652C4` ground rail + white sheet, Lexend, pill buttons, one job per colour (coral = pain only, yellow = unreviewed/today dot only, green = reviewed only, red = urgent only, blue = brand and the "attention" flag). Keep those colour jobs when adding UI.
- Auth: `src/services/api.ts` (fetch wrapper: base `/api`, bearer from localStorage, global 401 -> logout), `src/services/auth.ts` (`getMe` etc.), `src/stores/auth.ts` (Pinia: `restore`, `login(token)`, `logout`), `LoginView` (paste token), router guard redirects unauthenticated users to `/login`.
- Two main views: triage board (`/triage`, four columns) and patient profile (`/episodes/:id`).
- Typed API client generated from the API's OpenAPI document; one Pinia store per area (auth, triage, patient).
- Poll every few seconds for freshness; no websockets in v0.
- Dev server allows host `eskrb.home.sync.si` (see `vite.config.ts`).

## Cross-cutting
- OpenAPI (`Microsoft.AspNetCore.OpenApi`) is the contract artifact shared with the mobile dev; keep DTOs stable and tell the user when a contract change affects mobile.
- CORS: the dashboard calls the API same-origin through the Vite proxy (`/api/*` -> `API_URL`, default `http://localhost:5000`, prefix stripped), so no CORS is configured. If the dashboard is ever served from a different origin, allow only that origin (see "Deployment reality").
- Request logging: `UseHttpLogging` logs method, path, status and duration only (configured in `Program.cs`, level in `appsettings.json`). **Never add headers, query strings or bodies to it**: they carry bearer tokens and patient text.
- Secrets (LLM key, later Soniox key) via user-secrets / env vars, never committed.
- Tests: prioritize `RulesEngine` and triage-column computation (pure, cheap, carry the product logic). Don't over-invest elsewhere.
- Triage column logic (needs attention / missed check-in / awaiting patient reply / stable) is specified in `PLAN-backend.md`; the "missed check-in" cutoff hour is config, and "today" must be computed consistently (decide on one timezone, Europe/Ljubljana, and stick to it).

## Running the live API
Claude owns the process on :5000 (the live demo): after API changes, `dotnet build`, restart it (`dotnet run --project api`, background task; stop by PID, never `pkill` by name) and smoke-test `/me`, `/episodes`, `/protocols`, `/doctor-groups`, `/users` plus `localhost:5173/api/me`. New tables are created automatically on restart. A new/changed COLUMN needs `./reseed.sh` first (stop the API, reseed, start); warn before wiping data.

## Reseeding
`./reseed.sh [--run] [--force]` backs up (keeps last 3, in `api/data/backups/`) and removes the SQLite file; the API rebuilds the schema and reseeds on next start. It refuses if something listens on :5000 unless `--force`. The **stable login tokens are written in `reseed.sh` itself** (`TOKENS` table, by user's seed key); each run copies them into `dotnet user-secrets` (`Seed:Tokens:<key>`) so any way of starting the API in Development accepts them (`Seed__Tokens__<key>` env vars override). To change a token, edit the script. When adding a seeded user, add its key and a new token to `TOKENS` in `reseed.sh`. These tokens are committed credentials for the public instance (fake data only); don't reuse them for anything real. **Keep it in sync:** whenever you add persisted state (a new data directory such as media storage, another DB file, new required seed config/env vars), update the "persisted state" list and the cleanup in `reseed.sh` in the same change, and mention it in your summary.

## Working agreements
- Hackathon pace: smallest thing that works; no speculative abstraction beyond the seams listed above (`IMediaStore`, LLM/STT interfaces, token auth).
- Build order follows the plan: scaffold+seed -> demo auth -> check-ins+rules -> logs -> triage endpoint -> dashboard queue -> profile -> actions. The "reachable URL" step is already done (see "Deployment reality").
- Clinical thresholds in the protocol are placeholders pending clinical review; don't treat them as authoritative.
- Don't commit unless asked.
