import { MoreHorizontal, Plus } from 'lucide-react'
import { useState } from 'react'
import { Link } from '@tanstack/react-router'
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
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { Input } from '@/components/ui/input'
import { notifyError } from '@/lib/toasts'
import { cn } from '@/lib/utils'
import { useDeleteChat, useRenameChat, type ChatSession } from './queries'

interface Props {
  chats: ChatSession[]
  activeId?: string
  onNew: () => void
  /** Called after a chat is chosen, so a phone can close the list. */
  onPick?: () => void
  onDeleted: (id: string) => void
}

/** Past chats with rename and delete (FR-AI-08). */
export function ChatList({ chats, activeId, onNew, onPick, onDeleted }: Props) {
  const rename = useRenameChat()
  const remove = useDeleteChat()
  const [renaming, setRenaming] = useState<ChatSession | null>(null)
  const [title, setTitle] = useState('')
  const [deleting, setDeleting] = useState<ChatSession | null>(null)

  return (
    <div className="space-y-3">
      <Button className="w-full" variant="outline" onClick={onNew}>
        <Plus aria-hidden="true" /> New chat
      </Button>
      {chats.length === 0 ? (
        <p className="px-1 text-sm text-muted-foreground">Your questions will be kept here.</p>
      ) : (
        <ul aria-label="Chats" className="space-y-0.5">
          {chats.map((c) => (
            <li key={c.id} className="group flex items-center">
              <Link
                to="/ask/$sessionId"
                params={{ sessionId: c.id }}
                onClick={onPick}
                className={cn(
                  'min-w-0 flex-1 truncate rounded-md px-2 py-1.5 text-sm hover:bg-accent',
                  c.id === activeId && 'bg-accent font-medium',
                )}
                aria-current={c.id === activeId ? 'page' : undefined}
              >
                {c.title}
              </Link>
              <DropdownMenu>
                <DropdownMenuTrigger asChild>
                  <Button variant="ghost" size="icon-sm" aria-label={`Actions for ${c.title}`}>
                    <MoreHorizontal aria-hidden="true" />
                  </Button>
                </DropdownMenuTrigger>
                <DropdownMenuContent align="end">
                  <DropdownMenuItem
                    onSelect={() => {
                      setTitle(c.title)
                      setRenaming(c)
                    }}
                  >
                    Rename
                  </DropdownMenuItem>
                  <DropdownMenuItem
                    variant="destructive"
                    onSelect={() => {
                      setDeleting(c)
                    }}
                  >
                    Delete
                  </DropdownMenuItem>
                </DropdownMenuContent>
              </DropdownMenu>
            </li>
          ))}
        </ul>
      )}

      <Dialog
        open={renaming !== null}
        onOpenChange={(o) => {
          if (!o) setRenaming(null)
        }}
      >
        <DialogContent className="sm:max-w-sm">
          <form
            className="space-y-4"
            onSubmit={(e) => {
              e.preventDefault()
              const target = renaming
              if (!target || !title.trim()) return
              rename.mutate(
                { id: target.id, title: title.trim() },
                {
                  onSuccess: () => {
                    setRenaming(null)
                  },
                  onError: (e) => {
                    notifyError(e)
                  },
                },
              )
            }}
          >
            <DialogHeader>
              <DialogTitle>Rename chat</DialogTitle>
              <DialogDescription>Give this chat a name you will recognise.</DialogDescription>
            </DialogHeader>
            <Input
              aria-label="Chat name"
              value={title}
              maxLength={200}
              onChange={(e) => {
                setTitle(e.target.value)
              }}
            />
            <DialogFooter>
              <Button
                type="button"
                variant="outline"
                onClick={() => {
                  setRenaming(null)
                }}
              >
                Cancel
              </Button>
              <Button type="submit" disabled={!title.trim() || rename.isPending}>
                Save
              </Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      <AlertDialog
        open={deleting !== null}
        onOpenChange={(o) => {
          if (!o) setDeleting(null)
        }}
      >
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Delete this chat?</AlertDialogTitle>
            <AlertDialogDescription>
              &lsquo;{deleting?.title}&rsquo; and its answers will be removed. Your library is not
              affected.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Keep it</AlertDialogCancel>
            <AlertDialogAction
              onClick={() => {
                const target = deleting
                if (!target) return
                remove.mutate(target.id, {
                  onSuccess: () => {
                    onDeleted(target.id)
                  },
                  onError: (e) => {
                    notifyError(e)
                  },
                })
              }}
            >
              Delete
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  )
}
