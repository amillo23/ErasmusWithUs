import { useEffect, useMemo, useState, type FormEvent } from 'react'
import { ArrowUpRight, Bell, CalendarDays, Calculator, Check, ClipboardList, MessageSquareQuote, Plus, Star, TrendingDown, WalletCards } from 'lucide-react'
import { answerOpportunityQuestion, askAssistant, askOpportunityQuestion, createStudentPlan, getOpportunityQuestions, getStudentPlans, getStudentExperiences, saveStudentExperience, updateStudentPlan } from '../lib/api'
import type { DeadlineReminder, ErasmusOpportunity, ExperienceSummary, OpportunityQuestion, StudentPlan, User } from '../types/erasmus'
import { getLanguageLocale, usePreferences } from '../preferences'

export function AssistantPanel() {
  const { t } = usePreferences()
  const [message, setMessage] = useState('')
  const [answer, setAnswer] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!message.trim()) return
    setBusy(true); setError('')
    try {
      setAnswer((await askAssistant(message.trim())).answer)
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'The assistant is unavailable.')
    } finally { setBusy(false) }
  }

  return <section className="workspace-panel assistant-page">
    <span className="section-kicker">{t('PLAN WITH CONFIDENCE')}</span>
    <h2>{t('Ask the Erasmus assistant.')}</h2>
    <p>{t('Get a practical shortlist or budget sanity check. Verify final deadlines, grants, and eligibility with official university sources.')}</p>
    <form className="assistant-form" onSubmit={(event) => { void submit(event) }}>
      <textarea value={message} onChange={(event) => setMessage(event.target.value)} placeholder={t('Example: I study Computer Science, have EUR 650 per month, and want a safe city with strong student life.')} rows={5} />
      <button className="primary-button" type="submit" disabled={busy || !message.trim()}>{busy ? t('Thinking…') : t('Ask assistant')} <ArrowUpRight size={15} /></button>
    </form>
    {error && <p className="inline-error" role="alert">{t(error)}</p>}
    {answer && <div className="assistant-answer"><span className="section-kicker">{t('ASSISTANT')}</span><p>{answer}</p></div>}
  </section>
}

