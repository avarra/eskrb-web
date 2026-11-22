import { api } from './api'

const json = (method: string, body?: unknown): RequestInit => ({
  method,
  body: body === undefined ? undefined : JSON.stringify(body),
})

export interface NamedRef {
  id: string
  name: string
}
export interface GroupMember {
  userId: string
  name: string
  level: number
  title: string | null
  active: boolean
}
export interface Group {
  id: string
  name: string
  episodeCount: number
  members: GroupMember[]
}
export interface ProtocolSummary {
  id: string
  name: string
  durationDays: number
  questionCount: number
  ruleCount: number
  episodeCount: number
}
export interface ProtocolQuestion {
  id: string
  type: string
  text: string
  min: number | null
  max: number | null
  unit: string | null
  options: string[] | null
  required: boolean
}
export interface ProtocolRule {
  id: string
  field: string
  op: string
  value: string | number | boolean
  severity: string
}
export interface ProtocolDetail {
  id: string
  name: string
  durationDays: number
  questions: ProtocolQuestion[]
  rules: ProtocolRule[]
  episodes: EpisodeRow[]
}
export interface Patient {
  id: string
  name: string
  episodeCount: number
}
export interface StaffMembership {
  groupId: string
  groupName: string
  level: number
  title: string | null
  active: boolean
}
export interface StaffMember {
  id: string
  name: string
  role: string
  memberships: StaffMembership[]
}
export interface EpisodeRow {
  id: string
  patient: NamedRef
  protocol: NamedRef
  group: NamedRef
  startDate: string
  endDate: string
  dayNumber: number
  status: 'upcoming' | 'active' | 'ended'
}
export interface UserRow {
  id: string
  name: string
  role: string
}
export interface UserDetail extends UserRow {
  episodes: EpisodeRow[]
  memberships: StaffMembership[]
}
export interface EpisodeFilter {
  protocolId?: string
  groupId?: string
  employeeId?: string
  patientId?: string
}

export interface FlagRow {
  id: string
  ruleId: string
  severity: 'attention' | 'urgent'
  quote: string
}
export interface TimelineItem {
  kind: 'checkin' | 'log' | 'message'
  id: string
  at: string
  reviewed: boolean | null
  answers: Record<string, unknown> | null
  logType: 'text' | 'photo' | null
  source: 'typed' | 'dictated' | null
  text: string | null
  mediaUrl: string | null
  extracted: Record<string, unknown> | null
  fromName: string | null
  fromRole: string | null
  body: string | null
  flags: FlagRow[] | null
}
export interface TriageFlag {
  id: string
  ruleId: string
  severity: 'attention' | 'urgent'
  quote: string
  createdAt: string
}
export interface TriageCard {
  episodeId: string
  patientId: string
  patientName: string
  groupName: string
  protocolName: string
  dayNumber: number
  topFlag: TriageFlag | null
  openFlags: number
  missedCheckIn: boolean
  lastCheckInDate: string | null
  lastMessageAt: string | null
  lastMessageFromStaff: boolean
  escalation: {
    id: string
    sourceType: string
    sourceId: string
    fromName: string
    toLevel: number
    comment: string | null
    createdAt: string
  } | null
  lastReview: {
    status: ReviewStatus
    comment: string | null
    reviewerName: string
    createdAt: string
  } | null
}
export interface Triage {
  needsAttention: TriageCard[]
  missedCheckIn: TriageCard[]
  awaitingPatientReply: TriageCard[]
  stable: TriageCard[]
  generatedAt: string
}
export type ReviewStatus = 'no_action' | 'monitor' | 'contacted' | 'referred'
export const reviewStatusLabel: Record<ReviewStatus, string> = {
  no_action: 'No action needed',
  monitor: 'Keep monitoring',
  contacted: 'Contacted the patient',
  referred: 'Referred / needs a visit',
}
export interface ReviewRow {
  id: string
  status: ReviewStatus
  comment: string | null
  reviewerName: string
  createdAt: string
  checkIns: number
  logs: number
  flagsResolved: number
}
export interface ReviewResult {
  checkIns: number
  logs: number
  flagsResolved: number
}
export type NoteKind = 'medication' | 'advice' | 'private'
export interface Note {
  id: string
  kind: NoteKind
  title: string | null
  body: string
  createdAt: string
  updatedAt: string
}
export interface Escalation {
  id: string
  sourceType: 'checkin' | 'log' | 'message'
  sourceId: string
  fromName: string
  toLevel: number
  comment: string | null
  createdAt: string
  resolved: boolean
  resolvedAt: string | null
}
export interface Message {
  id: string
  fromUserId: string
  fromName: string
  fromRole: string
  body: string
  createdAt: string
}

