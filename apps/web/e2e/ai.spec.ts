import { expect, test } from '@playwright/test'
import { DESCRIPTION, register, savedResource, violations } from './support/helpers'

// Needs a stack with the AI service running (see README): run with E2E_AI=1. The language model and the embedding
// model are not needed for these: summaries and tag suggestions work without them, and semantic search and Ask
// then show their real "unavailable" behaviour.
test.skip(!process.env.E2E_AI, 'needs the AI service; set E2E_AI=1')

test.describe('with the AI service running', () => {
  test('Summarise stores a labelled summary that is still there after a reload', async ({
    page,
  }) => {
    await register(page)
    const r = await savedResource(page)
    await page.goto(`/library?r=${r.id}`)
    const drawer = page.getByRole('dialog', { name: 'Resource' })

    await drawer.getByRole('button', { name: /Summarise/ }).click()

    await expect(drawer.getByText('Extracted from the page')).toBeVisible()
    await expect(drawer.getByText(DESCRIPTION).first()).toBeVisible()
    expect(await violations(page), 'summary').toEqual([])
    await page.reload()
    await expect(
      page.getByRole('dialog', { name: 'Resource' }).getByText('Extracted from the page'),
    ).toBeVisible()
  })

  test('a page with too little text says so instead of failing', async ({ page }) => {
    await register(page)
    const r = await savedResource(page, 'Tiny')
    await page.request.fetch(`/api/v1/resources/${r.id}`, {
      method: 'PATCH',
      data: { description: 'short' },
      headers: {
        'X-CSRF-Token':
          (await page.context().cookies()).find((c) => c.name === 'kh_csrf')?.value ?? '',
      },
    })
    await page.goto(`/library?r=${r.id}`)
    const drawer = page.getByRole('dialog', { name: 'Resource' })
    await drawer.getByRole('button', { name: /Summarise/ }).click()
    await expect(drawer.getByText("This page doesn't have enough text to summarise.")).toBeVisible()
  })

  test('suggested tags are offered, and only added when clicked', async ({ page }) => {
    await register(page)
    const r = await savedResource(page, 'Npgsql connection pooling guide')
    await page.goto(`/library?r=${r.id}`)
    const drawer = page.getByRole('dialog', { name: 'Resource' })
    const group = drawer.getByRole('group', { name: 'Suggested tags' })
    await expect(group).toBeVisible()
    await expect(drawer.getByText('✦ Suggested tags')).toBeVisible()
    const first = group.getByRole('button', { name: /^Add tag / }).first()
    const label = await first.getAttribute('aria-label')
    const name = (label ?? '').replace('Add tag ', '')
    expect(name).not.toBe('')

    await expect(drawer.getByRole('button', { name: `Remove tag ${name}` })).toHaveCount(0) // not applied on its own
    await first.click()

    await expect(drawer.getByRole('button', { name: `Remove tag ${name}` })).toBeVisible()
    await page.reload()
    await expect(
      page
        .getByRole('dialog', { name: 'Resource' })
        .getByRole('button', { name: `Remove tag ${name}` }),
    ).toBeVisible()
    expect(await violations(page), 'drawer with suggestions').toEqual([])
  })

  test('Settings → AI shows the count after a summary', async ({ page }) => {
    await register(page)
    const r = await savedResource(page)
    await page.goto(`/library?r=${r.id}`)
    await page
      .getByRole('dialog', { name: 'Resource' })
      .getByRole('button', { name: /Summarise/ })
      .click()
    await expect(page.getByText('Extracted from the page')).toBeVisible()
    await page.goto('/settings/ai')
    await expect(page.getByRole('meter', { name: 'Summaries' })).toHaveAttribute('value', '1')
    await expect(page.getByText('1 of 300').first()).toBeVisible()
    await expect(page.getByText(/^✦\s*Free/)).toBeVisible()
    expect(await violations(page), 'settings AI with usage').toEqual([])
  })

  test('semantic search falls back to keyword results, with a banner, when the embedding model is not available', async ({
    page,
  }) => {
    await register(page)
    await savedResource(page, 'Pooling guide')
    await page.goto('/library')
    await page.getByRole('radio', { name: /Semantic/ }).click()
    await page.getByLabel('Search your library').fill('pooling')
    await page.keyboard.press('Enter')
    // The AI service tries to fetch the embedding model first; the Core API gives up after 5 seconds.
    await expect(
      page.getByText('Semantic search is unavailable, showing keyword results.'),
    ).toBeVisible({ timeout: 20_000 })
    await expect(page.getByRole('button', { name: 'Pooling guide', exact: true })).toBeVisible()
    expect(await violations(page), 'semantic fallback').toEqual([])
  })
})