export function ComparePanel({ opportunities, onBrowse }: { opportunities: ErasmusOpportunity[]; onBrowse: () => void }) {
  const { t, language } = usePreferences()
  const [selectedIds, setSelectedIds] = useState<number[]>(() => opportunities.slice(0, 3).map((item) => item.id))
  useEffect(() => {
    setSelectedIds((current) => {
      const available = new Set(opportunities.map((item) => item.id))
      const retained = current.filter((id) => available.has(id)).slice(0, 3)
      return retained.length > 0 ? retained : opportunities.slice(0, 3).map((item) => item.id)
    })
  }, [opportunities])
  const compared = selectedIds.map((id) => opportunities.find((item) => item.id === id)).filter((item): item is ErasmusOpportunity => Boolean(item))
  const bestValue = compared.length ? [...compared].sort((a, b) => (a.studentBudgetEstimate - a.monthlyGrant) - (b.studentBudgetEstimate - b.monthlyGrant))[0] : null
  const bestGrant = compared.length ? [...compared].sort((a, b) => b.monthlyGrant - a.monthlyGrant)[0] : null
  const soonestDeadline = compared.length ? [...compared].sort((a, b) => a.deadline.localeCompare(b.deadline))[0] : null
  const formatDate = (value: string) => new Date(value).toLocaleDateString(getLanguageLocale(language), { day: '2-digit', month: 'short', year: 'numeric', timeZone: 'UTC' })
  function toggleSelection(id: number) {
    setSelectedIds((current) => current.includes(id) ? current.filter((item) => item !== id) : current.length < 3 ? [...current, id] : current)
  }
  function exportCsv() {
    const escape = (value: string | number) => `"${String(value).replace(/"/g, '""')}"`
    const rows = [
      ['University', 'City', 'Country', 'Minimum GPA', 'Monthly grant EUR', 'Monthly cost EUR', 'Gap after grant EUR', 'Deadline', 'Verification'],
      ...compared.map((item) => [item.university, item.city, item.country, item.minimumGpa, item.monthlyGrant, item.studentBudgetEstimate, Math.max(0, item.studentBudgetEstimate - item.monthlyGrant), item.deadline, item.verificationStatus]),
    ]
    const blob = new Blob([rows.map((row) => row.map(escape).join(',')).join('\n')], { type: 'text/csv;charset=utf-8' })
    const url = URL.createObjectURL(blob)
    const link = document.createElement('a')
    link.href = url
    link.download = 'erasmuswithus-comparison.csv'
    link.click()
    URL.revokeObjectURL(url)
  }

  return <section className="workspace-panel">
    <div className="compare-heading"><div><span className="section-kicker">{t('COMPARE DESTINATIONS')}</span><h2>{t('Compare your saved choices.')}</h2><p>{t('See the decision-making details at a glance, from monthly affordability to application timing.')}</p></div><div className="compare-heading-actions"><span className="compare-count">{compared.length}/3 {t('destinations')}</span>{compared.length > 1 && <button className="secondary-button" type="button" onClick={exportCsv}>↓ {t('Export CSV')}</button>}</div></div>
    {opportunities.length > 0 && <div className="compare-picker"><div><strong>{t('Choose destinations')}</strong><small>{t('Select up to three saved universities for this comparison.')}</small></div><div className="compare-picker-options">{opportunities.map((item) => <button type="button" key={item.id} className={selectedIds.includes(item.id) ? 'compare-picker-option selected' : 'compare-picker-option'} onClick={() => toggleSelection(item.id)} aria-pressed={selectedIds.includes(item.id)}><span>{selectedIds.includes(item.id) ? '✓' : '+'}</span>{item.university}</button>)}</div></div>}
    {compared.length < 2 ? <>
      <p>{t('Save at least two destinations to compare GPA requirements, grants, monthly costs, and deadlines side by side.')}</p>
      <button className="primary-button" onClick={onBrowse}>{t('Discover destinations')} <ArrowUpRight size={15} /></button>
    </> : <>
      <div className="compare-insights">
        <div className="compare-insight"><span><TrendingDown size={15} />{t('Best monthly value')}</span><strong>{bestValue?.university}</strong><small>€{Math.max(0, (bestValue?.studentBudgetEstimate ?? 0) - (bestValue?.monthlyGrant ?? 0))} {t('estimated gap')}</small></div>
        <div className="compare-insight"><span><WalletCards size={15} />{t('Highest grant')}</span><strong>{bestGrant?.university}</strong><small>€{bestGrant?.monthlyGrant} / {t('month')}</small></div>
        <div className="compare-insight"><span><CalendarDays size={15} />{t('Soonest deadline')}</span><strong>{soonestDeadline?.university}</strong><small>{soonestDeadline ? formatDate(soonestDeadline.deadline) : ''}</small></div>
      </div>
      <div className="compare-table-wrap">
      <table className="compare-table">
        <thead><tr><th scope="col">{t('Details')}</th>{compared.map((item) => <th scope="col" key={item.id}><span className="compare-university">{item.university}</span><small>{item.city}, {item.country}</small><em className={item.id === bestValue?.id ? 'compare-badge positive' : 'compare-badge'}>{item.id === bestValue?.id ? t('Best value') : item.verificationStatus === 'Verified' ? t('Verified') : t('Review')}</em></th>)}</tr></thead>
        <tbody>
          <tr><th scope="row">{t('Minimum GPA')}</th>{compared.map((item) => <td key={item.id}><strong>{item.minimumGpa.toFixed(1)}</strong></td>)}</tr>
          <tr><th scope="row">{t('Grant / month')}</th>{compared.map((item) => <td key={item.id}><strong className="value-positive">€{item.monthlyGrant}</strong></td>)}</tr>
          <tr><th scope="row">{t('Estimated monthly cost')}</th>{compared.map((item) => <td key={item.id}><strong>€{item.studentBudgetEstimate}</strong><small className="table-muted">{t('rent + basics')}</small></td>)}</tr>
          <tr><th scope="row">{t('After grant')}</th>{compared.map((item) => <td key={item.id}><strong className={item.studentBudgetEstimate - item.monthlyGrant <= 0 ? 'value-positive' : 'value-warning'}>€{Math.max(0, item.studentBudgetEstimate - item.monthlyGrant)}</strong><small className="table-muted">{item.studentBudgetEstimate - item.monthlyGrant <= 0 ? t('covered in estimate') : t('to cover monthly')}</small></td>)}</tr>
          <tr><th scope="row">{t('Study fields')}</th>{compared.map((item) => <td key={item.id}>{item.studyFields.map(t).join(', ')}</td>)}</tr>
          <tr><th scope="row">{t('Teaching languages')}</th>{compared.map((item) => <td key={item.id}>{item.languages.map(t).join(', ')}</td>)}</tr>
          <tr><th scope="row">{t('Deadline')}</th>{compared.map((item) => <td key={item.id}><strong>{formatDate(item.deadline)}</strong></td>)}</tr>
          <tr><th scope="row">{t('Last checked')}</th>{compared.map((item) => <td key={item.id}>{formatDate(item.lastVerifiedAt)}</td>)}</tr>
        </tbody>
      </table>
      </div>
    </>}
  </section>
}

