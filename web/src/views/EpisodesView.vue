<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import AppModal from '@/components/AppModal.vue'
import { errorMessage } from '@/services/api'
import { useAuthStore } from '@/stores/auth'
import {
  directory,
  type EpisodeFilter,
  type EpisodeRow,
  type Group,
  type Patient,
  type ProtocolSummary,
  type StaffMember,
} from '@/services/directory'

const auth = useAuthStore()
const route = useRoute()
const router = useRouter()

const rows = ref<EpisodeRow[]>([])
const protocols = ref<ProtocolSummary[]>([])
const groups = ref<Group[]>([])
const staff = ref<StaffMember[]>([])
const patients = ref<Patient[]>([])
const error = ref('')

const filter = ref<EpisodeFilter>({})
const q = (k: keyof EpisodeFilter) => (route.query[k] as string | undefined) ?? ''

// per-row date editing, keyed by episode id
const dates = ref<Record<string, { startDate: string; endDate: string }>>({})
const creating = ref(false)
const editing = ref<EpisodeRow | null>(null)
const draft = ref({ patientId: '', protocolId: '', doctorGroupId: '', startDate: '' })

async function load() {
  filter.value = {
    protocolId: q('protocolId'),
    groupId: q('groupId'),
    employeeId: q('employeeId'),
    patientId: q('patientId'),
  }
  rows.value = await directory.episodes(filter.value)
  dates.value = Object.fromEntries(
    rows.value.map((e) => [e.id, { startDate: e.startDate, endDate: e.endDate }]),
  )
}

function apply() {
  const query = Object.fromEntries(Object.entries(filter.value).filter(([, v]) => v))
  router.replace({ name: 'episodes', query })
}

async function run(fn: () => Promise<unknown>) {
  error.value = ''
  try {
    await fn()
    await load()
  } catch (e) {
    error.value = errorMessage(e)
  }
}

const create = () =>
  run(async () => {
    await directory.createEpisode({
      ...draft.value,
      startDate: draft.value.startDate || undefined,
    })
    draft.value = { patientId: '', protocolId: '', doctorGroupId: '', startDate: '' }
    creating.value = false
    if (auth.isAdmin) patients.value = await directory.patients()
  })

const edit = (e: EpisodeRow) => {
  dates.value[e.id] = { startDate: e.startDate, endDate: e.endDate }
  editing.value = e
}

const saveDates = () =>
  run(async () => {
    const e = editing.value!
    await directory.updateEpisode(e.id, dates.value[e.id]!)
    editing.value = null
  })

const end = (e: EpisodeRow) =>
  confirm(`End ${e.patient.name}'s episode now? Check-ins stop immediately.`) &&
  run(() => directory.endEpisode(e.id))

const switchGroup = (e: EpisodeRow, groupId: string) =>
  groupId !== e.group.id && run(() => directory.setEpisodeGroup(e.id, groupId))

onMounted(async () => {
  ;[protocols.value, groups.value] = await Promise.all([directory.protocols(), directory.groups()])
  if (auth.isAdmin) {
    ;[staff.value, patients.value] = await Promise.all([directory.staff(), directory.patients()])
  }
  await load()
})
watch(() => route.query, load)
</script>

<template>
  <header class="page-head">
    <h1>Episodes</h1>
    <button v-if="auth.isAdmin" class="primary" @click="creating = true">New episode</button>
  </header>
  <p v-if="error" class="error">{{ error }}</p>

  <div class="filters">
    <label>
      Protocol
      <select v-model="filter.protocolId" @change="apply">
        <option value="">all</option>
        <option v-for="p in protocols" :key="p.id" :value="p.id">{{ p.name }}</option>
      </select>
    </label>
    <label>
      Group
      <select v-model="filter.groupId" @change="apply">
        <option value="">all</option>
        <option v-for="g in groups" :key="g.id" :value="g.id">{{ g.name }}</option>
      </select>
    </label>
    <label v-if="auth.isAdmin">
      Employee
      <select v-model="filter.employeeId" @change="apply">
        <option value="">all</option>
        <option v-for="s in staff" :key="s.id" :value="s.id">{{ s.name }}</option>
      </select>
    </label>
    <RouterLink :to="{ name: 'episodes' }">clear</RouterLink>
  </div>

  <table>
    <thead>
      <tr>
        <th>Patient</th>
        <th>Protocol</th>
        <th>Group</th>
        <th>Status</th>
        <th>Day</th>
        <th>Start</th>
        <th>End</th>
        <th></th>
      </tr>
    </thead>
    <tbody>
      <tr v-for="e in rows" :key="e.id" :class="{ inactive: e.status === 'ended' }">
        <td>
          <RouterLink :to="{ name: 'episode', params: { id: e.id } }">{{
            e.patient.name
          }}</RouterLink>
        </td>
        <td>{{ e.protocol.name }}</td>
        <td>
          <select
            :value="e.group.id"
            @change="switchGroup(e, ($event.target as HTMLSelectElement).value)"
          >
            <option v-for="g in groups" :key="g.id" :value="g.id">{{ g.name }}</option>
            <option v-if="!groups.some((g) => g.id === e.group.id)" :value="e.group.id">
              {{ e.group.name }}
            </option>
          </select>
        </td>
        <td>{{ e.status }}</td>
        <td>{{ e.status === 'upcoming' ? '' : e.dayNumber }}</td>
        <td>{{ e.startDate }}</td>
        <td>{{ e.endDate }}</td>
        <td>
          <button @click="edit(e)">Edit dates</button>
          <button :disabled="e.status === 'ended'" @click="end(e)">End now</button>
        </td>
      </tr>
      <tr v-if="!rows.length">
        <td colspan="8">No episodes.</td>
      </tr>
    </tbody>
  </table>

  <AppModal :open="creating" title="New episode" @close="creating = false">
    <form class="modal-form" @submit.prevent="create">
      <label>
        Patient
        <select v-model="draft.patientId">
          <option value="">select…</option>
          <option v-for="p in patients" :key="p.id" :value="p.id">{{ p.name }}</option>
        </select>
      </label>
      <label>
        Protocol
        <select v-model="draft.protocolId">
          <option value="">select…</option>
          <option v-for="p in protocols" :key="p.id" :value="p.id">{{ p.name }}</option>
        </select>
      </label>
      <label>
        Doctor group
        <select v-model="draft.doctorGroupId">
          <option value="">select…</option>
          <option v-for="g in groups" :key="g.id" :value="g.id">{{ g.name }}</option>
        </select>
      </label>
      <label>
        Start date (default today)
        <input v-model="draft.startDate" type="date" />
      </label>
      <p v-if="error" class="error">{{ error }}</p>
      <div class="actions">
        <button
          class="primary"
          :disabled="!draft.patientId || !draft.protocolId || !draft.doctorGroupId"
        >
          Create episode
        </button>
        <button type="button" @click="creating = false">Cancel</button>
      </div>
    </form>
  </AppModal>

  <AppModal
    :open="!!editing"
    :title="`Edit dates: ${editing?.patient.name ?? ''}`"
    @close="editing = null"
  >
    <form v-if="editing" class="modal-form" @submit.prevent="saveDates">
      <label>
        Start
        <input v-model="dates[editing.id]!.startDate" type="date" />
      </label>
      <label>
        End
        <input v-model="dates[editing.id]!.endDate" type="date" />
      </label>
      <p v-if="error" class="error">{{ error }}</p>
      <div class="actions">
        <button class="primary">Save dates</button>
        <button type="button" @click="editing = null">Cancel</button>
      </div>
    </form>
  </AppModal>
</template>
