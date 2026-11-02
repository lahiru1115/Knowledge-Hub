import type { ReactNode } from 'react'

export function AuthLayout({ title, children }: { title: string; children: ReactNode }) {
  return (
    <main className="mx-auto grid min-h-dvh w-full max-w-sm content-center gap-6 px-4 py-8">
      <header className="text-center">
        <p className="text-2xl font-semibold">Knowledge Hub</p>
        <p className="text-sm text-text-muted">
          Save, organise and learn from what you find online.
        </p>
      </header>
      <section
        aria-labelledby="auth-title"
        className="space-y-4 rounded-lg border border-border bg-bg p-6"
      >
        <h1 id="auth-title" className="text-xl font-semibold">
          {title}
        </h1>
        {children}
      </section>
    </main>
  )
}
