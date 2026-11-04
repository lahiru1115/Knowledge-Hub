import type { ReactNode } from 'react'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'

export function AuthLayout({ title, children }: { title: string; children: ReactNode }) {
  return (
    <main className="mx-auto grid min-h-dvh w-full max-w-sm content-center gap-6 px-4 py-8">
      <header className="text-center">
        <p className="text-2xl font-semibold">Knowledge Hub</p>
        <p className="text-sm text-muted-foreground">
          Save, organise and learn from what you find online.
        </p>
      </header>
      <Card role="region" aria-labelledby="auth-title">
        <CardHeader>
          <CardTitle>
            <h1 id="auth-title" className="text-xl">
              {title}
            </h1>
          </CardTitle>
        </CardHeader>
        <CardContent className="space-y-4">{children}</CardContent>
      </Card>
    </main>
  )
}
