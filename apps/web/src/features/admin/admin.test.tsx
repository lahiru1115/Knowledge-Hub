import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { HttpResponse, http } from 'msw'
import { describe, expect, it } from 'vitest'
import { fakeApi, type AdminUser, type UsageRow } from '@/test/fakeApi'
import { API, renderApp, signedInSession, user } from '@/test/helpers'
import { server } from '@/test/server'
import { perDay } from './AiUsagePage'

const day = (ago: number) => new Date(Date.now() - ago * 86_400_000).toISOString().slice(0, 10)

function adminUser(over: Partial<AdminUser> = {}): AdminUser {
  return {
    id: 'u-x',
    email: 'x@example.com',
    display_name: 'X',
    role: 'member',
    created_at: '2026-10-01T08:00:00Z',
    last_login_at: null,
    resource_count: 0,
    is_disabled: false,
    ...over,
  }
}

function setup(
  role: 'admin' | 'member' = 'admin',
  users: AdminUser[] = [],
  usage: UsageRow[] = [],
) {
  signedInSession()
  server.use(http.get(`${API}/me`, () => HttpResponse.json({ ...user, role })))
  const db = fakeApi()
  db.admin.users = [
    adminUser({ id: user.id, email: user.email, display_name: user.display_name, role }),
    ...users,
  ]
  db.admin.usage = usage
  return db
}

describe('admin: access', () => {
  it('shows a member no admin link, and says no when they open the page', async () => {
    const db = setup('member')
    renderApp('/admin/users')
    expect(await screen.findByText("You don't have access to this")).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Admin' })).not.toBeInTheDocument()
    expect(db.all('GET', '/admin/users')).toHaveLength(0) // nothing was asked of the API
  })

  it('shows an admin the Admin link and opens on the users', async () => {
    setup('admin')
    renderApp('/admin')
    expect(await screen.findByRole('table', { name: 'Users' })).toBeInTheDocument()
    const nav = screen.getByRole('navigation', { name: 'Main' })
    expect(within(nav).getByRole('link', { name: 'Admin' })).toBeInTheDocument()
  })
})

