import AxeBuilder from '@axe-core/playwright'
import { expect, type Page } from '@playwright/test'

export const password = 'a long unusual passphrase'
export const uniqueEmail = () => `e2e-${crypto.randomUUID()}@example.com`

/** WCAG 2.2 AA violations on the page as it is now. */
export async function violations(page: Page) {
  // Dialogs and menus fade in; measuring contrast mid-fade gives false results. Endless animations (the loading
  // shimmer) never finish and are not what causes false results.
  await page.waitForFunction(() =>
    document
      .getAnimations()
      .every(
        (a) => a.playState !== 'running' || a.effect?.getComputedTiming().iterations === Infinity,
      ),
  )
  const results = await new AxeBuilder({ page })
    .withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa'])
    .analyze()
  return results.violations.map(
    (v) => `${v.id}: ${v.help} (${v.nodes.map((n) => n.target.join(' ')).join(' | ')})`,
  )
}

export async function register(page: Page, name = 'Ada Lovelace') {
  await page.goto('/register')
  await page.getByLabel('Email').fill(uniqueEmail())
  await page.getByLabel('Your name').fill(name)
  await page.getByLabel('Password', { exact: true }).fill(password)
  await page.getByRole('button', { name: 'Create account' }).click()
  await expect(page.getByRole('heading', { name: `Welcome, ${name}` })).toBeVisible()
}

/** Calls the API as the signed-in browser does (same cookies, CSRF header), to arrange data quickly. */
export async function api(
  page: Page,
  method: 'POST' | 'PATCH' | 'DELETE',
  path: string,
  data?: unknown,
) {
  const csrf = (await page.context().cookies()).find((c) => c.name === 'kh_csrf')?.value ?? ''
  const response = await page.request.fetch(`/api/v1${path}`, {
    method,
    data,
    headers: { 'X-CSRF-Token': csrf },
  })
  expect(response.ok(), `${method} ${path}`).toBeTruthy()
  return response.status() === 204 ? undefined : ((await response.json()) as { id: string })
}

export const DESCRIPTION =
  'A practical guide to connection pooling in Npgsql. It explains batching, prepared statements and more.'

/** A resource with a title and description, so nothing needs the internet. */
export async function savedResource(page: Page, title = 'Pooling guide') {
  const created = await api(page, 'POST', '/resources', {
    url: `https://example.invalid/${crypto.randomUUID()}`,
    title,
    description: DESCRIPTION,
  })
  if (!created) throw new Error('The resource was not created')
  return created
}
