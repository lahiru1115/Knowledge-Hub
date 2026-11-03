import { expect, test } from '@playwright/test'
import { password, uniqueEmail, violations } from './support/helpers'

/**
 * The release smoke test (Doc 02 §10.3): the four things a visitor does first, in one run, on whatever stack is up.
 * Sign in, add a link, find it by searching, and open Ask. With the AI service off, Ask says so instead of failing;
 * with it on (E2E_AI=1) the question box is ready.
 */
test('sign in, add a link, find it by search, open Ask', async ({ page }) => {
  const email = uniqueEmail()
  await page.goto('/register')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Your name').fill('Smoke Test')
  await page.getByLabel('Password', { exact: true }).fill(password)
  await page.getByRole('button', { name: 'Create account' }).click()
  await expect(page.getByRole('heading', { name: 'Welcome, Smoke Test' })).toBeVisible()

  // Sign out and back in, so that signing in is what is tested.
  await page.getByRole('button', { name: /^Account menu for/ }).click()
  await page.getByRole('menuitem', { name: 'Sign out' }).click()
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible()
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password', { exact: true }).fill(password)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page.getByRole('heading', { name: 'Welcome, Smoke Test' })).toBeVisible()

  // Add a link. The address is one nothing can fetch, so there is no title: a tag is what makes it findable, and the
  // save must not depend on the internet.
  await page.keyboard.press('n')
  const dialog = page.getByRole('dialog', { name: 'Add a resource' })
  await dialog.getByLabel('URL').fill('https://example.invalid/smoke-test-guide')
  await dialog.getByPlaceholder('Add a tag…').fill('pooling')
  await dialog.getByPlaceholder('Add a tag…').press('Enter')
  await dialog.getByRole('button', { name: 'Save' }).click()
  await expect(dialog).toBeHidden()

  // Find it by searching the library.
  await page.goto('/library')
  await page.getByRole('searchbox', { name: 'Search your library' }).fill('pooling')
  await expect(page).toHaveURL(/\/library\?.*q=pooling/)
  await expect(
    page
      .getByRole('list', { name: 'Resources' })
      .getByText(/example\.invalid/)
      .first(),
  ).toBeVisible()
  expect(await violations(page), 'search results').toEqual([])

  // Open Ask.
  await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Ask' }).click()
  await expect(page.getByRole('heading', { name: 'Ask your library' })).toBeVisible()
  if (process.env.E2E_AI === '1') {
    await expect(page.getByRole('textbox', { name: /question/i })).toBeVisible()
  } else {
    await expect(page.getByText(/unavailable|isn't available|not available/i).first()).toBeVisible()
  }
  expect(await violations(page), 'ask').toEqual([])
})