describe('admin: users', () => {
  it('lists users with their details and offers no action on your own row', async () => {
    setup('admin', [
      adminUser({
        id: 'u-1',
        email: 'grace@example.com',
        display_name: 'Grace',
        resource_count: 12,
        last_login_at: new Date().toISOString(),
      }),
      adminUser({ id: 'u-2', email: 'old@example.com', display_name: 'Old', is_disabled: true }),
    ])
    renderApp('/admin/users')

    const grace = await screen.findByRole('row', { name: /grace@example.com/ })
    expect(within(grace).getByText('12')).toBeInTheDocument()
    expect(within(grace).getByText('just now')).toBeInTheDocument()
    expect(
      within(grace).getByRole('button', { name: 'Disable grace@example.com' }),
    ).toBeInTheDocument()
    const old = screen.getByRole('row', { name: /old@example.com/ })
    expect(within(old).getByText('Disabled')).toBeInTheDocument()
    expect(within(old).getByText('Never')).toBeInTheDocument()
    expect(within(old).getByRole('button', { name: 'Enable old@example.com' })).toBeInTheDocument()
    const me = screen.getByRole('row', { name: new RegExp(user.email) })
    expect(within(me).getByText('You')).toBeInTheDocument()
    expect(within(me).queryByRole('button')).not.toBeInTheDocument()
    expect(screen.getByText('3 users')).toBeInTheDocument()
  })

  it('searches by email', async () => {
    const db = setup('admin', [
      adminUser({ id: 'u-1', email: 'grace@example.com' }),
      adminUser({ id: 'u-2', email: 'linus@example.com' }),
    ])
    const u = userEvent.setup()
    renderApp('/admin/users')
    await screen.findByRole('row', { name: /grace@/ })

    await u.type(screen.getByRole('searchbox', { name: 'Search users by email' }), 'LIN')

    await waitFor(() => {
      expect(screen.queryByRole('row', { name: /grace@/ })).not.toBeInTheDocument()
    })
    expect(screen.getByRole('row', { name: /linus@/ })).toBeInTheDocument()
    expect(db.last('GET', '/admin/users')?.query.get('q')).toBe('LIN')
    const search = screen.getByRole('searchbox', { name: 'Search users by email' })
    await u.clear(search)
    await u.type(search, 'nobody')
    expect(await screen.findByText('No users match that search.')).toBeInTheDocument()
  })

  it('pages through the list', async () => {
    const many = Array.from({ length: 25 }, (_, i) =>
      adminUser({
        id: `u-${i.toString()}`,
        email: `person${i.toString().padStart(2, '0')}@example.com`,
      }),
    )
    setup('admin', many)
    const u = userEvent.setup()
    renderApp('/admin/users')
    await screen.findByText('Page 1 of 2')

    await u.click(screen.getByRole('button', { name: /Next/ }))

    expect(await screen.findByText('Page 2 of 2')).toBeInTheDocument()
    expect(screen.getByRole('combobox', { name: 'Users per page' })).toBeInTheDocument()
  })

  it('asks before disabling, says what happens, and then disables', async () => {
    const db = setup('admin', [adminUser({ id: 'u-1', email: 'grace@example.com' })])
    const u = userEvent.setup()
    renderApp('/admin/users')

    await u.click(await screen.findByRole('button', { name: 'Disable grace@example.com' }))
    const dialog = await screen.findByRole('alertdialog')
    expect(within(dialog).getByText(/signed out everywhere/)).toBeInTheDocument()
    expect(db.all('PATCH', '/admin/users/:id')).toHaveLength(0)
    await u.click(within(dialog).getByRole('button', { name: 'Disable' }))

    await waitFor(() => {
      expect(db.last('PATCH', '/admin/users/:id')?.body).toEqual({ is_disabled: true })
    })
    const row = await screen.findByRole('row', { name: /grace@/ })
    expect(await within(row).findByText('Disabled')).toBeInTheDocument()
    expect(
      within(row).getByRole('button', { name: 'Enable grace@example.com' }),
    ).toBeInTheDocument()
  })

  it('changes nothing when the confirmation is cancelled', async () => {
    const db = setup('admin', [adminUser({ id: 'u-1', email: 'grace@example.com' })])
    const u = userEvent.setup()
    renderApp('/admin/users')
    await u.click(await screen.findByRole('button', { name: 'Disable grace@example.com' }))
    await u.click(
      within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Cancel' }),
    )
    expect(db.all('PATCH', '/admin/users/:id')).toHaveLength(0)
  })

  it('enables a disabled user after a confirmation', async () => {
    const db = setup('admin', [
      adminUser({ id: 'u-1', email: 'old@example.com', is_disabled: true }),
    ])
    const u = userEvent.setup()
    renderApp('/admin/users')
    await u.click(await screen.findByRole('button', { name: 'Enable old@example.com' }))
    await u.click(
      within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Enable' }),
    )
    await waitFor(() => {
      expect(db.last('PATCH', '/admin/users/:id')?.body).toEqual({ is_disabled: false })
    })
    expect(
      await screen.findByRole('button', { name: 'Disable old@example.com' }),
    ).toBeInTheDocument()
  })

  it('shows a message when the change is refused', async () => {
    const db = setup('admin', [adminUser({ id: 'u-1', email: 'grace@example.com' })])
    db.failNext('PATCH', '/admin/users/:id', 403, 'forbidden')
    const u = userEvent.setup()
    renderApp('/admin/users')
    await u.click(await screen.findByRole('button', { name: 'Disable grace@example.com' }))
    await u.click(
      within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Disable' }),
    )
    expect(await screen.findByText("You don't have access to this.")).toBeInTheDocument()
  })
})

