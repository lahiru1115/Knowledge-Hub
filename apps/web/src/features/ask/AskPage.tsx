import { useQuery } from '@tanstack/react-query'
import { useNavigate, useParams } from '@tanstack/react-router'
import { MessagesSquare, Send, Square } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { ApiProblem } from '@/api/client'
import { messageFor } from '@/api/problems'
import { AiMark } from '@/features/ai/AiMark'
import { aiUsageQuery, useAi } from '@/features/ai/queries'
import { collectionsQuery, resourcesQuery, tagsQuery } from '@/features/library/queries'
import { toApiQuery } from '@/features/library/librarySearch'
import { ResourceDrawer } from '@/features/library/ResourceDrawer'
import { useQuickAdd } from '@/features/library/QuickAdd'
import { Button } from '@/components/ui/button'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetHeader,
  SheetTitle,
} from '@/components/ui/sheet'
import { Skeleton } from '@/components/ui/skeleton'
import { Textarea } from '@/components/ui/textarea'
import { timeAgo } from '@/lib/format'
import { notifyError } from '@/lib/toasts'
import { usePageTitle } from '@/lib/usePageTitle'
import { AssistantMessage, LiveTurn, UserMessage } from './ChatMessages'
import { ChatList } from './ChatList'
import { chatSessionQuery, chatSessionsQuery, useCreateChat } from './queries'
import { useAsk } from './useAsk'

const WHOLE_LIBRARY = 'all'
export const MAX_QUESTION = 4000

/** Three questions to start from, drawn from the user's own tags and collections (Doc 03 §5.5). */
export function suggestedQuestions(tags: string[], collections: string[]): string[] {
  const fromTags = tags.slice(0, 2).map((t) => `What did I save about ${t}?`)
  const fromCollection = collections.slice(0, 1).map((c) => `What is in my ${c} collection?`)
  const generic = [
    'What did I save recently?',
    'Which of my resources are about the same topic?',
    'Summarise what I have saved so far.',
  ]
  return [...fromTags, ...fromCollection, ...generic].slice(0, 3)
}

