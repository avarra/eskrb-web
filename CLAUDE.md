# eSkrb

Backend API and staff ("doctor") dashboard for a post-discharge recovery app, built for **Ada Hack 2026** (hackathon: favor speed and a working demo over polish). The patient-facing mobile app (Expo, TypeScript) lives elsewhere; we only owe it the REST contract.

Docs map: `README.md` (overview, secrets, run), `PLAN-backend.md` (original product spec and shared contract; the amendments below win), `APITODO.md` (open backlog; update it when you finish or add items), `docs/checkin-questions.md` (check-in questionnaire contract), `docs/uploads.md` (photos, notes, dictation, unread, push registration; keep in sync with the endpoints), `docs/buddy.md` (recovery twin spec). If code and plan disagree, flag it rather than silently picking one.

**Amendments that override `PLAN-backend.md`** (agreed with the mobile dev):
- **No audio, ever.** The backend never accepts, stores or forwards audio and never calls Soniox with audio. Speech becomes text on the phone; the patient reviews/edits it and only the confirmed text is sent. The dashboard shows text and photos only.
- **Log shape:** `id, episodeId, type ("text" | "photo"), source ("typed" | "dictated"), text, mediaUrl (photos only), extracted{}, authoredBy, reviewedAt`. No `voice` type, no `transcript`.
- **`POST /episodes/{id}/logs`:** JSON `{ type: "text", source, text }`, or multipart for photos only. Returns the saved log + `flags[]` + `patientAction`. LLM extraction and the rules run on `text`; the flag `quote` is the patient's own words.
- Photos can also be a check-in question type, and check-ins are a conditional, multi-choice questionnaire (see Check-ins).

## Deployment reality
**The dev instance IS the deployment** (the hackathon demo, reached by real phones and the dashboard) and it is **open to the world**: `launchSettings.json` binds `0.0.0.0:5000` and the Vite dev server serves `eskrb.home.sync.si`. No separate staging/prod. So:
- Treat every endpoint as publicly reachable. All data is fake/seeded (never real patient data), but it is health-themed and the LLM key costs money.
- Everything except `/auth/*` (not built yet) and the Swagger UI/OpenAPI doc requires a bearer token. No unauthenticated data endpoints, no public static media.
- Seed tokens are high-entropy random values. The user chose to keep the demo tokens committed in `reseed.sh` (fake data only; see Reseeding). Never use token-like guessable values.
- `POST /auth/demo-login` is **not built**; if added it must only issue tokens for seeded demo users, never staff unless intended, behind an `Auth:DemoLoginEnabled` kill switch.
- Paid/abusable paths are protected: upload size limits, image-only media (sniffed by magic bytes), `AddRateLimiter` policies (`llm` 20/min and `stt` 6/min per user).
- OpenAPI `servers` is the relative `"/"`, so Swagger "Try it out" follows the page origin (https behind a TLS proxy). Swagger UI is at `/`, the JSON at `/openapi/v1.json`, unauthenticated by design (the mobile dev uses it as the contract).
- CORS: the dashboard calls the API same-origin through the Vite proxy (`/api/*` -> `API_URL`, default `http://localhost:5000`, prefix stripped), so none is configured. If served from another origin, allow only that origin.
- The DB and media are disposable and reseedable, but a redeploy must not silently wipe the demo mid-pitch: use a persistent dir and avoid reseeding over existing data.

## Goal (v0)
One loop, one scenario (knee replacement): patient checks in on the phone -> backend flags it -> staff see it in the triage board and respond -> patient sees the response. Extras built around it: notes, escalation, charts, photo timeline, push, recovery twin.

## Layout and commands
- `api/` ASP.NET Core Web API (.NET 10, controllers, namespace `Eskrb.Api`); `web/` Vue 3 + TypeScript + Pinia + Vue Router (Vite, alias `@` -> `web/src`); `tests/Eskrb.Api.Tests` (xUnit); `Eskrb.slnx`.
- API: `dotnet run --project api`. Web: `cd web && npm run dev` (checks: `npm run type-check`, `lint`, `format`, `build`; Node `^22.18 || >=24.12`). Tests: `dotnet test` (unit + in-process integration with a temp DB and fake extractor/issuer/notifier; the integration classes share `[Collection("api")]` because the fixture sets process env vars).

