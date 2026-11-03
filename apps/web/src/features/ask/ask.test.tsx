import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, makeCollection, makeResource } from '@/test/fakeApi'
import { renderApp, signedInSession } from '@/test/helpers'

const pool = () =>
  makeResource({
    title: 'Pooling guide',
    url: 'https://example.org/pool',
    metadata_status: 'ready',
    tags: ['postgres'],
  })

function setup(
  path = '/ask',
  configure?: (db: ReturnType<typeof fakeApi>, r: ReturnType<typeof pool>) => void,
) {
  signedInSession()
  const r = pool()
  const db = fakeApi({ resources: [r], collections: [makeCollection({ name: 'Databases' })] })
  configure?.(db, r)
  const view = renderApp(path)
  return { db, r, u: userEvent.setup(), ...view }
}

const box = () => screen.findByLabelText('Ask about your library')

describe('asking a question', () => {
  it('streams the answer, shows the sources afterwards, saves the chat and gives it an address', async () => {
    const { db, u, router } = setup('/ask', (d, res) => {
      d.ai.answer = () => ({
        tokens: ['Pooling keeps ', 'connections open ', '[1].'],
        citations: [
          { n: 1, resource_id: res.id, title: 'Pooling guide', url: res.url, quote: 'a quote' },
        ],
      })
    })
    expect(await screen.findByRole('heading', { name: 'Ask your library' })).toBeInTheDocument()
    expect(
      screen.getByText(/Answers use AI and come from your saved resources/),
    ).toBeInTheDocument()

    await u.type(await box(), 'How does pooling work?{Enter}')

    const sources = await screen.findByRole('group', { name: 'Sources' })
    expect(
      within(sources).getByRole('button', { name: /Source 1: Pooling guide/ }),
    ).toBeInTheDocument()
    expect(screen.getByText(/Pooling keeps connections open/)).toBeInTheDocument()
    await waitFor(() => {
      expect(router.state.location.pathname).toBe('/ask/chat-001')
    })
    expect(db.last('POST', '/chat/sessions')?.body).toEqual({ scope_collection_id: null })
    expect(db.last('POST', '/chat/sessions/:id/messages')?.body).toEqual({
      content: 'How does pooling work?',
    })
    // The saved chat replaces the live answer without duplicating it.
    await waitFor(() => {
      expect(screen.getAllByText('How does pooling work?', { selector: 'p' })).toHaveLength(1)
    })
    expect(screen.getAllByText(/Pooling keeps connections open/)).toHaveLength(1)
  })

  it('opens the cited resource in the drawer from a source chip', async () => {
    const { u } = setup('/ask', (d, res) => {
      d.ai.answer = () => ({
        tokens: ['See [1].'],
        citations: [
          { n: 1, resource_id: res.id, title: 'Pooling guide', url: res.url, quote: 'q' },
        ],
      })
    })
    await u.type(await box(), 'q{Enter}')
    const sources = await screen.findByRole('group', { name: 'Sources' })
    await u.click(within(sources).getByRole('button', { name: /Source 1: Pooling guide/ }))
    const drawer = await screen.findByRole('dialog', { name: 'Resource' })
    expect(
      await within(drawer).findByRole('heading', { name: 'Pooling guide' }),
    ).toBeInTheDocument()
  })

  it('turns [1] in the answer into a button for the source, and leaves an unknown [7] as text', async () => {
    const { u } = setup('/ask', (d, res) => {
      d.ai.answer = () => ({
        tokens: ['Pools help [1] and so does [7].'],
        citations: [
          { n: 1, resource_id: res.id, title: 'Pooling guide', url: res.url, quote: 'q' },
        ],
      })
    })
    await u.type(await box(), 'q{Enter}')
    const marker = await screen.findByRole('button', { name: 'Citation 1: Pooling guide' })
    expect(screen.getByText(/and so does \[7\]\./)).toBeInTheDocument()
    await u.click(marker)
    expect(await screen.findByRole('dialog', { name: 'Resource' })).toBeInTheDocument()
  })

  it('does not send an empty question, and Shift+Enter makes a new line instead of sending', async () => {
    const { db, u } = setup()
    const input = await box()
    await u.type(input, '   {Enter}')
    await u.clear(input)
    expect(db.all('POST', '/chat/sessions')).toHaveLength(0)
    expect(screen.getByRole('button', { name: 'Ask' })).toBeDisabled()
    await u.type(input, 'line one{Shift>}{Enter}{/Shift}line two')
    expect(input).toHaveValue('line one\nline two')
    expect(db.all('POST', '/chat/sessions')).toHaveLength(0)
  })

  it('starts a chat limited to the chosen collection', async () => {
    const { db, u } = setup()
    await u.click(await screen.findByRole('combobox', { name: /Scope/ }))
    await u.click(await screen.findByRole('option', { name: /Databases/ }))
    await u.type(await box(), 'what is here?{Enter}')
    await waitFor(() => {
      expect(db.all('POST', '/chat/sessions')).toHaveLength(1)
    })
    const sent = db.last('POST', '/chat/sessions')?.body as { scope_collection_id: string }
    expect(sent.scope_collection_id).toMatch(/^col-/)
  })

  it('offers three starting questions built from the library, and sends one when clicked', async () => {
    const { db, u } = setup()
    const list = await screen.findByRole('list', { name: 'Suggested questions' })
    const buttons = within(list).getAllByRole('button')
    expect(buttons).toHaveLength(3)
    expect(buttons[0]).toHaveTextContent('What did I save about postgres?')
    await u.click(buttons[0] as HTMLElement)
    await waitFor(() => {
      expect(db.last('POST', '/chat/sessions/:id/messages')?.body).toEqual({
        content: 'What did I save about postgres?',
      })
    })
  })

  it('lets the user stop an answer and keeps what had arrived', async () => {
    const { u } = setup('/ask', (d) => {
      d.ai.answer = () => ({ tokens: ['Partial answer so far '], hang: true })
    })
    await u.type(await box(), 'q{Enter}')
    await screen.findByText(/Partial answer so far/)
    await u.click(await screen.findByRole('button', { name: 'Stop' }))
    expect(
      await screen.findByText(/Stopped\. What was written so far is kept above\./),
    ).toBeInTheDocument()
    expect(screen.getByText(/Partial answer so far/)).toBeInTheDocument()
    expect(await screen.findByRole('button', { name: 'Ask' })).toBeInTheDocument() // the composer is back
  })

  it('marks an interrupted answer, keeps the partial text, and retries the same question', async () => {
    let calls = 0
    const { db, u } = setup('/ask', (d) => {
      d.ai.answer = () =>
        ++calls === 1
          ? { tokens: ['Half an answer '], error: 'ai_unavailable' }
          : { tokens: ['Full answer.'] }
    })
    await u.type(await box(), 'what now?{Enter}')
    expect(await screen.findByText('Answer interrupted.')).toBeInTheDocument()
    expect(screen.getByText(/Half an answer/)).toBeInTheDocument()

    await u.click(screen.getByRole('button', { name: 'Retry' }))

    await waitFor(() => {
      expect(screen.getByText('Full answer.')).toBeInTheDocument()
    })
    expect(db.all('POST', '/chat/sessions/:id/messages').map((x) => x.body)).toEqual([
      { content: 'what now?' },
      { content: 'what now?' },
    ])
  })

  it('offers to search the library instead when the answer cites nothing', async () => {
    const { u, router } = setup('/ask', (d) => {
      d.ai.answer = () => ({
        tokens: ["I couldn't find anything about that in your library."],
        citations: [],
      })
    })
    await u.type(await box(), 'quantum gravity{Enter}')
    await u.click(await screen.findByRole('button', { name: 'Search the library instead' }))
    await waitFor(() => {
      expect(router.state.location.pathname).toBe('/library')
    })
    expect(router.state.location.search).toMatchObject({ q: 'quantum gravity', mode: 'semantic' })
  })

  it('copies an answer', async () => {
    const { u } = setup()
    await u.type(await box(), 'q{Enter}')
    await screen.findByRole('button', { name: 'Copy' })
    await u.click(screen.getByRole('button', { name: 'Copy' }))
    expect(await screen.findByText('Copied')).toBeInTheDocument()
  })
})

