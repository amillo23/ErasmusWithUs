import { useEffect, useMemo, useRef, useState, type ChangeEvent, type FormEvent } from 'react'
import {
  ArrowUpRight, Bookmark, Building2, Check, ChevronDown, Compass,
  Bell, BookOpen, Calculator, Filter, Heart, Languages, Menu, MessageCircle, Moon, Search, SlidersHorizontal, Sparkles, Sun, UserRound, Users, X,
} from 'lucide-react'
import './App.css'
import CommunityPanel from './components/CommunityPanel'
import OpportunityCard from './components/OpportunityCard'
import { AssistantPanel, BudgetPlannerPanel, ComparePanel, DeadlineRemindersPanel, PlannerPanel, StudentStoriesPanel } from './components/WorkspacePanels'
import UniversityDirectory from './components/UniversityDirectory'
import { getLanguageLocale, interfaceLanguages, usePreferences } from './preferences'
import {
  getCurrentUser,
  getConversations,
  getDeadlineReminders,
  getMatches,
  getOpportunities,
  loginAccount,
  registerAccount,
  requestPasswordReset,
  requestEmailVerification,
  resetPassword,
  deleteDeadlineReminder,
  saveDeadlineReminder,
  updateAccountSettings,
  verifyEmail,
} from './lib/api'
import type { DeadlineReminder, ErasmusOpportunity, MatchResult, StudentProfile, User } from './types/erasmus'

const savedKey = 'erasmuswithus.saved-opportunities'
const studyFieldOptions = ['Computer Science', 'Software Engineering', 'Artificial Intelligence', 'Data Science', 'Cybersecurity', 'Information Systems', 'Engineering', 'Mechanical Engineering', 'Electrical Engineering', 'Civil Engineering', 'Chemical Engineering', 'Environmental Engineering', 'Architecture', 'Urban Planning', 'Business', 'Finance', 'Marketing', 'Economics', 'Entrepreneurship', 'Management', 'Design', 'Product Design', 'Graphic Design', 'Arts', 'Media Studies', 'Film', 'Music', 'Social Sciences', 'Psychology', 'Sociology', 'Political Science', 'International Relations', 'Law', 'Medicine', 'Nursing', 'Pharmacy', 'Life Sciences', 'Biology', 'Physics', 'Mathematics']
const degreeOptions = ['Short cycle', 'Bachelor', 'Master', 'Doctorate']
const countryCatalog = ['Finland', 'Germany', 'Netherlands', 'Sweden', 'Denmark', 'Spain', 'Portugal', 'Italy', 'Austria', 'Belgium', 'France', 'Ireland', 'Poland', 'Czechia', 'Estonia', 'Latvia', 'Lithuania', 'Slovakia', 'Slovenia', 'Croatia', 'Norway', 'Switzerland', 'Greece', 'Romania', 'Hungary', 'United Kingdom', 'Iceland', 'Luxembourg', 'Malta', 'Cyprus', 'Serbia', 'Bosnia and Herzegovina', 'Andorra', 'Bulgaria', 'Turkey', 'North Macedonia', 'Montenegro', 'Georgia', 'Armenia', 'Moldova', 'Ukraine', 'Albania']
const languageOptions = ['English', 'German', 'French', 'Spanish', 'Italian', 'Dutch', 'Portuguese', 'Czech', 'Polish', 'Swedish', 'Finnish', 'Danish', 'Norwegian', 'Estonian', 'Latvian', 'Lithuanian', 'Hungarian', 'Greek', 'Romanian', 'Croatian', 'Slovenian', 'Slovak', 'Bulgarian', 'Turkish', 'Icelandic', 'Serbian', 'Arabic', 'Chinese', 'Japanese', 'Korean']
const lifestyleOptions = ['Tech ecosystem', 'Strong grant', 'Student life', 'Safe city', 'Warm climate', 'Outdoor activities', 'Creative scene', 'Affordable living', 'Urban culture', 'Quiet campus', 'Research focus', 'International vibe', 'Nightlife', 'Beach access', 'Mountains nearby', 'Startup scene', 'Arts and culture', 'Large campus', 'Small campus', 'Public transport', 'Bike friendly', 'English friendly', 'Part-time work', 'Green city', 'Historic city', 'Modern city', 'Food culture', 'Sports clubs', 'Entrepreneurship', 'Low competition']
const budgetOptions = [300, 400, 500, 600, 700, 800, 900, 1000, 1100, 1200, 1300, 1400, 1500]
const featuredCampuses = [
  {
    university: 'KU Leuven',
    city: 'Leuven',
    country: 'Belgium',
    rank: 59,
    credit: 'Bernt Rostad',
    license: 'CC BY 2.0',
    licenseSource: 'https://creativecommons.org/licenses/by/2.0/',
    image: 'https://thumb.wikimedia.org/wikipedia/commons/thumb/e/eb/KU_Leuven_%288898212081%29.jpg/1280px-KU_Leuven_%288898212081%29.jpg',
    imageSource: 'https://commons.wikimedia.org/wiki/File:KU_Leuven_(8898212081).jpg',
    qsSource: 'https://www.topuniversities.com/universities/ku-leuven',
  },
  {
    university: 'University of Helsinki',
    city: 'Helsinki',
    country: 'Finland',
    rank: 123,
    credit: 'Marty B',
    license: 'CC BY-SA 2.0',
    licenseSource: 'https://creativecommons.org/licenses/by-sa/2.0/',
    image: 'https://thumb.wikimedia.org/wikipedia/commons/thumb/b/b4/University_of_Helsinki%2C_Main_Building_%2852890870759%29.jpg/1280px-University_of_Helsinki%2C_Main_Building_%2852890870759%29.jpg',
    imageSource: 'https://commons.wikimedia.org/wiki/File:University_of_Helsinki,_Main_Building_(52890870759).jpg',
    qsSource: 'https://www.topuniversities.com/universities/university-helsinki',
  },
  {
    university: 'University of Barcelona',
    city: 'Barcelona',
    country: 'Spain',
    rank: 165,
    credit: 'Joe Mabel',
    license: 'CC BY-SA 4.0',
    licenseSource: 'https://creativecommons.org/licenses/by-sa/4.0/',
    image: 'https://thumb.wikimedia.org/wikipedia/commons/thumb/c/c5/University_of_Barcelona_-_main_buildings_07.jpg/1280px-University_of_Barcelona_-_main_buildings_07.jpg',
    imageSource: 'https://commons.wikimedia.org/wiki/File:University_of_Barcelona_-_main_buildings_07.jpg',
    qsSource: 'https://www.topuniversities.com/universities/universitat-de-barcelona',
  },
]

function readSavedIds(): number[] {
  try {
    const stored = JSON.parse(localStorage.getItem(savedKey) ?? '[]') as unknown
    return Array.isArray(stored) ? stored.filter((id): id is number => Number.isSafeInteger(id) && id > 0) : []
  } catch {
    return []
  }
}

const defaultMatchProfile: StudentProfile = {
  homeUniversity: 'My university is not listed',
  intake: 'Autumn 2026/27',
  country: 'Finland',
  studyField: 'Computer Science',
  level: 'Master',
  gpa: 3.2,
  preferredCountries: ['Finland', 'Germany', 'Netherlands'],
  languages: ['English'],
  likes: ['Tech ecosystem', 'Student life', 'Safe city', 'Strong grant'],
  dislikes: ['Very cold winters', 'High living cost', 'Very isolated city'],
  budget: 500,
}

function profileFromUser(user: User): StudentProfile {
  return {
    ...defaultMatchProfile,
    homeUniversity: user.homeUniversity ?? '',
    intake: user.intake ?? defaultMatchProfile.intake,
    country: user.country ?? '',
    studyField: user.studyField ?? defaultMatchProfile.studyField,
    level: user.level ?? defaultMatchProfile.level,
    gpa: user.gpa ?? defaultMatchProfile.gpa,
    preferredCountries: user.preferredCountries ?? [],
    languages: user.languages ?? ['English'],
  }
}

function getDataUrlByteLength(dataUrl: string) {
  const base64 = dataUrl.slice(dataUrl.indexOf(',') + 1)
  const padding = base64.endsWith('==') ? 2 : base64.endsWith('=') ? 1 : 0
  return Math.floor(base64.length * 3 / 4) - padding
}

