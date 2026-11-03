import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { HttpResponse, http } from 'msw'
import { describe, expect, it } from 'vitest'
import { fakeApi } from '@/test/fakeApi'
import { API, renderApp, signedInSession, user } from '@/test/helpers'
import { server } from '@/test/server'

function setup(
  configure?: (db: ReturnType<typeof fakeApi>) => void,
  prefs: Record<string, boolean> = {},
) {
  signedInSession()
  const db = fakeApi()
  configure?.(db)
  const patches: unknown[] = []
  let me = { ...user, preferences: { ...user.preferences, ...prefs } }
  server.use(
    http.get(`${API}/me`, () => HttpResponse.json(me)),
    http.patch(`${API}/me`, async ({ request }) => {
      const body = (await request.json()) as { preferences: Record<string, boolean> }
      patches.push(body)
      me = { ...me, preferences: { ...me.preferences, ...body.preferences } }
      return HttpResponse.json(me)
    }),
  )
  renderApp('/settings/ai')
  return { db, patches, u: userEvent.setup() }
}

describe('settings: AI', () => {
  it('shows both switches as set, and saves a change', async () => {
    const { patches, u } = setup()
    const summary = await screen.findByRole('switch', {
      name: 'Summarise new resources automatically',
    })
    expect(summary).toBeChecked()
    expect(screen.getByRole('switch', { name: 'Suggest tags when adding' })).toBeChecked()

    await u.click(summary)

    await waitFor(() => {
      expect(
        screen.getByRole('switch', { name: 'Summarise new resources automatically' }),
      ).not.toBeChecked()
    })
    expect(patches).toEqual([{ preferences: { ai_auto_summary: false } }])
  })

  it('shows a switch that was turned off earlier as off, and can turn tag suggestions off', async () => {
    const { patches, u } = setup(undefined, { ai_auto_summary: false })
    expect(
      await screen.findByRole('switch', { name: 'Summarise new resources automatically' }),
    ).not.toBeChecked()
    await u.click(screen.getByRole('switch', { name: 'Suggest tags when adding' }))
    await waitFor(() => {
      expect(patches).toEqual([{ preferences: { ai_tag_suggestions: false } }])
    })
  })

  it('reports a failed save in words', async () => {
    const { u } = setup()
    server.use(
      http.patch(`${API}/me`, () =>
        HttpResponse.json({ code: 'internal_error', status: 500, title: 'x' }, { status: 500 }),
      ),
    )
    await u.click(await screen.findByRole('switch', { name: 'Suggest tags when adding' }))
    expect(await screen.findByText(/Something went wrong on our side/)).toBeInTheDocument()
    expect(screen.getByRole('switch', { name: 'Suggest tags when adding' })).toBeChecked() // unchanged
  })

  it("shows today's use against each limit, and when it resets", async () => {
    setup()
    const bar = await screen.findByRole('meter', { name: 'Summaries' })
    expect(bar).toHaveAttribute('max', '300')
    expect(bar).toHaveAttribute('value', '4')
    expect(screen.getByText('4 of 300')).toBeInTheDocument()
    expect(screen.getByText(/Limits reset in 3 hours/)).toBeInTheDocument()
    expect(screen.getByRole('meter', { name: 'Questions to Ask' })).toHaveAttribute('value', '1')
  })

  it('says "no limit" for an operation without one, without a bar', async () => {
    setup((db) => {
      db.ai.usage.items = [{ operation: 'search', used: 7, limit: null }]
    })
    expect(await screen.findByText('7 today, no limit')).toBeInTheDocument()
    expect(screen.queryByRole('meter')).not.toBeInTheDocument()
  })

  it('names the mode and what it means', async () => {
    setup()
    const section = (await screen.findByRole('heading', { name: 'AI' })).closest(
      'section',
    ) as HTMLElement
    await waitFor(() => {
      expect(within(section).getByText(/^Free$/)).toBeInTheDocument()
    })
    expect(
      within(section).getByText(/Free mode runs on this server and costs nothing/),
    ).toBeInTheDocument()
    expect(within(section).getByText(/never used to train models/)).toBeInTheDocument()
  })

  it('says usage is unavailable, and that the service is not reachable, when the AI service is down', async () => {
    setup((db) => {
      db.ai.down = true
    })
    expect(await screen.findByText(/Usage is unavailable right now/)).toBeInTheDocument()
    expect(await screen.findByText(/not reachable right now/)).toBeInTheDocument()
    // The switches still work: they are saved preferences, not AI calls.
    expect(screen.getByRole('switch', { name: 'Suggest tags when adding' })).toBeEnabled()
  })
})