describe('admin: AI usage', () => {
  const rows: UsageRow[] = [
    { date: day(0), operation: 'chat', calls: 4, tokens_in: 900, tokens_out: 300, cost_usd: 0.75 },
    {
      date: day(1),
      operation: 'embed',
      calls: 10,
      tokens_in: 5000,
      tokens_out: 0,
      cost_usd: 0.0125,
    },
    { date: day(1), operation: 'chat', calls: 1, tokens_in: 100, tokens_out: 50, cost_usd: 0.25 },
  ]

  it('shows the totals, a chart with a table alternative, and cost by operation', async () => {
    setup('admin', [], rows)
    const u = userEvent.setup()
    renderApp('/admin/ai-usage')

    expect(await screen.findByText(/15 calls, \$1\.0125 in all\./)).toBeInTheDocument()
    expect(screen.getByRole('img', { name: /last 30 days, \$1\.0125 in all/ })).toBeInTheDocument()
    await u.click(screen.getByText('Show as a table', { selector: 'summary' }))
    const table = screen.getByRole('table', { name: 'AI usage per day for all users' })
    expect(within(table).getAllByRole('row')).toHaveLength(31) // a header and every day, empty ones too
    const ops = screen.getByRole('table', { name: 'AI usage by operation' })
    const first = within(ops).getAllByRole('row')[1]
    expect(first).toBeDefined()
    expect(within(first as HTMLElement).getByText('chat')).toBeInTheDocument() // dearest first
  })

  it('shows one user, chosen from the list', async () => {
    const db = setup(
      'admin',
      [adminUser({ id: 'u-1', email: 'grace@example.com' })],
      [
        ...rows,
        {
          date: day(0),
          user_id: 'u-1',
          operation: 'search',
          calls: 2,
          tokens_in: 10,
          tokens_out: 0,
          cost_usd: 0.5,
        },
      ],
    )
    const u = userEvent.setup()
    renderApp('/admin/ai-usage')
    await u.click(await screen.findByRole('combobox', { name: 'Choose a user' }))
    await u.click(await screen.findByRole('option', { name: 'grace@example.com' }))

    expect(await screen.findByText(/grace@example.com: 2 calls, \$0\.50/)).toBeInTheDocument()
    expect(db.last('GET', '/admin/ai-usage')?.query.get('user_id')).toBe('u-1')
  })

  it('says so when the AI service is down, and recovers on retry', async () => {
    const db = setup('admin', [], rows)
    db.admin.usageDown = true
    const u = userEvent.setup()
    renderApp('/admin/ai-usage')

    expect(await screen.findByText(/AI service isn't answering/)).toBeInTheDocument()
    db.admin.usageDown = false
    await u.click(screen.getByRole('button', { name: 'Try again' }))
    expect(await screen.findByText(/15 calls/)).toBeInTheDocument()
  })
})

describe('perDay', () => {
  it('gives thirty days, oldest first, with gaps filled and operations summed', () => {
    const now = new Date('2026-11-03T10:00:00Z')
    const days = perDay(
      [
        {
          date: '2026-11-03',
          operation: 'chat',
          calls: 1,
          tokens_in: 1,
          tokens_out: 1,
          cost_usd: 0.5,
        },
        {
          date: '2026-11-03',
          operation: 'embed',
          calls: 2,
          tokens_in: 2,
          tokens_out: 0,
          cost_usd: 0.25,
        },
        {
          date: '2026-10-05',
          operation: 'chat',
          calls: 1,
          tokens_in: 0,
          tokens_out: 0,
          cost_usd: 1,
        },
        {
          date: '2026-10-04',
          operation: 'chat',
          calls: 9,
          tokens_in: 0,
          tokens_out: 0,
          cost_usd: 9,
        },
      ],
      now,
    )
    expect(days).toHaveLength(30)
    expect(days[0]?.date).toBe('2026-10-05')
    expect(days[29]).toMatchObject({ date: '2026-11-03', calls: 3, cost_usd: 0.75 })
    expect(days[0]?.cost_usd).toBe(1) // 30 days back is included
    expect(days[10]?.calls).toBe(0)
    expect(days.reduce((n, d) => n + d.calls, 0)).toBe(4) // Oct 4 is outside the window
  })
})
