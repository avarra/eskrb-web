# API / dashboard TODO

Remaining work after the first write actions (episodes, groups, memberships, check-in submit).
Everything is admin-only unless noted. Keep this file current: delete items when done, add new ones.

## Users and tokens
- [ ] Create user: `POST /users` (name, role) and issue a login token for them. Without this new users cannot sign in.
- [ ] Issue / list / revoke tokens: `POST|GET|DELETE /users/{id}/tokens`. Show a new token once; store only the hash.
- [ ] Rename / change role: `PATCH /users/{id}`.
- [ ] Deactivate user (needs a `DisabledAt` column; deleting would orphan episodes and messages).
- [ ] Create patient (optionally with an episode in one step).

## Groups and memberships
- [ ] Rename group.
- [ ] Edit a membership (level, title, `ActiveFrom`/`ActiveUntil`).
- [ ] End a membership by setting `ActiveUntil` (keeps history) as an alternative to deleting.
- [ ] Bulk reassign all episodes of a group (absence cover).

## Protocols
- [ ] Edit / create / archive protocols from the dashboard. Decide the source of truth first: the seeder
      currently overwrites protocols from `api/Data/Seed/*.json` on every start, which would discard UI edits.
- [ ] Validate protocol edits: every rule `field` must be a question id, and the operator/value type must suit the question type.

## Notes, replies and the patient loop (from PLAN-backend.md)
- [~] **LLM extraction:** provider = OpenAI (decided), `OpenAiExtractor` written + unit-tested but not yet run against the real API (needs `OpenAI:ApiKey`). Open: Slovenian eval set and model-tier choice (see the last line of this file), rate limit done (`llm` policy).
- [ ] AI summary for the patient profile (cached, regenerated on new data).
- [ ] Staff patient profile `GET /staff/episodes/{id}`: open flags, summary, pain series (the timeline endpoint already exists).
- [ ] Optional photo caption (text sent with a photo) - decide; it would need extraction too.
- (done) unread count + mark read. [ ] Push notifications (Expo token table + send on staff message; generic text only).
- (done) Review: `POST /staff/episodes/{id}/review`. Still open: review a single item / unresolve.
- (done) `GET /staff/triage`. Open: the dashboard board (web).
- (done) Seed history (only for newly seeded episodes; reseed to get it on old ones).
- [ ] Decide whether required questions exist (today unanswered questions are allowed) and whether one check-in per day should be enforced.
- (done) cutoff is `Triage:MissedCutoffHour`.

## Cross-cutting
- [ ] EF migrations instead of `EnsureCreated` (schema now changes under real data; needs `dotnet-ef`).
- [ ] Audit log of admin/staff writes (who, action, target, time).
- [ ] `POST /auth/demo-login` (flag-gated) and rate limiting on public/paid paths.
- [ ] Soniox temporary-key endpoint (later; see CLAUDE.md).
- (done) photo questions + `required`. Still open: protocol editing UI/validation for the new `photo`/`required` fields.
- LLM: OpenAiExtractor is written but untested against the real API; build a Slovenian eval set (20 notes with expected values, esp. calf pain / breathing / negations) and pick the cheapest tier that gets every urgent case right. AI summary still open.

## Added later
- (done) Episode notes (medication/advice/private) with the meds check-in question, escalation to the next group level, triage board, review, unread count, dashboard restyle.
- [ ] Notify (push) on escalation and on new patient-visible notes. [ ] Escalation history on the triage card after resolve. [ ] Seeded history for the new `senior`/escalation demo. [ ] Mobile: show notes + the `medsTaken` question.
