import type {
  AuthResponse, CatalogCorrection, ChatMessage, CommunityStudent, ConversationSummary, ErasmusOpportunity,
  DeadlineReminder, ExperienceSummary, MatchResult, OpportunityAnswer, OpportunityQuestion, StudentExperience,
  StudentPlan, StudentProfile, User,
} from '../types/erasmus'

const API_BASE_URL = (import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5238').replace(/\/+$/, '')

async function request<T>(path: string, options?: RequestInit): Promise<T> {
  const controller = new AbortController()
  const timeout = window.setTimeout(() => controller.abort(), 15_000)
  const abortFromCaller = () => controller.abort()
  options?.signal?.addEventListener('abort', abortFromCaller, { once: true })

  try {
    const response = await fetch(`${API_BASE_URL}${path}`, {
      ...options,
      signal: controller.signal,
      headers: { 'Content-Type': 'application/json', ...options?.headers },
    })

    if (!response.ok) {
      const payload = await response.json().catch(() => null) as { message?: string } | null
      throw new Error(payload?.message ?? `API request failed with status ${response.status}`)
    }
    if (response.status === 204) return undefined as T
    return response.json() as Promise<T>
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') {
      throw new Error('The request took too long. Please check your connection and try again.')
    }
    throw error
  } finally {
    window.clearTimeout(timeout)
    options?.signal?.removeEventListener('abort', abortFromCaller)
  }
}

export function getOpportunities(params: { search?: string; country?: string; level?: string }) {
  const query = new URLSearchParams()
  Object.entries(params).forEach(([key, value]) => value && query.set(key, value))
  return request<ErasmusOpportunity[]>(`/api/opportunities?${query.toString()}`)
}

export function askAssistant(message: string) {
  return request<{ answer: string }>('/api/assistant/recommend', {
    method: 'POST',
    body: JSON.stringify({ message }),
  })
}

export function getMatches(profile: StudentProfile) {
  return request<MatchResult[]>('/api/matching', {
    method: 'POST',
    body: JSON.stringify(profile),
  })
}

export function registerAccount(payload: { email: string; password: string; displayName: string }) {
  return request<AuthResponse>('/api/auth/register', { method: 'POST', body: JSON.stringify(payload) })
}

export function loginAccount(payload: { email: string; password: string }) {
  return request<AuthResponse>('/api/auth/login', { method: 'POST', body: JSON.stringify(payload) })
}

export function getCurrentUser(token: string) {
  return request<User>('/api/auth/me', { cache: 'no-store', headers: { Authorization: `Bearer ${token}` } })
}

export function requestPasswordReset(payload: { email: string }) {
  return request<{ message: string; token?: string }>('/api/auth/forgot-password', {
    method: 'POST',
    body: JSON.stringify(payload),
  })
}

export function requestEmailVerification(payload: { email: string }) {
  return request<{ message: string; token?: string }>('/api/auth/request-email-verification', {
    method: 'POST',
    body: JSON.stringify(payload),
  })
}

export function verifyEmail(payload: { email: string; token: string }) {
  return request<{ message: string }>('/api/auth/verify-email', {
    method: 'POST',
    body: JSON.stringify(payload),
  })
}

export function resetPassword(payload: { email: string; token: string; newPassword: string }) {
  return request<{ message: string }>('/api/auth/reset-password', {
    method: 'POST',
    body: JSON.stringify(payload),
  })
}

export function updateAccountSettings(token: string, payload: Partial<User> & {
  country?: string | null
  studyField?: string | null
  level?: string | null
  gpa?: number | null
  preferredCountries?: string[]
  languages?: string[]
  homeUniversity?: string | null
  intake?: string | null
  bio?: string | null
  communityVisible?: boolean
  roommateSearch?: boolean
  profilePhotoDataUrl?: string | null
  removeProfilePhoto?: boolean
}) {
  return request<User>('/api/profile', {
    method: 'PUT',
    headers: { Authorization: `Bearer ${token}` },
    body: JSON.stringify(payload),
  })
}

export function getStudentPlans(token: string) {
  return request<StudentPlan[]>('/api/community/plans', { headers: { Authorization: `Bearer ${token}` } })
}

export function createStudentPlan(token: string, payload: { opportunityId: number; intake: string }) {
  return request<StudentPlan>('/api/community/plans', {
    method: 'POST', headers: { Authorization: `Bearer ${token}` }, body: JSON.stringify(payload),
  })
}

export function deleteStudentPlan(token: string, planId: number) {
  return request<void>(`/api/community/plans/${planId}`, { method: 'DELETE', headers: { Authorization: `Bearer ${token}` } })
}

export function updateStudentPlan(token: string, planId: number, payload: { stage: string; completedTasks: string[] }) {
  return request<StudentPlan>(`/api/community/plans/${planId}`, {
    method: 'PUT', headers: { Authorization: `Bearer ${token}` }, body: JSON.stringify(payload),
  })
}