## Architecture (API)
Single project, layered by folder:
- `Controllers/` thin: HTTP + DTOs only. `Domain/` entities (User, AuthToken, Protocol, DoctorGroup(+Member), Episode, CheckIn, Flag, Log, Message, EpisodeNote, EpisodeReview, Escalation, EpisodeReadState, PushDevice, BuddyPair, BuddyMessage).
- `Rules/` **pure functions, no I/O**: `RulesEngine`, `Conditions`, `AnswerValidator`, `Triage`, `Buddy` (facts, moments, cheer catalogue), `Quotes`. Unit-test these.
- `Services/` `AppTime` (Europe/Ljubljana), `Visibility`, `EpisodeAccess`, `EpisodeStatus`, `BuddyAccess`, `Media/` (`IMediaStore`, `ImageSniffer`).
- `Integrations/` behind interfaces so tests and the demo can fake them: `IExtractor` (`OpenAiExtractor`, else `NullExtractor`), `ISttKeyIssuer` (`SonioxKeyIssuer`), `INotificationService` (`ExpoPushService`). No audio handling anywhere.
- `Data/` EF Core `EskrbDb`, idempotent `Seeder` (+ `DemoSeeder`). `Auth/` token issuing and validation.

### Hard rules
- **The LLM never decides flags.** It only fills field values and quotes; `RulesEngine` decides. No LLM-based flagging (also none on buddy chat).
- `patientAction` is derived from max flag severity (`urgent` -> `call_112`, `attention` -> `contact_team`, else `none`); mobile picks its result screen from it alone.
- Protocols (questions + rules) are JSON data, not code. `authoredBy` goes on every check-in and log.
- Patient-facing copy is **Slovenian** (formal "vi"); code, field names and the dashboard are English.
- Never log keys, tokens, bodies or patient text. Request logging (`UseHttpLogging`) records method, path, status and duration only: never add headers, query strings or bodies.
- Persistence is swappable: avoid SQLite-specific SQL; never reference concrete DB/storage providers from controllers or domain code; GUIDs are stored as **uppercase** text (use `upper(...)` when poking with `sqlite3`).
- Clinical thresholds in the seeded protocol are placeholders pending clinical review.

### Persistence, schema and auth
- **DB:** SQLite + EF Core, JSON columns for protocol, answers, extracted (default `api/data/eskrb.db`, git-ignored; override `ConnectionStrings__Default`). `EnsureCreated` builds a fresh DB and every start `Seeder.AddMissingTablesAsync` also creates tables the model has but the DB lacks (so new entities need no reseed). It does NOT add or change columns: after such a change run `./reseed.sh` (until EF migrations exist).
- **Media:** local directory (`Media:Path`, default `data/media`) behind `IMediaStore`; the DB stores an opaque key. Files are served only through authorized `GET /media/{logId}` (owner or staff who can see the episode; `nosniff`), never as static files.
- **Auth:** standard bearer authentication + `[Authorize(Roles = ...)]` (`patient`, `staff`, `admin`, lowercase claims). Tokens are opaque random strings stored hashed in `AuthToken` (revoked/expiry checked), long-lived. Seeded users get fixed tokens from config `Seed:Tokens:<key>` (user-secrets/env; unset = generated and logged once). `GET /me` returns the caller.
- **Errors:** RFC 9457 problem details with a human-readable `detail` and `traceId` for every failure (controller errors, model binding, bodyless 401/403/404/415, unhandled 500s). Use `Problem(detail: ...)`; the web `errorMessage()` unwraps `detail`.

### Ownership model and visibility
- **Protocols are global; episodes are local.** `Episode` = patient + protocol + `doctorGroupId` + start/end dates; episode `status` is `upcoming | active | ended` (inclusive dates, `EpisodeStatus`). `DoctorGroupMember` links staff to a group with `Level` (1 = first responder, higher = escalated to), `Title`, and optional `ActiveFrom/ActiveUntil`.
- `Services/Visibility` is the single place for scoping: **admin sees everything; staff see only groups where they hold an active membership**, and the episodes/patients in them. Reuse `VisibleGroups` / `VisibleEpisodes` for every staff-facing query; never query `Episodes` directly in a staff endpoint. Staff get 404 for episodes outside their groups.
- Read-only, staff+admin (`DirectoryController`): `GET /doctor-groups`, `/protocols[/{id}]`, `/patients`, `/episodes?protocolId=&groupId=&employeeId=&patientId=` (staff may only pass their own `employeeId`, else 403). Admin only: `GET /staff`, `GET /users[?role=]`, `GET /users/{id}`.
- Write: `EpisodesController` (`POST /episodes` admin, 409 on overlap; `PATCH`; `POST .../end`; `PUT .../group`), `GroupsController` (admin: create/delete group, add/remove members; 409 on duplicates or while episodes remain).
- `GET /me/episode` (patient) returns the ACTIVE episode (404 if none), the full protocol, `dayNumber`, `checkInDoneToday`, `unreadMessages` and the patient-visible `notes`.

