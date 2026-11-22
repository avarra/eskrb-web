# Recovery twin (buddy) - agreed spec v0

Status: **backend + dashboard built (see below), mobile screen pending.** Slovenian copy below is a draft for a native speaker to review. The app voice is formal "vi".

## Concept
Staff pair two patients on the same protocol who are at about the same stage of recovery. The two see that the other is showing up, can send context-aware cheers and exchange short messages (typed or dictated). No health data is shared between them.

Decisions: pairing is **manual by staff** (demo), buddies see the **first name only**, free-text messages are allowed next to the cheers, staff can read threads (disclosed at pairing), no websockets (see Freshness).

## What a buddy sees (whitelist)
First name (first word of the name), recovery day, checked in today, streak (consecutive days with a check-in; today does not break it until the day ends), and a few derived facts below (missed days, finished). **Never**: surname, pain, flags, notes, photos, meds, answers, dates, ward. Flags/escalations on one person never change anything on the other's screen.

## Lifecycle
1. Staff open "New pair" in the dashboard, pick patient A, get suggestions for B (active, same protocol, no buddy yet, closest start date first), and tick the required box: *"Both patients agreed to be paired and know that staff can read their messages."* The pair stores who created it and when (the consent record).
2. Active: the buddy screen works.
3. Ends when either patient leaves (app), staff unpair, or either episode ends (thread becomes read-only; finished buddy shows "Kolega je zaključil okrevanje."). Wording to the other side is neutral: "Povezava s kolegom se je zaključila." The reason is never shown. A message to an ended pair is 409.

## Messages
- **Cheer**: one tap from a server-owned catalogue (below).
- **Text**: 1-300 chars, `source: typed | dictated`. No attachments, links rejected. About 30 per hour per sender.
- **Dictation**: the existing flow. The phone gets a temporary key from `POST /stt/temporary-key`, streams audio **phone -> Soniox only**, the patient reviews/edits the text, and only the confirmed text is sent. No audio ever touches our backend. (Soniox sees the audio, so the Soniox region/DPA points in `CLAUDE.md` apply to buddy chat too.)
- No LLM or flag extraction on buddy messages (peer chat is not clinical input; the LLM still never decides flags).
- Permanent banner under the thread: "Za zdravstvene težave pišite zdravstveni ekipi." linking to the team message screen.
- A patient can report a message (marks it; dashboard shows a "reported" badge). Staff can read a thread, hide a message, end the pair.
- Push on a new message/cheer is **generic** ("Imate novo sporočilo od kolega." / "Kolega vam pošilja pozdrav.", no names or health detail), at most one push per pair per recipient every 10 minutes, and none between 21:00 and 08:00 (the item still shows in the app). Uses the existing push service.

## Dynamic cheers
The "Pozdrav" sheet offers 3-4 big pills that fit the buddy's situation, first the moment-specific ones, then evergreen ones. The sheet also has **"Napišite svoje sporočilo"** (keyboard or microphone) for a custom text.

