# Backlog

Open work only (finished items are deleted; see `CLAUDE.md` for what exists). Keep this file current: remove items when done, add new ones.

## Before real use
- [ ] EF migrations instead of `EnsureCreated` (schema now changes under real data; needs `dotnet-ef`).
- [ ] Data residency and legal: EU Soniox key + `Soniox:BaseUrl`, EU OpenAI project + `OpenAI:BaseUrl`, signed DPAs, DB/media on EU hosting (README, "Target deployment").
- [ ] Clinical review of the protocol thresholds and rules, and of the 112 safety text.
- [ ] Native review of all Slovenian copy (questions, cheers, 112 text).
- [ ] Token expiry/rotation and real login (SI-PASS/passwords); `POST /auth/demo-login` behind `Auth:DemoLoginEnabled` if the demo needs it.
- [ ] Audit log of admin/staff writes and of staff reading buddy threads (who, action, target, time).

## Users, groups, protocols (admin)
- [ ] Create user + issue login token; list/revoke tokens; rename / change role; deactivate user (needs a `DisabledAt` column); create patient with an episode in one step.
- [ ] Rename group; edit a membership (level, title, `ActiveFrom`/`ActiveUntil`); end a membership without deleting; bulk reassign a group's episodes (absence cover).
- [ ] Edit/create/archive protocols from the dashboard. Decide the source of truth first: the seeder overwrites protocols from `api/Data/Seed/*.json` on every start.
- [ ] Validate protocol edits: rule/`showIf` fields must be question ids, operators and values must suit the question type, conditions may only reference earlier questions.

## Patient loop
- [ ] LLM: build a Slovenian eval set (about 20 notes with expected values, esp. calf pain, breathing, negations) and pick the cheapest model tier that gets every urgent case right.
- [ ] AI summary for the patient profile (cached, regenerated on new data) and a staff profile endpoint (open flags, summary, trends).
- [ ] Optional photo caption (text sent with a photo; it would need extraction too).
- [ ] Review a single item / un-resolve a flag; escalation history on triage cards after resolve.
- [ ] Decide whether one check-in per day should be enforced (several are allowed today).

## Push notifications
- [ ] Wire triggers: staff message, new patient-visible note, escalation, urgent flag (generic text only).
- [ ] Poll receipts (`GetReceiptsAsync`), retry/backoff on 429/5xx, set `Expo:AccessToken`.

## Recovery twin
- [ ] In-app consent instead of the staff checkbox; auto-matching.

## Dashboard
- [ ] Trends for ordinal choices (feeling, progress) and booleans; zoom/range; compare two episodes.
- [ ] Photos: overlay/side-by-side of two arbitrary dates, wound measurements.
- [ ] Dashboard polish: remaining list views are plain tables.

## Mobile (other repo, see `docs/`)
- [ ] Conditional/multi-choice check-in flow with the safety stop (`docs/checkin-questions.md`).
- [ ] Buddy screen with dynamic cheers and dictation (`docs/buddy.md`); push token registration; show medication/advice notes.