function App() {
  const { language, setLanguage, theme, setTheme, t } = usePreferences()
  const [opportunities, setOpportunities] = useState<ErasmusOpportunity[]>([])
  const [search, setSearch] = useState('')
  const [country, setCountry] = useState('')
  const [level, setLevel] = useState('')
  const [studyFieldFilter, setStudyFieldFilter] = useState('')
  const [languageFilter, setLanguageFilter] = useState('')
  const [saved, setSaved] = useState<number[]>(readSavedIds)
  const [activeNav, setActiveNav] = useState('Discover')
  const [isLoading, setIsLoading] = useState(true)
  const [catalogAttempt, setCatalogAttempt] = useState(0)
  const [error, setError] = useState('')
  const [showFilters, setShowFilters] = useState(false)
  const [matches, setMatches] = useState<MatchResult[]>([])
  const [isMatching, setIsMatching] = useState(false)
  const [sortMode, setSortMode] = useState<'recommended' | 'grant' | 'deadline'>('recommended')
  const [reminders, setReminders] = useState<DeadlineReminder[]>([])
  const [unreadCount, setUnreadCount] = useState(0)
  const previousUnreadCount = useRef<number | null>(null)
  const searchInput = useRef<HTMLInputElement>(null)
  const [mobileMenuOpen, setMobileMenuOpen] = useState(false)
  const [showMatchForm, setShowMatchForm] = useState(false)
  const [showAccountSettings, setShowAccountSettings] = useState(false)
  const [isSavingSettings, setIsSavingSettings] = useState(false)
  const [currentUser, setCurrentUser] = useState<User | null>(null)
  const [showLanding, setShowLanding] = useState(true)
  const [landingSlide, setLandingSlide] = useState(0)
  const [pointer, setPointer] = useState({ x: 0, y: 0 })
  const [matchProfile, setMatchProfile] = useState<StudentProfile>(defaultMatchProfile)
  const [settingsProfile, setSettingsProfile] = useState<StudentProfile>(defaultMatchProfile)
  const [accountDisplayName, setAccountDisplayName] = useState('')
  const [accountBio, setAccountBio] = useState('')
  const [communityVisible, setCommunityVisible] = useState(false)
  const [roommateSearch, setRoommateSearch] = useState(false)
  const [profilePhotoDataUrl, setProfilePhotoDataUrl] = useState<string | null>(null)
  const [profilePhotoDirty, setProfilePhotoDirty] = useState(false)
  const [removeProfilePhoto, setRemoveProfilePhoto] = useState(false)
  const [communityOpportunityId, setCommunityOpportunityId] = useState<number | undefined>()
  const [authMode, setAuthMode] = useState<'login' | 'register' | 'forgot-password' | 'reset-password' | 'verify-code' | null>(null)
  const [authEmail, setAuthEmail] = useState('')
  const [authPassword, setAuthPassword] = useState('')
  const [authNewPassword, setAuthNewPassword] = useState('')
  const [authName, setAuthName] = useState('')
  const [authMessage, setAuthMessage] = useState('')
  const [settingsMessage, setSettingsMessage] = useState('')

  function hydrateAccount(user: User) {
    const profile = profileFromUser(user)
    setSettingsProfile(profile)
    setMatchProfile(profile)
    setAccountDisplayName(user.displayName)
    setAccountBio(user.bio ?? '')
    setCommunityVisible(user.communityVisible ?? false)
    setRoommateSearch(user.roommateSearch ?? false)
    setProfilePhotoDataUrl(user.profilePhotoDataUrl ?? null)
    setProfilePhotoDirty(false)
    setRemoveProfilePhoto(false)
  }

  useEffect(() => {
    let cancelled = false
    getOpportunities({})
      .then((data) => {
        if (cancelled) return
        setOpportunities(data)
        const available = new Set(data.map((item) => item.id))
        setSaved((current) => {
          const next = current.filter((id) => available.has(id))
          if (next.length !== current.length) localStorage.setItem(savedKey, JSON.stringify(next))
          return next
        })
      })
      .catch(() => { if (!cancelled) setError('Could not load destinations. Check your connection and try again.') })
      .finally(() => { if (!cancelled) setIsLoading(false) })
    return () => { cancelled = true }
  }, [catalogAttempt])

  useEffect(() => {
    if (!currentUser?.emailVerified) {
      setReminders([])
      setUnreadCount(0)
      previousUnreadCount.current = null
      return
    }
    const token = localStorage.getItem('erasmuswithus.token') ?? ''
    if (!token) return
    let cancelled = false
    getDeadlineReminders(token).then((result) => { if (!cancelled) setReminders(result) }).catch(() => {})
    const refreshUnread = async () => {
      try {
        const conversations = await getConversations(token)
        if (cancelled) return
        const nextCount = conversations.reduce((sum, conversation) => sum + (conversation.unreadCount ?? 0), 0)
        setUnreadCount(nextCount)
        if (previousUnreadCount.current !== null && nextCount > previousUnreadCount.current &&
          document.visibilityState === 'hidden' && typeof Notification !== 'undefined' && Notification.permission === 'granted') {
          new Notification('Erasmus With Us', { body: t('You have a new message from another student.') })
        }
        previousUnreadCount.current = nextCount
      } catch { /* The community can be offline while the rest of the app remains available. */ }
    }
    void refreshUnread()
    const timer = window.setInterval(() => { void refreshUnread() }, 20000)
    return () => { cancelled = true; window.clearInterval(timer) }
  }, [currentUser?.id, currentUser?.emailVerified, t])

  useEffect(() => {
    const shouldLockScroll = showMatchForm || showAccountSettings || authMode !== null
    document.body.style.overflow = shouldLockScroll ? 'hidden' : ''
    return () => { document.body.style.overflow = '' }
  }, [showMatchForm, showAccountSettings, authMode])

  useEffect(() => {
    function handleSearchShortcut(event: KeyboardEvent) {
      if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault()
        searchInput.current?.focus()
      }
    }

    window.addEventListener('keydown', handleSearchShortcut)
    return () => window.removeEventListener('keydown', handleSearchShortcut)
  }, [])

  useEffect(() => {
    const token = localStorage.getItem('erasmuswithus.token')
    if (!token) {
      setShowLanding(true)
      return
    }

    getCurrentUser(token)
      .then((user) => {
        setCurrentUser(user)
        setShowLanding(false)
        hydrateAccount(user)
      })
      .catch(() => {
        localStorage.removeItem('erasmuswithus.token')
        setShowLanding(true)
      })
  }, [])

  useEffect(() => {
    if (currentUser) {
      setShowLanding(false)
    }
  }, [currentUser])

  useEffect(() => {
    const timer = window.setInterval(() => {
      setLandingSlide((previous) => (previous + 1) % 3)
    }, 4200)
    return () => window.clearInterval(timer)
  }, [])

  const countries = useMemo(() => Array.from(new Set(opportunities.map((item) => item.country))).sort(), [opportunities])
  const visibleOpportunities = useMemo(() => {
    const query = search.trim().toLocaleLowerCase()
    const filtered = opportunities.filter((item) => {
      const matchesSearch = !query || [item.university, item.city, ...item.studyFields]
        .some((value) => value.toLocaleLowerCase().includes(query))
      const matchesCountry = !country || item.country.toLocaleLowerCase() === country.toLocaleLowerCase()
      const matchesLevel = !level || item.levels.some((itemLevel) => itemLevel.toLocaleLowerCase() === level.toLocaleLowerCase())
      const matchesField = !studyFieldFilter || item.studyFields.includes(studyFieldFilter)
      const matchesLanguage = !languageFilter || item.languages.includes(languageFilter)
      return matchesSearch && matchesCountry && matchesLevel && matchesField && matchesLanguage && (activeNav !== 'Shortlist' || saved.includes(item.id))
    })

    if (sortMode === 'grant') return filtered.sort((a, b) => b.monthlyGrant - a.monthlyGrant)
    if (sortMode === 'deadline') return filtered.sort((a, b) => Date.parse(a.deadline) - Date.parse(b.deadline))
    return filtered
  }, [activeNav, country, languageFilter, level, opportunities, saved, search, sortMode, studyFieldFilter])

  const savedOpportunities = useMemo(() => opportunities.filter((item) => saved.includes(item.id)), [opportunities, saved])
  const showCatalog = ['Discover', 'Shortlist', 'Find my match'].includes(activeNav)
  const profileCompletionFields = currentUser ? [
    currentUser.homeUniversity,
    currentUser.country,
    currentUser.studyField,
    currentUser.level,
    currentUser.intake,
    currentUser.bio,
    currentUser.preferredCountries.length > 0 ? 'set' : '',
    currentUser.languages.length > 0 ? 'set' : '',
  ] : []
  const profileProgress = Math.round(profileCompletionFields.filter((value) => Boolean(value?.trim())).length / 8 * 100)

  function toggleSaved(id: number) {
    const next = saved.includes(id) ? saved.filter((item) => item !== id) : [...saved, id]
    setSaved(next)
    localStorage.setItem(savedKey, JSON.stringify(next))
  }

  async function toggleDeadlineReminder(opportunityId: number) {
    if (!currentUser?.emailVerified) {
      setAuthMode(currentUser ? 'verify-code' : 'login')
      return
    }
    const token = localStorage.getItem('erasmuswithus.token') ?? ''
    const existing = reminders.find((item) => item.opportunityId === opportunityId)
    setError('')
    try {
      if (existing) {
        await deleteDeadlineReminder(token, existing.id)
        setReminders((current) => current.filter((item) => item.id !== existing.id))
      } else {
        const created = await saveDeadlineReminder(token, { opportunityId, daysBefore: 7 })
        setReminders((current) => [...current.filter((item) => item.opportunityId !== opportunityId), created])
      }
    } catch (requestError) {
      setError(requestError instanceof Error ? requestError.message : 'Could not update this reminder.')
    }
  }

  async function removeDeadlineReminder(reminderId: number) {
    const token = localStorage.getItem('erasmuswithus.token') ?? ''
    try {
      await deleteDeadlineReminder(token, reminderId)
      setReminders((current) => current.filter((item) => item.id !== reminderId))
    } catch (requestError) {
      setError(requestError instanceof Error ? requestError.message : 'Could not remove this reminder.')
    }
  }

  function updateMatchProfile<K extends keyof StudentProfile>(key: K, value: StudentProfile[K]) {
    setMatchProfile((previous) => ({ ...previous, [key]: value }))
  }

  function updateSettingsProfile<K extends keyof StudentProfile>(key: K, value: StudentProfile[K]) {
    setSettingsProfile((previous) => ({ ...previous, [key]: value }))
  }

  function toggleMultiChoice(target: 'preferredCountries' | 'languages' | 'likes' | 'dislikes', value: string, profileState: StudentProfile, updater: (next: StudentProfile) => void) {
    const currentValues = profileState[target] ?? []
    const nextValues = currentValues.includes(value)
      ? currentValues.filter((item) => item !== value)
      : [...currentValues, value]
    updater({ ...profileState, [target]: nextValues })
  }

  async function findMatches(profileOverride?: StudentProfile) {
    setIsMatching(true)
    setError('')
    const profile = profileOverride ?? matchProfile
    const normalizedProfile: StudentProfile = {
      ...profile,
      homeUniversity: profile.homeUniversity ?? 'My university is not listed',
      intake: profile.intake ?? 'I am not sure yet',
      country: profile.country ?? '',
      studyField: profile.studyField ?? '',
      level: profile.level ?? '',
      gpa: Number(profile.gpa ?? 0),
      preferredCountries: profile.preferredCountries ?? [],
      languages: profile.languages ?? [],
      likes: profile.likes ?? [],
      dislikes: profile.dislikes ?? [],
      budget: Number(profile.budget ?? 0),
    }
    try {
      const result = await getMatches(normalizedProfile)
      setMatches(result)
      setShowMatchForm(false)
      setTimeout(() => document.getElementById('match-preview')?.scrollIntoView({ behavior: 'smooth' }), 80)
    }
    catch {
      setError('Matching is unavailable while the API is offline.')
    }
    finally { setIsMatching(false) }
  }

  function handleNav(label: string) {
    setActiveNav(label)
    setError('')
    if (label === 'Find my match') {
      setShowMatchForm(true)
      setTimeout(() => document.getElementById('match-preview')?.scrollIntoView({ behavior: 'smooth' }), 50)
    } else if (label === 'My account') {
      if (currentUser?.emailVerified) setShowAccountSettings(true)
      else setAuthMode(currentUser ? 'verify-code' : 'login')
      window.scrollTo({ top: 0, behavior: 'smooth' })
    } else if (label === 'Shortlist') {
      setTimeout(() => document.getElementById('opportunities')?.scrollIntoView({ behavior: 'smooth' }), 50)
    } else if (label === 'Discover') {
      window.scrollTo({ top: 0, behavior: 'smooth' })
    } else {
      window.scrollTo({ top: 0, behavior: 'smooth' })
    }
  }

  async function submitAuth(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setAuthMessage('')

    try {
      const response = authMode === 'register'
        ? await registerAccount({ email: authEmail, password: authPassword, displayName: authName })
        : await loginAccount({ email: authEmail, password: authPassword })

      localStorage.setItem('erasmuswithus.token', response.token)
      setCurrentUser(response.user)
      setShowLanding(false)
      hydrateAccount(response.user)
      if (response.verificationRequired) {
        setAuthMessage(`We sent a six-letter verification code to ${response.user.email}.${response.verificationCode ? ` Development code: ${response.verificationCode}` : ''}`)
        setAuthPassword('')
        setAuthMode('verify-code')
        return
      }
      setAuthMessage(`Welcome, ${response.user.displayName}. Your account is ready.`)
      setAuthPassword('')
      setAuthMode(null)
    } catch (requestError) {
      setAuthMessage(requestError instanceof Error ? requestError.message : 'Could not complete the request.')
    }
  }

  async function handleForgotPassword() {
    try {
      const response = await requestPasswordReset({ email: authEmail })
      setAuthMessage(`${response.message}${response.token ? ` Token: ${response.token}` : ''}`)
      setAuthMode('reset-password')
    } catch (requestError) {
      setAuthMessage(requestError instanceof Error ? requestError.message : 'Could not request a password reset.')
    }
  }

  async function handleResetPassword() {
    if (!authEmail || !authPassword || authNewPassword.length < 8) {
      setAuthMessage('Enter the reset code and a new password with at least 8 characters.')
      return
    }

    try {
      const response = await resetPassword({ email: authEmail, token: authPassword, newPassword: authNewPassword })
      setAuthPassword('')
      setAuthNewPassword('')
      setAuthMode('login')
      setAuthMessage(`${response.message} You can now sign in.`)
    } catch (requestError) {
      setAuthMessage(requestError instanceof Error ? requestError.message : 'Could not reset your password.')
    }
  }

  async function handleVerifyEmailToken() {
    if (!authEmail || !authPassword) {
      setAuthMessage('Please provide your email and the verification token shown by the server.')
      return
    }

    try {
      const response = await verifyEmail({ email: authEmail, token: authPassword })
      setAuthMessage(response.message)
      setCurrentUser((user) => user ? { ...user, emailVerified: true } : user)
      setAuthPassword('')
      setAuthMode(null)
    } catch (requestError) {
      setAuthMessage(requestError instanceof Error ? requestError.message : 'Verification failed.')
    }
  }

  async function handleResendVerificationCode() {
    if (!authEmail) {
      setAuthMessage('Enter your email address first.')
      return
    }

    try {
      const response = await requestEmailVerification({ email: authEmail })
      setAuthMessage(response.message + (response.token ? ' Development code: ' + response.token : ''))
    } catch (requestError) {
      setAuthMessage(requestError instanceof Error ? requestError.message : 'Could not resend the verification code.')
    }
  }

  async function handleProfilePhotoChange(event: ChangeEvent<HTMLInputElement>) {
    const file = event.currentTarget.files?.[0]
    event.currentTarget.value = ''
    if (!file) return
    if (!file.type.startsWith('image/') || file.size > 4_000_000) {
      setSettingsMessage('Choose an image file smaller than 4 MB.')
      return
    }

    try {
      const bitmap = await createImageBitmap(file)
      if (bitmap.width > 8000 || bitmap.height > 8000) {
        bitmap.close()
        setSettingsMessage('Choose a smaller image (maximum 8,000 pixels on each side).')
        return
      }
      const scale = Math.min(1, 320 / Math.max(bitmap.width, bitmap.height))
      const canvas = document.createElement('canvas')
      canvas.width = Math.max(1, Math.round(bitmap.width * scale))
      canvas.height = Math.max(1, Math.round(bitmap.height * scale))
      const context = canvas.getContext('2d')
      if (!context) throw new Error('Could not prepare this image.')
      context.drawImage(bitmap, 0, 0, canvas.width, canvas.height)
      bitmap.close()
      let quality = 0.72
      let photo = canvas.toDataURL('image/jpeg', quality)
      while (getDataUrlByteLength(photo) > 90_000 && quality > 0.4) {
        quality = Math.max(0.4, quality - 0.08)
        photo = canvas.toDataURL('image/jpeg', quality)
      }
      if (getDataUrlByteLength(photo) > 90_000) throw new Error('Choose a smaller or simpler image.')
      setProfilePhotoDataUrl(photo)
      setProfilePhotoDirty(true)
      setRemoveProfilePhoto(false)
      setSettingsMessage('')
    } catch (photoError) {
      setSettingsMessage(photoError instanceof Error ? photoError.message : 'Could not read this image.')
    }
  }

  async function saveSettings(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setSettingsMessage('')

    const token = localStorage.getItem('erasmuswithus.token')
    if (!token) {
      setSettingsMessage('Sign in to save your account settings.')
      return
    }

    setIsSavingSettings(true)
    try {
      const profileToSave = {
        displayName: accountDisplayName.trim(),
        country: settingsProfile.country || null,
        studyField: settingsProfile.studyField || null,
        level: settingsProfile.level || null,
        gpa: Number(settingsProfile.gpa ?? 0),
        preferredCountries: settingsProfile.preferredCountries ?? [],
        languages: settingsProfile.languages ?? [],
        homeUniversity: settingsProfile.homeUniversity.trim() || null,
        intake: settingsProfile.intake.trim() || null,
        bio: accountBio.trim() || null,
        communityVisible,
        roommateSearch,
        profilePhotoDataUrl: profilePhotoDirty ? profilePhotoDataUrl : undefined,
        removeProfilePhoto,
      }
      await updateAccountSettings(token, {
        ...profileToSave,
      })

      const persistedUser = await getCurrentUser(token)
      const photoWasPersisted = removeProfilePhoto
        ? persistedUser.profilePhotoDataUrl == null
        : profilePhotoDirty ? persistedUser.profilePhotoDataUrl === profilePhotoDataUrl : true
      const profileWasPersisted = persistedUser.displayName === profileToSave.displayName &&
        persistedUser.country === profileToSave.country &&
        persistedUser.studyField === profileToSave.studyField &&
        persistedUser.level === profileToSave.level &&
        persistedUser.gpa === profileToSave.gpa &&
        JSON.stringify(persistedUser.preferredCountries) === JSON.stringify(profileToSave.preferredCountries) &&
        JSON.stringify(persistedUser.languages) === JSON.stringify(profileToSave.languages) &&
        (persistedUser.homeUniversity ?? null) === profileToSave.homeUniversity &&
        (persistedUser.intake ?? null) === profileToSave.intake &&
        (persistedUser.bio ?? null) === profileToSave.bio &&
        persistedUser.communityVisible === profileToSave.communityVisible &&
        persistedUser.roommateSearch === profileToSave.roommateSearch &&
        photoWasPersisted

      setCurrentUser(persistedUser)
      hydrateAccount(persistedUser)
      if (!profileWasPersisted) {
        throw new Error('The server returned the old profile after saving. Your changes were not confirmed; please try again.')
      }
      setShowAccountSettings(false)
    } catch (requestError) {
      setSettingsMessage(requestError instanceof Error ? requestError.message : 'Could not save your settings.')
    } finally {
      setIsSavingSettings(false)
    }
  }

  const slides = [
    {
      title: 'Study in Europe with a clearer plan',
      text: 'Compare Erasmus destinations, shortlist the right universities, and stay ahead of budgets, deadlines, and student life decisions.',
      accent: 'Better match',
    },
    {
      title: 'Choose the country that fits your goals',
      text: 'Explore exchange options by city, funding, study field, and living costs before you commit to a destination.',
      accent: 'Country fit',
    },
    {
      title: 'Build your Erasmus profile before you apply',
      text: 'Track your study preferences, connect with students, and keep your match-ready profile current for every opportunity.',
      accent: 'Profile ready',
    },
  ]

  const handleLandingMove = (event: React.MouseEvent<HTMLDivElement>) => {
    const bounds = event.currentTarget.getBoundingClientRect()
    const x = ((event.clientX - bounds.left) / bounds.width - 0.5) * 18
    const y = ((event.clientY - bounds.top) / bounds.height - 0.5) * 18
    setPointer({ x, y })
  }

  if (showLanding && !currentUser) {
    return (
      <div className="front-panel" onMouseMove={handleLandingMove}>
        <div className="front-panel__noise" />
        <div className="front-panel__orb orb-one" style={{ transform: `translate(${pointer.x * 1.2}px, ${pointer.y * 1.2}px)` }} />
        <div className="front-panel__orb orb-two" style={{ transform: `translate(${pointer.x * -0.8}px, ${pointer.y * -0.8}px)` }} />
        <div className="front-panel__content">
          <div className="front-panel__badge">ERASMUS WITH US</div>
          <div className="front-panel__header">
            <div>
              <span className="section-kicker">Smart exchange planning</span>
              <h1>Find your next study chapter with confidence.</h1>
            </div>
            <div className="front-panel__features">
              <div><strong>35+</strong><span>countries</span></div>
              <div><strong>180+</strong><span>destinations</span></div>
              <div><strong>1-click</strong><span>shortlists</span></div>
            </div>
          </div>

          <p className="front-panel__text">Discover Erasmus opportunities, compare eligible universities, build your personal shortlist, and prepare your exchange journey with practical guidance from day one.</p>

          <div className="front-panel__actions">
            <button className="primary-button" type="button" onClick={() => setAuthMode('login')}>Log in</button>
            <button className="secondary-button" type="button" onClick={() => setAuthMode('register')}>Create account</button>
          </div>

          <div className="front-panel__slider" aria-live="polite">
            {slides.map((slide, index) => (
              <article key={slide.title} className={`front-panel__slide ${landingSlide === index ? 'active' : ''}`}>
                <span>{slide.accent}</span>
                <h2>{slide.title}</h2>
                <p>{slide.text}</p>
              </article>
            ))}
            <div className="front-panel__dots" aria-label="Slider navigation">
              {slides.map((slide, index) => (
                <button
                  key={slide.title}
                  type="button"
                  className={landingSlide === index ? 'dot active' : 'dot'}
                  onClick={() => setLandingSlide(index)}
                  aria-label={`Go to slide ${index + 1}`}
                />
              ))}
            </div>
          </div>
          <section className="front-panel__stories">
            <div className="front-panel__section-heading">
              <div><span>{t('STUDENT EXPERIENCES')}</span><h2>{t('What exchange students wish they knew earlier.')}</h2></div>
              <p>{t('Practical reminders for planning your exchange. Read student-submitted stories after signing in.')}</p>
            </div>
            <div className="front-panel__lesson-grid">
              <article><strong>{t('Housing')}</strong><p>{t('Start looking early. Check the contract, deposit, location and commute before you transfer money.')}</p></article>
              <article><strong>{t('Courses')}</strong><p>{t('Confirm course availability, teaching language and credit recognition with both universities.')}</p></article>
              <article><strong>{t('Budget')}</strong><p>{t('Plan for arrival and deposit costs; your grant may not cover every monthly expense.')}</p></article>
              <article><strong>{t('Paperwork')}</strong><p>{t('Keep nomination, learning agreement, insurance and visa dates on one checklist.')}</p></article>
            </div>
          </section>
          <section className="front-panel__campuses">
            <div className="front-panel__section-heading">
              <div><span>{t('QS WORLD UNIVERSITY RANKINGS 2027')}</span><h2>{t('A first look at popular campuses.')}</h2></div>
              <p>{t('Rankings are one comparison point, not a measure of Erasmus eligibility.')}</p>
            </div>
            <div className="front-panel__campus-grid">
              {featuredCampuses.map((campus) => (
                <article className="front-panel__campus-card" key={campus.university}>
                  <a className="front-panel__campus-photo" href={campus.imageSource} target="_blank" rel="noreferrer">
                    <img src={campus.image} alt={`${campus.university} campus`} loading="lazy" />
                  </a>
                  <div className="front-panel__campus-info">
                    <div><strong>{campus.university}</strong><span>{campus.city}, {t(campus.country)}</span></div>
                    <a className="front-panel__campus-rank" href={campus.qsSource} target="_blank" rel="noreferrer">#{campus.rank}<small>{t('QS WUR 2027')}</small></a>
                    <small className="front-panel__campus-credit">{t('Photo by')} {campus.credit} · {campus.license}</small>
                  </div>
                </article>
              ))}
            </div>
          </section>
          <div className="front-panel__bottom-cta">
            <div><strong>{t('Ready to plan your exchange?')}</strong><span>{t('Sign in to save destinations, see student stories and build your profile.')}</span></div>
            <button className="primary-button" type="button" onClick={() => setAuthMode('login')}>{t('Log in to continue')} <ArrowUpRight size={15} /></button>
          </div>
        </div>
        {authMode && <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget && authMode !== 'verify-code') setAuthMode(null) }}>
          <section className="auth-modal" role="dialog" aria-modal="true" aria-labelledby="auth-title">
            <button className="modal-close" onClick={() => { if (authMode !== 'verify-code') setAuthMode(null) }} aria-label={t('Close')}><X size={18} /></button>
            <span className="section-kicker">{t('ERASMUSWITHUS ACCOUNT')}</span>
            <h2 id="auth-title">{t(authMode === 'login' ? 'Welcome back.' : authMode === 'register' ? 'Start your journey.' : authMode === 'forgot-password' || authMode === 'reset-password' ? 'Reset your password.' : 'Check your email.')}</h2>
            <p>{t(authMode === 'login' || authMode === 'register' ? 'Save destinations, build your profile, and keep your Erasmus plan in one place.' : authMode === 'verify-code' ? 'Enter the six-letter code we sent to your email before continuing.' : authMode === 'reset-password' ? 'Enter the reset code from your email and choose a new password.' : 'Request a secure password reset code for your account.')}</p>
            {authMode === 'login' || authMode === 'register' ? <form onSubmit={submitAuth}>
              {authMode === 'register' && <label>{t('Name')}<input required value={authName} onChange={(event) => setAuthName(event.target.value)} placeholder={t('Your name')} /></label>}
              <label>{t('Email')}<input required type="email" value={authEmail} onChange={(event) => setAuthEmail(event.target.value)} placeholder="you@example.com" /></label>
              <label>{t('Password')}<input required minLength={8} type="password" value={authPassword} onChange={(event) => setAuthPassword(event.target.value)} placeholder={t('At least 8 characters')} /></label>
              <button className="primary-button auth-submit" type="submit">{t(authMode === 'login' ? 'Sign in' : 'Create account')} <ArrowUpRight size={15} /></button>
            </form> : authMode === 'forgot-password' ? <form onSubmit={(event) => { event.preventDefault(); void handleForgotPassword() }}>
              <label>{t('Email')}<input required type="email" value={authEmail} onChange={(event) => setAuthEmail(event.target.value)} placeholder="you@example.com" /></label>
              <button className="primary-button auth-submit" type="submit">{t('Send reset code')} <ArrowUpRight size={15} /></button>
            </form> : authMode === 'reset-password' ? <form onSubmit={(event) => { event.preventDefault(); void handleResetPassword() }}>
              <label>{t('Reset code')}<input required autoCapitalize="characters" value={authPassword} onChange={(event) => setAuthPassword(event.target.value.toUpperCase())} placeholder={t('Paste the code from your email')} /></label>
              <label>{t('New password')}<input required minLength={8} type="password" value={authNewPassword} onChange={(event) => setAuthNewPassword(event.target.value)} placeholder={t('At least 8 characters')} /></label>
              <button className="primary-button auth-submit" type="submit">{t('Set new password')} <ArrowUpRight size={15} /></button>
            </form> : <form onSubmit={(event) => { event.preventDefault(); void handleVerifyEmailToken() }}>
              <label>{t('Six-letter verification code')}<input required maxLength={6} autoCapitalize="characters" value={authPassword} onChange={(event) => setAuthPassword(event.target.value.toUpperCase())} placeholder="ABCDEF" /></label>
              <button className="primary-button auth-submit" type="submit">{t('Verify and continue')} <ArrowUpRight size={15} /></button>
            </form>}
            {authMessage && <div className="auth-message" role="status">{t(authMessage)}</div>}
            {authMode === 'verify-code' && <div className="auth-actions"><button className="switch-auth" type="button" onClick={() => { void handleResendVerificationCode() }}>{t('Resend verification code')}</button></div>}
            {authMode !== 'verify-code' && <div className="auth-actions">{authMode === 'login' || authMode === 'register' ? <>
              <button className="switch-auth" type="button" onClick={() => { setAuthMode(authMode === 'login' ? 'register' : 'login'); setAuthMessage('') }}>{t(authMode === 'login' ? 'Need an account? Create one' : 'Already have an account? Sign in')}</button>
              <button className="switch-auth" type="button" onClick={() => { setAuthMode('forgot-password'); setAuthPassword(''); setAuthNewPassword(''); setAuthMessage('') }}>{t('Forgot password?')}</button>
            </> : <button className="switch-auth" type="button" onClick={() => { setAuthMode('login'); setAuthMessage('') }}>{t('Back to sign in')}</button>}</div>}
          </section>
        </div>}
      </div>
    )
  }

  return (
    <div className="app-shell">
      {mobileMenuOpen && <button className="nav-backdrop" aria-label="Close menu" onClick={() => setMobileMenuOpen(false)} />}
      <aside className={`sidebar ${mobileMenuOpen ? 'sidebar-open' : ''}`}>
        <div className="brand"><img className="brand-logo" src="/logo.svg" alt="Erasmus With Us" /></div>
        <div className="sidebar-label">{t('Workspace')}</div>
        <nav className="nav-list">
          {[
            ['Discover', Compass], ['Universities', Building2], ['Find my match', Sparkles], ['Assistant', MessageCircle],
            ['Community', Users], ['Compare', SlidersHorizontal], ['Shortlist', Bookmark],
            ['Planner', Check], ['Budget', Calculator], ['Reminders', Bell], ['Student stories', BookOpen], ['My account', UserRound],
          ].map(([label, Icon]) => (
            <button className={`nav-item ${activeNav === label ? 'active' : ''}`} key={label as string} onClick={() => { handleNav(label as string); setMobileMenuOpen(false) }}>
              <Icon size={18} /><span>{t(label as string)}</span>{label === 'Shortlist' && saved.length > 0 && <b>{saved.length}</b>}{label === 'Community' && unreadCount > 0 && <b>{unreadCount > 99 ? '99+' : unreadCount}</b>}{label === 'Reminders' && reminders.length > 0 && <b>{reminders.length}</b>}
            </button>
          ))}
        </nav>
        <div className="sidebar-bottom">
          <div className="sidebar-label">{t('Your journey')}</div>
          <div className="journey-card"><div className="journey-top"><span>{t('Profile progress')}</span><strong>{profileProgress}%</strong></div><div className="progress"><i style={{ width: profileProgress + '%' }} /></div><p>{t(profileProgress === 100 ? 'Your Erasmus profile is ready.' : currentUser ? 'Complete your profile to get better matches and meet students.' : 'Create an account to build your Erasmus profile.')}</p><button onClick={() => setShowMatchForm(true)}>{t('Improve profile')} <ArrowUpRight size={14} /></button></div>
          <button className="user-chip" type="button" onClick={() => currentUser?.emailVerified ? setShowAccountSettings(true) : setAuthMode(currentUser ? 'verify-code' : 'login')}>
            <span className="avatar">{currentUser?.profilePhotoDataUrl ? <img src={currentUser.profilePhotoDataUrl} alt="" /> : currentUser ? currentUser.displayName.slice(0, 2).toUpperCase() : 'AM'}</span>
            <span><strong>{currentUser ? currentUser.displayName : t('Guest explorer')}</strong><small>{t(currentUser ? currentUser.emailVerified ? 'Account settings' : 'Verify email' : 'Set up account')}</small></span>
            <ChevronDown size={15} />
          </button>
        </div>
      </aside>

      <main className="main-content">
        <header className="topbar">
          <button className="mobile-menu" aria-label={t(mobileMenuOpen ? 'Close menu' : 'Open menu')} onClick={() => setMobileMenuOpen((previous) => !previous)}><Menu size={20} /></button>
          <div className="breadcrumb">ErasmusWithUs <span>/</span> <strong>{t(activeNav)}</strong></div>
          <div className="top-actions">
            <label className="language-control" aria-label={t('Language')}><Languages size={16} /><select value={language} onChange={(event) => setLanguage(event.target.value as typeof language)} aria-label={t('Language')}>{interfaceLanguages.map((item) => <option key={item.code} value={item.code}>{item.label}</option>)}</select></label>
            <button className="icon-button theme-toggle" type="button" aria-label={t(theme === 'dark' ? 'Light mode' : 'Dark mode')} title={t(theme === 'dark' ? 'Light mode' : 'Dark mode')} onClick={() => setTheme(theme === 'dark' ? 'light' : 'dark')}>{theme === 'dark' ? <Sun size={18} /> : <Moon size={18} />}</button>
            <button className="icon-button message-shortcut" type="button" aria-label={t('Open messages')} title={t('Open messages')} onClick={() => handleNav('Community')}><MessageCircle size={18} />{unreadCount > 0 && <b>{unreadCount > 99 ? '99+' : unreadCount}</b>}</button>
            <button className="icon-button" aria-label={t('Open shortlist')} onClick={() => handleNav('Shortlist')}><Heart size={18} /></button>
            <button className="profile-button" onClick={() => currentUser?.emailVerified ? setShowAccountSettings(true) : setAuthMode(currentUser ? 'verify-code' : 'login')}>
              {t(currentUser ? currentUser.emailVerified ? 'Account settings' : 'Verify email' : 'Sign in')} <ArrowUpRight size={15} />
            </button>
          </div>
        </header>

        {error && !showCatalog && <div className="error-banner page-error"><span>{t(error)}</span><button onClick={() => setError('')} aria-label={t('Dismiss error')}><X size={16} /></button></div>}

        {showCatalog && <section className="intro">
          <div>
            <div className="eyebrow"><span /> {t('2026 / 27 exchange catalogue')}</div>
            <h1>{t('Find the place')}<br /><em>{t("you'll grow into.")}</em></h1>
            <p>{t('Explore exchange opportunities across Europe, then find the ones that fit your degree, language, budget, and ambitions.')}</p>
          </div>
          <div className="intro-note">
            <span className="note-icon"><Building2 size={18} /></span>
            <div>
              <strong>{t('Explore universities')}</strong>
              <small>{t('Browse {count} Erasmus destinations, compare study options, and check official sources.').replace('{count}', String(opportunities.length))}</small>
            </div>
            <div className="intro-note-actions">
              <button onClick={() => handleNav('Universities')}>{t('Browse universities')} <ArrowUpRight size={15} /></button>
              <button className="match-cta" onClick={() => setShowMatchForm(true)} disabled={isMatching}>{t(isMatching ? 'Matching...' : 'Find my match')} <Sparkles size={15} /></button>
            </div>
          </div>
        </section>}

        {showCatalog && <section className="stats-strip">
          <div><strong>{isLoading ? '—' : opportunities.length}</strong><span>{t('destinations indexed')}</span></div>
          <div><strong>{isLoading ? '—' : new Set(opportunities.map((item) => item.country)).size}</strong><span>{t('countries indexed')}</span></div>
          <div><strong>{isLoading ? '—' : opportunities.filter((item) => item.verificationStatus === 'NeedsReview').length}</strong><span>{t('need source review')}</span></div>
          <div className="verified-legend"><span className="status-dot" /> {t('Source and review status shown per listing')}</div>
        </section>}

        {showCatalog && <section className="assistant-next-step">
          <div className="assistant-next-step-icon"><Sparkles size={18} /></div>
          <div>
            <span className="section-kicker">{t('YOUR NEXT STEP')}</span>
            <strong>{!currentUser ? t('Start with a profile, not a random search.') : profileProgress < 75 ? t('Complete your profile to make every recommendation more personal.') : saved.length === 0 ? t('Save two destinations so you can compare them side by side.') : t('Compare your shortlist, then start an application tracker.')}</strong>
            <small>{!currentUser ? t('Add your field, GPA, language, budget and preferred countries to get evidence-based matches.') : profileProgress < 75 ? t('Your study field, level, GPA and language profile improve the quality of the shortlist.') : saved.length === 0 ? t('Saved choices unlock comparison, budgeting and a clearer decision.') : t('The coordinator still confirms the agreement, nomination and credit recognition.')}</small>
          </div>
          <button className="secondary-button" type="button" onClick={() => {
            if (!currentUser || profileProgress < 75) setShowMatchForm(true)
            else if (saved.length === 0) document.getElementById('opportunities')?.scrollIntoView({ behavior: 'smooth' })
            else handleNav('Compare')
          }}>{!currentUser || profileProgress < 75 ? t('Build my profile') : saved.length === 0 ? t('Browse destinations') : t('Compare shortlist')} <ArrowUpRight size={14} /></button>
        </section>}

        {activeNav === 'Assistant' && <AssistantPanel />}
        {activeNav === 'Compare' && <ComparePanel opportunities={savedOpportunities} onBrowse={() => handleNav('Discover')} />}
        {activeNav === 'Planner' && <PlannerPanel opportunities={opportunities} user={currentUser} onSignIn={() => setAuthMode(currentUser ? 'verify-code' : 'login')} />}
        {activeNav === 'Budget' && <BudgetPlannerPanel opportunities={opportunities} />}
        {activeNav === 'Reminders' && <DeadlineRemindersPanel reminders={reminders} user={currentUser} onRemove={(id) => { void removeDeadlineReminder(id) }} onSignIn={() => setAuthMode(currentUser ? 'verify-code' : 'login')} />}
        {activeNav === 'Student stories' && <StudentStoriesPanel opportunities={opportunities} user={currentUser} onSignIn={() => setAuthMode(currentUser ? 'verify-code' : 'login')} />}
        {activeNav === 'Universities' && <UniversityDirectory opportunities={opportunities} reminders={reminders} onToggleReminder={toggleDeadlineReminder} canSetReminder={Boolean(currentUser?.emailVerified)} user={currentUser} onSignIn={() => setAuthMode(currentUser ? 'verify-code' : 'login')} onCatalogChanged={() => { void getOpportunities({}).then(setOpportunities).catch((caught) => setError(caught instanceof Error ? caught.message : 'Could not refresh the university catalogue.')) }} onFindPeers={(id) => { setCommunityOpportunityId(id); handleNav('Community') }} />}
        {activeNav === 'Community' && <CommunityPanel opportunities={opportunities} user={currentUser} initialOpportunityId={communityOpportunityId} onEditProfile={() => handleNav('My account')} onSignIn={() => setAuthMode(currentUser ? 'verify-code' : 'login')} />}
        {activeNav === 'My account' && <section className="account-overview">
          {currentUser?.emailVerified ? <>
            <div className="account-overview-head">
              <div className="account-avatar-large">{currentUser.profilePhotoDataUrl ? <img src={currentUser.profilePhotoDataUrl} alt="" /> : currentUser.displayName.slice(0, 1).toUpperCase()}</div>
              <div><span className="section-kicker">{t('MY ACCOUNT · VERIFIED')}</span><h2>{currentUser.displayName}</h2><p>{currentUser.email}</p><p>{currentUser.homeUniversity || t('Add your home university')} · {currentUser.studyField ? t(currentUser.studyField) : t('Add your study field')}</p></div>
              <button className="primary-button" onClick={() => setShowAccountSettings(true)}>{t('Edit profile')} <ArrowUpRight size={15} /></button>
            </div>
            <div className="account-summary-grid">
              <div><small>{t('HOME UNIVERSITY')}</small><strong>{currentUser.homeUniversity || t('Not added')}</strong></div>
              <div><small>{t('PLANNED INTAKE')}</small><strong>{currentUser.intake || t('Not selected')}</strong></div>
              <div><small>{t('STUDY PROFILE')}</small><strong>{[currentUser.studyField, currentUser.level].filter(Boolean).map((value) => t(value!)).join(' · ') || t('Not completed')}</strong></div>
              <div><small>{t('COMMUNITY PROFILE')}</small><strong>{t(currentUser.communityVisible ? 'Visible to matching students' : 'Private')}</strong></div>
            </div>
            <p className="account-bio">{currentUser.bio || t('Add a short introduction so students with the same Erasmus plans know a little about you.')}</p>
            <div className="account-progress">
              <div><span className="section-kicker">{t('YOUR ERASMUS PROFILE')}</span><strong>{profileProgress}%</strong></div>
              <div className="account-progress-track"><span style={{ width: `${profileProgress}%` }} /></div>
              <small>{t('A complete profile helps students with similar plans find you.')}</small>
              <button className="text-button" type="button" onClick={() => setShowAccountSettings(true)}>{t('Complete your profile')} <ArrowUpRight size={14} /></button>
            </div>
          </> : <div className="community-gate"><span className="section-kicker">{t('MY ACCOUNT')}</span><h2>{t('Your Erasmus profile starts here.')}</h2><p>{t('Sign in and verify your email to add your home university, photo, study details, and exchange plans.')}</p><button className="primary-button" onClick={() => setAuthMode(currentUser ? 'verify-code' : 'login')}>{t('Sign in or verify email')} <ArrowUpRight size={15} /></button></div>}
          {currentUser?.emailVerified && <>
            <section className="account-experience">
              <div className="account-section-heading">
                <div><span className="section-kicker">{t('ERASMUS STUDENT EXPERIENCES')}</span><h3>{t('Learn from students who have been there.')}</h3></div>
                <button className="text-button" type="button" onClick={() => handleNav('Student stories')}>{t('Read student stories')} <ArrowUpRight size={14} /></button>
              </div>
              <p>{t('Read real student-submitted stories, including self-reported alumni experiences, about applications, courses, housing and everyday life.')}</p>
              <div className="account-lessons">
                <article><strong>{t('Housing')}</strong><span>{t('Start searching early and confirm the lease, deposit and commute before paying.')}</span></article>
                <article><strong>{t('Courses')}</strong><span>{t('Check teaching language, course availability and credit recognition with both universities.')}</span></article>
                <article><strong>{t('Budget')}</strong><span>{t('Plan for the arrival deposit and first-month costs; a grant may not cover every expense.')}</span></article>
                <article><strong>{t('Paperwork')}</strong><span>{t('Keep nomination, learning agreement, insurance and visa dates on one checklist.')}</span></article>
              </div>
            </section>
            <section className="account-campus-section">
              <div className="account-section-heading">
                <div><span className="section-kicker">{t('FEATURED CAMPUSES')}</span><h3>{t('Explore universities by QS ranking.')}</h3></div>
                <button className="text-button" type="button" onClick={() => handleNav('Universities')}>{t('Browse universities')} <ArrowUpRight size={14} /></button>
              </div>
              <div className="account-campus-grid">
                {featuredCampuses.map((campus) => (
                  <article className="account-campus-card" key={campus.university}>
                    <div className="account-campus-image">
                      <img src={campus.image} alt={`${campus.university} campus`} loading="lazy" />
                      <a className="account-campus-rank" href={campus.qsSource} target="_blank" rel="noreferrer">{t('QS WUR 2027')} <strong>#{campus.rank}</strong></a>
                    </div>
                    <div className="account-campus-info">
                      <strong>{campus.university}</strong>
                      <span>{campus.city}, {t(campus.country)}</span>
                      <span>{t('Photo by')} {campus.credit}</span>
                      <div><a href={campus.imageSource} target="_blank" rel="noreferrer">{t('Wikimedia Commons')}</a><span> · </span><a href={campus.licenseSource} target="_blank" rel="noreferrer">{campus.license}</a></div>
                    </div>
                  </article>
                ))}
              </div>
              <small className="account-campus-note">{t('QS World University Rankings 2027. Rankings are one comparison point, not a measure of exchange eligibility. Photos are linked to their Wikimedia Commons source.')}</small>
            </section>
          </>}
        </section>}

        {showCatalog && <section className="discovery" id="opportunities">
          <div className="section-heading">
            <div>
              <span className="section-kicker">{t(activeNav === 'Shortlist' ? 'YOUR SHORTLIST' : 'DISCOVER OPPORTUNITIES')}</span>
              <h2>{t(activeNav === 'Shortlist' ? 'Your saved destinations.' : 'Where could you go?')}</h2>
            </div>
            <button className="text-button" onClick={() => { handleNav('Discover'); setSearch(''); setCountry(''); setLevel('') }}>{t('View all destinations')} <ArrowUpRight size={15} /></button>
          </div>

          <div className="search-row">
            <label className="search-box"><Search size={19} /><input ref={searchInput} value={search} onChange={(event) => setSearch(event.target.value)} placeholder={t('Search university, city, or subject...')} /><kbd>⌘ K</kbd></label>
            <button className="filter-button" onClick={() => setShowFilters(!showFilters)}><Filter size={17} /> {t('Filters')} <span>{[country, level, studyFieldFilter, languageFilter].filter(Boolean).length || ''}</span></button>
            <label className="sort-control"><span className="sr-only">{t('Sort destinations')}</span><select className="sort-button" value={sortMode} onChange={(event) => setSortMode(event.target.value as typeof sortMode)}><option value="recommended">{t('Recommended')}</option><option value="grant">{t('Grant: high to low')}</option><option value="deadline">{t('Deadline: soonest')}</option></select><ChevronDown size={16} aria-hidden="true" /></label>
          </div>

          {showFilters && <div className="filter-panel"><label>{t('Country')}<select value={country} onChange={(event) => setCountry(event.target.value)}><option value="">{t('All countries')}</option>{countries.map((item) => <option key={item} value={item}>{t(item)}</option>)}</select></label><label>{t('Study field')}<select value={studyFieldFilter} onChange={(event) => setStudyFieldFilter(event.target.value)}><option value="">{t('All study fields')}</option>{studyFieldOptions.map((item) => <option key={item} value={item}>{t(item)}</option>)}</select></label><label>{t('Degree level')}<select value={level} onChange={(event) => setLevel(event.target.value)}><option value="">{t('All levels')}</option><option value="Bachelor">{t('Bachelor')}</option><option value="Master">{t('Master')}</option></select></label><label>{t('Teaching language')}<select value={languageFilter} onChange={(event) => setLanguageFilter(event.target.value)}><option value="">{t('All languages')}</option>{languageOptions.map((item) => <option key={item} value={item}>{t(item)}</option>)}</select></label><button onClick={() => { setCountry(''); setLevel(''); setStudyFieldFilter(''); setLanguageFilter('') }}>{t('Clear filters')} <X size={14} /></button></div>}
          {error && <div className="error-banner"><span>{t(error)}</span><button className="text-button" type="button" onClick={() => { setError(''); setIsLoading(true); setCatalogAttempt((attempt) => attempt + 1) }}>{t('Retry')}</button><button type="button" onClick={() => setError('')} aria-label={t('Dismiss error')}><X size={16} /></button></div>}
          <div className="results-meta"><span>{isLoading ? t('Loading destinations...') : `${visibleOpportunities.length} ${t('opportunities')}`}</span><span className="results-meta-right"><span className="status-dot" /> {t('Source and review status shown per listing')}</span></div>
          <div className="opportunity-grid">{isLoading ? [...Array(4)].map((_, index) => <div className="opportunity-card skeleton" key={index} />) : visibleOpportunities.map((opportunity) => <OpportunityCard key={opportunity.id} opportunity={opportunity} saved={saved.includes(opportunity.id)} onSave={toggleSaved} reminder={reminders.find((item) => item.opportunityId === opportunity.id)} onToggleReminder={toggleDeadlineReminder} canSetReminder={Boolean(currentUser?.emailVerified)} />)}</div>
          {!isLoading && visibleOpportunities.length === 0 && <div className="empty-state"><Compass size={28} /><strong>{t(activeNav === 'Shortlist' ? 'Your shortlist is empty' : 'No destinations found')}</strong><span>{t(activeNav === 'Shortlist' ? 'Use the bookmark icon to save a destination.' : 'Try clearing a filter or searching for another city.')}</span></div>}
        </section>}

        {showCatalog && <section className="match-preview" id="match-preview">
          <div className="match-copy">
            <span className="section-kicker">{t('A SHORTCUT TO THE RIGHT FIT')}</span>
            <h2>{t('Your next chapter')}<br /><em>{t('is closer than you think.')}</em></h2>
            <p>{t('Our matching engine weighs your field, GPA, language, budget, and what you really enjoy so the suggestions become personal rather than generic.')}</p>
            <button className="primary-button" onClick={() => setShowMatchForm(true)} disabled={isMatching}>{t(isMatching ? 'Calculating...' : 'Build my shortlist')} <ArrowUpRight size={16} /></button>
          </div>
          <div className="match-visual"><div className="orbit orbit-one" /><div className="orbit orbit-two" /><div className="match-center"><Sparkles size={23} /><strong>{matches.length ? `${matches[0].score}%` : '—'}</strong><span>{t('best match')}</span></div><span className="city-label city-a">Helsinki</span><span className="city-label city-b">Berlin</span><span className="city-label city-c">Lisbon</span></div>
        </section>}

        {showCatalog && matches.length > 0 && <section className="match-results"><div><span className="section-kicker">{t('YOUR MATCH RESULTS')}</span><h2>{t('Start with these.')}</h2><p className="match-disclaimer">{t('The score explains fit, not eligibility. Your home university coordinator must confirm the bilateral agreement and nomination.')}</p></div><div className="match-result-list">{matches.slice(0, 3).map((match) => {
          const eligibility = match.eligibility
          const compatible = Boolean(eligibility?.meetsPublishedRequirements && eligibility.hasEnoughProfileData)
          const checks = eligibility?.reasons.filter((reason) => !reason.startsWith('The bilateral')).slice(0, 4) ?? []
          const nextStep = !eligibility?.hasEnoughProfileData
            ? 'Complete your study field, degree level, and GPA to check the listed requirements.'
            : !eligibility.meetsPublishedRequirements
              ? 'Review the failed requirement checks before saving this destination.'
              : 'Confirm the bilateral agreement, course language, and nomination with your home university.'
          return <article key={match.opportunity.id}>
            <span className="match-score">{match.score}%</span>
            <div>
              <strong>{match.opportunity.university}</strong>
              <small>{match.opportunity.city}, {match.opportunity.country} · {t(match.reasons[0] ?? 'Fits your profile')}</small>
              <div className="match-breakdown">{match.breakdown.filter((factor) => factor.points !== 0).slice(0, 5).map((factor) => <span className={factor.points < 0 ? 'negative' : ''} key={factor.name}>{t(factor.name)} {factor.points > 0 ? '+' : ''}{factor.points}</span>)}</div>
              <div className={`eligibility-summary ${compatible ? 'eligible' : 'needs-check'}`}>
                <strong>{t(eligibility ? compatible ? 'Published checks look compatible' : 'Review the listed requirements' : 'Refresh the API to see eligibility checks')}</strong>
                {checks.map((reason) => <small key={reason}>{t(reason)}</small>)}
                <small><b>{t('Your next step')}:</b> {t(nextStep)}</small>
                <small>{t('The bilateral agreement and nomination must still be confirmed by your home university.')}</small>
                <small>{t('Source status')}: {t(match.opportunity.verificationStatus === 'Verified' ? 'Verified' : 'Needs review')} · {t('Last checked')} {new Date(match.opportunity.lastVerifiedAt).toLocaleDateString(getLanguageLocale(language), { day: '2-digit', month: 'short', year: 'numeric', timeZone: 'UTC' })}</small>
              </div>
              <a className="source-link match-source-link" href={match.opportunity.sourceUrl} target="_blank" rel="noreferrer">{t('View university source')} <ArrowUpRight size={13} /></a>
            </div>
            <button onClick={() => toggleSaved(match.opportunity.id)} aria-label={t('Save match')}><Bookmark size={17} fill={saved.includes(match.opportunity.id) ? 'currentColor' : 'none'} /></button>
          </article>
        })}</div></section>}
      </main>

      {showMatchForm && <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) setShowMatchForm(false) }}>
        <section className="auth-modal match-modal" role="dialog" aria-modal="true" aria-labelledby="match-title">
          <button className="modal-close" onClick={() => setShowMatchForm(false)} aria-label={t('Close')}><X size={18} /></button>
          <span className="section-kicker">{t('FIND MY MATCH')}</span><h2 id="match-title">{t('Tell us what suits you.')}</h2>
          <p>{t('Pick your preferences below to get a personalised shortlist without typing free-form answers.')}</p>
          <form className="match-form" onSubmit={(event) => { event.preventDefault(); void findMatches(matchProfile) }}>
            <div className="match-grid">
              <label>{t('Home country')}<select value={matchProfile.country} onChange={(event) => updateMatchProfile('country', event.target.value)}><option value="">{t('Select country')}</option>{countryCatalog.map((item) => <option key={item} value={item}>{t(item)}</option>)}</select></label>
              <label>{t('Study field')}<select value={matchProfile.studyField} onChange={(event) => updateMatchProfile('studyField', event.target.value)}>{studyFieldOptions.map((item) => <option key={item} value={item}>{t(item)}</option>)}</select></label>
              <label>{t('Degree level')}<select value={matchProfile.level} onChange={(event) => updateMatchProfile('level', event.target.value)}>{degreeOptions.map((item) => <option key={item} value={item}>{t(item)}</option>)}</select></label>
              <label>{t('GPA')}<select value={String(matchProfile.gpa)} onChange={(event) => updateMatchProfile('gpa', Number(event.target.value))}>{[2.0, 2.5, 2.7, 3.0, 3.2, 3.5, 3.7, 4.0].map((value) => <option key={value} value={String(value)}>{value.toFixed(1)}</option>)}</select></label>
              <label>{t('Monthly budget (EUR)')}<select value={String(matchProfile.budget ?? 0)} onChange={(event) => updateMatchProfile('budget', Number(event.target.value))}>{budgetOptions.map((value) => <option key={value} value={String(value)}>{value} EUR</option>)}</select></label>
            </div>
            <label>{t('Preferred countries')}<div className="chip-grid">{countries.map((countryName) => <button type="button" key={countryName} className={`chip ${matchProfile.preferredCountries.includes(countryName) ? 'active' : ''}`} onClick={() => toggleMultiChoice('preferredCountries', countryName, matchProfile, (next) => setMatchProfile(next))}>{t(countryName)}</button>)}</div></label>
            <label>{t('Languages')}<div className="chip-grid">{languageOptions.map((item) => <button type="button" key={item} className={`chip ${matchProfile.languages.includes(item) ? 'active' : ''}`} onClick={() => toggleMultiChoice('languages', item, matchProfile, (next) => setMatchProfile(next))}>{t(item)}</button>)}</div></label>
            <label>{t('What you like')}<div className="chip-grid">{lifestyleOptions.map((item) => <button type="button" key={item} className={`chip ${matchProfile.likes?.includes(item) ? 'active' : ''}`} onClick={() => toggleMultiChoice('likes', item, matchProfile, (next) => setMatchProfile(next))}>{t(item)}</button>)}</div></label>
            <label>{t('What you dislike')}<div className="chip-grid">{['Very cold winters', 'High living cost', 'Very isolated city', 'Low grant', 'Fast-paced city', 'Limited international community', 'Heavy bureaucracy', 'Remote campus'].map((item) => <button type="button" key={item} className={`chip ${matchProfile.dislikes?.includes(item) ? 'active' : ''}`} onClick={() => toggleMultiChoice('dislikes', item, matchProfile, (next) => setMatchProfile(next))}>{t(item)}</button>)}</div></label>
            <button type="submit" className="primary-button auth-submit" disabled={isMatching}>{t(isMatching ? 'Matching...' : 'Find best fit')} <ArrowUpRight size={15} /></button>
          </form>
        </section>
      </div>}

      {showAccountSettings && <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) setShowAccountSettings(false) }}>
        <section className="auth-modal account-modal" role="dialog" aria-modal="true" aria-labelledby="settings-title">
          <button className="modal-close" onClick={() => setShowAccountSettings(false)} aria-label={t('Close')}><X size={18} /></button>
          <span className="section-kicker">{t('MY ERASMUS PROFILE')}</span>
          <h2 id="settings-title">{t('Your account, built around your exchange.')}</h2>
          <p>{t('Set up your study profile and choose what other students planning the same university can see.')}</p>
          <form className="account-form" onSubmit={saveSettings}>
            <div className="account-photo-picker">
              <div className="account-avatar-large">{profilePhotoDataUrl ? <img src={profilePhotoDataUrl} alt={t('Profile preview')} /> : accountDisplayName.slice(0, 1).toUpperCase()}</div>
              <div className="account-photo-copy"><strong>{t('Profile photo')}</strong><small>{t('JPEG after upload. Your email and GPA stay private from other students.')}</small></div>
              <label className="photo-upload-button">{t('Choose photo')}<input type="file" accept="image/*" onChange={(event) => { void handleProfilePhotoChange(event) }} /></label>
              {profilePhotoDataUrl && <button className="photo-remove-button" type="button" onClick={() => { setProfilePhotoDataUrl(null); setProfilePhotoDirty(false); setRemoveProfilePhoto(true) }}>{t('Remove')}</button>}
            </div>
            <div className="account-section-heading"><strong>{t('About you')}</strong><span>{t('Used for your profile and university matching.')}</span></div>
            <div className="match-grid">
              <label>{t('Display name')}<input required minLength={2} maxLength={80} value={accountDisplayName} onChange={(event) => setAccountDisplayName(event.target.value)} /></label>
              <label>{t('Home university')}<input maxLength={160} value={settingsProfile.homeUniversity} onChange={(event) => updateSettingsProfile('homeUniversity', event.target.value)} placeholder={t('Your current university')} /></label>
              <label>{t('Home country')}<select value={settingsProfile.country} onChange={(event) => updateSettingsProfile('country', event.target.value)}><option value="">{t('Select country')}</option>{countryCatalog.map((item) => <option key={item} value={item}>{t(item)}</option>)}</select></label>
              <label>{t('Study field')}<select value={settingsProfile.studyField} onChange={(event) => updateSettingsProfile('studyField', event.target.value)}>{studyFieldOptions.map((item) => <option key={item} value={item}>{t(item)}</option>)}</select></label>
              <label>{t('Degree level')}<select value={settingsProfile.level} onChange={(event) => updateSettingsProfile('level', event.target.value)}>{degreeOptions.map((item) => <option key={item} value={item}>{t(item)}</option>)}</select></label>
              <label>{t('Current GPA')}<select value={String(settingsProfile.gpa)} onChange={(event) => updateSettingsProfile('gpa', Number(event.target.value))}>{[2.0, 2.5, 2.7, 3.0, 3.2, 3.5, 3.7, 4.0].map((value) => <option key={value} value={String(value)}>{value.toFixed(1)}</option>)}</select></label>
              <label>{t('Planned Erasmus intake')}<select value={settingsProfile.intake} onChange={(event) => updateSettingsProfile('intake', event.target.value)}>{['Autumn 2026/27', 'Spring 2026/27', 'Autumn 2027/28', 'Spring 2027/28'].map((item) => <option key={item} value={item}>{t(item)}</option>)}</select></label>
              <label className="account-bio-field">{t('Short introduction')}<textarea maxLength={320} value={accountBio} onChange={(event) => setAccountBio(event.target.value)} placeholder={t('What are you studying and what are you looking forward to?')} rows={3} /></label>
            </div>
            <label className="account-field-label">{t('Preferred countries')}<div className="chip-grid">{countries.map((countryName) => <button type="button" key={countryName} className={'chip ' + (settingsProfile.preferredCountries.includes(countryName) ? 'active' : '')} onClick={() => toggleMultiChoice('preferredCountries', countryName, settingsProfile, (next) => setSettingsProfile(next))}>{t(countryName)}</button>)}</div></label>
            <label className="account-field-label">{t('Languages')}<div className="chip-grid">{languageOptions.map((language) => <button type="button" key={language} className={'chip ' + (settingsProfile.languages.includes(language) ? 'active' : '')} onClick={() => toggleMultiChoice('languages', language, settingsProfile, (next) => setSettingsProfile(next))}>{t(language)}</button>)}</div></label>
            <label className="community-visibility"><input type="checkbox" checked={communityVisible} onChange={(event) => setCommunityVisible(event.target.checked)} /><span><strong>{t('Show my profile to matching students')}</strong><small>{t('Students with the same destination and intake can see your photo, display name, home university, field and introduction. Your email and GPA are never shown.')}</small></span></label>
            <label className="community-visibility"><input type="checkbox" checked={roommateSearch} onChange={(event) => setRoommateSearch(event.target.checked)} /><span><strong>{t('I am looking for a roommate')}</strong><small>{t('Let students going to the same university and intake find you for shared housing. This is optional and does not reveal your contact details.')}</small></span></label>
            {settingsMessage && <div className="auth-message" role="status">{t(settingsMessage)}</div>}
            <button className="primary-button auth-submit" type="submit" disabled={isSavingSettings}>{t(isSavingSettings ? 'Saving…' : 'Save my Erasmus profile')} <ArrowUpRight size={15} /></button>
          </form>
        </section>
      </div>}

      {authMode && <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget && authMode !== 'verify-code') setAuthMode(null) }}>
        <section className="auth-modal" role="dialog" aria-modal="true" aria-labelledby="auth-title">
          <button className="modal-close" onClick={() => { if (authMode !== 'verify-code') setAuthMode(null) }} aria-label={t('Close')}><X size={18} /></button>
          <span className="section-kicker">{t('ERASMUSWITHUS ACCOUNT')}</span>
          <h2 id="auth-title">{t(authMode === 'login' ? 'Welcome back.' : authMode === 'register' ? 'Start your journey.' : authMode === 'forgot-password' || authMode === 'reset-password' ? 'Reset your password.' : 'Check your email.')}</h2>
          <p>{t(authMode === 'login' || authMode === 'register' ? 'Save destinations, build your profile, and keep your Erasmus plan in one place.' : authMode === 'verify-code' ? 'Enter the six-letter code we sent to your email before continuing.' : authMode === 'reset-password' ? 'Enter the reset code from your email and choose a new password.' : 'Request a secure password reset code for your account.')}</p>
          {authMode === 'login' || authMode === 'register' ? <form onSubmit={submitAuth}>
            {authMode === 'register' && <label>{t('Name')}<input required value={authName} onChange={(event) => setAuthName(event.target.value)} placeholder={t('Your name')} /></label>}
            <label>{t('Email')}<input required type="email" value={authEmail} onChange={(event) => setAuthEmail(event.target.value)} placeholder="you@example.com" /></label>
            <label>{t('Password')}<input required minLength={8} type="password" value={authPassword} onChange={(event) => setAuthPassword(event.target.value)} placeholder={t('At least 8 characters')} /></label>
            <button className="primary-button auth-submit" type="submit">{t(authMode === 'login' ? 'Sign in' : 'Create account')} <ArrowUpRight size={15} /></button>
          </form> : authMode === 'forgot-password' ? <form onSubmit={(event) => { event.preventDefault(); void handleForgotPassword() }}>
            <label>{t('Email')}<input required type="email" value={authEmail} onChange={(event) => setAuthEmail(event.target.value)} placeholder="you@example.com" /></label>
            <button className="primary-button auth-submit" type="submit">{t('Send reset code')} <ArrowUpRight size={15} /></button>
          </form> : authMode === 'reset-password' ? <form onSubmit={(event) => { event.preventDefault(); void handleResetPassword() }}>
            <label>{t('Reset code')}<input required autoCapitalize="characters" value={authPassword} onChange={(event) => setAuthPassword(event.target.value.toUpperCase())} placeholder={t('Paste the code from your email')} /></label>
            <label>{t('New password')}<input required minLength={8} type="password" value={authNewPassword} onChange={(event) => setAuthNewPassword(event.target.value)} placeholder={t('At least 8 characters')} /></label>
            <button className="primary-button auth-submit" type="submit">{t('Set new password')} <ArrowUpRight size={15} /></button>
          </form> : <form onSubmit={(event) => { event.preventDefault(); void handleVerifyEmailToken() }}>
            <label>{t('Six-letter verification code')}<input required maxLength={6} autoCapitalize="characters" value={authPassword} onChange={(event) => setAuthPassword(event.target.value.toUpperCase())} placeholder="ABCDEF" /></label>
            <button className="primary-button auth-submit" type="submit">{t('Verify and continue')} <ArrowUpRight size={15} /></button>
          </form>}
          {authMessage && <div className="auth-message" role="status">{t(authMessage)}</div>}
          {authMode === 'verify-code' && <div className="auth-actions"><button className="switch-auth" type="button" onClick={() => { void handleResendVerificationCode() }}>{t('Resend verification code')}</button></div>}
          {authMode !== 'verify-code' && <div className="auth-actions">{authMode === 'login' || authMode === 'register' ? <>
            <button className="switch-auth" type="button" onClick={() => { setAuthMode(authMode === 'login' ? 'register' : 'login'); setAuthMessage('') }}>{t(authMode === 'login' ? 'Need an account? Create one' : 'Already have an account? Sign in')}</button>
            <button className="switch-auth" type="button" onClick={() => { setAuthMode('forgot-password'); setAuthPassword(''); setAuthNewPassword(''); setAuthMessage('') }}>{t('Forgot password?')}</button>
          </> : <button className="switch-auth" type="button" onClick={() => { setAuthMode('login'); setAuthMessage('') }}>{t('Back to sign in')}</button>}</div>}
        </section>
      </div>}
    </div>
  )
}

export default App
