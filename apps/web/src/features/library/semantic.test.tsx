import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, makeResource } from '@/test/fakeApi'
import { renderApp, signedInSession } from '@/test/helpers'

const car = () =>
  makeResource({
    title: 'Weekend notes',
    description: 'Washing the automobile',
    metadata_status: 'ready',
  })

async function setup(
  path = '/library',
  configure?: (db: ReturnType<typeof fakeApi>) => void,
  resources = [car()],
) {
  signedInSession()
  const db = fakeApi({ resources })
  configure?.(db)
  const view = renderApp(path)
  return { db, u: userEvent.setup(), ...view }
}

describe('searching by meaning', () => {
  it('switches mode, waits for Enter, and shows the matching passage with the AI mark', async () => {
    const { db, u } = await setup()
    await screen.findByRole('button', { name: 'Weekend notes' })

    await u.click(screen.getByRole('radio', { name: /Semantic/ }))
    const box = screen.getByLabelText('Search your library')
    expect(screen.getByText(/Describe what you are looking for/)).toBeInTheDocument()
    expect(box).toHaveAttribute('placeholder', expect.stringContaining('press Enter'))

    await u.type(box, 'automobile')
    await new Promise((r) => setTimeout(r, 450))
    expect(db.all('GET', '/search/semantic')).toHaveLength(0) // typing does not spend the daily limit

    await u.keyboard('{Enter}')

    const list = await screen.findByRole('list', { name: 'Resources' })
    expect(within(list).getByText(/Matching passage:/)).toBeInTheDocument()
    expect(within(list).getByText(/…Washing the automobile…/)).toBeInTheDocument()
    expect(db.last('GET', '/search/semantic')?.query.get('q')).toBe('automobile')
    expect(screen.getByText('1 resource')).toBeInTheDocument()
    expect(within(list).queryByRole('checkbox')).not.toBeInTheDocument() // not selectable
  })

  it('keeps the mode and query in the URL, so a reload or a bookmark returns to the same search', async () => {
    const { db, router } = await setup('/library?mode=semantic&q=automobile')
    expect(await screen.findByText(/…Washing the automobile…/)).toBeInTheDocument()
    expect(router.state.location.search).toMatchObject({ mode: 'semantic', q: 'automobile' })
    expect(db.all('GET', '/resources')).toHaveLength(0) // the keyword list is not fetched in this mode
    expect(screen.getByRole('radio', { name: /Semantic/ })).toBeChecked()
    expect(screen.getByText(/Filters and sorting aren't available/)).toBeInTheDocument()
  })

  it('returns to keyword search from the switch', async () => {
    const both = makeResource({
      title: 'Weekend notes',
      description: 'Weekend notes about the automobile',
      metadata_status: 'ready',
    })
    const { u, router } = await setup('/library?mode=semantic&q=weekend', undefined, [both])
    await screen.findByText(/Matching passage:/)
    await u.click(screen.getByRole('radio', { name: 'Keyword' }))
    await waitFor(() => {
      expect(router.state.location.search).not.toHaveProperty('mode')
    })
    expect(await screen.findByRole('button', { name: 'Weekend notes' })).toBeInTheDocument() // the keyword list is back
    expect(screen.queryByText(/Matching passage:/)).not.toBeInTheDocument()
    expect(screen.queryByText(/Filters and sorting aren't available/)).not.toBeInTheDocument()
  })

  it('says so, and offers keyword search, when nothing matches', async () => {
    const { u } = await setup('/library?mode=semantic&q=zebra')
    expect(
      await screen.findByText(/Nothing in your library matches ‘zebra’ by meaning\./),
    ).toBeInTheDocument()
    await u.click(screen.getByRole('button', { name: 'Use keyword search' }))
    await waitFor(() => {
      expect(screen.queryByText(/by meaning\./)).not.toBeInTheDocument()
    })
  })

  it('opens the drawer from a result', async () => {
    const { u, router } = await setup('/library?mode=semantic&q=automobile')
    await u.click(await screen.findByRole('button', { name: 'Weekend notes' }))
    await waitFor(() => {
      expect(router.state.location.search).toHaveProperty('r')
    })
  })
})

describe('when the AI service is not available', () => {
  it('disables the Semantic option with the reason', async () => {
    await setup('/library', (db) => {
      db.ai.down = true
    })
    const option = await screen.findByRole('radio', { name: /Semantic/ })
    await waitFor(() => {
      expect(option).toBeDisabled()
    })
    expect(option).toHaveAttribute('title', 'AI features are unavailable right now.')
  })

  it('shows keyword results with a banner if the search was already by meaning', async () => {
    await setup('/library?mode=semantic&q=weekend', (db) => {
      db.ai.down = true
    })
    expect(
      await screen.findByText('Semantic search is unavailable, showing keyword results.'),
    ).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Weekend notes' })).toBeInTheDocument()
    expect(screen.queryByText(/Matching passage:/)).not.toBeInTheDocument() // no snippets in a fallback
  })

  it('reports the daily limit with its reset time, not a generic error', async () => {
    await setup('/library?mode=semantic&q=anything', (db) => {
      db.failNext('GET', '/search/semantic', 429, 'ai_quota_exceeded', {
        reset_at: new Date(Date.now() + 2 * 3600_000 + 60_000).toISOString(),
      })
    })
    expect(
      await screen.findByText(/used today's AI limit\. It resets in 2 hours/),
    ).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Use keyword search' })).toBeInTheDocument()
  })
})

describe('"Search by meaning instead"', () => {
  it('is offered when a keyword search finds nothing, and runs the same query', async () => {
    const { db, u, router } = await setup('/library?q=automobile-xyz')
    await u.click(await screen.findByRole('button', { name: /Search by meaning instead/ }))
    await waitFor(() => {
      expect(router.state.location.search).toMatchObject({ mode: 'semantic', q: 'automobile-xyz' })
    })
    await waitFor(() => {
      expect(db.last('GET', '/search/semantic')?.query.get('q')).toBe('automobile-xyz')
    })
  })

  it('is not offered while the AI service is down', async () => {
    await setup('/library?q=nothing-here', (db) => {
      db.ai.down = true
    })
    expect(await screen.findByText(/Nothing matches 'nothing-here'/)).toBeInTheDocument()
    expect(
      screen.queryByRole('button', { name: /Search by meaning instead/ }),
    ).not.toBeInTheDocument()
  })
})
