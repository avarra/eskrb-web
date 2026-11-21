# Ada Hack 2026 — MVP plan: backend and dashboard

## MVP goal

The first test proves one loop end to end: a patient logs on the phone, the backend flags it, staff see it in the triage queue and respond, and the patient sees the response.

Everything here serves that loop for one scenario, knee replacement. Features outside the loop are listed at the bottom as the next round.

Assumptions (change them if wrong):

- Mobile is Expo with TypeScript. Backend stack is the backend dev's choice; the contract is plain REST and JSON.
- No real auth. Demo login picks a seeded user.
- All patient-facing copy is Slovenian from the start. Code, field names and the dashboard can be English.
- Audio and photos upload to the API. The backend calls Soniox and the LLM, so no API keys live on the phone.
- Both sides build against the contract in parallel. Mobile uses fixture JSON until the API is up.

## Shared contract

Agree on this section before either of us writes code. It is the only thing we both depend on.

### Data model

| Entity | Key fields | Notes |
| --- | --- | --- |
| User | id, name, role (patient, caretaker, staff) | Caretaker role exists but is unused in this round |
| Protocol | id, name, durationDays, questions[], rules[] | Stored as JSON, one seeded: knee replacement |
| Episode | id, patientId, protocolId, startDate, endDate | One recovery period for one patient |
| CheckIn | id, episodeId, date, answers{}, authoredBy, reviewedAt, reviewedBy | answers is keyed by question id |
| Log | id, episodeId, type (text, voice, photo), text, transcript, mediaUrl, extracted{}, authoredBy, reviewedAt | extracted uses the same keys as check-in answers |
| Flag | id, episodeId, sourceType, sourceId, ruleId, severity (attention, urgent), quote, resolvedAt | quote is the patient's own words or the answer that triggered it |
| Message | id, episodeId, fromUserId, body, createdAt | Staff to patient and back |

`authoredBy` goes on every check-in and log now, so caretakers can be added later without a migration.

### Protocol JSON

```json
{
  "id": "knee-replacement",
  "durationDays": 42,
  "questions": [
    { "id": "pain", "type": "scale", "min": 0, "max": 10, "text": "Kako močna je bolečina v kolenu?" },
    { "id": "temperature", "type": "number", "unit": "°C", "text": "Kakšna je vaša telesna temperatura?" },
    { "id": "swelling", "type": "choice", "options": ["less", "same", "more"], "text": "Kakšna je oteklina v primerjavi z včeraj?" },
    { "id": "wound", "type": "choice", "options": ["dry", "red", "discharge"], "text": "Kakšna je rana?" },
    { "id": "calfPain", "type": "boolean", "text": "Ali čutite bolečino ali oteklino v mečih?" },
    { "id": "breathing", "type": "boolean", "text": "Ali težko dihate ali čutite bolečino v prsih?" },
    { "id": "walking", "type": "boolean", "text": "Ali ste danes hodili z berglami?" },
    { "id": "wellbeing", "type": "scale", "min": 1, "max": 5, "text": "Kako se danes počutite?" }
  ],
  "rules": [
    { "id": "fever", "field": "temperature", "op": ">=", "value": 38.0, "severity": "attention" },
    { "id": "wound-discharge", "field": "wound", "op": "==", "value": "discharge", "severity": "attention" },
    { "id": "high-pain", "field": "pain", "op": ">=", "value": 8, "severity": "attention" },
    { "id": "calf", "field": "calfPain", "op": "==", "value": true, "severity": "urgent" },
    { "id": "breathing", "field": "breathing", "op": "==", "value": true, "severity": "urgent" }
  ]
}
```

The questions and thresholds are placeholders to make the loop testable. A clinical mentor should correct them before the pitch.

### API

| Endpoint | Used by | Returns |
| --- | --- | --- |
| POST /auth/demo-login | both | token for a seeded user id |
| GET /me/episode | mobile | episode, protocol, day number, whether today's check-in is done |
| POST /episodes/:id/checkins | mobile | saved check-in, flags[], patientAction |
| POST /episodes/:id/logs (multipart) | mobile | saved log with transcript, flags[], patientAction |
| GET /episodes/:id/timeline | mobile | check-ins, logs and messages, newest first, with reviewed status |
| POST /episodes/:id/messages | both | saved message |
| GET /staff/triage | dashboard | episodes grouped into the four columns |
| GET /staff/episodes/:id | dashboard | profile: summary, open flags, timeline, pain series |
| POST /staff/episodes/:id/review | dashboard | marks items reviewed and resolves their flags |

`patientAction` is one of `none`, `contact_team` or `call_112`. The mobile app decides which result screen to show from this field alone.

