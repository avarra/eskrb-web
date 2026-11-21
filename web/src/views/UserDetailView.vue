<script setup lang="ts">
import { ref, watchEffect } from 'vue'
import { useRoute } from 'vue-router'
import { directory, type UserDetail } from '@/services/directory'

const route = useRoute()
const user = ref<UserDetail | null>(null)

watchEffect(async () => {
  user.value = await directory.user(route.params.id as string)
})
</script>

<template>
  <RouterLink :to="{ name: 'users' }">&larr; Users</RouterLink>
  <template v-if="user">
    <h1>{{ user.name }}</h1>
    <p>Role: {{ user.role }}</p>

    <template v-if="user.role === 'patient'">
      <h2>Protocols</h2>
      <table>
        <thead>
          <tr>
            <th>Protocol</th>
            <th>Group</th>
            <th>Day</th>
            <th>Start</th>
            <th>End</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="e in user.episodes" :key="e.id">
            <td>{{ e.protocol.name }}</td>
            <td>{{ e.group.name }}</td>
            <td>{{ e.dayNumber }}</td>
            <td>{{ e.startDate }}</td>
            <td>{{ e.endDate }}</td>
          </tr>
          <tr v-if="!user.episodes.length">
            <td colspan="5">No protocols.</td>
          </tr>
        </tbody>
      </table>
    </template>

    <template v-else>
      <h2>Group memberships</h2>
      <table>
        <thead>
          <tr>
            <th>Group</th>
            <th>Level</th>
            <th>Title</th>
            <th>Status</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="m in user.memberships" :key="m.groupId" :class="{ inactive: !m.active }">
            <td>{{ m.groupName }}</td>
            <td>{{ m.level }}</td>
            <td>{{ m.title ?? '' }}</td>
            <td>{{ m.active ? 'active' : 'inactive' }}</td>
          </tr>
          <tr v-if="!user.memberships.length">
            <td colspan="4">No memberships.</td>
          </tr>
        </tbody>
      </table>
      <p v-if="user.role === 'staff'">
        <RouterLink :to="{ name: 'episodes', query: { employeeId: user.id } }"
          >Episodes of this employee</RouterLink
        >
      </p>
    </template>
  </template>
</template>
