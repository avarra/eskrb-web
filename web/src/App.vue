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
  <div v-if="auth.user" class="shell">
    <aside class="rail">
      <div class="brand">eSkrb</div>
      <nav>
        <template v-if="auth.isStaff">
          <RouterLink to="/triage">Triage</RouterLink>
          <RouterLink to="/episodes">Episodes</RouterLink>
          <RouterLink to="/patients">Patients</RouterLink>
          <RouterLink to="/groups">Doctor groups</RouterLink>
          <RouterLink to="/protocols">Protocols</RouterLink>
          <RouterLink v-if="auth.isAdmin" to="/users">Users</RouterLink>
          <RouterLink v-if="auth.isAdmin" to="/notifications">Notifications</RouterLink>
        </template>
        <RouterLink v-else to="/">Home</RouterLink>
      </nav>
      <div class="who">
        <div class="name">{{ auth.user.name }}</div>
        <div class="role">{{ auth.user.role }}</div>
        <button class="outline" @click="logout">Sign out</button>
      </div>
    </aside>
    <main class="sheet">
      <RouterView />
    </main>
  </div>
  <RouterView v-else />
</template>

<style scoped>
.shell {
  display: grid;
  grid-template-columns: 232px 1fr;
  min-height: 100vh;
  background:
    radial-gradient(120% 40% at 50% 0%, rgba(255, 255, 255, 0.1), transparent 70%), var(--ground);
}
.rail {
  position: sticky;
  top: 0;
  height: 100vh;
  display: flex;
  flex-direction: column;
  padding: var(--space-5) var(--space-4) var(--space-4);
  color: var(--ground-ink);
}
.brand {
  font: 700 24px/30px var(--font);
  letter-spacing: -0.3px;
  padding: 0 var(--space-3) var(--space-5);
}
nav {
  display: flex;
  flex-direction: column;
  gap: var(--space-1);
  flex: 1;
}
nav a {
  color: var(--ground-ink-2);
  font: 500 16px/22px var(--font);
  padding: 10px var(--space-3);
  border-radius: 999px;
}
nav a:hover {
  background: var(--tonal);
  text-decoration: none;
}
nav a.router-link-active {
  background: var(--tonal);
  box-shadow: inset 0 0 0 1px var(--tonal-line);
  color: var(--ground-ink);
  font-weight: 600;
}
.who {
  padding: 0 var(--space-3);
}
.name {
  font: 600 15px/20px var(--font);
}
.role {
  color: var(--ground-ink-2);
  font-size: 13px;
  margin-bottom: var(--space-3);
  text-transform: capitalize;
}
.who button {
  color: var(--ground-ink);
  min-height: 36px;
  width: 100%;
}
.sheet {
  margin: var(--space-4) var(--space-4) 0 0;
  background: var(--sheet);
  border-radius: var(--radius-sheet) var(--radius-sheet) 0 0;
  padding: var(--space-6) var(--space-6) var(--space-6);
  min-height: calc(100vh - var(--space-4));
  min-width: 0;
}
@media (max-width: 860px) {
  .shell {
    grid-template-columns: 1fr;
  }
  .rail {
    position: static;
    height: auto;
  }
  nav {
    flex-direction: row;
    flex-wrap: wrap;
  }
  .sheet {
    margin: 0;
    padding: var(--space-5) var(--space-4);
  }
}
</style>
