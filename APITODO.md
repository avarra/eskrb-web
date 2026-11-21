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
- [ ] **Pick an LLM provider and implement `IExtractor`** (today only `NullExtractor`: text notes raise no flags). Constrain output to JSON for the protocol's question ids + exact quotes; test with Slovenian text early. Needs an API key in secrets; add rate limiting (public instance, paid calls).
- [ ] AI summary for the patient profile (cached, regenerated on new data).
- [ ] Staff patient profile `GET /staff/episodes/{id}`: open flags, summary, pain series (the timeline endpoint already exists).
- [ ] Optional photo caption (text sent with a photo) - decide; it would need extraction too.
- [ ] Messages: mark-as-read / unread counts (phone badge). Notifications to the phone are a later task.
- [ ] Review: `POST /staff/episodes/{id}/review` marks items reviewed and resolves their flags.
- [ ] Triage: `GET /staff/triage` (needs attention / missed check-in / awaiting patient reply / stable) + the dashboard board.
- [ ] Seed history: past check-ins and flags for the demo patients so the triage board has something to show.
- [ ] Decide whether required questions exist (today unanswered questions are allowed) and whether one check-in per day should be enforced.
- [ ] Missed check-in cutoff hour as config.

## Cross-cutting
- [ ] EF migrations instead of `EnsureCreated` (schema now changes under real data; needs `dotnet-ef`).
- [ ] Audit log of admin/staff writes (who, action, target, time).
- [ ] `POST /auth/demo-login` (flag-gated) and rate limiting on public/paid paths.
- [ ] Soniox temporary-key endpoint (later; see CLAUDE.md).
