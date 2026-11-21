<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { errorMessage } from '@/services/api'
import { directory, type Group, type StaffMember } from '@/services/directory'
import { useAuthStore } from '@/stores/auth'

const auth = useAuthStore()
const groups = ref<Group[]>([])
const staff = ref<StaffMember[]>([])
const error = ref('')

const newName = ref('')
// add-member form state per group id
const form = ref<Record<string, { userId: string; level: number; title: string }>>({})

async function load() {
  groups.value = await directory.groups()
  if (auth.isAdmin) staff.value = (await directory.staff()).filter((s) => s.role === 'staff')
  for (const g of groups.value) form.value[g.id] ??= { userId: '', level: 1, title: '' }
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
  })

const remove = (g: Group) =>
  confirm(`Delete group "${g.name}"?`) && run(() => directory.deleteGroup(g.id))

const add = (g: Group) =>
  run(async () => {
    const f = form.value[g.id]!
    await directory.addMember(g.id, {
      userId: f.userId,
      level: f.level,
      title: f.title || undefined,
    })
    form.value[g.id] = { userId: '', level: 1, title: '' }
  })

const removeMember = (g: Group, userId: string, name: string) =>
  confirm(`Remove ${name} from "${g.name}"?`) && run(() => directory.removeMember(g.id, userId))

const candidates = (g: Group) =>
  staff.value.filter((s) => !g.members.some((m) => m.userId === s.id))

onMounted(load)
</script>

<template>
  <h1>Doctor groups</h1>
  <p v-if="error" class="error">{{ error }}</p>

  <form v-if="auth.isAdmin" class="filters" @submit.prevent="create">
    <label>
      New group
      <input v-model="newName" placeholder="Name" />
    </label>
    <button :disabled="!newName.trim()">Create</button>
  </form>

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
          <form v-if="auth.isAdmin && form[g.id]" class="filters" @submit.prevent="add(g)">
            <select v-model="form[g.id]!.userId">
              <option value="">add member…</option>
              <option v-for="s in candidates(g)" :key="s.id" :value="s.id">{{ s.name }}</option>
            </select>
            <input v-model.number="form[g.id]!.level" type="number" min="1" style="width: 4rem" />
            <input v-model="form[g.id]!.title" placeholder="Title" />
            <button :disabled="!form[g.id]!.userId">Add</button>
          </form>
        </td>
        <td v-if="auth.isAdmin"><button @click="remove(g)">Delete group</button></td>
      </tr>
    </tbody>
  </table>
</template>
