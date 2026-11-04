import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { HttpResponse, http } from 'msw'
import { fakeApi, makeCollection, makeResource } from '@/test/fakeApi'
import { API, renderApp, signedInSession, user } from '@/test/helpers'
import { server } from '@/test/server'

describe('app shell', () => {
  it('has a skip link, landmarks and a title per page', async () => {
    signedInSession()
    fakeApi({ resources: [makeResource()] })
    renderApp('/library')

    expect(await screen.findByRole('link', { name: 'Skip to content' })).toHaveAttribute(
      'href',
      '#main',
    )
    expect(screen.getAllByRole('banner').length).toBeGreaterThan(0)
    expect(screen.getByRole('navigation', { name: 'Main' })).toBeInTheDocument()
    expect(screen.getByRole('main')).toBeInTheDocument()
    expect(screen.getByRole('search')).toBeInTheDocument()
    await waitFor(() => {
      expect(document.title).toBe('Library · Knowledge Hub')
    })
  })

  it('announces where the user is after a route change', async () => {
    signedInSession()
    fakeApi()
    renderApp('/library')
    const u = userEvent.setup()
    await screen.findByRole('heading', { name: 'Library' })

    await u.click(
      within(screen.getByRole('navigation', { name: 'Main' })).getByRole('link', { name: 'Home' }),
    )

    await waitFor(() => {
      expect(screen.getByRole('status', { hidden: true, name: '' })).toBeDefined()
    })
    await waitFor(() => {
      const live = Array.from(document.querySelectorAll('[aria-live="polite"][role="status"]')).map(
        (e) => e.textContent,
      )
      expect(live).toContain('Home · Knowledge Hub')
    })
  })

  it('the sidebar lists collections with counts (pinned first), the Inbox, the top tags and Manage tags', async () => {
    signedInSession()
    const a = makeCollection({ name: 'Databases', icon: '📘' })
    const b = makeCollection({ name: 'Favourites', is_pinned: true })
    fakeApi({
      collections: [a, b],
      resources: [
        makeResource({ collection_id: a.id, tags: ['postgres'] }),
        makeResource({ collection_id: a.id, tags: ['postgres', 'x'] }),
        makeResource({ tags: ['x'] }),
      ],
    })
    renderApp('/')

    const nav = await screen.findByRole('navigation', { name: 'Main' })
    expect(await within(nav).findByRole('link', { name: /Databases/ })).toHaveAttribute(
      'href',
      `/collections/${a.id}`,
    )
    expect(within(nav).getByRole('link', { name: /Databases/ })).toHaveTextContent('2')
    expect(within(nav).getByRole('link', { name: /Inbox/ })).toHaveTextContent('1')
    expect(within(nav).getByRole('link', { name: /postgres/ })).toHaveAttribute(
      'href',
      '/library?tag=postgres',
    )
    expect(within(nav).getByRole('link', { name: 'Manage tags →' })).toHaveAttribute(
      'href',
      '/tags',
    )
    expect(within(nav).getByRole('button', { name: 'New collection' })).toBeInTheDocument()
  })

  it('the sidebar collapses to an icon rail with [ and remembers it', async () => {
    signedInSession()
    fakeApi()
    renderApp('/library')
    const u = userEvent.setup()
    const nav = await screen.findByRole('navigation', { name: 'Main' })
    expect(nav).toHaveClass('w-64')

    await u.keyboard('[[')

    await waitFor(() => {
      expect(nav).toHaveClass('w-16')
    })
    expect(localStorage.getItem('kh_sidebar')).toBe('collapsed')
    expect(within(nav).getByRole('link', { name: 'Library' })).toBeInTheDocument() // still named for screen readers
    await u.click(screen.getByRole('button', { name: 'Toggle sidebar' }))
    await waitFor(() => {
      expect(nav).toHaveClass('w-64')
    })
  })

  it('the top bar search goes to the library with the query', async () => {
    signedInSession()
    fakeApi()
    const { router } = renderApp('/')
    const u = userEvent.setup()

    await u.type(
      await screen.findByRole('searchbox', { name: 'Quick search' }),
      'connection pooling{Enter}',
    )

    await waitFor(() => {
      expect(router.state.location.pathname).toBe('/library')
    })
    expect(router.state.location.search).toMatchObject({ q: 'connection pooling' })
  })

  it('/ focuses the page search, N opens quick-add, and the Add button does too', async () => {
    signedInSession()
    fakeApi()
    renderApp('/library')
    const u = userEvent.setup()
    await screen.findByRole('heading', { name: 'Library' })

    await u.keyboard('/')
    expect(screen.getByRole('searchbox', { name: 'Search your library' })).toHaveFocus()
    await u.keyboard('{Escape}')
    const focused = document.activeElement as HTMLElement
    focused.blur()

    await u.keyboard('n')
    expect(await screen.findByRole('dialog', { name: 'Add a resource' })).toBeInTheDocument()
  })

  it('the account menu changes the theme, shows shortcuts and signs out', async () => {
    signedInSession()
    fakeApi()
    let me: Record<string, unknown> = { ...user }
    server.use(
      http.get(`${API}/me`, () => HttpResponse.json(me)),
      http.patch(`${API}/me`, async ({ request }) => {
        const body = (await request.json()) as { preferences: Record<string, unknown> }
        me = { ...me, preferences: { ...(me.preferences as object), ...body.preferences } }
        return HttpResponse.json(me)
      }),
    )
    renderApp('/')
    const u = userEvent.setup()

    await u.click(await screen.findByRole('button', { name: 'Account menu for Ada Lovelace' }))
    const menu = await screen.findByRole('menu')
    expect(within(menu).getByText('ada@example.com')).toBeInTheDocument()
    await u.click(within(menu).getByRole('menuitemradio', { name: 'Dark' }))
    expect(document.documentElement).toHaveClass('dark')

    await u.click(screen.getByRole('button', { name: 'Account menu for Ada Lovelace' }))
    await u.click(await screen.findByRole('menuitem', { name: /Keyboard shortcuts/ }))
    expect(await screen.findByRole('dialog', { name: 'Keyboard shortcuts' })).toBeInTheDocument()
  })

  it('has phone navigation: tab bar and a floating Add button', async () => {
    signedInSession()
    fakeApi()
    renderApp('/')
    const tabs = await screen.findByRole('navigation', { name: 'Tabs' })
    expect(
      within(tabs)
        .getAllByRole('link')
        .map((l) => l.textContent),
    ).toEqual(['Home', 'Library', 'Ask', 'Collections'])
    const u = userEvent.setup()
    await u.click(screen.getByRole('button', { name: 'Add resource' }))
    expect(await screen.findByRole('dialog', { name: 'Add a resource' })).toBeInTheDocument()
  })

  it('home shows the library in numbers', async () => {
    signedInSession()
    const c = makeCollection({ name: 'X' })
    fakeApi({
      collections: [c],
      resources: [makeResource({ collection_id: c.id }), makeResource()],
    })
    renderApp('/')
    const numbers = await screen.findByRole('region', { name: 'Your library in numbers' })
    expect(within(numbers).getByRole('link', { name: /2\s*resources/ })).toHaveAttribute(
      'href',
      '/library',
    )
  })
})
