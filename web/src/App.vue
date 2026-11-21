<script setup lang="ts">
import { RouterLink, RouterView, useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'

const auth = useAuthStore()
const router = useRouter()

function logout() {
  auth.logout()
  router.push('/login')
}
</script>

<template>
  <nav v-if="auth.user">
    <RouterLink to="/">Home</RouterLink>
    <template v-if="auth.isStaff">
      <RouterLink to="/episodes">Episodes</RouterLink>
      <RouterLink to="/patients">Patients</RouterLink>
      <RouterLink to="/groups">Doctor groups</RouterLink>
      <RouterLink to="/protocols">Protocols</RouterLink>
      <RouterLink v-if="auth.isAdmin" to="/users">Users</RouterLink>
    </template>
    <span class="spacer" />
    <span>{{ auth.user.name }} ({{ auth.user.role }})</span>
    <button @click="logout">Sign out</button>
  </nav>
  <main>
    <RouterView />
  </main>
</template>

<style scoped>
nav {
  display: flex;
  gap: 1rem;
  align-items: center;
  padding: 0.75rem 1rem;
  border-bottom: 1px solid var(--color-border);
}
.spacer {
  flex: 1;
}
main {
  padding: 1rem;
}
</style>

<style>
table {
  border-collapse: collapse;
  width: 100%;
}
th,
td {
  text-align: left;
  padding: 0.4rem 0.75rem;
  border-bottom: 1px solid var(--color-border);
}
.filters {
  display: flex;
  gap: 1rem;
  align-items: end;
  margin-bottom: 1rem;
}
.filters label {
  display: flex;
  flex-direction: column;
  font-size: 0.85rem;
}
.error {
  color: #c0392b;
}
.inactive {
  opacity: 0.5;
}
</style>
