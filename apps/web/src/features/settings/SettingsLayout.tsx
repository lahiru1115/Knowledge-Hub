import { Link, Outlet } from '@tanstack/react-router'

const sections = [
  { to: '/settings/profile', label: 'Profile' },
  { to: '/settings/appearance', label: 'Appearance' },
  { to: '/settings/account', label: 'Account' },
] as const

/** Settings sections in a list on the left (stacked on small screens), Doc 03 §5.8. */
export function SettingsLayout() {
  return (
    <div className="grid gap-8 md:grid-cols-[12rem_1fr]">
      <nav aria-label="Settings">
        <h1 className="mb-3 text-2xl font-semibold">Settings</h1>
        <ul className="flex gap-1 md:flex-col">
          {sections.map((s) => (
            <li key={s.to}>
              <Link
                to={s.to}
                className="block rounded-md px-3 py-2 text-sm hover:bg-bg-subtle"
                activeProps={{
                  className: 'bg-accent-subtle font-medium text-accent',
                  'aria-current': 'page',
                }}
              >
                {s.label}
              </Link>
            </li>
          ))}
        </ul>
      </nav>
      <div className="min-w-0">
        <Outlet />
      </div>
    </div>
  )
}