const trackerTasks = [
  'Confirm exchange agreement with my coordinator',
  'Check eligibility and course language',
  'Prepare transcript and language certificate',
  'Submit my university nomination',
  'Submit the application and learning agreement',
  'Arrange housing, insurance, and travel',
]
const trackerStages = ['Considering', 'Preparing', 'Submitted', 'Nominated', 'Accepted', 'Completed']
const plannerIntakes = ['Autumn 2026/27', 'Spring 2026/27', 'Autumn 2027/28', 'Spring 2027/28']

export function PlannerPanel({ opportunities, user, onSignIn }: {
  opportunities: ErasmusOpportunity[]
  user: User | null
  onSignIn: () => void
}) {
  const { t, language } = usePreferences()
  const token = localStorage.getItem('erasmuswithus.token') ?? ''
  const [plans, setPlans] = useState<StudentPlan[]>([])
  const [opportunityId, setOpportunityId] = useState(opportunities[0]?.id ?? 0)
  const [intake, setIntake] = useState(user?.intake || plannerIntakes[0])
  const [busy, setBusy] = useState(false)
  const [busyPlanId, setBusyPlanId] = useState<number | null>(null)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')

  useEffect(() => {
    if (!token || !user?.emailVerified) { setPlans([]); return }
    let cancelled = false
    getStudentPlans(token).then((result) => { if (!cancelled) setPlans(result) })
      .catch((caught) => { if (!cancelled) setError(caught instanceof Error ? caught.message : 'Could not load your application plans.') })
    return () => { cancelled = true }
  }, [token, user?.id, user?.emailVerified])

  async function addPlan() {
    if (!token || !opportunityId) return
    setBusy(true); setError(''); setNotice('')
    try {
      const plan = await createStudentPlan(token, { opportunityId, intake })
      setPlans((current) => [plan, ...current.filter((item) => item.id !== plan.id)])
      setNotice('Application tracker created and saved to your account.')
    } catch (caught) { setError(caught instanceof Error ? caught.message : 'Could not create your application tracker.') }
    finally { setBusy(false) }
  }

  async function savePlan(plan: StudentPlan, changes: { stage?: string; completedTasks?: string[] }) {
    if (!token) return
    setBusyPlanId(plan.id); setError(''); setNotice('')
    try {
      const updated = await updateStudentPlan(token, plan.id, {
        stage: changes.stage ?? plan.stage,
        completedTasks: changes.completedTasks ?? plan.completedTasks,
      })
      setPlans((current) => current.map((item) => item.id === updated.id ? updated : item))
      setNotice('Tracker update saved.')
    } catch (caught) { setError(caught instanceof Error ? caught.message : 'Could not save this tracker update.') }
    finally { setBusyPlanId(null) }
  }

  if (!user?.emailVerified || !token) return <section className="workspace-panel planner-gate">
    <span className="section-kicker">{t('APPLICATION TRACKER')}</span><h2>{t('Follow every university application in one place.')}</h2>
    <p>{t('Sign in and verify your email to save your stages and checklist across devices.')}</p>
    <button className="primary-button" onClick={onSignIn}>{t('Sign in or create an account')} <ArrowUpRight size={15} /></button>
  </section>

  return <section className="workspace-panel tracker-page">
    <span className="section-kicker">{t('APPLICATION TRACKER')}</span>
    <h2>{t('Follow every university application in one place.')}</h2>
    <p>{t('Track each destination and intake separately. Your changes are saved to your account.')}</p>
    <div className="tracker-create">
      <label>{t('Destination university')}<select value={opportunityId} onChange={(event) => setOpportunityId(Number(event.target.value))}>{opportunities.map((item) => <option key={item.id} value={item.id}>{item.university} · {item.country}</option>)}</select></label>
      <label>{t('Intake')}<select value={intake} onChange={(event) => setIntake(event.target.value)}>{plannerIntakes.map((item) => <option key={item} value={item}>{t(item)}</option>)}</select></label>
      <button className="primary-button" type="button" onClick={() => { void addPlan() }} disabled={busy || !opportunityId}><Plus size={16} />{t(busy ? 'Saving…' : 'Track this application')}</button>
    </div>
    {error && <p className="inline-error" role="alert">{t(error)}</p>}{notice && <p className="inline-success" role="status">{t(notice)}</p>}
    {plans.length === 0 ? <div className="tracker-empty"><ClipboardList size={25} /><strong>{t('No applications tracked yet')}</strong><span>{t('Add a university above to start your first application checklist.')}</span></div> : <div className="tracker-list">{plans.map((plan) => {
      const destination = opportunities.find((item) => item.id === plan.opportunityId)
      const completed = new Set(plan.completedTasks ?? [])
      const progress = Math.round(completed.size / trackerTasks.length * 100)
      return <article className="tracker-card" key={plan.id}>
        <div className="tracker-card-head"><div><strong>{plan.university}</strong><small>{destination ? `${destination.city}, ${destination.country}` : ''} · {t(plan.intake)}</small></div><label>{t('Application stage')}<select value={plan.stage || 'Considering'} disabled={busyPlanId === plan.id} onChange={(event) => { void savePlan(plan, { stage: event.target.value }) }}>{trackerStages.map((stage) => <option key={stage} value={stage}>{t(stage)}</option>)}</select></label></div>
        <div className="tracker-progress"><div><span>{t('Checklist progress')}</span><strong>{completed.size}/{trackerTasks.length}</strong></div><i><b style={{ width: `${progress}%` }} /></i></div>
        <div className="tracker-tasks">{trackerTasks.map((task) => <label className="tracker-task" key={task}><input type="checkbox" checked={completed.has(task)} disabled={busyPlanId === plan.id} onChange={(event) => { const next = event.target.checked ? [...completed, task] : [...completed].filter((item) => item !== task); void savePlan(plan, { completedTasks: next }) }} /><span>{t(task)}</span><Check size={14} /></label>)}</div>
        <small className="tracker-updated">{t('Last saved')} · {new Date(plan.updatedAt || plan.createdAt).toLocaleDateString(getLanguageLocale(language))}</small>
      </article>
    })}</div>}
  </section>
}