export function getCommunityStudents(token: string, opportunityId: number, intake: string, roommatesOnly = false) {
  const query = new URLSearchParams({ opportunityId: String(opportunityId), intake, roommatesOnly: String(roommatesOnly) })
  return request<CommunityStudent[]>(`/api/community/students?${query.toString()}`, {
    headers: { Authorization: `Bearer ${token}` },
  })
}

export function getConversations(token: string) {
  return request<ConversationSummary[]>('/api/community/conversations', { headers: { Authorization: `Bearer ${token}` } })
}

export function getDeadlineReminders(token: string) {
  return request<DeadlineReminder[]>('/api/reminders', { headers: { Authorization: `Bearer ${token}` } })
}

export function saveDeadlineReminder(token: string, payload: { opportunityId: number; daysBefore?: number }) {
  return request<DeadlineReminder>('/api/reminders', {
    method: 'POST', headers: { Authorization: `Bearer ${token}` }, body: JSON.stringify(payload),
  })
}

export function deleteDeadlineReminder(token: string, reminderId: number) {
  return request<void>(`/api/reminders/${reminderId}`, { method: 'DELETE', headers: { Authorization: `Bearer ${token}` } })
}

export function getCommunityBlocks(token: string) {
  return request<number[]>('/api/community/blocks', { headers: { Authorization: `Bearer ${token}` } })
}

export function blockCommunityUser(token: string, userId: number) {
  return request<void>(`/api/community/users/${userId}/block`, { method: 'POST', headers: { Authorization: `Bearer ${token}` } })
}

export function unblockCommunityUser(token: string, userId: number) {
  return request<void>(`/api/community/users/${userId}/block`, { method: 'DELETE', headers: { Authorization: `Bearer ${token}` } })
}

export function reportCommunityUser(token: string, payload: { reportedUserId: number; conversationId?: number; reason: string }) {
  return request<void>('/api/community/reports', {
    method: 'POST', headers: { Authorization: `Bearer ${token}` }, body: JSON.stringify(payload),
  })
}

export function getStudentExperiences(opportunityId: number) {
  return request<ExperienceSummary>(`/api/opportunities/${opportunityId}/experiences`)
}

export function saveStudentExperience(token: string, opportunityId: number, payload: {
  rating: number; experienceType: 'Alumni' | 'Planning'; intake: string; content: string
}) {
  return request<StudentExperience>(`/api/opportunities/${opportunityId}/experiences`, {
    method: 'POST', headers: { Authorization: `Bearer ${token}` }, body: JSON.stringify(payload),
  })
}

export function getOpportunityQuestions(opportunityId: number) {
  return request<OpportunityQuestion[]>(`/api/opportunities/${opportunityId}/questions`)
}

export function askOpportunityQuestion(token: string, opportunityId: number, content: string) {
  return request<OpportunityQuestion>(`/api/opportunities/${opportunityId}/questions`, {
    method: 'POST', headers: { Authorization: `Bearer ${token}` }, body: JSON.stringify({ content }),
  })
}

export function answerOpportunityQuestion(token: string, questionId: number, payload: { content: string; experienceType: 'Alumni' | 'Planning' }) {
  return request<OpportunityAnswer>(`/api/questions/${questionId}/answers`, {
    method: 'POST', headers: { Authorization: `Bearer ${token}` }, body: JSON.stringify(payload),
  })
}

export function submitCatalogCorrection(token: string, opportunityId: number, payload: {
  fields: Record<string, string>; sourceUrl?: string; note: string
}) {
  return request<{ id: number; status: string; createdAt: string }>(`/api/opportunities/${opportunityId}/corrections`, {
    method: 'POST', headers: { Authorization: `Bearer ${token}` }, body: JSON.stringify(payload),
  })
}

export function getPendingCatalogCorrections(reviewKey: string) {
  return request<CatalogCorrection[]>('/api/catalog/corrections/review', {
    cache: 'no-store', headers: { 'X-Catalog-Review-Key': reviewKey },
  })
}

export function reviewCatalogCorrection(reviewKey: string, id: number, payload: {
  status: 'Accepted' | 'Rejected' | 'NeedsMoreInfo'; reviewerNote?: string
}) {
  return request<{ id: number; status: string; reviewerNote: string | null; reviewedAt: string }>(`/api/catalog/corrections/${id}/review`, {
    method: 'PUT', headers: { 'X-Catalog-Review-Key': reviewKey }, body: JSON.stringify(payload),
  })
}

export function startConversation(token: string, payload: { recipientUserId: number; opportunityId: number; intake: string }) {
  return request<{ conversationId: number }>('/api/community/conversations', {
    method: 'POST', headers: { Authorization: `Bearer ${token}` }, body: JSON.stringify(payload),
  })
}

export function getMessages(token: string, conversationId: number) {
  return request<ChatMessage[]>(`/api/community/conversations/${conversationId}/messages`, {
    headers: { Authorization: `Bearer ${token}` },
  })
}

export function sendMessage(token: string, conversationId: number, content: string) {
  return request<ChatMessage>(`/api/community/conversations/${conversationId}/messages`, {
    method: 'POST', headers: { Authorization: `Bearer ${token}` }, body: JSON.stringify({ content }),
  })
}
