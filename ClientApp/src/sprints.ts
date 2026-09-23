import type { BoardSprint } from './api'

/**
 * "10 Aug – 21 Aug 2026", or as much of that as the sprint has.
 *
 * Shown beside every sprint name wherever one is listed, on purpose: a board
 * reuses names, and two "Sprint 1"s a year apart are only told apart by when
 * they ran. The id is what is ever sent; the dates are for the person.
 */
export function sprintDates(sprint: BoardSprint): string {
  const day = (iso: string | null, year: boolean) =>
    iso
      ? new Date(iso).toLocaleDateString(undefined, {
          day: 'numeric',
          month: 'short',
          ...(year ? { year: 'numeric' } : {}),
        })
      : null

  const start = day(sprint.startDate, false)
  const end = day(sprint.endDate, true)

  if (start && end) return `${start} – ${end}`
  return end ?? start ?? 'no dates'
}