describe('limits and availability', () => {
  it('disables asking, with the reset time, when the daily limit is reached', async () => {
    setup('/ask', (d) => {
      d.ai.usage.items = [{ operation: 'chat', used: 100, limit: 100 }]
    })
    expect(
      await screen.findByText(/You've reached today's limit\. It resets in 3 hours\./),
    ).toBeInTheDocument()
    expect(await box()).toBeDisabled()
  })

  it('says how many questions are left once 80% is used', async () => {
    setup('/ask', (d) => {
      d.ai.usage.items = [{ operation: 'chat', used: 92, limit: 100 }]
    })
    expect(await screen.findByText('8 questions left today')).toBeInTheDocument()
    expect(await box()).toBeEnabled()
  })

  it('shows the limit message and locks the composer if the server refuses a question for it', async () => {
    const { u } = setup('/ask', (d) => {
      d.failNext('POST', '/chat/sessions/:id/messages', 429, 'ai_quota_exceeded', {
        reset_at: new Date(Date.now() + 5 * 3600_000 + 60_000).toISOString(),
      })
    })
    await u.type(await box(), 'q{Enter}')
    expect(await screen.findByText(/It resets in 5 hours/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Retry' })).not.toBeInTheDocument() // retrying cannot help
    expect(await box()).toBeDisabled()
  })

  it('disables the composer with a banner while the AI service is down, and recovers on Try again', async () => {
    const { db, u } = setup('/ask', (d) => {
      d.ai.down = true
    })
    expect(
      await screen.findByText('Ask is unavailable right now. Your library still works.'),
    ).toBeInTheDocument()
    expect(await box()).toBeDisabled()
    db.ai.down = false
    await u.click(screen.getByRole('button', { name: 'Try again' }))
    await waitFor(() => {
      expect(screen.queryByText(/Ask is unavailable/)).not.toBeInTheDocument()
    })
    expect(await box()).toBeEnabled()
  })

  it('tells a new user to save something first, when the library is empty', async () => {
    signedInSession()
    fakeApi()
    renderApp('/ask')
    expect(await screen.findByText('Ask needs something to read')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Add a resource' })).toBeInTheDocument()
    expect(screen.queryByLabelText('Ask about your library')).not.toBeInTheDocument()
  })
})

describe('past chats', () => {
  const seed = (db: ReturnType<typeof fakeApi>, r: ReturnType<typeof pool>) => {
    const now = new Date().toISOString()
    db.ai.sessions.push({
      id: 'chat-old',
      title: 'Connection pools',
      scope_collection_id: null,
      created_at: now,
      updated_at: now,
    })
    db.ai.messages['chat-old'] = [
      { id: 'm1', role: 'user', content: 'How big should a pool be?', created_at: now },
      {
        id: 'm2',
        role: 'assistant',
        content: 'About ten per instance [1].',
        citations: [{ n: 1, resource_id: r.id, title: 'Pooling guide', url: r.url, quote: 'q' }],
        created_at: now,
      },
    ]
  }

  it('lists chats, reopens one with its messages and sources, and continues it', async () => {
    const { db, u } = setup('/ask', seed)
    const list = await screen.findByRole('list', { name: 'Chats' })
    await u.click(within(list).getByRole('link', { name: 'Connection pools' }))
    expect(await screen.findByText('How big should a pool be?')).toBeInTheDocument()
    expect(screen.getByText(/About ten per instance/)).toBeInTheDocument()
    expect(
      within(screen.getByRole('group', { name: 'Sources' })).getByRole('button', {
        name: /Pooling guide/,
      }),
    ).toBeInTheDocument()

    await u.type(await box(), 'and a second one?{Enter}')
    await waitFor(() => {
      expect(db.last('POST', '/chat/sessions/:id/messages')?.body).toEqual({
        content: 'and a second one?',
      })
    })
    expect(db.all('POST', '/chat/sessions')).toHaveLength(0) // continued, not recreated
  })

  it('renames a chat', async () => {
    const { db, u } = setup('/ask', seed)
    await u.click(await screen.findByRole('button', { name: 'Actions for Connection pools' }))
    await u.click(await screen.findByRole('menuitem', { name: 'Rename' }))
    const dialog = await screen.findByRole('dialog', { name: 'Rename chat' })
    const input = within(dialog).getByLabelText('Chat name')
    await u.clear(input)
    await u.type(input, 'Pool sizing')
    await u.click(within(dialog).getByRole('button', { name: 'Save' }))
    expect(await screen.findByRole('link', { name: 'Pool sizing' })).toBeInTheDocument()
    expect(db.last('PATCH', '/chat/sessions/:id')?.body).toEqual({ title: 'Pool sizing' })
  })

  it('deletes a chat after confirming, and leaves a chat that is open', async () => {
    const { db, u, router } = setup('/ask/chat-old', seed)
    await screen.findByText('How big should a pool be?')
    await u.click(screen.getByRole('button', { name: 'Actions for Connection pools' }))
    await u.click(await screen.findByRole('menuitem', { name: 'Delete' }))
    await u.click(
      within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Delete' }),
    )
    await waitFor(() => {
      expect(router.state.location.pathname).toBe('/ask')
    })
    expect(db.ai.sessions).toHaveLength(0)
  })

  it('says a chat is gone when its address is not found', async () => {
    setup('/ask/chat-missing')
    expect(await screen.findByText("This chat doesn't exist or was deleted.")).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Start a new chat' })).toBeInTheDocument()
  })
})

describe('navigation', () => {
  it('is reachable from the sidebar', async () => {
    const { u } = setup('/library')
    await u.click(
      within(await screen.findByRole('navigation', { name: 'Main' })).getByRole('link', {
        name: 'Ask',
      }),
    )
    expect(await screen.findByRole('heading', { name: 'Ask your library' })).toBeInTheDocument()
  })
})