export interface NewEpisode {
  patientId: string
  protocolId: string
  doctorGroupId: string
  startDate?: string
}
export interface NewMember {
  userId: string
  level: number
  title?: string
}

export const directory = {
  groups: () => api<Group[]>('/doctor-groups'),
  protocols: () => api<ProtocolSummary[]>('/protocols'),
  protocol: (id: string) => api<ProtocolDetail>(`/protocols/${id}`),
  patients: () => api<Patient[]>('/patients'),
  staff: () => api<StaffMember[]>('/staff'),
  users: (role?: string) => api<UserRow[]>('/users' + (role ? `?role=${role}` : '')),
  user: (id: string) => api<UserDetail>(`/users/${id}`),
  episodes: (f: EpisodeFilter = {}) => {
    const qs = new URLSearchParams()
    for (const [k, v] of Object.entries(f)) if (v) qs.set(k, v)
    const s = qs.toString()
    return api<EpisodeRow[]>('/episodes' + (s ? `?${s}` : ''))
  },

  timeline: (episodeId: string) => api<TimelineItem[]>(`/episodes/${episodeId}/timeline`),
  reviews: (episodeId: string) => api<ReviewRow[]>(`/staff/episodes/${episodeId}/reviews`),
  triage: () => api<Triage>('/staff/triage'),
  notes: (episodeId: string) => api<Note[]>(`/episodes/${episodeId}/notes`),
  escalations: (episodeId: string) => api<Escalation[]>(`/episodes/${episodeId}/escalations`),
  // --- writes ---
  addNote: (episodeId: string, b: { kind: NoteKind; title?: string; body: string }) =>
    api<Note>(`/episodes/${episodeId}/notes`, json('POST', b)),
  updateNote: (
    episodeId: string,
    id: string,
    b: { kind?: NoteKind; title?: string; body?: string },
  ) => api<Note>(`/episodes/${episodeId}/notes/${id}`, json('PATCH', b)),
  deleteNote: (episodeId: string, id: string) =>
    api<void>(`/episodes/${episodeId}/notes/${id}`, json('DELETE')),
  escalate: (episodeId: string, b: { sourceType: string; sourceId: string; comment?: string }) =>
    api<Escalation>(`/episodes/${episodeId}/escalations`, json('POST', b)),
  resolveEscalation: (episodeId: string, id: string) =>
    api<Escalation>(`/episodes/${episodeId}/escalations/${id}/resolve`, json('POST')),
  review: (episodeId: string, b: { status: ReviewStatus; comment?: string }) =>
    api<ReviewResult>(`/staff/episodes/${episodeId}/review`, json('POST', b)),
  sendMessage: (episodeId: string, body: string) =>
    api<Message>(`/episodes/${episodeId}/messages`, json('POST', { body })),
  createEpisode: (b: NewEpisode) => api<EpisodeRow>('/episodes', json('POST', b)),
  updateEpisode: (id: string, b: { startDate?: string; endDate?: string }) =>
    api<EpisodeRow>(`/episodes/${id}`, json('PATCH', b)),
  endEpisode: (id: string) => api<EpisodeRow>(`/episodes/${id}/end`, json('POST')),
  setEpisodeGroup: (id: string, doctorGroupId: string) =>
    api<EpisodeRow>(`/episodes/${id}/group`, json('PUT', { doctorGroupId })),
  createGroup: (name: string) => api<Group>('/doctor-groups', json('POST', { name })),
  deleteGroup: (id: string) => api<void>(`/doctor-groups/${id}`, json('DELETE')),
  addMember: (groupId: string, b: NewMember) =>
    api<GroupMember>(`/doctor-groups/${groupId}/members`, json('POST', b)),
  removeMember: (groupId: string, userId: string) =>
    api<void>(`/doctor-groups/${groupId}/members/${userId}`, json('DELETE')),
}
