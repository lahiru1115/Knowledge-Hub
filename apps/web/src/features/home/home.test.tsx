import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { HttpResponse, http } from 'msw'
import { describe, expect, it } from 'vitest'
import { fakeApi, makeCollection, makeResource } from '@/test/fakeApi'
import { API, renderApp, signedInSession, user } from '@/test/helpers'
import { server } from '@/test/server'

function setup(
  configure?: (db: ReturnType<typeof fakeApi>) => void,
  seed: Parameters<typeof fakeApi>[0] = {},
) {
  signedInSession()
  const db = fakeApi(seed)
  configure?.(db)
  const patches: unknown[] = []
  let me: Record<string, unknown> = { ...user }
  server.use(
    http.get(`${API}/me`, () => HttpResponse.json(me)),
    http.patch(`${API}/me`, async ({ request }) => {
      const body = (await request.json()) as { preferences: Record<string, unknown> }
      patches.push(body)
      me = { ...me, preferences: { ...(me.preferences as object), ...body.preferences } }
      db.dash.dismissed = body.preferences.onboarding_dismissed === true
      return HttpResponse.json(me)
    }),
  )
  const view = renderApp('/')
  return { db, patches, u: userEvent.setup(), ...view }
}

const library = () => [
  makeResource({
    title: 'Pooling guide',
    type: 'article',
    is_favourite: true,
    tags: ['postgres'],
    metadata_status: 'ready',
  }),
  makeResource({ title: 'PgBouncer explained', type: 'video', metadata_status: 'ready' }),
  makeResource({ title: 'Repo tour', type: 'repo', metadata_status: 'ready' }),
]

describe('home', () => {
  it('greets the user and shows the library in numbers, each linking to the matching list', async () => {
    const { u, router } = setup(undefined, {
      resources: library(),
      collections: [makeCollection({ name: 'Databases' })],
    })
    expect(
      await screen.findByRole('heading', { name: 'Welcome, Ada Lovelace' }),
    ).toBeInTheDocument()
    const numbers = await screen.findByRole('region', { name: 'Your library in numbers' })
    expect(within(numbers).getByRole('link', { name: /3\s*resources/ })).toBeInTheDocument()
    expect(within(numbers).getByRole('link', { name: /1\s*collection$/ })).toBeInTheDocument()
    expect(within(numbers).getByRole('link', { name: /1\s*tag$/ })).toBeInTheDocument()
    await u.click(within(numbers).getByRole('link', { name: /1\s*favourite$/ }))
    await waitFor(() => {
      expect(router.state.location.pathname).toBe('/library')
    })
    expect(router.state.location.search).toMatchObject({ favourite: 'true' })
  })

  it('lists what was added most recently and opens it in the drawer', async () => {
    const { u, router } = setup(undefined, { resources: library() })
    const section = await screen.findByRole('region', { name: 'Recently added' })
    const links = within(section).getAllByRole('link')
    expect(links).toHaveLength(3)
    await u.click(within(section).getByRole('link', { name: /PgBouncer explained/ }))
    await waitFor(() => {
      expect(router.state.location.search).toHaveProperty('r')
    })
  })

  it('draws the types as bars with the number written next to each, largest first, linking to the filtered list', async () => {
    const many = [...library(), makeResource({ type: 'article', metadata_status: 'ready' })]
    const { u, router } = setup(undefined, { resources: many })
    const list = await screen.findByRole('list', { name: 'Resources by type' })
    const rows = within(list).getAllByRole('link')
    expect(rows.map((r) => r.textContent)).toEqual(['Article2', 'Video1', 'Repository1'])
    await u.click(rows[0] as HTMLElement)
    await waitFor(() => {
      expect(router.state.location.search).toMatchObject({ type: 'article' })
    })
  })

  it('has an activity chart with a table alternative holding the same numbers', async () => {
    setup(
      (db) => {
        db.dash.weekly = [0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 5]
      },
      { resources: library() },
    )
    const chart = await screen.findByRole('img', { name: /last 12 weeks, 7 in all/ })
    expect(chart).toBeInTheDocument()
    const table = screen.getByRole('table', { name: 'Resources added per week', hidden: true })
    const rows = within(table).getAllByRole('row', { hidden: true })
    expect(rows).toHaveLength(13) // header + 12 weeks
    expect(rows.at(-1)).toHaveTextContent('5')
    expect(screen.getByText(/7 resources added/)).toBeInTheDocument()
  })

  it('asks a new user to add their first resource', async () => {
    const { u } = setup()
    expect(await screen.findByText('Your library is empty.')).toBeInTheDocument()
    await u.click(screen.getByRole('button', { name: 'Add your first resource' }))
    expect(await screen.findByRole('dialog', { name: 'Add a resource' })).toBeInTheDocument()
    expect(screen.queryByRole('region', { name: 'Recently added' })).not.toBeInTheDocument()
  })

  it('says it could not load, and recovers on Try again', async () => {
    const { u } = setup((db) => {
      db.failNext('GET', '/dashboard', 500, 'internal_error')
    })
    await u.click(await screen.findByRole('button', { name: 'Try again' }))
    expect(
      await screen.findByRole('region', { name: 'Your library in numbers' }),
    ).toBeInTheDocument()
  })

  it("asks for the dashboard in the browser's own time zone", async () => {
    const { db } = setup()
    await screen.findByRole('region', { name: 'Your library in numbers' })
    const zone = Intl.DateTimeFormat().resolvedOptions().timeZone
    expect(db.last('GET', '/dashboard')?.query.get('time_zone')).toBe(zone)
  })
})

describe('the checklist', () => {
  it('shows what is done and what is not, with a way to do each, and counts them', async () => {
    setup(
      (db) => {
        db.dash.triedSearch = true
      },
      { resources: library(), collections: [] },
    )
    const box = await screen.findByRole('region', { name: /Get started/ })
    expect(within(box).getByText('2 of 4')).toBeInTheDocument()
    const items = within(box)
      .getAllByRole('listitem')
      .map((li) => li.textContent)
    expect(items[0]).toContain('Save your first resource (done)')
    expect(items[1]).toContain('Create a collection (not done yet)')
    expect(items[2]).toContain('Try a search (done)')
    expect(within(box).getByRole('link', { name: 'Go to collections' })).toBeInTheDocument()
    expect(within(box).getByRole('link', { name: 'Ask your library' })).toBeInTheDocument()
    expect(within(box).queryByRole('button', { name: 'Add one' })).not.toBeInTheDocument() // already done
  })

  it('can be dismissed, which is saved as a preference', async () => {
    const { patches, u } = setup(undefined, { resources: library() })
    await u.click(await screen.findByRole('button', { name: 'Dismiss' }))
    await waitFor(() => {
      expect(screen.queryByRole('region', { name: /Get started/ })).not.toBeInTheDocument()
    })
    expect(patches).toEqual([{ preferences: { onboarding_dismissed: true } }])
  })

  it('is not shown once dismissed, or once every step is done', async () => {
    setup(
      (db) => {
        db.dash.dismissed = true
      },
      { resources: library() },
    )
    await screen.findByRole('region', { name: 'Your library in numbers' })
    expect(screen.queryByRole('region', { name: /Get started/ })).not.toBeInTheDocument()
  })

  it('goes away on its own when all four steps are done', async () => {
    setup(
      (db) => {
        db.dash.triedSearch = true
        db.dash.askedQuestion = true
      },
      { resources: library(), collections: [makeCollection()] },
    )
    await screen.findByRole('region', { name: 'Your library in numbers' })
    expect(screen.queryByRole('region', { name: /Get started/ })).not.toBeInTheDocument()
  })
})