/** Ask your library (SCR-ASK): past chats on the left, the conversation on the right. */
export function AskPage() {
  usePageTitle('Ask')
  const { sessionId } = useParams({ strict: false })
  const navigate = useNavigate()
  const quickAdd = useQuickAdd()
  const ai = useAi()
  const usage = useQuery(aiUsageQuery)
  const sessions = useQuery(chatSessionsQuery)
  const session = useQuery({ ...chatSessionQuery(sessionId ?? ''), enabled: Boolean(sessionId) })
  const { data: collections } = useQuery(collectionsQuery)
  const { data: tags } = useQuery(tagsQuery)
  const starters =
    collections && tags
      ? suggestedQuestions(
          [...tags.items].sort((a, b) => b.resource_count - a.resource_count).map((t) => t.name),
          collections.items.map((c) => c.name),
        )
      : undefined
  const library = useQuery(resourcesQuery(toApiQuery({})))
  const create = useCreateChat()
  const { turn, ask, stop, clear, busy } = useAsk()

  const [text, setText] = useState('')
  const [scope, setScope] = useState(WHOLE_LIBRARY)
  const [openId, setOpenId] = useState<string>()
  const [chatsOpen, setChatsOpen] = useState(false)
  const end = useRef<HTMLDivElement>(null)

  // Another chat, or a new one, starts clean.
  const [shownFor, setShownFor] = useState(sessionId)
  if (sessionId !== shownFor && !busy) {
    setShownFor(sessionId)
    clear()
  }
  useEffect(() => {
    end.current?.scrollIntoView({ block: 'end' })
  }, [turn?.answer, session.data?.messages.length])

  const chat = usage.data?.items.find((i) => i.operation === 'chat')
  const limit = chat?.limit ?? null
  const reached = limit !== null && chat !== undefined && chat.used >= limit
  const left = limit !== null && chat ? limit - chat.used : null
  const nearLimit = limit !== null && chat !== undefined && chat.used / limit >= 0.8 && !reached
  const quotaFailure = turn?.status === 'failed' && turn.error === 'ai_quota_exceeded'
  const unavailable = !ai.loading && !ai.chatAvailable
  const empty = library.data?.total === 0
  const disabled = unavailable || reached || quotaFailure
  const scopeName = collections?.items.find(
    (c) => c.id === (session.data?.scope_collection_id ?? scope),
  )?.name

  async function send(question: string) {
    const q = question.trim()
    if (!q || busy || disabled) return
    setText('')
    let id = sessionId
    if (!id) {
      try {
        const created = await create.mutateAsync(scope === WHOLE_LIBRARY ? null : scope)
        id = created.id
        setShownFor(id) // this chat owns the answer that is about to stream, so arriving at its address keeps it
        void navigate({ to: '/ask/$sessionId', params: { sessionId: id }, replace: true })
      } catch (e) {
        setText(q) // keep the question so the user can retry
        notifyError(e)
        void ai.refetch()
        return
      }
    }
    if (await ask(id, q, sessionId ? (session.data?.messages.length ?? 0) : 0)) clear()
    else void usage.refetch() // a refusal for the daily limit shows up in the usage too
  }

  // Once the saved chat has the answer, the live copy would show it twice.
  const savedAlready =
    turn?.status === 'done' && (session.data?.messages.length ?? 0) >= turn.baseCount + 2
  const retry = () => {
    if (turn) void send(turn.question)
  }
  const messages = session.data?.messages ?? []
  const lastAssistant = messages.length > 0 && messages[messages.length - 1]?.role === 'assistant'
  const lastQuestion = [...messages].reverse().find((m) => m.role === 'user')?.content
  const chatList = (
    <ChatList
      chats={sessions.data?.items ?? []}
      activeId={sessionId}
      onNew={() => {
        setChatsOpen(false)
        void navigate({ to: '/ask' })
      }}
      onPick={() => {
        setChatsOpen(false)
      }}
      onDeleted={(id) => {
        if (id === sessionId) void navigate({ to: '/ask' })
      }}
    />
  )

  return (
    <div className="grid gap-6 md:grid-cols-[16rem_1fr]">
      <aside aria-label="Chats" className="hidden md:block">
        {chatList}
      </aside>

      <section aria-labelledby="ask-title" className="flex min-h-[60dvh] min-w-0 flex-col gap-4">
        <header className="space-y-2">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <h1 id="ask-title" className="text-2xl font-semibold">
              Ask your library
            </h1>
            <Button
              variant="outline"
              className="md:hidden"
              onClick={() => {
                setChatsOpen(true)
              }}
            >
              <MessagesSquare aria-hidden="true" /> Chats
            </Button>
          </div>
          <p className="text-sm text-ai">
            <AiMark /> Answers use AI and come from your saved resources. Check the sources.
          </p>
          <div className="flex items-center gap-2 text-sm">
            <span id="scope-label" className="text-muted-foreground">
              Scope
            </span>
            {sessionId ? (
              <span>
                {session.data?.scope_collection_id
                  ? (scopeName ?? 'One collection')
                  : 'Whole library'}
              </span>
            ) : (
              <Select value={scope} onValueChange={setScope}>
                <SelectTrigger aria-labelledby="scope-label" className="w-56">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={WHOLE_LIBRARY}>Whole library</SelectItem>
                  {collections?.items.map((c) => (
                    <SelectItem key={c.id} value={c.id}>
                      {c.icon ?? '📁'} {c.name}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            )}
          </div>
        </header>

        {empty ? (
          <div className="space-y-3 rounded-md border p-8 text-center">
            <p className="text-lg font-medium">Ask needs something to read</p>
            <p className="text-muted-foreground">
              Save a few resources and Ask will answer from them, with sources.
            </p>
            <Button
              onClick={() => {
                quickAdd.open()
              }}
            >
              Add a resource
            </Button>
          </div>
        ) : (
          <>
            <div className="flex-1 space-y-6" aria-live="off">
              {sessionId && session.isPending ? (
                <div aria-busy="true" className="space-y-3">
                  <Skeleton className="h-4 w-1/3" />
                  <Skeleton className="h-16 w-full" />
                </div>
              ) : sessionId && session.isError ? (
                <div role="alert" className="space-y-3 rounded-md border p-6 text-center">
                  <p>
                    {session.error instanceof ApiProblem && session.error.code === 'not_found'
                      ? "This chat doesn't exist or was deleted."
                      : "Couldn't load this chat."}
                  </p>
                  <Button
                    variant="outline"
                    onClick={() => {
                      void navigate({ to: '/ask' })
                    }}
                  >
                    Start a new chat
                  </Button>
                </div>
              ) : (
                <>
                  {messages.length === 0 && !turn && starters && (
                    <div className="space-y-3 py-6">
                      <p className="text-muted-foreground">
                        Answers come from your saved resources and link to them.
                      </p>
                      <ul className="flex flex-wrap gap-2" aria-label="Suggested questions">
                        {starters.map((q) => (
                          <li key={q} className="max-w-full">
                            <Button
                              variant="outline"
                              className="h-auto max-w-full py-1.5 text-left whitespace-normal"
                              size="sm"
                              disabled={disabled}
                              onClick={() => {
                                void send(q)
                              }}
                            >
                              {q}
                            </Button>
                          </li>
                        ))}
                      </ul>
                    </div>
                  )}
                  {messages.map((m, i) =>
                    m.role === 'user' ? (
                      <UserMessage key={m.id} text={m.content} />
                    ) : (
                      <AssistantMessage
                        key={m.id}
                        text={m.content}
                        citations={m.citations ?? []}
                        onOpen={setOpenId}
                        onRetry={
                          i === messages.length - 1 &&
                          lastAssistant &&
                          lastQuestion &&
                          !busy &&
                          !disabled
                            ? () => {
                                void send(lastQuestion)
                              }
                            : undefined
                        }
                        onSearchInstead={() => {
                          void navigate({
                            to: '/library',
                            search: { q: lastQuestion, mode: 'semantic' },
                          })
                        }}
                      />
                    ),
                  )}
                  {turn && !savedAlready && (
                    <LiveTurn turn={turn} onOpen={setOpenId} onRetry={retry} />
                  )}
                  <div ref={end} />
                </>
              )}
            </div>

            <div className="sticky bottom-16 space-y-2 border-t bg-background pt-3 md:bottom-0">
              {unavailable && (
                <div
                  role="alert"
                  className="flex flex-wrap items-center gap-3 rounded-md border bg-muted p-3 text-sm"
                >
                  <span>Ask is unavailable right now. Your library still works.</span>
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={() => {
                      void ai.refetch()
                    }}
                  >
                    Try again
                  </Button>
                </div>
              )}
              {reached && (
                <p role="status" className="rounded-md border bg-muted p-3 text-sm">
                  You&apos;ve reached today&apos;s limit. It resets{' '}
                  {usage.data ? timeAgo(usage.data.reset_at) : 'at midnight UTC'}.
                </p>
              )}
              <form
                className="flex items-end gap-2"
                onSubmit={(e) => {
                  e.preventDefault()
                  void send(text)
                }}
              >
                <Textarea
                  aria-label="Ask about your library"
                  placeholder="Ask about your library…"
                  rows={2}
                  maxLength={MAX_QUESTION}
                  disabled={disabled}
                  value={text}
                  onChange={(e) => {
                    setText(e.target.value)
                  }}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
                      e.preventDefault()
                      void send(text)
                    }
                  }}
                />
                {busy ? (
                  <Button type="button" variant="outline" onClick={stop}>
                    <Square aria-hidden="true" /> Stop
                  </Button>
                ) : (
                  <Button type="submit" disabled={disabled || !text.trim() || create.isPending}>
                    <Send aria-hidden="true" /> Ask
                  </Button>
                )}
              </form>
              <p className="text-xs text-muted-foreground">
                {nearLimit && left !== null
                  ? `${left.toString()} ${left === 1 ? 'question' : 'questions'} left today`
                  : 'Enter to send, Shift+Enter for a new line.'}
              </p>
              <p className="sr-only" role="status">
                {turn?.status === 'done'
                  ? 'Answer ready.'
                  : turn?.status === 'failed'
                    ? messageFor('ai_unavailable')
                    : ''}
              </p>
            </div>
          </>
        )}
      </section>

      <Sheet open={chatsOpen} onOpenChange={setChatsOpen}>
        <SheetContent side="left" className="w-72 p-4">
          <SheetHeader>
            <SheetTitle>Chats</SheetTitle>
            <SheetDescription>Your past questions.</SheetDescription>
          </SheetHeader>
          {chatList}
        </SheetContent>
      </Sheet>

      {openId && (
        <ResourceDrawer
          id={openId}
          siblings={[openId]}
          onNavigate={setOpenId}
          onClose={() => {
            setOpenId(undefined)
          }}
        />
      )}
    </div>
  )
}