### Moments (draft copy, formal)
| Moment (buddy's situation) | Sender sees | Buddy receives |
|---|---|---|
| `not_checked_in` (no check-in today) | "Spomnite na prijavo" | "Kolega misli nate. Ne pozabite na današnjo prijavo." |
| `missed_days` (2+ days missed) | "Vprašajte, kako gre" | "Kolega misli nate. Kako vam gre?" (also opens the message box with this as a starter) |
| `checked_in` (checked in today) | "Čestitajte za prijavo" | "Bravo za današnjo prijavo." |
| `both_checked_in` (you and the buddy today) | "Oba sva danes opravila" | "Danes sva oba opravila prijavo." |
| `streak_N` (N = 3, 7, 14, 30) | "Čestitajte za N dni zapored" | "N dni zapored, bravo." |
| `day_N` (N = 7, 14, 21, halfway) | "Že teden dni za nama" | "Teden dni okrevanja je za vama." |
| `returned` (back after a gap) | "Pozdravite ob vrnitvi" | "Lepo, da ste spet tu." |
| `finished` | "Čestitajte" | "Čestitke za zaključeno okrevanje." |
| always | "Držim pesti", "Do jutri" | the same words |

### Rules that keep it kind
- **Sender and receiver see different words.** A "remind" label never reaches the buddy as an accusation.
- At most **one nudge-type cheer** (`not_checked_in`, `missed_days`) per buddy per day; `missed_days` at most once every few days; **3 cheers a day** per buddy. Enforced by the server (409/429 with a readable `detail`).
- The sender never sees counts of missed days or "reminder not answered".
- Ended/finished pairs only offer `finished`.
- Options come only from the shared whitelist, so a bad pain day or a flag can never change what is offered.

### Optional sender card (included)
After the nudge hour, if the buddy has not checked in, the sender's buddy screen shows a quiet in-app card: "Ana še ni opravila prijave. Pošljete ji prijazen opomnik?" (button = the `not_checked_in` cheer). App only, no extra push.

## Client / server split
- **Server**: pairing and consent record, buddy facts, the cheer catalogue (copy lives on the server so it can change without an app release), validation and caps, storage, push, staff oversight.
- **Client (mobile)**: decides which moments apply and which pills to show, **including the nudge hour (17:00) and the absent-buddy card** (server never gates by time of day), ordering, polling, the dictation UI.

## API
Patient:
| Endpoint | Purpose |
|---|---|
| `GET /me/buddy` | `{ status: none\|active\|ended, buddy: {firstName, dayNumber, checkedInToday, streak, missedDays, finished}, unread, moments: [...keys that apply, no time-of-day rule], cheers: [{id, moment, label, text}] }` (client combines with its own `checkInDoneToday`, applies the hour, picks the pills) |
| `GET /me/buddy/messages?after=` | thread oldest -> newest: `{id, fromMe, kind: cheer\|text, text, source, createdAt}` |
| `POST /me/buddy/messages` | `{ text, source }` |
| `POST /me/buddy/cheers` | `{ cheerId }` |
| `POST /me/buddy/read` | resets `unread` |
| `POST /me/buddy/messages/{id}/report` | report |
| `DELETE /me/buddy` | leave the pair |

Staff/admin (scoped by doctor group; both patients must be visible to the person pairing):
| Endpoint | Purpose |
|---|---|
| `GET /staff/buddies` | pairs: names, protocol, days, created by/when, reported count |
| `GET /staff/buddies/candidates?episodeId=` | suggested partners |
| `POST /staff/buddies` | `{ episodeAId, episodeBId, consentConfirmed: true }` (both active, different patients, same protocol, no current buddy, else 409) |
| `DELETE /staff/buddies/{id}` | unpair |
| `GET /staff/buddies/{id}/messages` | read the thread |
| `DELETE /staff/buddies/{id}/messages/{msgId}` | hide a message |

New tables only (`BuddyPair`, `BuddyMessage`), so no reseed.

## Freshness: no websockets
Two older patients exchange a handful of messages a day, so a 15-30 s delay is invisible. A push says "something arrived", and the app refreshes `GET /me/buddy` (and the thread) every 15-30 s while the screen is open (about 5 s while the thread itself is open if it feels slow). The dashboard already polls the same way. Websockets would add reconnects, proxies, TLS and background suspension for no visible gain.

## Mobile screen
Design language: blue ground, one big white action. Buddy card (initial avatar, first name, "Dan 12", ✓ if checked in today, a yellow dot for unread, "5 dni zapored"); thread bubbles; reply box 64 tall with a large "Pošlji" pill and a microphone; "Pozdrav" opens the pill sheet (plus "Napišite svoje sporočilo"); the safety banner (18 px minimum); no buddy yet: "Vašega kolega vam bo dodelila ekipa."

## Dashboard
A "Buddies" page (staff): pair table (view thread, unpair, "reported" badge) and a "New pair" modal with suggestions and the consent checkbox. Thread view is read-only with a hide button per message.

## Seed and demo
One seeded pair, ana + luka, with a short conversation (typed, dictated and cheers; the last two messages from luka are unread). ana is mid-recovery (day 20) with a 5-day check-in streak after a short gap a week ago. marko and nina stay free so staff can pair them live during the demo (nina has missed check-ins, so the neutral state shows).

## Effort
About 3 h backend with tests, 1.5 h dashboard, about 3 h mobile screen (teammate).

## Implementation notes (as built)
- Tables `BuddyPair` and `BuddyMessage` (created automatically). Pure logic in `api/Rules/Buddy.cs` (facts, moments, the Slovenian cheer catalogue, quiet hours); controllers `BuddyController` (patient) and `BuddiesController` (staff); dashboard page `/buddies`.
- `GET /me/buddy` `cheers` is the whole catalogue (when the pair is running; only `congrats-finish` for a finished buddy). The client shows the ones whose `moment` is in `moments` plus `always`, and applies the nudge hour itself. Sending a cheer that does not fit right now is 409; caps are 429: 3 cheers/day, 1 nudge-type cheer/day, `ask` once per 3 days, 30 texts/hour.
- Status `ended`: `buddy` is null when a person ended it (left/staff); a pair whose episode simply ended keeps `buddy` (so "finished" can be shown) and only allows the one `congrats-finish` cheer.
- `GET /me/buddy/messages?after=<ISO instant>` returns only newer messages. Hidden messages never reach patients.
- Seeded on a fresh DB: ana + luka are paired with about ten messages over four days; marko and nina are free for a live demo pairing.

## Not in v0
Auto-matching, leaderboards, groups, photos, sharing pain/mood, LLM scan of buddy chat, websockets.
