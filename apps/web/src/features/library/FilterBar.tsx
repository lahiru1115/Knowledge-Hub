import { useQuery } from '@tanstack/react-query'
import { ChevronDown, Search, SlidersHorizontal, Star, X } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Input } from '@/components/ui/input'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
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
import { Toggle } from '@/components/ui/toggle'
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group'
import { AiMark } from '@/features/ai/AiMark'
import { UNAVAILABLE_TIP, useAi } from '@/features/ai/queries'
import { useIsDesktop } from '@/lib/useMediaQuery'
import { activeFilterCount, type LibrarySearch } from './librarySearch'
import { collectionsQuery, tagsQuery } from './queries'
import { RESOURCE_TYPES, SORTS, typeLabel, type ResourceType, type Sort } from './types'

export type SetSearch = (next: Partial<LibrarySearch>, opts?: { replace?: boolean }) => void

interface Props {
  search: LibrarySearch
  setSearch: SetSearch
  /** On a collection page the collection is fixed, so its filter is hidden. */
  scoped?: boolean
}

/** Keyword box, filters and sort. All of it is URL state (Doc 03 §5.2); changing any of it returns to page 1. */
export function FilterBar({ search, setSearch, scoped }: Props) {
  const desktop = useIsDesktop()
  const [sheet, setSheet] = useState(false)
  const count = activeFilterCount(search)
  const semantic = search.mode === 'semantic'

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-2">
        <QueryBox search={search} setSearch={setSearch} />
        {!scoped && <ModeToggle search={search} setSearch={setSearch} />}
        {semantic ? (
          <p className="text-xs text-muted-foreground">
            Filters and sorting aren&apos;t available when searching by meaning.
          </p>
        ) : desktop ? (
          <>
            <FilterControls search={search} setSearch={setSearch} scoped={scoped} />
            <SortSelect search={search} setSearch={setSearch} />
          </>
        ) : (
          <Button
            variant="outline"
            onClick={() => {
              setSheet(true)
            }}
          >
            <SlidersHorizontal aria-hidden="true" /> Filters
            {count > 0 ? ` (${count.toString()})` : ''}
          </Button>
        )}
      </div>
      {!desktop && !semantic && (
        <Sheet open={sheet} onOpenChange={setSheet}>
          <SheetContent side="bottom" className="max-h-[85dvh] overflow-y-auto px-4 pb-6">
            <SheetHeader>
              <SheetTitle>Filters</SheetTitle>
              <SheetDescription>Narrow the list, then close this panel.</SheetDescription>
            </SheetHeader>
            <div className="flex flex-col items-start gap-3">
              <FilterControls search={search} setSearch={setSearch} scoped={scoped} />
              <SortSelect search={search} setSearch={setSearch} />
              <Button
                className="w-full"
                onClick={() => {
                  setSheet(false)
                }}
              >
                Show results
              </Button>
            </div>
          </SheetContent>
        </Sheet>
      )}
      {!semantic && <ActiveFilters search={search} setSearch={setSearch} scoped={scoped} />}
    </div>
  )
}

/** Keyword | Semantic ✦ (Doc 03 §5.2). Semantic is disabled, with the reason, while the AI service is down. */
function ModeToggle({ search, setSearch }: Pick<Props, 'search' | 'setSearch'>) {
  const ai = useAi()
  const mode = search.mode ?? 'keyword'
  return (
    <ToggleGroup
      type="single"
      variant="outline"
      aria-label="Search mode"
      value={mode}
      onValueChange={(value) => {
        // Pressing the active item again reports an empty value; keep the mode.
        if (value)
          setSearch({ mode: value === 'semantic' ? 'semantic' : undefined, page: undefined })
      }}
    >
      <ToggleGroupItem value="keyword">Keyword</ToggleGroupItem>
      <ToggleGroupItem
        value="semantic"
        disabled={!ai.available && mode !== 'semantic'}
        title={ai.available ? 'Find resources by meaning, not just words' : UNAVAILABLE_TIP}
      >
        Semantic <AiMark />
      </ToggleGroupItem>
    </ToggleGroup>
  )
}

