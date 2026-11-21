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
    error.value = 'Invalid token'
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <form @submit.prevent="submit">
    <h1>eSkrb sign in</h1>
    <input v-model="token" type="password" placeholder="Access token" autocomplete="off" />
    <button :disabled="busy || !token">Sign in</button>
    <p v-if="error">{{ error }}</p>
  </form>
</template>