### Triage columns

- **Needs attention:** the episode has an unresolved flag. Urgent sorts above attention.
- **Missed check-in:** no check-in for today after the cutoff hour, or none yesterday.
- **Awaiting patient reply:** the last message is from staff.
- **Stable:** everything else.

## Backend and dashboard plan

Build in this order. Steps 1 to 3 unblock the mobile app, so do them first.

1. **Scaffold and seed.** API, database and the schema from the contract. A seed script creates the knee protocol, one staff user and four patients with different histories: one stable, one who missed check-ins, one getting worse, and one with an unanswered staff message.
2. **Demo auth.** `POST /auth/demo-login` returns a token for a seeded user id. No passwords.
3. **Check-ins and rules.** Save the check-in, then run the rules engine. The engine is one pure function: answers and protocol rules in, flags out. Any urgent flag sets `patientAction` to `call_112`, any attention flag sets `contact_team`.
4. **Logs.** Accept multipart uploads and store media on disk or in a bucket. For voice, transcribe with Soniox. For any text or transcript, ask the LLM to extract values for the protocol's question ids plus the exact quote for each. Run the same rules engine on the extracted values.
5. **Triage endpoint.** Compute each episode's column from the rules in the contract and return the four groups.
6. **Dashboard: triage queue.** Four columns. Each card shows patient name, procedure, day number, the top open flag and a missed check-in marker.
7. **Dashboard: patient profile.** A short AI summary of the last few days, open flags with their quotes, the full timeline with transcripts and photos, and a pain-over-time line.
8. **Actions.** Mark as reviewed, which resolves the flags, and send a message to the patient.
9. **Reachable URL.** Deploy or tunnel so a real phone can hit the API.

Keep the LLM out of the flagging decision. It only fills in fields and quotes; the rules decide. Regenerate the summary when new data arrives and cache it, so the profile opens instantly.

Keep step 4 synchronous for now so flags come back in the upload response. Move it to a background job only if latency hurts the demo.

## First test

Run this script together once both sides are up. If every step works, the MVP is done.

- [ ] Log in on the phone as the stable seeded patient. Home shows the right day number.
- [ ] Trigger the reminder with the debug button. Tapping it opens the check-in.
- [ ] Submit a normal check-in. The phone shows the thank-you, and the patient sits in Stable on the dashboard.
- [ ] Record a voice note in Slovenian saying the calf hurts. The transcript comes back and the phone shows the urgent screen.
- [ ] The patient moves to Needs attention, with the flag and the quoted sentence on the card and profile.
- [ ] Add a wound photo. It appears in the profile timeline.
- [ ] On the dashboard, send a message and mark the items reviewed. The patient moves to Awaiting patient reply.
- [ ] The phone timeline shows the message and the reviewed badge. Reply from the phone and the patient returns to Stable.
- [ ] The seeded patient with missed check-ins sits in the Missed check-in column.

## Covered by this plan

- Daily structured check-in, driven by a per-procedure protocol
- Extra logs: text, voice with Slovenian transcription, and photos
- Wellbeing as one slider question inside the check-in
- Daily reminder notification that opens the check-in
- Deterministic red-flag rules per procedure
- AI extraction from free text and voice into structured fields, with source quotes
- AI summary on the patient profile
- Urgent and contact-team prompts on the phone
- Triage queue with four columns, including missed check-ins
- Patient profile with timeline, flags and a pain trend
- Staff actions: mark reviewed and message the patient
- Patient sees that staff reviewed a log, and can reply
- Seeded demo data and demo login
- Large, simple, Slovenian patient UI

## Still to do after this plan

### Backend and dashboard

- Expected recovery curve and deviation from it
- Trend rules, such as pain rising two days in a row
- Protocol drafted from the discharge letter, approved by staff
- Creating patients and assigning procedures in the dashboard, not only seeded
- Substitute staff during absence, for all patients or per patient
- Role-limited views for patronaža and the osebni zdravnik
- More actions: call, book a check-up, request additional data
- Wearable data and medication adherence on the profile
- Push notifications from staff to the phone
- Medication schedule and questions-and-doubts endpoints for the mobile app
- Consent settings per data type and per procedure

### Caretakers (backend side)

- Caretaker accounts linked to patients, many-to-many, with proxy or observer mode
- Invite codes
- Escalation chain: caretaker first, then staff

### Pitch (shared)

- Clinical check of the questions and thresholds
- Demo storyline and the seed data that tells it
- Roadmap slide: SI-PASS login and eZdravje integration
- Slides on staff time saved, human in the loop, and privacy
