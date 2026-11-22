<script setup lang="ts">
import { onMounted, ref } from 'vue'
import AppModal from '@/components/AppModal.vue'
import { errorMessage } from '@/services/api'
import { directory, type Group, type StaffMember } from '@/services/directory'
import { useAuthStore } from '@/stores/auth'

const auth = useAuthStore()
const groups = ref<Group[]>([])
const staff = ref<StaffMember[]>([])
const error = ref('')

const newName = ref('')
const creating = ref(false)
// add-member modal: the group it is open for + its form
const memberFor = ref<Group | null>(null)
const member = ref({ userId: '', level: 1, title: '' })

async function load() {
  groups.value = await directory.groups()
  if (auth.isAdmin) staff.value = (await directory.staff()).filter((s) => s.role === 'staff')
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
    await directory.createGroup(newName.value)
    newName.value = ''
    creating.value = false
  })

const remove = (g: Group) =>
  confirm(`Delete group "${g.name}"?`) && run(() => directory.deleteGroup(g.id))

const add = () =>
  run(async () => {
    const g = memberFor.value!
    await directory.addMember(g.id, {
      userId: member.value.userId,
      level: member.value.level,
      title: member.value.title || undefined,
    })
    memberFor.value = null
  })

function openAdd(g: Group) {
  member.value = { userId: '', level: 1, title: '' }
  memberFor.value = g
}

const removeMember = (g: Group, userId: string, name: string) =>
  confirm(`Remove ${name} from "${g.name}"?`) && run(() => directory.removeMember(g.id, userId))

const candidates = (g: Group) =>
  staff.value.filter((s) => !g.members.some((m) => m.userId === s.id))

onMounted(load)
</script>

<template>
  <header class="page-head">
    <h1>Doctor groups</h1>
    <button v-if="auth.isAdmin" class="primary" @click="creating = true">New group</button>
  </header>
  <p v-if="error" class="error">{{ error }}</p>

  <table>
    <thead>
      <tr>
        <th>Group</th>
        <th>Episodes</th>
        <th>Members (level)</th>
        <th v-if="auth.isAdmin"></th>
      </tr>
    </thead>
    <tbody>
      <tr v-for="g in groups" :key="g.id">
        <td>{{ g.name }}</td>
        <td>
          <RouterLink :to="{ name: 'episodes', query: { groupId: g.id } }">{{
            g.episodeCount
          }}</RouterLink>
        </td>
        <td>
          <div v-for="m in g.members" :key="m.userId" :class="{ inactive: !m.active }">
            {{ m.name }} (L{{ m.level }}<template v-if="m.title">, {{ m.title }}</template
            >)<template v-if="!m.active"> inactive</template>
            <button v-if="auth.isAdmin" @click="removeMember(g, m.userId, m.name)">remove</button>
          </div>
          <button v-if="auth.isAdmin" class="small" @click="openAdd(g)">Add member</button>
        </td>
        <td v-if="auth.isAdmin"><button @click="remove(g)">Delete group</button></td>
      </tr>
    </tbody>
  </table>

  <AppModal :open="creating" title="New doctor group" @close="creating = false">
    <form class="modal-form" @submit.prevent="create">
      <label>
        Name
        <input v-model="newName" maxlength="100" autofocus />
      </label>
      <div class="actions">
        <button class="primary" :disabled="!newName.trim()">Create group</button>
        <button type="button" @click="creating = false">Cancel</button>
      </div>
    </form>
  </AppModal>

  <AppModal
    :open="!!memberFor"
    :title="`Add a doctor to ${memberFor?.name ?? ''}`"
    @close="memberFor = null"
  >
    <form v-if="memberFor" class="modal-form" @submit.prevent="add">
      <label>
        Doctor
        <select v-model="member.userId">
          <option value="">select…</option>
          <option v-for="s in candidates(memberFor)" :key="s.id" :value="s.id">{{ s.name }}</option>
        </select>
      </label>
      <label>
        Level (1 = first responder, higher = escalated to)
        <input v-model.number="member.level" type="number" min="1" />
      </label>
      <label>
        Title (optional)
        <input v-model="member.title" maxlength="100" />
      </label>
      <p v-if="error" class="error">{{ error }}</p>
      <div class="actions">
        <button class="primary" :disabled="!member.userId">Add doctor</button>
        <button type="button" @click="memberFor = null">Cancel</button>
      </div>
    </form>
  </AppModal>
</template>
