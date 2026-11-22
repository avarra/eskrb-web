<script setup lang="ts">
import { onMounted, ref } from 'vue'
import AppModal from '@/components/AppModal.vue'
import { errorMessage } from '@/services/api'
import { directory, type NotifyResult, type PushUser, type UserRow } from '@/services/directory'

const registered = ref<PushUser[]>([])
const users = ref<UserRow[]>([])
const error = ref('')

const open = ref(false)
const audience = ref<'user' | 'patients' | 'staff'>('user')
const userId = ref('')
const title = ref('')
const body = ref('')
const busy = ref(false)
const result = ref<NotifyResult | null>(null)

const when = (iso: string) => new Date(iso.endsWith('Z') ? iso : iso + 'Z').toLocaleString()

async function load() {
  try {
    ;[registered.value, users.value] = await Promise.all([
      directory.pushDevices(),
      directory.users(),
    ])
  } catch (e) {
    error.value = errorMessage(e)
  }
}

function openSend(forUser?: PushUser) {
  audience.value = forUser ? 'user' : 'patients'
  userId.value = forUser?.userId ?? ''
  result.value = null
  error.value = ''
  open.value = true
}

async function send() {
  busy.value = true
  error.value = ''
  try {
    result.value = await directory.sendNotification({
      audience: audience.value,
      userId: audience.value === 'user' ? userId.value : undefined,
      title: title.value,
      body: body.value,
    })
  } catch (e) {
    error.value = errorMessage(e)
  } finally {
    busy.value = false
  }
}

onMounted(load)
</script>

<template>
  <header class="page-head">
    <h1>Notifications</h1>
    <button class="primary" @click="openSend()">Send notification</button>
  </header>
  <p class="meta">
    Push notifications go to phones that signed in to the app and registered for them. Lock screens
    are public: keep the text generic, with no names or health details.
  </p>
  <p v-if="error && !open" class="error">{{ error }}</p>

  <h2>Registered devices</h2>
  <table>
    <thead>
      <tr>
        <th>User</th>
        <th>Role</th>
        <th>Devices</th>
        <th></th>
      </tr>
    </thead>
    <tbody>
      <tr v-for="u in registered" :key="u.userId">
        <td>{{ u.name }}</td>
        <td>{{ u.role }}</td>
        <td>
          <span v-for="(d, i) in u.devices" :key="i" class="chip dev"
            >{{ d.platform }} &middot; since {{ when(d.registeredAt) }}</span
          >
        </td>
        <td><button class="small" @click="openSend(u)">Send</button></td>
      </tr>
      <tr v-if="!registered.length">
        <td colspan="4">No device has registered yet.</td>
      </tr>
    </tbody>
  </table>

  <AppModal :open="open" title="Send notification" @close="open = false">
    <form v-if="!result" class="modal-form" @submit.prevent="send">
      <label>
        To
        <select v-model="audience">
          <option value="user">One user</option>
          <option value="patients">All patients</option>
          <option value="staff">All staff</option>
        </select>
      </label>
      <label v-if="audience === 'user'">
        User
        <select v-model="userId">
          <option value="">select…</option>
          <option v-for="u in users" :key="u.id" :value="u.id">{{ u.name }} ({{ u.role }})</option>
        </select>
      </label>
      <label>
        Title
        <input v-model="title" maxlength="100" />
      </label>
      <label>
        Message
        <textarea v-model="body" rows="3" maxlength="500" />
      </label>
      <p v-if="error" class="error">{{ error }}</p>
      <div class="actions">
        <button
          class="primary"
          :disabled="busy || !title.trim() || !body.trim() || (audience === 'user' && !userId)"
        >
          Send now
        </button>
        <button type="button" @click="open = false">Cancel</button>
      </div>
    </form>
    <div v-else class="modal-form">
      <p v-if="result.disabled" class="error">Notifications are switched off on the server.</p>
      <p v-else-if="!result.attempted">
        Nobody in that audience has a registered device, so nothing was sent.
      </p>
      <p v-else>
        Sent to {{ result.attempted }} device{{ result.attempted === 1 ? '' : 's' }};
        {{ result.accepted }} accepted by Expo<template v-if="result.removed"
          >, {{ result.removed }} stale device{{
            result.removed === 1 ? '' : 's'
          }}
          removed</template
        >.
      </p>
      <div class="actions">
        <button class="primary" @click="open = false">Done</button>
      </div>
    </div>
  </AppModal>
</template>

<style scoped>
.dev {
  margin-right: var(--space-2);
}
</style>
