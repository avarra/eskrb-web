import { createRouter, createWebHistory } from 'vue-router'
import HomeView from '../views/HomeView.vue'
import LoginView from '../views/LoginView.vue'
import { useAuthStore } from '@/stores/auth'

const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes: [
    { path: '/', name: 'home', component: HomeView },
    { path: '/login', name: 'login', component: LoginView, meta: { public: true } },
    {
      path: '/triage',
      name: 'triage',
      component: () => import('../views/TriageView.vue'),
      meta: { staff: true },
    },
    {
      path: '/episodes',
      name: 'episodes',
      component: () => import('../views/EpisodesView.vue'),
      meta: { staff: true },
    },
    {
      path: '/episodes/:id',
      name: 'episode',
      component: () => import('../views/EpisodeDetailView.vue'),
      meta: { staff: true },
    },
    {
      path: '/patients',
      name: 'patients',
      component: () => import('../views/PatientsView.vue'),
      meta: { staff: true },
    },
    {
      path: '/buddies',
      name: 'buddies',
      component: () => import('../views/BuddiesView.vue'),
      meta: { staff: true },
    },
    {
      path: '/groups',
      name: 'groups',
      component: () => import('../views/GroupsView.vue'),
      meta: { staff: true },
    },
    {
      path: '/protocols',
      name: 'protocols',
      component: () => import('../views/ProtocolsView.vue'),
      meta: { staff: true },
    },
    {
      path: '/protocols/:id',
      name: 'protocol',
      component: () => import('../views/ProtocolDetailView.vue'),
      meta: { staff: true },
    },
    {
      path: '/notifications',
      name: 'notifications',
      component: () => import('../views/NotificationsView.vue'),
      meta: { admin: true },
    },
    {
      path: '/users',
      name: 'users',
      component: () => import('../views/UsersView.vue'),
      meta: { admin: true },
    },
    {
      path: '/users/:id',
      name: 'user',
      component: () => import('../views/UserDetailView.vue'),
      meta: { admin: true },
    },
  ],
})

router.beforeEach((to) => {
  const auth = useAuthStore()
  if (!to.meta.public && !auth.isAuthenticated) {
    return { name: 'login', query: { redirect: to.fullPath } }
  }
  if (to.name === 'login' && auth.isAuthenticated) return { name: 'home' }
  if (to.name === 'home' && auth.isStaff) return { name: 'triage' }
  if ((to.meta.staff && !auth.isStaff) || (to.meta.admin && !auth.isAdmin)) return { name: 'home' }
})

export default router
