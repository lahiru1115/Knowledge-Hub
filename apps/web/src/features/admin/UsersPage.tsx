import { useMutation, useQuery, useQueryClient, useSuspenseQuery } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { ApiProblem, api, unwrap } from '@/api/client'
import { messageFor } from '@/api/problems'
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from '@/components/ui/alert-dialog'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Skeleton } from '@/components/ui/skeleton'
import { meQueryOptions } from '@/features/auth/session'
import { Pagination } from '@/features/library/Pagination'
import { timeAgo } from '@/lib/format'
import { notify, notifyError } from '@/lib/toasts'
import { usePageTitle } from '@/lib/usePageTitle'
import { adminUsersQuery, type AdminUser } from './queries'

const dateFormat = new Intl.DateTimeFormat('en', { dateStyle: 'medium' })

/** Waits for typing to pause before the search is sent. */
function useDebounced<T>(value: T, ms = 300): T {
  const [v, setV] = useState(value)
  useEffect(() => {
    const t = setTimeout(() => {
      setV(value)
    }, ms)
    return () => {
      clearTimeout(t)
    }
  }, [value, ms])
  return v
}

/** SCR-ADMIN users: search by email, page through, disable or enable (FR-ADM-01, FR-ADM-02). */
export function UsersPage() {
  usePageTitle('Admin · Users')
  const { data: me } = useSuspenseQuery(meQueryOptions)
  const queryClient = useQueryClient()
  const [text, setText] = useState('')
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [target, setTarget] = useState<AdminUser | null>(null)
  const q = useDebounced(text.trim())
  const { data, isPending, isError, error, refetch } = useQuery(adminUsersQuery(q, page, pageSize))

  const setDisabled = useMutation({
    mutationFn: ({ user, disabled }: { user: AdminUser; disabled: boolean }) =>
      unwrap(
        api.PATCH('/admin/users/{id}', {
          params: { path: { id: user.id } },
          body: { is_disabled: disabled },
        }),
      ),
    onSuccess: (user) => {
      void queryClient.invalidateQueries({ queryKey: ['admin', 'users'] })
      notify(user.is_disabled ? `${user.email} is disabled.` : `${user.email} is enabled.`)
    },
    onError: (e) => {
      notifyError(e)
    },
  })

  const items = data?.items ?? []
  const total = data?.total ?? 0

  return (
    <section aria-labelledby="users-title" className="space-y-4">
      <h2 id="users-title" className="sr-only">
        Users
      </h2>
      <div className="flex flex-wrap items-center gap-3">
        <Input
          type="search"
          aria-label="Search users by email"
          placeholder="Search by email"
          className="max-w-xs"
          value={text}
          onChange={(e) => {
            setText(e.target.value)
            setPage(1)
          }}
        />
        <p className="text-sm text-muted-foreground" aria-live="polite">
          {data ? `${total.toString()} ${total === 1 ? 'user' : 'users'}` : ''}
        </p>
      </div>

      {isError ? (
        <div role="alert" className="space-y-2">
          <p>
            {error instanceof ApiProblem ? messageFor(error.code, error) : 'Could not load users.'}
          </p>
          <Button
            variant="outline"
            onClick={() => {
              void refetch()
            }}
          >
            Try again
          </Button>
        </div>
      ) : isPending ? (
        <Skeleton className="h-40 w-full" />
      ) : items.length === 0 ? (
        <p className="text-muted-foreground">No users match that search.</p>
      ) : (
        <div className="overflow-x-auto rounded-md border border-border">
          <table className="w-full text-left text-sm">
            <caption className="sr-only">Users</caption>
            <thead className="bg-muted/50">
              <tr>
                <th scope="col" className="p-3 font-medium">
                  User
                </th>
                <th scope="col" className="p-3 font-medium">
                  Role
                </th>
                <th scope="col" className="p-3 text-right font-medium">
                  Resources
                </th>
                <th scope="col" className="p-3 font-medium">
                  Joined
                </th>
                <th scope="col" className="p-3 font-medium">
                  Last sign-in
                </th>
                <th scope="col" className="p-3 font-medium">
                  Status
                </th>
                <th scope="col" className="p-3 font-medium">
                  <span className="sr-only">Actions</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {items.map((u) => (
                <tr key={u.id} className="border-t border-border">
                  <th scope="row" className="p-3 font-normal">
                    <span className="block font-medium">{u.display_name}</span>
                    <span className="block text-muted-foreground">{u.email}</span>
                  </th>
                  <td className="p-3 capitalize">{u.role}</td>
                  <td className="p-3 text-right tabular-nums">{u.resource_count}</td>
                  <td className="p-3">{dateFormat.format(new Date(u.created_at))}</td>
                  <td className="p-3">{u.last_login_at ? timeAgo(u.last_login_at) : 'Never'}</td>
                  <td className="p-3">
                    {u.is_disabled ? <Badge variant="destructive">Disabled</Badge> : 'Active'}
                  </td>
                  <td className="p-3 text-right">
                    {u.id === me.id ? (
                      <span className="text-muted-foreground">You</span>
                    ) : (
                      <Button
                        variant="outline"
                        size="sm"
                        aria-label={`${u.is_disabled ? 'Enable' : 'Disable'} ${u.email}`}
                        onClick={() => {
                          setTarget(u)
                        }}
                      >
                        {u.is_disabled ? 'Enable' : 'Disable'}
                      </Button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {total > 0 && (
        <Pagination
          page={page}
          pageSize={pageSize}
          total={total}
          label="Users per page"
          onPage={setPage}
          onPageSize={(n) => {
            setPageSize(n)
            setPage(1)
          }}
        />
      )}

      <AlertDialog
        open={target !== null}
        onOpenChange={(open) => {
          if (!open) setTarget(null)
        }}
      >
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>
              {target?.is_disabled ? 'Enable this user?' : 'Disable this user?'}
            </AlertDialogTitle>
            <AlertDialogDescription>
              {target?.is_disabled
                ? `${target.email} will be able to sign in again. Sessions that were ended stay ended.`
                : `${target?.email ?? ''} is signed out everywhere and can't sign in until you enable them again. Their data is kept.`}
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction
              onClick={() => {
                if (target) setDisabled.mutate({ user: target, disabled: !target.is_disabled })
              }}
            >
              {target?.is_disabled ? 'Enable' : 'Disable'}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </section>
  )
}
