<script setup lang="ts">
import { ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'

const auth = useAuthStore()
const router = useRouter()
const route = useRoute()

const token = ref('')
const error = ref('')
const busy = ref(false)

async function submit() {
  busy.value = true
  error.value = ''
  try {
    await auth.login(token.value)
    await router.replace((route.query.redirect as string) || '/')
  } catch {
    error.value = 'That token is not valid.'
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="ground">
    <div class="brand">eSkrb</div>
    <form class="card" @submit.prevent="submit">
      <h1>Sign in</h1>
      <p class="meta">Use the access token you were given for the staff dashboard.</p>
      <label>
        Access token
        <input v-model="token" type="password" autocomplete="off" autofocus />
      </label>
      <button class="primary" :disabled="busy || !token">Sign in</button>
      <p v-if="error" class="error">{{ error }}</p>
    </form>
  </div>
</template>

<style scoped>
.ground {
  min-height: 100vh;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: var(--space-5);
  padding: var(--space-5) var(--space-4);
  background:
    radial-gradient(120% 40% at 50% 0%, rgba(255, 255, 255, 0.1), transparent 70%), var(--ground);
}
.brand {
  color: #fff;
  font: 700 36px/40px var(--font);
  letter-spacing: -0.5px;
}
.card {
  width: 100%;
  max-width: 400px;
  display: flex;
  flex-direction: column;
  gap: var(--space-3);
  background: var(--sheet);
  border-radius: var(--radius-card);
  padding: var(--space-5);
  box-shadow: var(--shadow-object);
}
.card h1 {
  margin: 0;
}
.card p {
  margin: 0;
}
label {
  display: flex;
  flex-direction: column;
  gap: var(--space-1);
}
</style>