### Check-ins (contract: `docs/checkin-questions.md`)
- `POST /episodes/{id}/checkins` (patient, own active episode, else 404/409; several per day allowed; `date` is set by the server). JSON `{ answers }`, or multipart (text field `answers` + one image part per photo question, named after the question id; max 10 MB each, sniffed, validated before anything is stored). Response `{ id, date, answers, flags[], patientAction }`.
- A protocol question may have `showIf {question, op, value}` (asked only when an earlier answer matches; ops `== != > >= < <=` and `in`), type `multichoice` (array of option ids; `exclusiveOptions`), `labels` (Slovenian text per option id), `skipLabel`, `required` and `immediateAlert` (safety stop: the phone submits at once and shows the 112 screen). `AnswerValidator` rejects answers to questions that are not asked and enforces `required` only on asked ones. Photo answers are stored as photo Logs; `answers[questionId]` is `/media/<logId>`.
- Freeform `text` questions: the LLM fills empty structured questions from them (never overriding direct answers, never `skipExtraction` ones); rules still decide.
- Seeded `knee-replacement`: 8 daily questions (feeling, progress, pain, swelling, wound, exercises, meds, symptoms), 10 conditional follow-ups, an optional `notes` text. Rules: fever (>= 38 or reported), calf, high pain (>= 8), wound discharge/opening, progress much worse, swelling much more, meds missed (all attention); breathing/chest and fainting are urgent. Protocols are seeded from `api/Data/Seed/*.json` and **overwritten on every start**; check-ins stored before a change keep their old keys. Flag quotes for check-ins are `"<question>: <label>"`.

### Notes (logs), messages, timeline
- `POST /episodes/{id}/logs` (patient, own active episode): JSON text (max 4000) or multipart photos (1-5 parts, all-or-nothing, each its own photo log; JPEG/PNG/WebP/HEIC/AVIF by magic bytes, never the client Content-Type; max 10 MB). Text logs: the extractor proposes `{questionId: value + quote}`, each value is validated (invalid dropped), `RulesEngine` raises flags, and the quote must be a substring of the patient's text (else the whole text). If extraction throws the note is still saved with `extractionFailed: true`.
- **LLM: OpenAI, plain text extraction (no Decisions API; decided).** `OpenAiExtractor` calls the Responses API (`POST {OpenAI:BaseUrl}/responses`) with strict JSON-schema output (one nullable `{value, quote}` per extractable question; multichoice = array of enum), `store: false`, low reasoning effort. Config (user-secrets/env, never git): `OpenAI:ApiKey` (unset = `NullExtractor`), `OpenAI:Model` (default `gpt-6-luna`, verified working; fallbacks `gpt-6.1-terra`, `gpt-6.2-sol`), `OpenAI:Effort`, `OpenAI:BaseUrl` (EU residency: EU-region key + `https://eu.api.openai.com/v1`). Sandbox: `POST /demo/extract` (staff/admin, stores nothing). No Slovenian eval set yet.
- `POST /episodes/{id}/messages` (staff on visible episodes, or the patient on their own active episode), `GET /episodes/{id}/timeline` (check-ins, logs, messages newest first; **staff additionally get `flags` and `extracted`**; photos attached to check-in answers show inside the check-in). `POST /episodes/{id}/read` resets the patient's `unreadMessages`.
- **Soniox dictation:** `POST /stt/temporary-key` (patient, active episode; 6/min) issues a single-use ~15 min key via `SonioxKeyIssuer` (`{Soniox:BaseUrl}/v1/auth/temporary-api-key`, `client_reference_id` = episode id). Key in user-secrets `Soniox:ApiKey` (never `reseed.sh`); 503 if unset, 502 if Soniox fails. The configured key is non-EU, so the default `Soniox:BaseUrl` is `https://api.soniox.com`; for EU use an EU key + `https://api.eu.soniox.com` (the phone must use the same region). Never use Soniox's async/file API (it stores audio and transcripts). Before real data: data residency and a signed DPA.

### Staff workflow
- **Triage** (`StaffController`): `GET /staff/triage` -> `{needsAttention, missedCheckIn, awaitingPatientReply, stable}`, each ACTIVE visible episode in exactly one column (first match wins; pure `Rules/Triage`; missed = no check-in today and (after `Triage:MissedCutoffHour`, default 20, or yesterday also missed)). `POST /staff/episodes/{id}/review` `{status: no_action|monitor|contacted|referred, comment?}` marks unreviewed check-ins/logs reviewed, resolves open flags and records an `EpisodeReview` (`GET .../reviews`, latest on cards as `lastReview`).
- **Notes** (`NotesController`): kinds `medication`, `advice` (patient-visible) and `private` (staff only, never sent to patients); shown on `/me/episode`. The protocol's daily `meds` question covers whether meds were taken.
- **Escalation** (`EscalationsController`): `POST /episodes/{id}/escalations {sourceType: checkin|log|message|episode, sourceId, comment?}` hands up to the lowest ACTIVE level above the caller's (409 if nobody above or no membership, e.g. admin); shows in that level's needs attention until resolved (`POST .../{id}/resolve`, or via review). Seeded `doctor` is level 1, `senior` level 2.
- **Charts** `GET /episodes/{id}/charts` (number/scale readings with rule thresholds + per-day activity) and **photos** `GET /episodes/{id}/photos` (series per photo question + `_other`, oldest first). Dashboard: `EpisodeCharts.vue` (activity strip + SVG charts with normal/attention/urgent zones, no chart library) and `PhotoTimeline.vue` (preloaded images in fixed frames, slider that snaps to photo days, compare with first).
- **Push** (`ExpoPushService`): `POST/DELETE /me/push-token` (idempotent; a token moves to the latest signed-in user); admin `GET /admin/push-devices` and `POST /admin/notifications`. `Notifications:Enabled` defaults TRUE (false = kill switch), optional `Expo:AccessToken`. Texts must be GENERIC (lock screens are public), ids in `data`. No automatic triggers yet; receipts are not polled.
- **Recovery twin** (`docs/buddy.md`): staff pair patients by hand (consent box required); patients get `/me/buddy*`. Buddies only see first name, day, checked-in-today, streak, missed days, finished. Moments are computed without any time-of-day rule (the phone decides the nudge hour); generic push max 1 per 10 min per pair, none 21:00-08:00.