function QueryBox({ search, setSearch }: Pick<Props, 'search' | 'setSearch'>) {
  const semantic = search.mode === 'semantic'
  const [text, setText] = useState(search.q ?? '')
  // The `q` this box last agreed with the URL on. A different value in the URL means it changed from outside
  // (Back, Clear all, the top bar search), so the box follows; our own writes never trigger this.
  const [synced, setSynced] = useState(search.q)
  if (search.q !== synced) {
    setSynced(search.q)
    setText(search.q ?? '')
  }
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)
  useEffect(
    () => () => {
      clearTimeout(timer.current)
    },
    [],
  )

  function commit(value: string) {
    clearTimeout(timer.current)
    setSynced(value.trim() || undefined)
    setSearch({ q: value.trim() || undefined, page: undefined }, { replace: true })
  }

  /**
   * Typing updates the box at once. For keyword search the URL follows 300 ms after the last keystroke; a search by
   * meaning counts against a daily limit, so it waits for Enter.
   */
  function onChange(value: string) {
    setText(value)
    clearTimeout(timer.current)
    if (semantic) return
    timer.current = setTimeout(() => {
      setSynced(value || undefined)
      setSearch({ q: value || undefined, page: undefined }, { replace: true })
    }, 300)
  }

  return (
    <form
      className="relative min-w-52 flex-1 basis-64"
      onSubmit={(e) => {
        e.preventDefault()
        commit(text)
      }}
    >
      <Search
        className="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted-foreground"
        aria-hidden="true"
      />
      <Input
        type="search"
        aria-label="Search your library"
        data-page-search
        placeholder={
          semantic ? 'Describe what you are looking for, then press Enter' : 'Search your library…'
        }
        className="pl-9"
        value={text}
        onChange={(e) => {
          onChange(e.target.value)
        }}
      />
    </form>
  )
}