export function DeadlineRemindersPanel({ reminders, user, onRemove, onSignIn }: {
  reminders: DeadlineReminder[]
  user: User | null
  onRemove: (id: number) => void
  onSignIn: () => void
}) {
  const { t, language } = usePreferences()
  function exportCalendar() {
    const escape = (value: string) => value.replace(/\\/g, '\\\\').replace(/\n/g, '\\n').replace(/,/g, '\\,').replace(/;/g, '\\;')
    const stamp = new Date().toISOString().replace(/[-:]/g, '').replace(/\.\d{3}/, '')
    const events = reminders.flatMap((item) => {
      const date = new Date(`${item.deadline}T00:00:00Z`)
      if (Number.isNaN(date.getTime())) return []
      const start = date.toISOString().slice(0, 10).replace(/-/g, '')
      const end = new Date(date.getTime() + 86_400_000).toISOString().slice(0, 10).replace(/-/g, '')
      return [`BEGIN:VEVENT\r\nUID:deadline-${item.id}@erasmuswithus.app\r\nDTSTAMP:${stamp}\r\nDTSTART;VALUE=DATE:${start}\r\nDTEND;VALUE=DATE:${end}\r\nSUMMARY:${escape(`${item.university} application deadline`)}\r\nDESCRIPTION:${escape(`Confirm the date with the university. Email reminder is set for ${item.daysBefore} days before.`)}\r\nEND:VEVENT`]
    })
    const file = new Blob([`BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Erasmus With Us//Application reminders//EN\r\nCALSCALE:GREGORIAN\r\n${events.join('\r\n')}\r\nEND:VCALENDAR\r\n`], { type: 'text/calendar;charset=utf-8' })
    const url = URL.createObjectURL(file)
    const link = document.createElement('a')
    link.href = url
    link.download = 'erasmuswithus-deadlines.ics'
    link.click()
    URL.revokeObjectURL(url)
  }
  return <section className="workspace-panel reminders-page">
    <div className="reminders-heading"><div><span className="section-kicker">{t('DEADLINE REMINDERS')}</span><h2>{t('Stay ahead of application dates.')}</h2><p>{t('Choose a reminder from any destination card. We email you once before its listed deadline.')}</p></div>{reminders.length > 0 && <button className="secondary-button" type="button" onClick={exportCalendar}><CalendarDays size={16} />{t('Export calendar')}</button>}</div>
    {!user?.emailVerified ? <div className="reminders-empty"><p>{t('Sign in and verify your email to save email reminders.')}</p><button className="primary-button" onClick={onSignIn}>{t('Sign in or create an account')} <ArrowUpRight size={15} /></button></div> : reminders.length === 0 ? <div className="reminders-empty"><p>{t('You have no active reminders yet.')}</p><small>{t('Open a destination card, expand its guide, then choose “Remind me”.')}</small></div> : <div className="reminder-list">{reminders.map((item) => <article className="reminder-item" key={item.id}><div className="reminder-icon"><Bell size={18} /></div><div><strong>{item.university}</strong><small>{t('Deadline')} · {new Date(item.deadline).toLocaleDateString(getLanguageLocale(language), { day: '2-digit', month: 'long', year: 'numeric', timeZone: 'UTC' })}</small><span>{t('Email me {days} days before').replace('{days}', String(item.daysBefore))}{item.sentAt ? ` · ${t('Email sent')}` : ''}</span></div><button className="text-button" onClick={() => onRemove(item.id)}>{t('Remove')}</button></article>)}</div>}
    <p className="reminder-note">{t('Deadlines may change. Confirm the current application date with the university and your home coordinator.')}</p>
  </section>
}