## Dashboard (web)
- **Design language:** the mobile one adapted to a dense desktop tool (tokens in `src/assets/base.css`): blue `#1652C4` rail + white sheet, Lexend, pill buttons; one job per colour (coral = pain only, yellow = unreviewed/today only, green = reviewed only, red = urgent only, blue = brand and the "attention" flag). Deliberate exception: review outcomes use green/yellow/blue/red. Create/add/edit forms open in `AppModal.vue` (native `<dialog>`); keep that pattern.
- Auth: `services/api.ts` (fetch wrapper, base `/api`, bearer from localStorage, 401 -> logout), `stores/auth.ts`, `LoginView` (paste a token), router guard. API types/calls are hand-written in `services/directory.ts`. Views: Triage (landing), Episodes, EpisodeDetail, Patients, Groups, Protocols(+Detail), Buddies, and admin-only Users(+Detail) and Notifications. Polling (10 s), no websockets.

## Seed profiles and reseeding
- `Seed:Profile` (user-secret/env, default `dev`): **dev** = 4 patients (ana, marko, nina, luka) with today's check-ins, ana+luka paired; **demo** (`./reseed.sh --demo`) = the judging DB: 10 patients at recovery days 3-40 (janez, marija, ana, marko, nina, franc, ivana, luka, tone, milena) with history up to YESTERDAY and **no check-in today**, all in buddy pairs with conversations, wound-photo stories (`api/Data/Seed/photos/knee_dayN.jpg`), a few notes, something in every triage column. Code: `Data/Seeder.cs`, `Data/DemoSeeder.cs`; covered by `DemoSeedTests`. Seeded history exists only for episodes the seeder creates.
- `./reseed.sh [--demo] [--run] [--force]` keeps registered push devices (exported to `<DB dir>/preserved-push-devices.json` before the wipe, needs the `sqlite3` CLI, re-imported on next start), backs up the DB (last 3 in `api/data/backups/`), removes it and the media dir; the API rebuilds and reseeds on next start. It refuses while something listens on :5000 unless `--force`. The stable login tokens are written in `reseed.sh` (`TOKENS`, by seed key) and copied into user-secrets on every run; add a key and token there when adding a seeded user. **Keep the script's "persisted state" list and cleanup in sync** whenever you add persisted state (new data directory, DB file, required seed config) and mention it in your summary.
- Secrets never go in `reseed.sh`: `Soniox:ApiKey`, `OpenAI:ApiKey`, `Expo:AccessToken` live in user-secrets/env only (list in `README.md`).

## Running the live API
Claude owns the process on :5000 (the live demo): after API changes, `dotnet build`, restart it (`dotnet run --project api`, as a background task; stop it by task id or PID, **never `pkill` by process name**) and smoke-test `/me`, `/episodes`, `/protocols`, `/doctor-groups`, `/users` (admin token from `reseed.sh`) plus `localhost:5173/api/me`. New tables appear automatically; a new/changed COLUMN needs `./reseed.sh` first (stop the API, reseed, start; warn before wiping data). To test elsewhere use `ASPNETCORE_ENVIRONMENT=Development --no-launch-profile --urls http://127.0.0.1:<port>` with a scratch `ConnectionStrings__Default` (the launch profile and `appsettings.Development.json` otherwise force :5000).

## Working agreements
- Hackathon pace: smallest thing that works; no speculative abstraction beyond the seams above.
- Tests: prioritize the pure `Rules/` code (rules, conditions, validator, triage, buddy); don't over-invest elsewhere.
- Keep OpenAPI DTOs stable and tell the user when a contract change affects the mobile app (update the docs in `docs/`).
- Don't commit unless asked.
