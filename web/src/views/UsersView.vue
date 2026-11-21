<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { directory, type UserRow } from '@/services/directory'

const users = ref<UserRow[]>([])
const role = ref('')

async function load() {
  users.value = await directory.users(role.value)
}
onMounted(load)
watch(role, load)
</script>

<template>
  <h1>Users</h1>
  <div class="filters">
    <label>
      Role
      <select v-model="role">
        <option value="">all</option>
        <option value="patient">patient</option>
        <option value="staff">staff</option>
        <option value="admin">admin</option>
      </select>
    </label>
  </div>
  <table>
    <thead>
      <tr>
        <th>Name</th>
        <th>Role</th>
      </tr>
    </thead>
    <tbody>
      <tr v-for="u in users" :key="u.id">
        <td>
          <RouterLink :to="{ name: 'user', params: { id: u.id } }">{{ u.name }}</RouterLink>
        </td>
        <td>{{ u.role }}</td>
      </tr>
    </tbody>
  </table>
</template>