type MonthlyCosts = { rent: number; living: number; extras: number; personalBudget: number; moveIn: number }

function readCosts(opportunityId: number, opportunity: ErasmusOpportunity): MonthlyCosts {
  const fallback = { rent: opportunity.monthlyRentEstimate, living: opportunity.monthlyLivingEstimate, extras: 0, personalBudget: 500, moveIn: 0 }
  try {
    const stored = JSON.parse(localStorage.getItem('erasmuswithus.budget') ?? '{}') as Record<string, Partial<MonthlyCosts>>
    return { ...fallback, ...stored[String(opportunityId)] }
  } catch {
    return fallback
  }
}

export function BudgetPlannerPanel({ opportunities }: { opportunities: ErasmusOpportunity[] }) {
  const { t } = usePreferences()
  const [selectedId, setSelectedId] = useState(opportunities[0]?.id ?? 0)
  const selected = opportunities.find((item) => item.id === selectedId) ?? opportunities[0]
  const [costs, setCosts] = useState<MonthlyCosts>(() => selected ? readCosts(selected.id, selected) : { rent: 0, living: 0, extras: 0, personalBudget: 500, moveIn: 0 })

  useEffect(() => {
    if (!selected) return
    setSelectedId(selected.id)
    setCosts(readCosts(selected.id, selected))
  }, [selected?.id])

  const comparison = useMemo(() => [...opportunities]
    .sort((a, b) => (a.studentBudgetEstimate - a.monthlyGrant) - (b.studentBudgetEstimate - b.monthlyGrant))
    .slice(0, 5), [opportunities])

  function updateCost(field: keyof MonthlyCosts, value: string) {
    if (!selected) return
    const next = { ...costs, [field]: Math.max(0, Number(value) || 0) }
    setCosts(next)
    try {
      const stored = JSON.parse(localStorage.getItem('erasmuswithus.budget') ?? '{}') as Record<string, MonthlyCosts>
      localStorage.setItem('erasmuswithus.budget', JSON.stringify({ ...stored, [String(selected.id)]: next }))
    } catch { /* Keep the working values for this session if storage is unavailable. */ }
  }

  if (!selected) return <section className="workspace-panel"><p>{t('No destinations are available yet.')}</p></section>
  const monthlyCost = costs.rent + costs.living + costs.extras
  const firstMonthGap = monthlyCost + costs.moveIn - selected.monthlyGrant
  const remainingBudget = costs.personalBudget - Math.max(0, firstMonthGap)
  const coverage = monthlyCost > 0 ? Math.min(100, Math.round(selected.monthlyGrant / monthlyCost * 100)) : 0
  const costParts = [['rent', 'Rent / housing', costs.rent, '#0d6a68'], ['living', 'Food, transport & basics', costs.living, '#2c8a6d'], ['extras', 'Other monthly costs', costs.extras, '#f0b65d']] as const

  return <section className="budget-page">
    <div className="budget-heading"><span className="section-kicker">{t('BUDGET PLANNER')}</span><h2>{t('Know what you may need each month.')}</h2><p>{t('Use your own estimates alongside the listed grant and city cost figures. All amounts are monthly EUR estimates.')}</p></div>
    <div className="budget-kpis"><div><span>{t('Monthly estimate')}</span><strong>€{monthlyCost.toLocaleString()}</strong><small>{t('your planned baseline')}</small></div><div><span>{t('Grant coverage')}</span><strong>{coverage}%</strong><small>{t('of monthly costs')}</small></div><div className={firstMonthGap > 0 ? 'warning' : 'positive'}><span>{t('Monthly gap')}</span><strong>€{Math.max(0, monthlyCost - selected.monthlyGrant).toLocaleString()}</strong><small>{t('after listed grant')}</small></div><div className={remainingBudget < 0 ? 'warning' : 'positive'}><span>{t('First-month buffer')}</span><strong>{remainingBudget < 0 ? `− €${Math.abs(remainingBudget).toLocaleString()}` : `€${remainingBudget.toLocaleString()}`}</strong><small>{t('after arrival costs')}</small></div></div>
    <div className="budget-layout">
      <article className="budget-editor">
        <label>{t('Destination university')}<select value={selected.id} onChange={(event) => setSelectedId(Number(event.target.value))}>{opportunities.map((item) => <option value={item.id} key={item.id}>{item.university} · {item.country}</option>)}</select></label>
        <div className="budget-input-grid">
          {([['rent', 'Rent / housing'], ['living', 'Food, transport & basics'], ['extras', 'Other monthly costs'], ['moveIn', 'One-time arrival costs'], ['personalBudget', 'My first-month funds']] as const).map(([field, label]) => <label key={field}>{t(label)}<span className="currency-input"><b>€</b><input type="number" min="0" max="20000" value={costs[field]} onChange={(event) => updateCost(field, event.target.value)} /></span></label>)}
        </div>
        <div className="budget-breakdown"><div className="budget-breakdown-head"><strong>{t('Monthly cost breakdown')}</strong><span>€{monthlyCost.toLocaleString()}</span></div><div className="budget-bar">{costParts.map(([key, label, value, color]) => <i key={key} style={{ width: `${monthlyCost ? value / monthlyCost * 100 : 0}%`, background: color }} title={`${t(label)}: €${value}`} />)}</div><div className="budget-legend">{costParts.map(([key, label, value, color]) => <span key={key}><i style={{ background: color }} />{t(label)} <b>€{value.toLocaleString()}</b></span>)}</div></div>
        <div className="budget-summary">
          <div><span>{t('Your estimated monthly costs')}</span><strong>€{monthlyCost.toLocaleString()}</strong></div>
          <div><span>{t('One-time arrival and deposit costs')}</span><strong>+ €{costs.moveIn.toLocaleString()}</strong></div>
          <div><span>{t('Listed monthly grant')}</span><strong>− €{selected.monthlyGrant.toLocaleString()}</strong></div>
          <div className={firstMonthGap > 0 ? 'budget-warning' : 'budget-positive'}><span>{t('Estimated first-month amount to cover after grant')}</span><strong>€{Math.max(0, firstMonthGap).toLocaleString()}</strong></div>
          <div className={remainingBudget < 0 ? 'budget-warning' : 'budget-positive'}><span>{t('Your funds after first-month costs')}</span><strong>{remainingBudget < 0 ? `− €${Math.abs(remainingBudget).toLocaleString()}` : `€${remainingBudget.toLocaleString()}`}</strong></div>
        </div>
        <p className="budget-disclaimer">{t('Grant amounts, housing and living costs can change. Confirm the current grant and use local housing quotes before making financial decisions.')}</p>
      </article>
      <aside className="budget-comparison"><div className="budget-comparison-title"><Calculator size={18} /><div><strong>{t('Destinations with the smallest estimated gap')}</strong><small>{t('Listed cost estimate minus listed grant')}</small></div></div>
        {comparison.map((item) => <button type="button" className={item.id === selected.id ? 'budget-rank selected' : 'budget-rank'} key={item.id} onClick={() => setSelectedId(item.id)}><span><strong>{item.university}</strong><small>{item.city}, {item.country}</small></span><b>{Math.max(0, item.studentBudgetEstimate - item.monthlyGrant) === 0 ? t('Covered in estimate') : `€${Math.max(0, item.studentBudgetEstimate - item.monthlyGrant)}`}</b></button>)}
      </aside>
    </div>
  </section>
}

