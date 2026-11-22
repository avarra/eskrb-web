# eSkrb API: patient app guide (photos, notes, dictation, messages, push)

Companion to `checkin-questions.md` (the check-in questionnaire) and `buddy.md` (recovery twin). Live Swagger UI at `/`, OpenAPI JSON at `/openapi/v1.json`.

**No audio, ever:** the backend never accepts, stores or forwards audio. Speech becomes text on the phone; only photos and confirmed text are uploaded.

**Auth:** `Authorization: Bearer <patient token>`. Only the patient who owns the episode can write to it, and the episode must be active today (otherwise 404 not yours / 409 not active).

**Errors:** every failure is `{ title, status, detail, traceId }` (RFC 9457). Show `detail`; quote `traceId` when reporting a bug. Typical: 400 bad input, 401 missing/invalid token, 403 wrong role, 404 not found or not yours, 409 episode not active, 415 wrong Content-Type (JSON endpoints need `application/json`, photos `multipart/form-data`), 429 rate limited.

## Accepted images
JPEG, PNG, WebP, HEIC and AVIF. The server identifies the file by its first bytes and ignores the `Content-Type` header, so anything else (audio, HTML, a renamed file) is a 400. Max 10 MB per file; empty files are rejected.

## 1. Extra notes and photos: `POST /episodes/{episodeId}/logs`
**Photos** (`multipart/form-data`)
- One or more image file parts; the part name doesn't matter (`file` by convention). Max 5 files per request.
- Each file becomes its own photo log. All-or-nothing: if one file is bad you get a 400 and nothing is saved.
- 201 response: `{ log, logs: [...], extracted, flags: [], patientAction }`. `log` is the first photo, `logs` has all of them; each has `mediaUrl: "/media/<logId>"`. Photos are stored and shown, never analysed, so they raise no flags.

**Text** (`application/json`): `{ "type": "text", "source": "typed" | "dictated", "text": "..." }`, max 4000 chars. The response has `flags[]` and `patientAction` (`none | contact_team | call_112`).

## 2. Photos as a check-in question: `POST /episodes/{episodeId}/checkins`
A protocol question can have `type: "photo"` (see `protocol.questions` in `GET /me/episode`; often conditional and skippable, e.g. `woundPhoto`).
- When the check-in includes a photo, send `multipart/form-data` with
  - a text field `answers`: JSON object of the non-photo answers, e.g. `{"pain":3,"wound":["redness"]}`
  - one image file part per photo question; **the part name is the question id**, e.g. `woundPhoto`. One file per question.
- A photo for a question that is not asked (its `showIf` is not met) is a 400. A required question that isn't answered is a 400. A photo-only check-in is allowed.
- Photo answers can't go in a JSON body (400 pointing here). Check-ins without a photo keep the plain JSON form `{ "answers": { ... } }`.
- 201 response: `{ id, date, answers, flags[], patientAction }`. `answers.<photoQuestionId>` is `/media/<logId>`.
- Everything is validated before anything is stored; a part named after a non-photo question, a duplicate part, or a bad file is a 400.

## Showing a photo
`GET <mediaUrl>` with the same bearer token (media is never public, so a bare `<Image uri>` needs the auth header). Staff and admin see photos of episodes in their groups on the dashboard.

## Expo / React Native notes
- Don't set the `Content-Type` header yourself for multipart: `FormData` adds the boundary. Append files as `{ uri, name, type }`.
- A `file://` URI that doesn't point at a real file uploads 0 bytes and gets a 400 ("between 1 byte and 10 MB").
- Swagger's "Try it out" handles the multipart forms poorly; use curl or the app: `curl -H "Authorization: Bearer $T" -F file=@a.jpg -F other=@b.png http://<host>:5000/episodes/<id>/logs`

## Dictation: Soniox temporary key
`POST /stt/temporary-key` (patient token, no body) -> `{ apiKey, expiresAt }`. The key is single-use, valid ~15 min to open one stream that may run up to 15 min; stream audio **directly to Soniox** (`transcribe_websocket`, in the same region as the server's key) with it. Audio never goes to our API. Show the transcript to the patient, and send only the confirmed text (as a `dictated` log, check-in text answer or buddy message). Limits: 6 requests/min per patient (429), 404 without an active episode, 503 if the server has no Soniox key configured, 502 if Soniox is down.

## Messages, notes and staff replies
- **Freeform check-in question:** protocol questions may have `type: "text"` (e.g. the optional `notes`). Send it as a string in `answers`. The backend's LLM fills structured questions you left empty from it; values you answered directly are never overridden, and the response `answers` may contain the filled ones.
- **Unread staff messages:** `GET /me/episode` has `unreadMessages` (number). When the patient opens the messages screen call `POST /episodes/{id}/read` (204) to reset it. Read messages with `GET /episodes/{id}/timeline` (send with `POST /episodes/{id}/messages {body}`). Poll `/me/episode` every 15-30 s while foregrounded, or react to the push.
- **Reviewed badge:** timeline items of kind `checkin`/`log` have `reviewed: true` once staff marked them reviewed.
- **Staff notes:** `GET /me/episode` has `notes`: `[{id, kind: "medication"|"advice", title, body, createdAt, updatedAt}]` (private doctor notes are never sent). Show them as the patient's instructions. The daily `meds` check-in question covers whether medication was taken.

## Push notifications
After the app has its Expo push token, call `POST /me/push-token` with `{ "expoToken": "ExponentPushToken[...]", "platform": "ios" | "android", "deviceId": "<optional>" }` (bearer token, 204; safe to repeat on every app start). On sign-out call `DELETE /me/push-token` with `{ "expoToken": "..." }`. The backend sends generic texts (e.g. "Imate novo sporočilo.") with `data: { kind, episodeId }`; open the right screen after the user is signed in. Today only an admin sends manual pushes from the dashboard; automatic triggers are still to do.
