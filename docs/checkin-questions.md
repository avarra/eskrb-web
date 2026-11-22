# Daily check-in questionnaire (mobile contract)

The protocol (`GET /me/episode` -> `protocol.questions`, `protocol.rules`) is data: **render exactly what the API returns, in order**. Slovenian copy, formal "vi". Clinical thresholds are placeholders pending clinical review.

## Question fields
| Field | Meaning |
|---|---|
| `id`, `type`, `text` | `type` is `scale`, `number`, `choice`, `multichoice`, `photo` or `text` |
| `options` | stable option **ids** for `choice` / `multichoice` (answers are sent and stored as ids) |
| `labels` | `{ optionId: "Display text" }`, may contain emoji. Show `labels[id] ?? id` |
| `min`, `max`, `unit` | `scale` (whole numbers, e.g. 0-10) and `number` (e.g. `°C`) |
| `exclusiveOptions` | multichoice: options that exclude all others (e.g. `none`). Picking one clears the rest; picking another clears it |
| `showIf` | `{ question, op, value }`: ask this question only when the condition holds on an **earlier** answer (see below). No `showIf` = always asked |
| `immediateAlert` | `{ options[], title, text, action }`: safety stop (see below) |
| `skipLabel` | the question may be skipped with a button of this text ("Preskoči", "Nisem izmeril"); a skipped question has **no** answer (omit the key) |
| `required` | rare; a required question that is asked must be answered |
| `skipExtraction` | server-side only (LLM free-text extraction), ignore |

## Flow
1. Walk `questions` in order. Before each one, evaluate `showIf` against the answers collected so far; skip it when it is not met. Answers to questions that become hidden again (the patient went back and changed the parent answer) **must be dropped**: the server rejects answers to questions that are not asked (`400`, "is not asked for these answers").
2. `showIf.op` is one of `== != > >= < <=` (numbers, strings, booleans) or `in` (`value` is an array: a `choice` answer is one of them; a `multichoice` answer shares **any** of them). A missing parent answer = not asked.
3. Answers (JSON `{ answers: { questionId: value } }`):
   - `scale` / `number`: number; `choice`: option id string; `multichoice`: **non-empty array of option ids**, no duplicates, an exclusive option only on its own; `text`: string (max 2000); `photo`: not in JSON (multipart file part named after the question id, see `uploads.md`); skipped question: omit.
4. Submit with `POST /episodes/{id}/checkins` (JSON, or multipart when a photo was taken). The response has `flags[]` and `patientAction` (`none` | `contact_team` | `call_112`): pick the result screen from that alone.

## Safety stop (`immediateAlert`)
`symptoms` carries `immediateAlert { options: ["breathing_chest","fainting"], title, text, action: "call_112" }`. When the patient selects any of those options the app must **stop the normal flow immediately** (no more questions):
1. Submit the check-in right away with the answers collected so far (partial check-ins are allowed). The server raises an urgent flag, so staff see it, and returns `patientAction: "call_112"`.
2. Show `immediateAlert.title` / `text` (full screen, "Pokličite 112") with a call button. Do not wait for the rest of the questionnaire.

## Conditional follow-ups in the seeded `knee-replacement` protocol
| Question | Asked when |
|---|---|
| `feelingCause` | `feeling in [bad, very_bad]` |
| `worseningCause` | `progress in [worse, much_worse]` |
| `painWhen` | `pain >= 5` |
| `swellingMobility` | `swelling in [slightly_more, much_more]` |
| `woundPhoto` (photo, skippable) | `wound in [redness, discharge, opening]` |
| `woundWhyNot` | `wound in [cannot_check]` |
| `exercisesWhy` | `exercises in [partly, no]` |
| `medsWhy` | `meds in [some_skipped, no]` |
| `temperature` (number °C, skippable "Nisem izmeril") | `symptoms in [fever]` |
| `calfLeg` | `symptoms in [calf]` |

`wound` and `symptoms` are `multichoice` (exclusive: `none`, and for `wound` also `cannot_check`). The optional last question `notes` is freeform text.

## Medication
The daily `meds` question covers medication ("Danes nimam predpisanih zdravil" = none prescribed). Staff medication/advice notes arrive in `notes` on `/me/episode` and are shown as instructions; they no longer add a question.
