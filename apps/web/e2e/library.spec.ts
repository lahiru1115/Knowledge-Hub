import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'

const password = 'a long unusual passphrase'
const uniqueEmail = () => `e2e-${crypto.randomUUID()}@example.com`

async function violations(page: Page) {
  // Dialogs and menus fade in; measuring contrast mid-fade gives false results.
  // (Endless animations, such as the loading shimmer, never finish and are not what causes false results.)
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

async function register(page: Page, name = 'Ada Lovelace') {
  await page.goto('/register')
  await page.getByLabel('Email').fill(uniqueEmail())
  await page.getByLabel('Your name').fill(name)
  await page.getByLabel('Password', { exact: true }).fill(password)
  await page.getByRole('button', { name: 'Create account' }).click()
  await expect(page.getByRole('heading', { name: `Welcome, ${name}` })).toBeVisible()
}

/** Calls the API as the signed-in browser does (same cookies, CSRF header), to arrange data quickly. */
async function api(page: Page, method: 'POST' | 'PATCH' | 'DELETE', path: string, data?: unknown) {
  const csrf = (await page.context().cookies()).find((c) => c.name === 'kh_csrf')?.value ?? ''
  const response = await page.request.fetch(`/api/v1${path}`, {
    method,
    data,
    headers: { 'X-CSRF-Token': csrf },
  })
  expect(response.ok(), `${method} ${path}`).toBeTruthy()
  return response.status() === 204 ? undefined : ((await response.json()) as { id: string })
}

const resource = (page: Page, url: string, extra: object = {}) =>
  api(page, 'POST', '/resources', { url, ...extra })

async function addViaDialog(page: Page, url: string, tags: string[] = []) {
  await page.keyboard.press('n')
  const dialog = page.getByRole('dialog', { name: 'Add a resource' })
  await dialog.getByLabel('URL').fill(url)
  for (const t of tags) {
    await dialog.getByPlaceholder('Add a tag…').fill(t)
    await dialog.getByPlaceholder('Add a tag…').press('Enter')
  }
  await dialog.getByRole('button', { name: 'Save' }).click()
  await expect(dialog).toBeHidden()
}

test.describe('the library, end to end', () => {
  test('save a link with N, find it, edit it in the drawer, and it is all still there after a reload', async ({
    page,
  }) => {
    await register(page)
    await page.goto('/library')
    await expect(page.getByText('Your library is empty.')).toBeVisible()

    await addViaDialog(page, 'https://www.npgsql.org/doc/performance.html', ['postgres', 'dotnet'])
    await expect(page.getByText('Saved to Inbox')).toBeVisible()
    const row = page.getByRole('list', { name: 'Resources' }).getByRole('listitem').first()
    await expect(row.getByText(/npgsql\.org · /)).toBeVisible() // the type may be refined once details arrive
    await expect(row.getByRole('button', { name: 'Filter by tag postgres' })).toBeVisible()

    await row
      .getByRole('button', { name: 'https://www.npgsql.org/doc/performance.html', exact: true })
      .click()
    const drawer = page.getByRole('dialog', { name: 'Resource' })
    await expect(page).toHaveURL(/[?&]r=/)
    await drawer.getByRole('button', { name: 'Edit title' }).click()
    await drawer.getByLabel('Title').fill('Npgsql performance tips')
    await drawer.getByLabel('Title').press('Enter')
    await expect(drawer.getByRole('heading', { name: 'Npgsql performance tips' })).toBeVisible()

    await drawer.getByLabel('Notes (Markdown)').fill('Use `Max Auto Prepare` for hot queries.')
    await expect(drawer.getByText('Saved ✓')).toBeVisible({ timeout: 5000 }) // autosave after a pause
    await drawer.getByRole('tab', { name: 'Preview' }).click()
    await expect(drawer.locator('code')).toHaveText('Max Auto Prepare')

    await page.reload()
    const again = page.getByRole('dialog', { name: 'Resource' })
    await expect(again.getByRole('heading', { name: 'Npgsql performance tips' })).toBeVisible()
    await expect(again.getByLabel('Notes (Markdown)')).toHaveValue(
      'Use `Max Auto Prepare` for hot queries.',
    )
    await expect(again.getByRole('button', { name: 'Remove tag postgres' })).toBeVisible()
  })

  test('keyword search uses the real index: stemming, prefixes, tags, and the URL', async ({
    page,
  }) => {
    await register(page)
    await resource(page, 'https://example.com/a', {
      title: 'Indexing databases for speed',
      tags: ['performance'],
    })
    await resource(page, 'https://pgbouncer.org/docs', { title: 'Connection pooling guide' })
    await resource(page, 'https://example.com/c', {
      title: 'Cooking pasta',
      notes: 'no databases here, only recipes about pasta',
    })
    await page.goto('/library')
    const rows = page.getByRole('list', { name: 'Resources' }).locator('> li')
    const box = page.getByRole('searchbox', { name: 'Search your library' })

    await box.fill('database')
    await expect(page).toHaveURL(/[?&]q=database/)
    await expect(
      rows.first().getByRole('button', { name: 'Indexing databases for speed', exact: true }),
    ).toBeVisible() // title ranks first
    await expect(page.getByText('2 resources')).toBeVisible() // stemming finds "databases" and the notes mention

    await box.fill('poo')
    await expect(
      page.getByRole('button', { name: 'Connection pooling guide', exact: true }),
    ).toBeVisible() // prefix of the last word
    await box.fill('pgbouncer')
    await expect(
      page.getByRole('button', { name: 'Connection pooling guide', exact: true }),
    ).toBeVisible() // a word from the URL
    await box.fill('performance')
    await expect(
      page.getByRole('button', { name: 'Indexing databases for speed', exact: true }),
    ).toBeVisible() // a tag name
    await box.fill("zzz'; DROP TABLE x; --")
    await expect(page.getByText(/Nothing matches/)).toBeVisible()
    await page.getByRole('button', { name: 'Clear filters' }).click()
    await expect(page.getByText('3 resources')).toBeVisible()
  })

  test('filters live in the URL, so Back and bookmarks work', async ({ page }) => {
    await register(page)
    await resource(page, 'https://example.com/v', { title: 'A video', type: 'video', tags: ['x'] })
    await resource(page, 'https://example.com/p', {
      title: 'A paper',
      type: 'pdf',
      tags: ['x', 'y'],
    })
    await resource(page, 'https://example.com/a', { title: 'An article', type: 'article' })

    await page.goto('/library?type=video&type=pdf&tag=x&sort=title')
    await expect(page.getByText('2 resources')).toBeVisible()
    const titles = page
      .getByRole('list', { name: 'Resources' })
      .getByRole('listitem')
      .getByRole('button', { name: /^A / })
    await expect(titles.nth(0)).toHaveText('A paper') // A to Z
    await expect(page.getByRole('group', { name: 'Active filters' })).toContainText('#x')

    await page.getByRole('button', { name: 'Remove filter Video' }).click()
    await expect(page.getByText('1 resource')).toBeVisible()
    await page.goBack()
    await expect(page.getByText('2 resources')).toBeVisible() // Back restores the previous filter
    await page.getByRole('button', { name: 'Clear all' }).click()
    await expect(page.getByText('3 resources')).toBeVisible()
  })

  test('collections: create, move a resource in, see counts, and deleting offers a choice', async ({
    page,
  }) => {
    await register(page)
    await resource(page, 'https://example.com/1', { title: 'To move' })
    await resource(page, 'https://example.com/2', { title: 'Stays in the Inbox' })

    await page.goto('/collections')
    await page.getByRole('main').getByRole('button', { name: 'New collection' }).click()
    const dialog = page.getByRole('dialog', { name: 'New collection' })
    await dialog.getByLabel('Name').fill('Databases')
    await dialog.getByLabel('Description (optional)').fill('Notes on Postgres')
    await dialog.getByRole('button', { name: 'Use 📘' }).click()
    await dialog.getByRole('radio', { name: 'teal' }).click()
    await dialog.getByRole('button', { name: 'Create collection' }).click()
    await expect(page.getByRole('main').getByRole('link', { name: /Databases/ })).toBeVisible()

    // A duplicate name (any case) is refused with a clear message.
    await page.getByRole('main').getByRole('button', { name: 'New collection' }).click()
    await page.getByRole('dialog').getByLabel('Name').fill('databases')
    await page.getByRole('dialog').getByRole('button', { name: 'Create collection' }).click()
    await expect(page.getByText("You already have a collection called 'databases'.")).toBeVisible()
    await page.getByRole('dialog').getByRole('button', { name: 'Cancel' }).click()

    await page.goto('/library')
    await page.getByRole('button', { name: 'Actions for To move' }).click()
    await page.getByRole('menuitem', { name: 'Move to…' }).click()
    await page.getByRole('dialog', { name: 'Move resource' }).getByRole('combobox').click()
    await page.getByRole('option', { name: /Databases/ }).click()
    await page
      .getByRole('dialog', { name: 'Move resource' })
      .getByRole('button', { name: 'Move resource' })
      .click()

    const sidebar = page.getByRole('navigation', { name: 'Main' })
    await expect(sidebar.getByRole('link', { name: /Databases/ })).toContainText('1')
    await expect(sidebar.getByRole('link', { name: /Inbox/ })).toContainText('1')
    await sidebar.getByRole('link', { name: /Databases/ }).click()
    await expect(page.getByRole('heading', { name: '📘 Databases' })).toBeVisible()
    await expect(page.getByText('Notes on Postgres')).toBeVisible()
    await expect(page.getByRole('button', { name: 'To move', exact: true })).toBeVisible()
    await expect(page.getByRole('button', { name: 'Stays in the Inbox', exact: true })).toHaveCount(
      0,
    )

    await page.getByRole('button', { name: /Delete…/ }).click()
    const del = page.getByRole('dialog', { name: /Delete .*Databases/ })
    await expect(del.getByText('It contains 1 resource. What should happen to them?')).toBeVisible()
    await del.getByRole('button', { name: 'Delete' }).click()
    await expect(page).toHaveURL(/\/collections$/, { timeout: 10_000 })
    await page.goto('/library?collection=inbox')
    await expect(page.getByText('2 resources')).toBeVisible() // the moved one came back to the Inbox
  })

  test('tags: rename, merge look-alikes, and delete with Undo; search follows', async ({
    page,
  }) => {
    await register(page)
    await resource(page, 'https://example.com/1', { title: 'One', tags: ['postgres'] })
    await resource(page, 'https://example.com/2', { title: 'Two', tags: ['postgresql', 'react'] })

    await page.goto('/tags')
    const row = (name: string) =>
      page
        .getByRole('list', { name: 'Tags' })
        .locator('> li')
        // Match the row by its exact tag link: "Similar to #x" hints in other rows also contain tag names.
        .filter({ has: page.getByRole('link', { name: `#${name}`, exact: true }) })
    await expect(row('postgresql').getByText(/Similar to #postgres/)).toBeVisible()
    await row('postgresql').getByRole('button', { name: 'Merge' }).click()
    await expect(page.getByText('Merged #postgresql into #postgres')).toBeVisible()
    await expect(row('postgres').getByText('2 resources')).toBeVisible()

    await row('react').getByRole('button', { name: 'Rename' }).click()
    const renameInput = page.getByLabel('New name for #react', { exact: true })
    await renameInput.fill('React JS')
    await renameInput.press('Enter')
    await expect(row('react js')).toBeVisible()
    // Rename onto an existing tag offers a merge.
    await row('react js').getByRole('button', { name: 'Rename' }).click()
    // While renaming, the row shows an input instead of the link, so find the pieces directly.
    const input = page.getByLabel('New name for #react js')
    await input.fill('postgres')
    await input.press('Enter')
    await expect(
      page.getByRole('alert').filter({ hasText: 'You already have #postgres' }),
    ).toContainText('Merge the two tags instead?')
    await page.getByRole('button', { name: 'Cancel' }).click()

    await page.goto('/library?q=react')
    await expect(page.getByRole('button', { name: 'Two', exact: true })).toBeVisible() // the index follows the rename

    await page.goto('/tags')
    await row('postgres').getByRole('button', { name: 'More actions for #postgres' }).click()
    await page.getByRole('menuitem', { name: 'Delete' }).click()
    await expect(row('postgres')).toHaveCount(0)
    await page.getByRole('button', { name: 'Undo' }).click()
    await expect(row('postgres').getByText('2 resources')).toBeVisible() // back on both resources
  })

  test('bulk actions and keyboard shortcuts', async ({ page }) => {
    await register(page)
    for (const n of ['One', 'Two', 'Three'])
      await resource(page, `https://example.com/${n}`, { title: n })
    await page.goto('/library')
    const rows = page.getByRole('list', { name: 'Resources' }).locator('> li')
    await expect(rows).toHaveCount(3)

    await page.keyboard.press('j')
    await expect(rows.first()).toHaveAttribute('data-active', 'true')
    await page.keyboard.press('x')
    await page.keyboard.press('j')
    await page.keyboard.press('x')
    await expect(page.getByRole('toolbar', { name: 'Bulk actions' })).toContainText('2 selected')
    await page.getByRole('button', { name: 'Add tags' }).click()
    await page.getByRole('dialog').getByPlaceholder('Add a tag…').fill('batch')
    await page.getByRole('dialog').getByPlaceholder('Add a tag…').press('Enter')
    await page.getByRole('dialog').getByRole('button', { name: 'Add 1 tag' }).click()
    await expect(page.getByRole('button', { name: 'Filter by tag batch' })).toHaveCount(2)

    await page.keyboard.press('j')
    await page.keyboard.press('f') // favourite the active one
    await expect(page.getByRole('button', { name: /Remove .* from favourites/ })).toHaveCount(1)
    await page.keyboard.press('Delete')
    await expect(page.getByText(/Deleted '/)).toBeVisible()
    await page.getByRole('button', { name: 'Undo' }).click()
    await expect(rows).toHaveCount(3)
  })

  test('pagination over the real API', async ({ page }) => {
    await register(page)
    for (let i = 0; i < 25; i++)
      await resource(page, `https://example.com/p${i.toString()}`, {
        title: `Item ${i.toString().padStart(2, '0')}`,
      })
    await page.goto('/library?sort=title')
    const rows = page.getByRole('list', { name: 'Resources' }).locator('> li')
    await expect(page.getByText('Page 1 of 2')).toBeVisible()
    await expect(rows).toHaveCount(20)
    await page.getByRole('button', { name: /Next/ }).click()
    await expect(page.getByText('Page 2 of 2')).toBeVisible()
    await expect(rows).toHaveCount(5)
    await expect(page).toHaveURL(/page=2/)
    await page.getByRole('combobox', { name: 'Resources per page' }).click()
    await page.getByRole('option', { name: '50 per page' }).click()
    await expect(rows).toHaveCount(25)
  })

  test("another user's resource, collection and tag are not reachable", async ({
    page,
    browser,
  }) => {
    await register(page)
    const secret = await resource(page, 'https://example.com/secret', {
      title: 'Secret plan',
      tags: ['private'],
    })
    const collection = await api(page, 'POST', '/collections', { name: 'Private' })

    const other = await browser.newContext()
    const otherPage = await other.newPage()
    await register(otherPage, 'Grace Hopper')
    await otherPage.goto(`/library?r=${secret?.id ?? ''}`)
    await expect(otherPage.getByText("This resource doesn't exist or was deleted.")).toBeVisible()
    await otherPage.goto(`/collections/${collection?.id ?? ''}`)
    await expect(otherPage.getByText("This collection doesn't exist or was deleted.")).toBeVisible()
    await otherPage.goto('/library?q=secret')
    await expect(otherPage.getByText(/Nothing matches|Your library is empty/)).toBeVisible()
    await otherPage.goto('/tags')
    await expect(otherPage.getByText('No tags yet')).toBeVisible()
    await other.close()
  })
})

test.describe('fetching details, end to end', () => {
  test('a link to a private address is refused by the server, and the row and preview say so', async ({
    page,
  }) => {
    await register(page)
    await page.goto('/library')
    await expect(page.getByText('Your library is empty.')).toBeVisible()
    await page.keyboard.press('n')
    const dialog = page.getByRole('dialog', { name: 'Add a resource' })
    await dialog.getByLabel('URL').fill('http://169.254.169.254/latest/meta-data/')
    await expect(dialog.getByText("Couldn't load a preview. You can still save it.")).toBeVisible()
    await dialog.getByRole('button', { name: 'Save' }).click()
    await expect(dialog).toBeHidden()

    const row = page.getByRole('list', { name: 'Resources' }).getByRole('listitem').first()
    // The background worker refuses the address; the page polls and then shows why.
    await expect(row.getByText("This address can't be fetched.")).toBeVisible({ timeout: 20_000 })
    await expect(row.getByText("Couldn't fetch details")).toBeVisible()
    await expect(row.getByRole('button', { name: /Retry fetching/ })).toHaveCount(0)
    expect(await violations(page), 'failed fetch').toEqual([])
  })

  test('a link that cannot be reached stays "fetching" while the server retries, and Retry is not offered yet', async ({
    page,
  }) => {
    await register(page)
    await page.goto('/library')
    await expect(page.getByText('Your library is empty.')).toBeVisible()
    await addViaDialog(page, 'https://no-such-host.invalid/page')
    const row = page.getByRole('list', { name: 'Resources' }).getByRole('listitem').first()
    await expect(row.getByText('Fetching details…')).toBeVisible()
    expect(await violations(page), 'fetching').toEqual([])
    await row
      .getByRole('button', { name: 'https://no-such-host.invalid/page', exact: true })
      .click()
    await expect(
      page.getByRole('dialog', { name: 'Resource' }).getByText('Fetching details…'),
    ).toBeVisible()
  })
})

test.describe('on a phone (360 px)', () => {
  test.use({ viewport: { width: 360, height: 740 }, hasTouch: true })

  test('has a tab bar and a floating Add button, quick-add is a bottom sheet, and nothing scrolls sideways', async ({
    page,
  }) => {
    await register(page)
    await resource(page, 'https://example.com/a-very-long-address/' + 'segment/'.repeat(30), {
      title: 'A resource with an extremely long title '.repeat(6),
      tags: ['alpha', 'beta', 'gamma'],
    })

    const tabs = page.getByRole('navigation', { name: 'Tabs' })
    await expect(tabs).toBeVisible()
    await expect(page.getByRole('navigation', { name: 'Main' })).toBeHidden() // the sidebar is desktop only
    for (const path of ['/library', '/collections', '/tags', '/settings/profile', '/']) {
      await page.goto(path)
      const overflow = await page.evaluate(
        () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
      )
      expect(overflow, `${path} scrolls sideways`).toBeLessThanOrEqual(0)
    }

    await page.goto('/library')
    await page.getByRole('button', { name: 'Add resource' }).click()
    const sheet = page.getByRole('dialog', { name: 'Add a resource' })
    await expect(sheet).toBeVisible()
    const box = await sheet.boundingBox()
    expect(box?.width).toBeGreaterThan(340) // full width
    expect((box?.y ?? 0) + (box?.height ?? 0)).toBeGreaterThan(700) // at the bottom
    await page.keyboard.press('Escape')

    await page.getByRole('button', { name: /^Filters/ }).click()
    await expect(page.getByRole('dialog', { name: 'Filters' })).toBeVisible()
    await page
      .getByRole('dialog', { name: 'Filters' })
      .getByRole('button', { name: 'Favourites' })
      .click()
    await page.getByRole('button', { name: 'Show results' }).click()
    await expect(page).toHaveURL(/favourite=true/)
  })

  test('touch targets are at least 24 px (WCAG 2.5.8) and the drawer is full screen', async ({
    page,
  }) => {
    await register(page)
    const r = await resource(page, 'https://example.com/t', { title: 'Tap me' })
    await page.goto('/library')
    const small = await page.evaluate(() => {
      const bad: string[] = []
      for (const el of document.querySelectorAll<HTMLElement>(
        'button, a[href], input, [role="checkbox"], [role="combobox"]',
      )) {
        const r = el.getBoundingClientRect()
        if (r.width === 0 || r.height === 0) continue
        if (r.width < 24 || r.height < 24)
          bad.push(
            `${el.tagName} ${el.getAttribute('aria-label') ?? el.textContent.slice(0, 20)} ${Math.round(r.width)}x${Math.round(r.height)}`,
          )
      }
      return bad
    })
    expect(small).toEqual([])
    await page.goto(`/library?r=${r?.id ?? ''}`)
    const drawer = page.getByRole('dialog', { name: 'Resource' })
    await expect(drawer.getByRole('heading', { name: 'Tap me' })).toBeVisible()
    const box = await drawer.boundingBox()
    expect(box?.width).toBeGreaterThanOrEqual(359)
  })
})

for (const scheme of ['light', 'dark'] as const) {
  test(`the library screens have no accessibility violations (${scheme})`, async ({ page }) => {
    await page.emulateMedia({ colorScheme: scheme })
    await register(page)
    const col = await api(page, 'POST', '/collections', {
      name: 'Databases',
      icon: '📘',
      description: 'Postgres notes',
    })
    await resource(page, 'https://example.com/a', {
      title: 'Connection pooling',
      description: 'About pools',
      tags: ['postgres', 'dotnet'],
      collection_id: col?.id,
      notes: '# Notes\n\n- one',
    })
    await resource(page, 'https://example.com/b', { title: 'Another one', type: 'video' })

    // Empty-state-free list with data, then each overlay.
    await page.goto('/library')
    await expect(
      page.getByRole('button', { name: 'Connection pooling', exact: true }),
    ).toBeVisible()
    expect(await violations(page), 'library list').toEqual([])

    await page.getByRole('button', { name: /^Type/ }).click()
    await expect(page.getByRole('checkbox', { name: 'Video' })).toBeVisible()
    expect(await violations(page), 'type filter open').toEqual([])
    await page.keyboard.press('Escape')

    await page.getByRole('checkbox', { name: 'Select Another one' }).check()
    await expect(page.getByRole('toolbar', { name: 'Bulk actions' })).toBeVisible()
    expect(await violations(page), 'bulk bar').toEqual([])
    await page.getByRole('button', { name: /Cancel/ }).click()

    await page.getByRole('button', { name: 'Connection pooling', exact: true }).click()
    await expect(
      page
        .getByRole('dialog', { name: 'Resource' })
        .getByRole('heading', { name: 'Connection pooling' }),
    ).toBeVisible()
    expect(await violations(page), 'drawer').toEqual([])
    await page
      .getByRole('dialog', { name: 'Resource' })
      .getByRole('tab', { name: 'Preview' })
      .click()
    expect(await violations(page), 'drawer notes preview').toEqual([])
    await page.keyboard.press('Escape')

    await page.keyboard.press('n')
    await expect(page.getByRole('dialog', { name: 'Add a resource' })).toBeVisible()
    await page.getByRole('button', { name: /More details/ }).click()
    expect(await violations(page), 'quick-add').toEqual([])
    await page.keyboard.press('Escape')

    await page.goto('/library?q=zzzznothing')
    await expect(page.getByText(/Nothing matches/)).toBeVisible()
    expect(await violations(page), 'no results').toEqual([])

    await page.goto('/collections')
    await expect(page.getByRole('main').getByRole('link', { name: /Databases/ })).toBeVisible()
    expect(await violations(page), 'collections').toEqual([])
    await page.getByRole('button', { name: 'Actions for Databases' }).click()
    await page.getByRole('menuitem', { name: 'Delete…' }).click()
    await expect(page.getByRole('dialog', { name: /Delete/ })).toBeVisible()
    await expect(page.getByRole('menu')).toHaveCount(0) // scan once the menu has finished fading out
    expect(await violations(page), 'delete collection dialog').toEqual([])
    await page.keyboard.press('Escape')

    await page.goto(`/collections/${col?.id ?? ''}`)
    await expect(page.getByRole('heading', { name: '📘 Databases' })).toBeVisible()
    expect(await violations(page), 'collection page').toEqual([])

    await page.goto('/tags')
    await expect(page.getByRole('list', { name: 'Tags' })).toBeVisible()
    expect(await violations(page), 'tags').toEqual([])

    await page.goto('/')
    await page.getByRole('button', { name: 'Account menu for Ada Lovelace' }).click()
    await expect(page.getByRole('menu')).toBeVisible()
    await expect(page.getByRole('menuitem').first()).toHaveCSS('opacity', '1') // fully faded in
    expect(await violations(page), 'account menu').toEqual([])
  })
}
