import { expect, test } from '@playwright/test'
import { api, register, savedResource, violations } from './support/helpers'

// The default stack has no AI service (AiService:Enabled is off), so these are the real "AI unavailable" states.
test.describe('with the AI service switched off, the app still works and says so', () => {
  test('the drawer: Summarise is disabled with the reason, writing a summary by hand still works', async ({
    page,
  }) => {
    await register(page)
    const r = await savedResource(page)
    await page.goto(`/library?r=${r.id}`)
    const drawer = page.getByRole('dialog', { name: 'Resource' })

    await expect(drawer.getByText('AI features are unavailable right now.').first()).toBeVisible()
    await expect(drawer.getByRole('button', { name: /Summarise/ })).toBeDisabled()
    await expect(drawer.getByText('Tag suggestions are unavailable right now.')).toBeVisible()
    await expect(drawer.getByRole('region', { name: /Related/ })).toHaveCount(0)

    await drawer.getByRole('button', { name: 'Write a summary' }).click()
    await drawer.getByRole('textbox', { name: 'Summary' }).fill('My own words.')
    await drawer.getByRole('button', { name: 'Save summary' }).click()
    await expect(drawer.getByText('My own words.')).toBeVisible()
    await expect(drawer.getByText('Written by you')).toBeVisible()
    expect(await violations(page), 'drawer without AI').toEqual([])
  })

  test('the library: Semantic is disabled, keyword search is unaffected', async ({ page }) => {
    await register(page)
    await savedResource(page, 'Pooling guide')
    await page.goto('/library')
    const semantic = page.getByRole('radio', { name: /Semantic/ })
    await expect(semantic).toBeDisabled()
    await expect(semantic).toHaveAttribute('title', 'AI features are unavailable right now.')
    await page.getByLabel('Search your library').fill('pooling')
    await expect(page.getByRole('button', { name: 'Pooling guide', exact: true })).toBeVisible()
    expect(await violations(page), 'library mode switch').toEqual([])
  })

  test('Ask: the composer is disabled with a banner, and the rest of the app is one click away', async ({
    page,
  }) => {
    await register(page)
    await savedResource(page)
    await page.goto('/library')
    await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Ask' }).click()
    await expect(page.getByRole('heading', { name: 'Ask your library' })).toBeVisible()
    await expect(
      page.getByText('Ask is unavailable right now. Your library still works.'),
    ).toBeVisible()
    await expect(page.getByLabel('Ask about your library')).toBeDisabled()
    await expect(page.getByText(/Answers use AI and come from your saved resources/)).toBeVisible()
    expect(await violations(page), 'ask without AI').toEqual([])
  })

  test('Ask: an empty library asks for something to read first', async ({ page }) => {
    await register(page)
    await page.goto('/ask')
    await expect(page.getByText('Ask needs something to read')).toBeVisible()
    expect(await violations(page), 'ask, empty').toEqual([])
  })

  test('Settings → AI: the switches are saved on the server, and usage says it is unavailable', async ({
    page,
  }) => {
    await register(page)
    await page.goto('/settings/ai')
    const summary = page.getByRole('switch', { name: 'Summarise new resources automatically' })
    const tags = page.getByRole('switch', { name: 'Suggest tags when adding' })
    await expect(summary).toBeChecked()
    await summary.click()
    await expect(summary).not.toBeChecked()

    await page.reload()
    await expect(
      page.getByRole('switch', { name: 'Summarise new resources automatically' }),
    ).not.toBeChecked()
    await expect(tags).toBeChecked()
    await expect(page.getByText(/Usage is unavailable right now/)).toBeVisible()
    await expect(page.getByText(/Free mode runs on this server and costs nothing/)).toBeVisible()
    expect(await violations(page), 'settings AI').toEqual([])
  })

  test('turning suggestions off hides the note in quick-add, and nothing about AI blocks saving', async ({
    page,
  }) => {
    await register(page)
    await api(page, 'PATCH', '/me', { preferences: { ai_tag_suggestions: false } })
    const r = await savedResource(page)
    await page.goto(`/library?r=${r.id}`)
    const drawer = page.getByRole('dialog', { name: 'Resource' })
    await expect(drawer.getByRole('heading', { name: 'Pooling guide' })).toBeVisible()
    await expect(drawer.getByText('Tag suggestions are unavailable right now.')).toHaveCount(0)
  })
})

test.describe('Ask on a phone (360 px)', () => {
  test.use({ viewport: { width: 360, height: 740 }, hasTouch: true })

  test('the chat list is behind a Chats button, nothing scrolls sideways, and Ask is in the tab bar', async ({
    page,
  }) => {
    await register(page)
    await savedResource(page)
    await page.goto('/ask')
    await expect(page.getByRole('heading', { name: 'Ask your library' })).toBeVisible()
    await expect(
      page.getByRole('navigation', { name: 'Tabs' }).getByRole('link', { name: 'Ask' }),
    ).toBeVisible()
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= document.documentElement.clientWidth,
      ),
    ).toBe(true)

    await page.getByRole('button', { name: 'Chats' }).click()
    const sheet = page.getByRole('dialog', { name: 'Chats' })
    await expect(sheet.getByRole('button', { name: 'New chat' })).toBeVisible()
    expect(await violations(page), 'chats sheet').toEqual([])
  })
})