function FilterControls({ search, setSearch, scoped }: Props) {
  const { data: tags } = useQuery(tagsQuery)
  const { data: collections } = useQuery(collectionsQuery)
  const [tagText, setTagText] = useState('')
  const types = search.type ?? []
  const chosenTags = search.tag ?? []
  const toggle = <T extends string>(list: T[], v: T) =>
    list.includes(v) ? list.filter((x) => x !== v) : [...list, v]

  return (
    <>
      <Popover>
        <PopoverTrigger asChild>
          <Button variant="outline">
            Type{types.length > 0 ? ` (${types.length.toString()})` : ''}{' '}
            <ChevronDown aria-hidden="true" />
          </Button>
        </PopoverTrigger>
        <PopoverContent className="w-52" align="start">
          <fieldset className="space-y-2">
            <legend className="sr-only">Resource type</legend>
            {RESOURCE_TYPES.map((t) => (
              <label key={t.value} className="flex cursor-pointer items-center gap-2 text-sm">
                <Checkbox
                  checked={types.includes(t.value)}
                  onCheckedChange={() => {
                    setSearch({ type: toggle(types, t.value), page: undefined })
                  }}
                />
                {t.label}
              </label>
            ))}
          </fieldset>
        </PopoverContent>
      </Popover>

      <Popover>
        <PopoverTrigger asChild>
          <Button variant="outline">
            Tags{chosenTags.length > 0 ? ` (${chosenTags.length.toString()})` : ''}{' '}
            <ChevronDown aria-hidden="true" />
          </Button>
        </PopoverTrigger>
        <PopoverContent className="w-60" align="start">
          <Input
            aria-label="Filter tags"
            placeholder="Filter tags…"
            value={tagText}
            className="mb-2"
            onChange={(e) => {
              setTagText(e.target.value)
            }}
          />
          <fieldset className="max-h-56 space-y-2 overflow-y-auto">
            <legend className="sr-only">Tags (a resource must have all you tick)</legend>
            {(tags?.items ?? [])
              .filter((t) => t.name.includes(tagText.trim().toLowerCase()))
              .map((t) => (
                <label key={t.id} className="flex cursor-pointer items-center gap-2 text-sm">
                  <Checkbox
                    checked={chosenTags.includes(t.name)}
                    onCheckedChange={() => {
                      setSearch({ tag: toggle(chosenTags, t.name), page: undefined })
                    }}
                  />
                  #{t.name}
                  <span className="ml-auto text-xs text-muted-foreground">{t.resource_count}</span>
                </label>
              ))}
            {tags?.items.length === 0 && (
              <p className="text-sm text-muted-foreground">No tags yet.</p>
            )}
          </fieldset>
        </PopoverContent>
      </Popover>

      {!scoped && (
        <Select
          value={search.collection ?? 'all'}
          onValueChange={(v) => {
            setSearch({ collection: v === 'all' ? undefined : v, page: undefined })
          }}
        >
          <SelectTrigger aria-label="Collection" className="w-44">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="all">All collections</SelectItem>
            <SelectItem value="inbox">Inbox</SelectItem>
            {collections?.items.map((c) => (
              <SelectItem key={c.id} value={c.id}>
                {c.icon ?? '📁'} {c.name}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      )}

      <Toggle
        variant="outline"
        pressed={search.favourite === true}
        onPressedChange={(p) => {
          setSearch({ favourite: p || undefined, page: undefined })
        }}
      >
        <Star aria-hidden="true" /> Favourites
      </Toggle>
      <Toggle
        variant="outline"
        pressed={search.archived === true}
        onPressedChange={(p) => {
          setSearch({ archived: p || undefined, page: undefined })
        }}
      >
        Archived
      </Toggle>
    </>
  )
}

function SortSelect({ search, setSearch }: Pick<Props, 'search' | 'setSearch'>) {
  const hasQuery = Boolean(search.q)
  const current: Sort = search.sort ?? (hasQuery ? 'relevance' : 'newest')
  return (
    <Select
      value={current}
      onValueChange={(v) => {
        setSearch({ sort: v as Sort, page: undefined })
      }}
    >
      <SelectTrigger aria-label="Sort by" className="w-44">
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        {SORTS.filter((s) => s.value !== 'relevance' || hasQuery).map((s) => (
          <SelectItem key={s.value} value={s.value}>
            Sort: {s.label}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  )
}

function ActiveFilters({ search, setSearch, scoped }: Props) {
  const { data: collections } = useQuery(collectionsQuery)
  const chips: { key: string; label: string; remove: Partial<LibrarySearch> }[] = []
  for (const t of search.type ?? []) {
    chips.push({
      key: `type-${t}`,
      label: typeLabel(t as ResourceType),
      remove: { type: (search.type ?? []).filter((x) => x !== t) },
    })
  }
  for (const t of search.tag ?? []) {
    chips.push({
      key: `tag-${t}`,
      label: `#${t}`,
      remove: { tag: (search.tag ?? []).filter((x) => x !== t) },
    })
  }
  if (!scoped && search.collection) {
    const name =
      search.collection === 'inbox'
        ? 'Inbox'
        : (collections?.items.find((c) => c.id === search.collection)?.name ?? 'Collection')
    chips.push({ key: 'collection', label: name, remove: { collection: undefined } })
  }
  if (search.favourite)
    chips.push({ key: 'fav', label: 'Favourites', remove: { favourite: undefined } })
  if (search.archived)
    chips.push({ key: 'arch', label: 'Archived', remove: { archived: undefined } })
  if (chips.length === 0) return null

  return (
    <div className="flex flex-wrap items-center gap-2" aria-label="Active filters" role="group">
      {chips.map((c) => (
        <Badge key={c.key} variant="secondary" className="gap-1 pr-1 text-sm font-normal">
          {c.label}
          <button
            type="button"
            aria-label={`Remove filter ${c.label}`}
            className="grid size-5 place-items-center rounded-sm hover:bg-background/60"
            onClick={() => {
              setSearch({ ...c.remove, page: undefined })
            }}
          >
            <X className="size-3" aria-hidden="true" />
          </button>
        </Badge>
      ))}
      <Button
        variant="link"
        size="sm"
        onClick={() => {
          setSearch({
            type: undefined,
            tag: undefined,
            collection: scoped ? search.collection : undefined,
            favourite: undefined,
            archived: undefined,
            page: undefined,
          })
        }}
      >
        Clear all
      </Button>
    </div>
  )
}