const experienceIntakes = ['Autumn 2026/27', 'Spring 2026/27', 'Autumn 2025/26', 'Spring 2025/26', 'Autumn 2024/25', 'Spring 2024/25']

export function StudentStoriesPanel({ opportunities, user, onSignIn }: {
  opportunities: ErasmusOpportunity[]
  user: User | null
  onSignIn: () => void
}) {
  const { t } = usePreferences()
  const [opportunityId, setOpportunityId] = useState(opportunities[0]?.id ?? 0)
  const [summary, setSummary] = useState<ExperienceSummary | null>(null)
  const [rating, setRating] = useState(5)
  const [experienceType, setExperienceType] = useState<'Alumni' | 'Planning'>('Planning')
  const [intake, setIntake] = useState(experienceIntakes[0])
  const [content, setContent] = useState('')
  const [busy, setBusy] = useState(false)
  const [loadingSummary, setLoadingSummary] = useState(true)
  const [questions, setQuestions] = useState<OpportunityQuestion[]>([])
  const [questionDraft, setQuestionDraft] = useState('')
  const [answerDrafts, setAnswerDrafts] = useState<Record<number, string>>({})
  const [answerTypes, setAnswerTypes] = useState<Record<number, 'Alumni' | 'Planning'>>({})
  const [questionBusy, setQuestionBusy] = useState(false)
  const [error, setError] = useState('')
  const selected = opportunities.find((item) => item.id === opportunityId) ?? opportunities[0]

  useEffect(() => {
    if (!selected) return
    setOpportunityId(selected.id)
    let cancelled = false
    setLoadingSummary(true)
    Promise.all([getStudentExperiences(selected.id), getOpportunityQuestions(selected.id)])
      .then(([result, nextQuestions]) => {
        if (!cancelled) { setSummary(result); setQuestions(nextQuestions) }
      })
      .catch((requestError) => { if (!cancelled) setError(requestError instanceof Error ? requestError.message : 'Could not load student experiences.') })
      .finally(() => { if (!cancelled) setLoadingSummary(false) })
    return () => { cancelled = true }
  }, [selected?.id])

  async function submitExperience(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!selected || !user?.emailVerified) return
    setBusy(true); setError('')
    try {
      await saveStudentExperience(localStorage.getItem('erasmuswithus.token') ?? '', selected.id, { rating, experienceType, intake, content })
      setSummary(await getStudentExperiences(selected.id))
      setContent('')
    } catch (requestError) {
      setError(requestError instanceof Error ? requestError.message : 'Could not save this experience.')
    } finally { setBusy(false) }
  }

  async function submitQuestion(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!selected || !user?.emailVerified || questionDraft.trim().length < 10) return
    setQuestionBusy(true); setError('')
    try {
      await askOpportunityQuestion(localStorage.getItem('erasmuswithus.token') ?? '', selected.id, questionDraft.trim())
      setQuestions(await getOpportunityQuestions(selected.id))
      setQuestionDraft('')
    } catch (requestError) { setError(requestError instanceof Error ? requestError.message : 'Could not post your question.') }
    finally { setQuestionBusy(false) }
  }

  async function submitAnswer(question: OpportunityQuestion) {
    const answer = answerDrafts[question.id]?.trim() ?? ''
    if (!user?.emailVerified || answer.length < 10 || !selected) return
    setQuestionBusy(true); setError('')
    try {
      await answerOpportunityQuestion(localStorage.getItem('erasmuswithus.token') ?? '', question.id, {
        content: answer,
        experienceType: answerTypes[question.id] ?? 'Planning',
      })
      setQuestions(await getOpportunityQuestions(selected.id))
      setAnswerDrafts((current) => ({ ...current, [question.id]: '' }))
    } catch (requestError) { setError(requestError instanceof Error ? requestError.message : 'Could not send your answer.') }
    finally { setQuestionBusy(false) }
  }

  return <section className="stories-page">
    <div className="stories-heading"><div><span className="section-kicker">{t('STUDENT EXPERIENCES')}</span><h2>{t('Learn from students planning the same move.')}</h2><p>{t('Share practical details about applying, studying, housing, and everyday life.')}</p></div><label>{t('Destination university')}<select value={selected?.id ?? ''} onChange={(event) => { setOpportunityId(Number(event.target.value)); setSummary(null); setError('') }}>{opportunities.map((item) => <option key={item.id} value={item.id}>{item.university} · {item.country}</option>)}</select></label></div>
    <div className="stories-layout">
      <div className="story-feed"><div className="story-rating"><Star size={19} fill="currentColor" /><strong>{summary?.count ? summary.averageRating.toFixed(1) : '—'}</strong><span>{summary?.count ?? 0} {t('student stories')}</span></div>
        <p className="stories-disclosure">{t('Stories are community contributions. “Alumni” is self-reported and not independently verified by Erasmus With Us.')}</p>
        {loadingSummary ? <p className="story-loading">{t('Loading student stories…')}</p> : !summary?.experiences.length ? <div className="community-empty"><MessageSquareQuote size={25} /><strong>{t('No stories yet')}</strong><span>{t('Be the first student to share a useful experience for this destination.')}</span></div> : summary.experiences.map((story) => <article className="story-card" key={story.id}><div className="story-card-top"><div><strong>{story.displayName}</strong><small>{t(story.experienceType)} · {t(story.intake)}</small></div><span className="story-stars" aria-label={`${story.rating} out of 5`}>{'★'.repeat(story.rating)}<i>{'★'.repeat(5 - story.rating)}</i></span></div><p>{story.content}</p></article>)}
      </div>
      <form className="story-form" onSubmit={(event) => { void submitExperience(event) }}><span className="section-kicker">{t('ADD YOUR EXPERIENCE')}</span><h3>{t('Help the next student prepare.')}</h3>
        {!user?.emailVerified ? <><p>{t('Sign in and verify your email to post or update one story for this destination.')}</p><button type="button" className="primary-button" onClick={onSignIn}>{t('Sign in to share')} <ArrowUpRight size={15} /></button></> : <>
          <label>{t('Your experience')}<select value={experienceType} onChange={(event) => setExperienceType(event.target.value as 'Alumni' | 'Planning')}><option value="Planning">{t('Planning')}</option><option value="Alumni">{t('Alumni')}</option></select></label>
          <label>{t('Exchange intake')}<input value={intake} maxLength={40} onChange={(event) => setIntake(event.target.value)} /></label>
          <label>{t('Rating')}<select value={rating} onChange={(event) => setRating(Number(event.target.value))}>{[5, 4, 3, 2, 1].map((value) => <option value={value} key={value}>{value} / 5</option>)}</select></label>
          <label>{t('Your story')}<textarea value={content} minLength={20} maxLength={1500} rows={6} onChange={(event) => setContent(event.target.value)} placeholder={t('What should another student know?')} /></label>
          <small>{content.length}/1500 · {t('At least 20 characters')}</small>
          <button className="primary-button" type="submit" disabled={busy || content.trim().length < 20}>{t(busy ? 'Saving…' : 'Publish experience')} <ArrowUpRight size={15} /></button>
        </>}
        {error && <p className="inline-error">{t(error)}</p>}
      </form>
    </div>
    <section className="mentorship-panel">
      <div className="mentorship-heading"><div><span className="section-kicker">{t('ALUMNI Q&A')}</span><h3>{t('Ask someone who has been there.')}</h3><p>{t('Ask about applications, courses, housing or daily life. Alumni labels are self-reported by students.')}</p></div><span className="mentorship-mark">Q&amp;A</span></div>
      {user?.emailVerified ? <form className="question-form" onSubmit={(event) => { void submitQuestion(event) }}><label className="sr-only" htmlFor="opportunity-question">{t('Your question')}</label><textarea id="opportunity-question" value={questionDraft} maxLength={500} rows={2} onChange={(event) => setQuestionDraft(event.target.value)} placeholder={t('What would you like to ask about this university?')} /><button className="primary-button" type="submit" disabled={questionBusy || questionDraft.trim().length < 10}>{t('Ask question')} <ArrowUpRight size={15} /></button></form> : <div className="question-signin"><span>{t('Sign in and verify your email to ask or answer a question.')}</span><button className="text-button" type="button" onClick={onSignIn}>{t('Sign in')} <ArrowUpRight size={14} /></button></div>}
      {loadingSummary ? <p className="story-loading">{t('Loading questions…')}</p> : questions.length === 0 ? <div className="community-empty"><MessageSquareQuote size={23} /><strong>{t('No questions yet')}</strong><span>{t('Start the conversation for this destination.')}</span></div> : <div className="question-list">{questions.map((question) => <article className="question-card" key={question.id}><div className="question-meta"><strong>{question.displayName}</strong><small>{new Date(question.createdAt).toLocaleDateString()}</small></div><p className="question-copy">{question.content}</p>
        {question.answers.map((answer) => <div className="answer-card" key={answer.id}><div className="question-meta"><strong>{answer.displayName}</strong><span className={answer.experienceType === 'Alumni' ? 'alumni-label' : 'student-label'}>{t(answer.experienceType)}</span></div><p>{answer.content}</p></div>)}
        {user?.emailVerified && <div className="answer-form"><label>{t('Your experience')}<select value={answerTypes[question.id] ?? 'Planning'} onChange={(event) => setAnswerTypes((current) => ({ ...current, [question.id]: event.target.value as 'Alumni' | 'Planning' }))}><option value="Planning">{t('Planning')}</option><option value="Alumni">{t('Alumni')}</option></select></label><textarea value={answerDrafts[question.id] ?? ''} maxLength={1200} rows={2} onChange={(event) => setAnswerDrafts((current) => ({ ...current, [question.id]: event.target.value }))} placeholder={t('Share a helpful answer…')} /><button className="text-button" type="button" disabled={questionBusy || (answerDrafts[question.id] ?? '').trim().length < 10} onClick={() => { void submitAnswer(question) }}>{t('Post answer')}</button></div>}
      </article>)}</div>}
      {error && <p className="inline-error">{t(error)}</p>}
    </section>
    <div className="stories-footnote"><Bell size={14} /><span>{t('Keep personal contact details and sensitive information out of public stories.')}</span></div>
  </section>
}
