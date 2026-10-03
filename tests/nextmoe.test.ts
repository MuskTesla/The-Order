import assert from 'node:assert/strict'
import { getEventListeners } from 'node:events'
import test from 'node:test'
import { NextMoeClient, NextMoeError, normalizeBaseUrl } from '../electron/nextmoe'

const options = { baseUrl: 'https://api.nextmoe.dev/v2/', apiKey: 'test-secret-key', includeNsfw: true }
const work = { object: 'work', id: '86', display_name: 'クラナド', localized: { 'zh-Hans': { value: '克兰纳德' } } }
const release = {
  object: 'release', id: '1024', work_id: null, title: 'CLANNAD HD Edition', release_kind: 'digital',
  date: '2025-11-27', lang: 'ja', platform: 'win', platforms: ['win'],
  refs: [{ source: 'vndb', external_id: 'r1024' }, { source: 'steam', external_id: '3241660' }]
}
const hit = (id: string) => ({ object: 'search_result', target_object: 'work', id, display_name: 'Search title' })
const json = (body: unknown, status = 200, headers: Record<string, string> = {}) => new Response(JSON.stringify(body), {
  status, headers: { 'content-type': status >= 400 ? 'application/problem+json' : 'application/json', ...headers }
})
const errorCode = (code: string) => (error: unknown) => error instanceof NextMoeError && error.code === code

test('normalizes the base URL and rejects credentials, insecure remote HTTP and URL suffixes', () => {
  assert.equal(normalizeBaseUrl(' https://api.nextmoe.dev '), 'https://api.nextmoe.dev/v2/')
  assert.equal(normalizeBaseUrl('https://proxy.example/prefix/v2'), 'https://proxy.example/prefix/v2/')
  assert.equal(normalizeBaseUrl('http://127.0.0.1:1234/v2'), 'http://127.0.0.1:1234/v2/')
  assert.equal(normalizeBaseUrl('http://[::1]:1234'), 'http://[::1]:1234/v2/')
  for (const invalid of ['http://example.com/v2', 'https://name:password@example.com', 'https://example.com/?key=x', 'https://example.com/#key', 'file:///tmp', 'https://']) {
    assert.throws(() => normalizeBaseUrl(invalid), errorCode('INVALID_BASE_URL'))
  }
  assert.throws(() => new NextMoeClient({ ...options, apiKey: 'secret\nInjected: header' }), errorCode('MISSING_API_KEY'))
})

test('ref resolution sends one encoded batch with unique exact refs, metadata includes and no pagination', async () => {
  let calls = 0
  const largeId = '999999999999999999999999'
  const client = new NextMoeClient({ ...options, includeNsfw: false }, { fetch: async (input, init) => {
    calls++
    const url = new URL(String(input))
    assert.equal(url.pathname, '/v2/catalog/works')
    assert.equal(url.searchParams.get('refs'), `vndb:v4,bangumi:13,steam:${largeId}`)
    assert.equal(url.searchParams.get('include'), 'refs,titles,covers,intros,companies,ratings,tags')
    assert.equal(url.searchParams.get('nsfw'), 'false')
    assert.equal(url.searchParams.has('cursor'), false)
    assert.equal(url.searchParams.has('limit'), false)
    assert.equal(url.searchParams.has('ids'), false)
    assert.equal(url.searchParams.has('q'), false)
    assert.match(url.search, /refs=vndb%3Av4%2Cbangumi%3A13%2Csteam%3A/)
    assert.equal(new Headers(init?.headers).get('Authorization'), 'Bearer test-secret-key')
    assert.equal(init?.redirect, 'error')
    return json({ object: 'list', items: [
      { ...work, id: '3', refs: [{ source: 'steam', external_id: largeId }] },
      { ...work, id: '999', refs: [{ source: 'vndb', external_id: 'v999' }] },
      { ...work, id: '4', refs: [{ source: 'vndb', external_id: ' v4 ' }] },
      { ...work, id: '2' },
      { ...work, refs: [{ source: 'vndb', external_id: 'v4' }], intros: [{ lang: 'zh-Hans', value: '批量简介' }] }
    ], missing: ['bangumi:13'] })
  } })
  const result = await client.resolveWorksByRefs(['vndb:v4', 'bangumi:13', 'vndb:v4', `steam:${largeId}`])
  assert.deepEqual(result.map(item => item.id), ['3', '86'])
  assert.equal(result[0].references[0].id, largeId)
  assert.equal(result[1].description, '批量简介')
  assert.equal(calls, 1)
})

test('ref resolution deduplicates work IDs and preserves distinct works claiming the same ref', async () => {
  const item = { ...work, refs: [{ source: 'vndb', external_id: 'v4' }, { source: 'bangumi', external_id: '13' }] }
  const client = new NextMoeClient(options, { fetch: async () => json({ object: 'list', items: [item, { ...item, id: '99' }, item] }) })
  assert.deepEqual((await client.resolveWorksByRefs(['vndb:v4', 'bangumi:13'])).map(item => item.id), ['86', '99'])
})

test('missing or hidden refs return an empty result without searching or guessing a catalog ID', async () => {
  let calls = 0
  const client = new NextMoeClient(options, { fetch: async () => { calls++; return json({ object: 'list', items: [], missing: ['vndb:v4'] }) } })
  assert.deepEqual(await client.resolveWorksByRefs(['vndb:v4']), [])
  assert.equal(calls, 1)
})

test('work releases map documented nullable parent, title, kind, date and exact source refs', async () => {
  const client = new NextMoeClient({ ...options, includeNsfw: false }, { fetch: async (input, init) => {
    const url = new URL(String(input))
    assert.equal(url.pathname, '/v2/catalog/works/86/releases')
    assert.equal(url.searchParams.get('limit'), '100')
    assert.equal(url.searchParams.get('nsfw'), 'false')
    assert.equal(url.searchParams.has('include'), false)
    assert.equal(url.searchParams.has('cursor'), false)
    assert.equal(new Headers(init?.headers).get('Authorization'), 'Bearer test-secret-key')
    assert.equal(init?.redirect, 'error')
    return json({ object: 'list', items: [release, { ...release, id: '1025', work_id: '86', release_kind: 'trial' },
      { ...release, id: '1026', release_kind: 'patch' }] })
  } })
  const result = await client.getWorkReleases('86')
  assert.deepEqual(result[0], {
    id: '1024', workId: '86', title: 'CLANNAD HD Edition', originalTitle: 'CLANNAD HD Edition', kind: 'digital', date: '2025-11-27',
    references: [{ source: 'vndb', id: 'r1024', url: 'https://vndb.org/r1024' },
      { source: 'steam', id: '3241660', url: 'https://store.steampowered.com/app/3241660/' }]
  })
  assert.deepEqual(result.map(item => item.kind), ['digital', 'trial', 'patch'])
})

test('partial release metadata remains explicitly unknown and never invents a title or a full release', async () => {
  const client = new NextMoeClient(options, { fetch: async () => json({ object: 'list', items: [
    { object: 'release', id: '1', work_id: null },
    { object: 'release', id: '2', work_id: '86', title: null, release_kind: 'future-kind', date: '2026', refs: [] },
    { ...release, id: '3', refs: [...release.refs, release.refs[0], { source: 'future-source', external_id: 'opaque:release-1' }] }
  ] }) })
  const result = await client.getWorkReleases('86')
  assert.deepEqual(result.slice(0, 2), ['1', '2'].map(id => ({ id, workId: '86', title: '', originalTitle: '', kind: 'unknown', date: null, references: [] })))
  assert.equal(result[2].references.length, 3)
  assert.deepEqual(result[2].references[2], { source: 'future-source', id: 'opaque:release-1' })
})

test('maps compact real release responses without confusing a release ID with its parent work', async () => {
  // Reduced public catalogue samples from the 2026-10-02 release probe. No
  // credentials, local paths or complete catalogue responses belong in fixtures.
  const samples = [
    { workId: '60', row: {
      object: 'release', id: '251847', work_id: null, release_kind: 'default', date: '2026-08-19',
      title: 'STEINS;GATE RE:BOOT', lang: 'ja', platform: 'win', platforms: ['win'],
      engine: { object: 'engine', id: '81', display_name: 'Kaleido ADV Workshop' },
      refs: [{ source: 'steam', external_id: '4012810' }, { source: 'vndb', external_id: 'r128730' }]
    } },
    { workId: '4', row: {
      object: 'release', id: '213462', work_id: null, release_kind: 'default', date: '2024-08-17',
      title: 'Summer Pockets REFLECTION BLUE', lang: 'vi', platform: 'win', platforms: ['win'],
      engine: { object: 'engine', id: '33', display_name: 'SiglusEngine' },
      refs: [{ source: 'vndb', external_id: 'r78064' }]
    } },
    { workId: '23', row: {
      object: 'release', id: '194181', work_id: null, release_kind: 'default', date: '2023-04-28',
      title: '天使☆騒々 RE-BOOT! 通常版', lang: 'ja', platform: 'win', platforms: ['win'],
      refs: [{ source: 'dmm', external_id: '1158apc14266' }, { source: 'getchu', external_id: '1210049' },
        { source: 'vndb', external_id: 'r106563' }]
    } }
  ]
  for (const { workId, row } of samples) {
    let calls = 0
    const client = new NextMoeClient(options, { fetch: async input => {
      calls++
      assert.equal(new URL(String(input)).pathname, `/v2/catalog/works/${workId}/releases`)
      return json({ $schema: 'https://api.nextmoe.dev/ListRelease.json', object: 'list', items: [row] })
    } })
    const [result] = await client.getWorkReleases(workId)
    assert.equal(result.id, row.id)
    assert.equal(result.workId, workId)
    assert.equal(result.title, row.title)
    assert.equal(result.originalTitle, row.title)
    assert.equal(result.kind, 'default')
    assert.equal(result.date, row.date)
    assert.deepEqual(result.references.map(ref => [ref.source, ref.id]), row.refs.map(ref => [ref.source, ref.external_id]))
    const vndbRelease = result.references.find(ref => ref.source === 'vndb')!
    assert.match(vndbRelease.id, /^r\d+$/)
    assert.equal(vndbRelease.url, `https://vndb.org/${vndbRelease.id}`)
    // Preserving an upstream r* anchor must never turn it into a v* work lookup.
    await assert.rejects(client.resolveWorksByRefs([`vndb:${vndbRelease.id}`]), errorCode('INVALID_REFS'))
    assert.equal(calls, 1)
  }
})

test('real unnamed patch and named trial releases retain their distinct kinds and dates', async () => {
  const client = new NextMoeClient(options, { fetch: async () => json({
    $schema: 'https://api.nextmoe.dev/ListRelease.json', object: 'list', items: [
      { object: 'release', id: '162294', work_id: null, release_kind: 'patch', date: '2023-04-28',
        title: null, lang: 'zh-Hans', platform: 'win', platforms: ['win'],
        refs: [{ source: 'vndb', external_id: 'r106612' }] },
      { object: 'release', id: '308675', work_id: null, release_kind: 'trial', date: '2023-03-25',
        title: '天使☆騒々 RE-BOOT! 体験版', lang: 'ja', platform: 'win', platforms: ['win'],
        engine: { object: 'engine', id: '11', display_name: 'KiriKiri' },
        refs: [{ source: 'vndb', external_id: 'r104969' }] }
    ]
  }) })
  const result = await client.getWorkReleases('23')
  assert.deepEqual(result.map(item => [item.id, item.workId, item.title, item.originalTitle, item.kind, item.date]), [
    ['162294', '23', '', '', 'patch', '2023-04-28'],
    ['308675', '23', '天使☆騒々 RE-BOOT! 体験版', '天使☆騒々 RE-BOOT! 体験版', 'trial', '2023-03-25']
  ])
})

test('work releases reject foreign parents, missing identity and malformed protocol fields', async () => {
  for (const row of [
    null, { ...release, object: 'work' }, { ...release, id: '../other' }, { ...release, id: ' 1 ' },
    { ...release, id: 1 }, { ...release, work_id: '99' }, { ...release, work_id: undefined },
    { ...release, work_id: 86 }, { ...release, title: {} }, { ...release, title: 'x'.repeat(513) },
    { ...release, refs: null }, { ...release, refs: {} }, { ...release, refs: [null] },
    { ...release, refs: [{ source: 'steam', external_id: 12 }] },
    { ...release, refs: [{ source: 'steam', external_id: ' 12 ' }] }
  ]) {
    const client = new NextMoeClient(options, { fetch: async () => json({ object: 'list', items: [row] }) })
    await assert.rejects(client.getWorkReleases('86'), errorCode('INVALID_RESPONSE'))
  }
})

test('work releases follow one cursor, deduplicate IDs and stop after two bounded pages', async () => {
  const urls: URL[] = []
  const signals: AbortSignal[] = []
  const client = new NextMoeClient(options, { fetch: async (input, init) => {
    urls.push(new URL(String(input)))
    signals.push(init!.signal as AbortSignal)
    return json({ object: 'list', items: Array.from({ length: 100 }, (_, index) => ({
      ...release, id: String(index + (urls.length === 1 ? 1 : 100))
    })), next_cursor: urls.length === 1 ? 'cur_next.page-2' : 'cur_page3' })
  } })
  const result = await client.getWorkReleases('86')
  assert.equal(urls.length, 2)
  assert.equal(urls[0].searchParams.has('cursor'), false)
  assert.equal(urls[1].searchParams.get('cursor'), 'cur_next.page-2')
  assert.equal(result.length, 199)
  assert.equal(new Set(signals).size, 1)
})

test('work releases reject invalid lists, cursors and repeated pagination without leaking server values', async () => {
  for (const body of [null, { object: 'work', items: [] }, { object: 'list', items: null },
    { object: 'list', items: Array(101).fill(release) },
    ...['bad', 'cur_', 'cur_a/b', 'cur_x\n', 'cur_' + 'x'.repeat(509), null, 42].map(next_cursor => ({ object: 'list', items: [], next_cursor }))]) {
    const client = new NextMoeClient(options, { fetch: async () => json(body) })
    await assert.rejects(client.getWorkReleases('86'), errorCode('INVALID_RESPONSE'))
  }
  let calls = 0
  const client = new NextMoeClient(options, { fetch: async () => { calls++; return json({ object: 'list', items: [], next_cursor: 'cur_loop' }) } })
  await assert.rejects(client.getWorkReleases('86'), errorCode('INVALID_RESPONSE'))
  assert.equal(calls, 2)
})

test('work release merges discard previous pages and validate the canonical parent', async () => {
  const urls: URL[] = []
  const client = new NextMoeClient(options, { fetch: async input => {
    urls.push(new URL(String(input)))
    if (urls.length === 1) return json({ object: 'list', items: [release], next_cursor: 'cur_old' })
    if (urls.length === 2) return json({ object: 'work', code: 'ENTITY_MERGED', current_id: '123' }, 404)
    return json({ object: 'list', items: [{ ...release, id: '1025', work_id: '123' }] })
  } })
  const result = await client.getWorkReleases('86')
  assert.deepEqual(result.map(item => [item.id, item.workId]), [['1025', '123']])
  assert.deepEqual(urls.map(url => url.pathname), ['/v2/catalog/works/86/releases', '/v2/catalog/works/86/releases', '/v2/catalog/works/123/releases'])
  assert.equal(urls[2].searchParams.has('cursor'), false)
  const foreign = new NextMoeClient(options, { fetch: async input => new URL(String(input)).pathname.includes('/86/')
    ? json({ object: 'work', code: 'ENTITY_MERGED', current_id: '123' }, 404)
    : json({ object: 'list', items: [{ ...release, work_id: '86' }] }) })
  await assert.rejects(foreign.getWorkReleases('86'), errorCode('INVALID_RESPONSE'))
})

test('work releases bound merge chains and reject invalid request IDs before transport', async () => {
  let calls = 0
  const client = new NextMoeClient(options, { fetch: async () => json({ object: 'work', code: 'ENTITY_MERGED', current_id: String(++calls) }, 404) })
  await assert.rejects(client.getWorkReleases('../other'), errorCode('INVALID_ID'))
  assert.equal(calls, 0)
  await assert.rejects(client.getWorkReleases('86'), errorCode('MERGE_LIMIT'))
  assert.equal(calls, 4)
  const loop = new NextMoeClient(options, { fetch: async () => json({ object: 'work', code: 'ENTITY_MERGED', current_id: '86' }, 404) })
  await assert.rejects(loop.getWorkReleases('86'), errorCode('MERGE_LOOP'))
})

test('work release pages share the retry budget and total deadline', async () => {
  let calls = 0
  const signals: AbortSignal[] = []
  const delays: number[] = []
  const client = new NextMoeClient(options, {
    fetch: async (_, init) => {
      signals.push(init!.signal as AbortSignal)
      if (++calls === 2) return json({ object: 'list', items: [release], next_cursor: 'cur_second' })
      return json({ code: 'SERVICE_UNAVAILABLE' }, 503)
    }, delay: async ms => { delays.push(ms) }
  })
  await assert.rejects(client.getWorkReleases('86'), errorCode('SERVICE_UNAVAILABLE'))
  assert.equal(calls, 4)
  assert.deepEqual(delays, [500, 1000])
  assert.equal(new Set(signals).size, 1)
  let stalledCalls = 0
  const stalled = new NextMoeClient(options, { timeoutMs: 20, fetch: async () => ++stalledCalls === 1
    ? json({ object: 'list', items: [release], next_cursor: 'cur_second' }) : new Promise<Response>(() => {}) })
  await assert.rejects(stalled.getWorkReleases('86'), errorCode('TIMEOUT'))
  assert.equal(stalledCalls, 2)
})

test('work release cancellation stops pagination and sanitizes the caller reason', async () => {
  const controller = new AbortController()
  const signals: AbortSignal[] = []
  let started!: () => void
  const secondPage = new Promise<void>(resolve => { started = resolve })
  const client = new NextMoeClient({ ...options, signal: controller.signal }, { fetch: async (_, init) => {
    signals.push(init!.signal as AbortSignal)
    if (signals.length === 1) return json({ object: 'list', items: [release], next_cursor: 'cur_second' })
    started()
    return new Promise<Response>(() => {})
  } })
  const pending = client.getWorkReleases('86')
  const rejected = assert.rejects(pending, error => {
    assert.ok(error instanceof NextMoeError)
    assert.equal(error.code, 'CANCELLED')
    assert.equal(error.message.includes(options.apiKey), false)
    return true
  })
  await secondPage
  controller.abort(new Error(options.apiKey))
  await rejected
  assert.equal(signals.length, 2)
  assert.ok(signals.every(signal => signal.aborted))
  assert.equal(getEventListeners(controller.signal, 'abort').length, 0)
})

test('ref inputs reject unsupported sources, nonpositive IDs, nonwork IDs and batch limits before transport', async () => {
  let calls = 0
  const client = new NextMoeClient(options, { fetch: async () => { calls++; return json({ object: 'list', items: [] }) } })
  const invalid: unknown[] = [
    [], Array(1), null, 'vndb:v4', [42], ['vndb:r4'], ['vndb:v0'], ['vndb:v04'], ['VNDB:v4'], ['vndb:4'],
    ['bangumi:0'], ['bangumi:-1'], ['bangumi:01'], ['steam:1.2'], ['steam:0'], ['steam:1,steam:2'],
    ['vndb:v4 '], ['https://vndb.org/v4'], ['erogamescape:4'], ['steam:1\n'],
    Array.from({ length: 101 }, (_, index) => `steam:${index + 1}`), [`steam:${'1'.repeat(4091)}`]
  ]
  for (const refs of invalid) await assert.rejects(client.resolveWorksByRefs(refs as string[]), errorCode('INVALID_REFS'))
  assert.equal(calls, 0)
  assert.deepEqual(await client.resolveWorksByRefs(Array(101).fill('steam:1')), [])
  assert.deepEqual(await client.resolveWorksByRefs(Array.from({ length: 100 }, (_, index) => `steam:${index + 1}`)), [])
  assert.deepEqual(await client.resolveWorksByRefs([`steam:${'1'.repeat(4090)}`]), [])
  assert.equal(calls, 3)
})

test('ref resolution reports malformed envelopes, refs and work records explicitly', async () => {
  for (const body of [
    null, { object: 'list', items: null }, { object: 'work', items: [] },
    { object: 'list', items: [], missing: 'vndb:v4' }, { object: 'list', items: [], missing: [4] },
    { object: 'list', items: [], missing: ['steam:99'] }, { object: 'list', items: [null] },
    { object: 'list', items: [{ ...work, id: '../other' }] },
    { object: 'list', items: [{ ...work, refs: {} }] },
    { object: 'list', items: [{ ...work, refs: [null] }] },
    { object: 'list', items: [{ ...work, refs: [{ source: 'vndb', external_id: 4 }] }] }
  ]) {
    const client = new NextMoeClient(options, { fetch: async () => json(body) })
    await assert.rejects(client.resolveWorksByRefs(['vndb:v4']), errorCode('INVALID_RESPONSE'))
  }
})

test('ref resolution shares the controlled two-retry limit and total timeout', async () => {
  let calls = 0
  const signals: AbortSignal[] = []
  const retrying = new NextMoeClient(options, { fetch: async (_, init) => {
    calls++
    signals.push(init!.signal as AbortSignal)
    return json({ code: 'SERVICE_UNAVAILABLE' }, 503)
  }, delay: async () => {} })
  await assert.rejects(retrying.resolveWorksByRefs(['vndb:v4']), errorCode('SERVICE_UNAVAILABLE'))
  assert.equal(calls, 3)
  assert.equal(new Set(signals).size, 1)
  const stalled = new NextMoeClient(options, { timeoutMs: 20, fetch: async () => new Promise<Response>(() => {}) })
  await assert.rejects(stalled.resolveWorksByRefs(['vndb:v4']), errorCode('TIMEOUT'))
})

test('an already cancelled task performs no requests and never reveals the caller abort reason', async () => {
  const controller = new AbortController()
  controller.abort(new Error(`private ${options.apiKey}`))
  let calls = 0
  const client = new NextMoeClient({ ...options, signal: controller.signal }, { fetch: async () => { calls++; return json(work) } })
  for (const operation of [() => client.getWork('86'), () => client.getWorkReleases('86'), () => client.searchWorks('name'), () => client.resolveWorksByRefs(['vndb:v4'])]) {
    await assert.rejects(operation(), error => {
      assert.ok(error instanceof NextMoeError)
      assert.equal(error.code, 'CANCELLED')
      assert.equal(error.message.includes(options.apiKey), false)
      assert.equal(error.message.includes('private'), false)
      return true
    })
  }
  assert.equal((await client.testConnection()).ok, false)
  assert.equal(calls, 0)
})

test('task cancellation aborts concurrent operations even when their transports ignore it', async () => {
  const controller = new AbortController()
  const signals: AbortSignal[] = []
  const client = new NextMoeClient({ ...options, signal: controller.signal }, { fetch: async (_, init) => {
    signals.push(init!.signal as AbortSignal)
    return new Promise<Response>(() => {})
  } })
  const detail = client.getWork('86')
  const batch = client.resolveWorksByRefs(['vndb:v4'])
  const rejected = Promise.all([assert.rejects(detail, errorCode('CANCELLED')), assert.rejects(batch, errorCode('CANCELLED'))])
  controller.abort('untrusted reason')
  await rejected
  assert.equal(signals.length, 2)
  assert.ok(signals.every(signal => signal.aborted))
  assert.equal(getEventListeners(controller.signal, 'abort').length, 0)
})

test('task cancellation cancels a stalled JSON body and reports CANCELLED instead of invalid JSON', async () => {
  const controller = new AbortController()
  let cancelled = false
  let startReading!: () => void
  const reading = new Promise<void>(resolve => { startReading = resolve })
  const stream = new ReadableStream<Uint8Array>({
    start(streamController) { streamController.enqueue(new TextEncoder().encode('{')) },
    pull() { startReading() },
    cancel() { cancelled = true }
  })
  const client = new NextMoeClient({ ...options, signal: controller.signal }, { fetch: async () => new Response(stream) })
  const request = client.resolveWorksByRefs(['vndb:v4'])
  const rejected = assert.rejects(request, errorCode('CANCELLED'))
  await reading
  controller.abort(new Error(options.apiKey))
  await rejected
  assert.equal(cancelled, true)
})

test('task cancellation during retry delay prevents every subsequent request', async () => {
  const controller = new AbortController()
  let calls = 0
  const client = new NextMoeClient({ ...options, signal: controller.signal }, {
    fetch: async () => { calls++; return json({ code: 'SERVICE_UNAVAILABLE' }, 503) },
    delay: async (_, signal) => {
      controller.abort('private reason')
      assert.equal(signal.aborted, true)
      // Also exercise a test transport that ignores its cancellation signal.
      return new Promise<void>(() => {})
    }
  })
  await assert.rejects(client.resolveWorksByRefs(['vndb:v4']), errorCode('CANCELLED'))
  assert.equal(calls, 1)
})

test('completed operations remove the parent abort listener without cancelling their completed requests', async () => {
  const controller = new AbortController()
  let signal: AbortSignal | undefined
  const client = new NextMoeClient({ ...options, signal: controller.signal }, { fetch: async (_, init) => {
    signal = init!.signal as AbortSignal
    return json(work)
  } })
  assert.equal((await client.getWork('86')).id, '86')
  assert.equal(getEventListeners(controller.signal, 'abort').length, 0)
  controller.abort()
  assert.equal(signal?.aborted, false)
})

test('encodes Unicode search and cursor, attaches request-local Bearer auth and refuses redirects', async () => {
  const calls: { url: URL; init?: RequestInit }[] = []
  const client = new NextMoeClient(options, { fetch: async (input, init) => {
    calls.push({ url: new URL(String(input)), init })
    return calls.length === 1 ? json({ object: 'list', items: [hit('86')], next_cursor: 'cur_next' })
      : json({ object: 'list', items: [work], missing: [] })
  } })
  const query = '夏日 & 雨?# / +'
  const result = await client.searchWorks(query, 'cur_abc._~-')
  assert.equal(result.items[0].title, '克兰纳德')
  assert.equal(result.nextCursor, 'cur_next')
  assert.equal(calls[0].url.pathname, '/v2/catalog/search')
  assert.equal(calls[0].url.searchParams.get('object'), 'work')
  assert.equal(calls[0].url.searchParams.get('limit'), '20')
  assert.equal(calls[0].url.searchParams.get('q'), query)
  assert.equal(calls[0].url.searchParams.get('cursor'), 'cur_abc._~-')
  assert.equal(calls[0].url.searchParams.get('nsfw'), 'true')
  assert.equal(calls[1].url.pathname, '/v2/catalog/works')
  assert.equal(calls[1].url.searchParams.get('ids'), '86')
  assert.equal(calls[1].url.searchParams.get('q'), null)
  assert.equal(calls[1].url.searchParams.get('cursor'), null)
  assert.equal(calls[1].url.searchParams.get('include'), 'ratings,companies,refs,titles,covers')
  assert.equal(calls[1].init?.signal, calls[0].init?.signal)
  assert.equal(new Headers(calls[0].init?.headers).get('Authorization'), 'Bearer test-secret-key')
  assert.equal(calls[0].init?.redirect, 'error')
  assert.equal(calls[0].url.href.includes(options.apiKey), false)
  await assert.rejects(client.searchWorks(''), errorCode('INVALID_QUERY'))
  await assert.rejects(client.searchWorks('a', 'bad-cursor'), errorCode('INVALID_CURSOR'))
  assert.equal(calls.length, 2)
})

test('pagination is explicit and missing optional metadata stays empty', async () => {
  let calls = 0
  const client = new NextMoeClient({ ...options, includeNsfw: false }, { fetch: async (input) => {
    calls++
    assert.equal(new URL(String(input)).searchParams.get('nsfw'), 'false')
    if (calls === 1) return json({ object: 'list', items: [hit('99999999999999999999')], next_cursor: 'cur_page2' })
    if (calls === 2) return json({ object: 'list', items: [{ object: 'work', id: '99999999999999999999' }] })
    assert.equal(new URL(String(input)).searchParams.get('cursor'), 'cur_page2')
    return json({ object: 'list', items: [] })
  } })
  const first = await client.searchWorks('title')
  assert.equal(calls, 2)
  assert.equal(first.items[0].title, '作品 99999999999999999999')
  assert.equal(first.items[0].coverUrl, null)
  assert.equal(first.items[0].webUrl, null)
  assert.equal(first.items[0].releaseDate, null)
  assert.deepEqual(first.items[0].ratings, [])
  assert.deepEqual(first.items[0].characters, [])
  assert.deepEqual(await client.searchWorks('title', first.nextCursor!), { items: [], nextCursor: null })
  assert.equal(calls, 3)
})

test('batch hydration uses exact search IDs, preserves relevance order and skips unavailable hits', async () => {
  let calls = 0
  const client = new NextMoeClient(options, { fetch: async input => {
    calls++
    if (calls === 1) return json({ object: 'list', items: [hit('3'), hit('1'), hit('2')], next_cursor: 'cur_search_page' })
    const url = new URL(String(input))
    assert.equal(url.searchParams.get('ids'), '3,1,2')
    assert.equal(url.searchParams.get('nsfw'), 'true')
    return json({ object: 'list', items: [{ ...work, id: '2' }, { ...work, id: '3' }, { ...work, id: '999' }], missing: ['1'], next_cursor: 'cur_ignored_batch' })
  } })
  const result = await client.searchWorks('CLANNAD')
  assert.deepEqual(result.items.map(item => item.id), ['3', '2'])
  assert.equal(result.nextCursor, 'cur_search_page')
  assert.equal(calls, 2)
})

test('an empty search page does not request hydration and retains its search cursor', async () => {
  let calls = 0
  const client = new NextMoeClient(options, { fetch: async () => { calls++; return json({ object: 'list', items: [], next_cursor: 'cur_more' }) } })
  assert.deepEqual(await client.searchWorks('no results'), { items: [], nextCursor: 'cur_more' })
  assert.equal(calls, 1)
})

test('invalid search IDs or other entity kinds are rejected before hydration', async () => {
  for (const invalid of [{ ...hit('1'), id: '../keys' }, { ...hit('1'), target_object: 'character' }]) {
    let calls = 0
    const client = new NextMoeClient(options, { fetch: async () => { calls++; return json({ object: 'list', items: [invalid] }) } })
    await assert.rejects(client.searchWorks('name'), errorCode('INVALID_RESPONSE'))
    assert.equal(calls, 1)
  }
})

test('search and hydration share the retry budget instead of starting a second budget', async () => {
  let calls = 0
  const client = new NextMoeClient(options, {
    fetch: async () => {
      calls++
      return calls === 3 ? json({ object: 'list', items: [hit('86')] }) : json({ code: 'SERVICE_UNAVAILABLE' }, 503)
    },
    delay: async () => {}
  })
  await assert.rejects(client.searchWorks('name'), errorCode('SERVICE_UNAVAILABLE'))
  assert.equal(calls, 4)
})

test('hydration keeps the search timeout signal and aborts when the operation times out', async () => {
  const signals: AbortSignal[] = []
  const client = new NextMoeClient(options, { timeoutMs: 20, fetch: async (_, init) => {
    signals.push(init!.signal as AbortSignal)
    return signals.length === 1 ? json({ object: 'list', items: [hit('86')] }) : new Promise<Response>(() => {})
  } })
  await assert.rejects(client.searchWorks('name'), errorCode('TIMEOUT'))
  assert.equal(signals.length, 2)
  assert.equal(signals[1], signals[0])
  assert.equal(signals[0].aborted, true)
})

test('maps source-native scores, developers, aliases and safe source links', async () => {
  const client = new NextMoeClient(options, { fetch: async () => json({
    ...work,
    titles: [{ title: '克兰纳德' }, { title: 'Clannad' }, { title: 'Clannad' }],
    intros: [{ lang: 'en', value: 'English' }, { lang: 'zh-Hans', value: '中文简介' }],
    companies: [{ display_name: 'Key', attribution_role: 'developer' }, { display_name: 'Publisher', attribution_role: 'publisher' }],
    ratings: [
      { source: 'vndb', score: 8.8, vote_count: 100 }, { source: 'erogamescape', score: 90, vote_count: 50 },
      { source: 'unknown', score: 2 }, { source: 'bangumi', score: 999 }, { source: 'howlongtobeat', score: 90 }
    ],
    refs: [{ source: 'vndb', external_id: 'v4' }, { source: 'bangumi', external_id: '13' }, { source: 'web', external_id: 'javascript:alert(1)' }],
    links: [{ source: 'vndb', url: 'https://vndb.org/v4' }, { source: 'dlsite', url: 'https://www.dlsite.com/home/work/=/product_id/RJ123456.html' },
      { source: 'web', url: 'https://vndb.org.evil.example/steal' }, { source: 'web', url: 'https://user:secret@vndb.org/v4' }],
    cover: { url: 'https://images.example/cover.webp' }, banner: { url: 'javascript:alert(1)' },
    claim: { site: 'kungal', state: 'live', site_work_id: '199' },
    tags: [{ display_name: '泣きゲー', tier: 'core', spoiler: 'none' }, { display_name: 'Spoiler', spoiler: 'major' }],
    characters: [{ id: '100', display_name: '古河 渚', localized: { 'zh-Hans': { value: '古河渚' } }, roster_role: 'main',
      image: { url: 'https://images.example/nagisa.webp' }, voices: [{ display_name: '中原麻衣' }] }]
  }) })
  const result = await client.getWork('86')
  assert.equal(result.originalTitle, 'クラナド')
  assert.deepEqual(result.aliases, ['Clannad'])
  assert.equal(result.description, '中文简介')
  assert.deepEqual(result.developers, ['Key'])
  assert.deepEqual(result.ratings, [{ source: 'vndb', score: 8.8, scale: 10, votes: 100 }, { source: 'erogamescape', score: 90, scale: 100, votes: 50 }])
  assert.equal(result.references[0].url, 'https://vndb.org/v4')
  assert.equal(result.references[1].url, 'https://bgm.tv/subject/13')
  assert.equal(result.references[2].url, undefined)
  assert.equal(result.references[3].url, 'https://www.dlsite.com/home/work/=/product_id/RJ123456.html')
  assert.equal(result.references.length, 4)
  assert.equal(result.webUrl, 'https://www.kungal.com/galgame/199')
  assert.equal(result.bannerUrl, null)
  assert.deepEqual(result.tags, ['泣きゲー'])
  assert.equal(result.characters[0].name, '古河渚')
  assert.deepEqual(result.characters[0].voiceActors, ['中原麻衣'])
})

test('detail keeps upstream elected artwork regardless of votes and shape without extra requests', async () => {
  let calls = 0
  const elected = { id: '1', url: 'https://images.example/default.webp', width: 900, height: 600, sexual: 'safe', vote_count: 0 }
  const alternative = { id: '2', url: 'https://images.example/portrait.webp', width: 500, height: 700, vote_count: 99, sexual: 'safe' }
  const client = new NextMoeClient(options, { fetch: async input => {
    calls++
    const url = new URL(String(input))
    assert.equal(url.pathname, '/v2/catalog/works/86')
    assert.ok(url.searchParams.get('include')?.split(',').includes('covers'))
    return json({ ...work, cover: elected, covers: [alternative, elected] })
  } })
  const detail = await client.getWork('86')
  assert.equal(detail.coverUrl, elected.url)
  assert.deepEqual(detail.covers?.map(cover => [cover.id, cover.votes]), [['2', 99], ['1', 0]])
  assert.equal(calls, 1)
})

test('unavailable defaults fall back to the first permitted HTTPS artwork regardless of votes', async () => {
  const first = { id: '1', url: 'https://images.example/first.webp', vote_count: 0 }
  const highVote = { id: '2', url: 'https://images.example/second.webp', vote_count: 99 }
  for (const cover of [undefined, { url: 'http://images.example/default.webp' }, { url: 'https://user:secret@images.example/default.webp' }, { url: 'https://images.example/adult.webp', sexual: 'explicit' }]) {
    const client = new NextMoeClient({ ...options, includeNsfw: false }, { fetch: async () => json({
      ...work, cover, covers: [{ id: '3', url: 'javascript:alert(1)' }, first, highVote]
    }) })
    assert.equal((await client.getWork('86')).coverUrl, first.url)
  }
})

test('cover choices expose only bounded safe IDs and HTTPS images with normalized optional information', async () => {
  const first = { id: '1', url: 'https://images.example/first.webp', width: 500, height: 700, vote_count: 0, sexual: 'safe' }
  const second = { id: '2', url: 'https://images.example/second.webp', width: 0, height: '700', vote_count: '9', sexual: 'unknown' }
  const client = new NextMoeClient(options, { fetch: async () => json({ ...work, covers: [
    first, second, { ...first, id: '3' }, { ...second, url: 'https://images.example/duplicate-id.webp' },
    { ...first, id: '../other', url: 'https://images.example/traversal.webp' },
    { ...first, id: ' 4', url: 'https://images.example/spaces.webp' },
    { ...first, id: '123456789012345678901', url: 'https://images.example/oversized-id.webp' },
    { ...first, id: '5', url: 'http://images.example/insecure.webp' },
    { ...first, id: '6', url: 'https://name:secret@images.example/private.webp' },
    { ...first, id: '7', url: 'https://images.example/' + 'x'.repeat(8192) },
    { id: '8', url: 'https://images.example/invalid-metrics.webp', width: Number.MAX_SAFE_INTEGER + 1, height: -1, vote_count: 1.5, sexual: null },
    { id: '9', url: 'https://images.example/suggestive.webp', width: 600, height: 500, vote_count: 2, sexual: 'suggestive' }
  ] }) })
  assert.deepEqual((await client.getWork('86')).covers, [
    { id: '1', url: first.url, width: 500, height: 700, votes: 0, sexual: 'safe' },
    { id: '2', url: second.url, width: null, height: null, votes: null, sexual: null },
    { id: '8', url: 'https://images.example/invalid-metrics.webp', width: null, height: null, votes: null, sexual: null },
    { id: '9', url: 'https://images.example/suggestive.webp', width: 600, height: 500, votes: 2, sexual: 'suggestive' }
  ])
})

test('adult settings filter the default and the entire personal choice pool', async () => {
  const explicit = { id: '1', url: 'https://images.example/adult.webp', sexual: 'explicit', vote_count: 99 }
  const safe = { id: '2', url: 'https://images.example/safe.webp', sexual: 'safe', vote_count: 0 }
  for (const includeNsfw of [false, true]) {
    const client = new NextMoeClient({ ...options, includeNsfw }, { fetch: async () => json({ ...work, cover: explicit, covers: [explicit, safe] }) })
    const detail = await client.getWork('86')
    assert.equal(detail.coverUrl, includeNsfw ? explicit.url : safe.url)
    assert.deepEqual(detail.covers?.map(cover => cover.id), includeNsfw ? ['1', '2'] : ['2'])
    const noFallback = new NextMoeClient({ ...options, includeNsfw }, { fetch: async () => json({ ...work, cover: explicit, covers: [explicit] }) })
    assert.equal((await noFallback.getWork('86')).coverUrl, includeNsfw ? explicit.url : null)
  }
  const malformed = new NextMoeClient({ ...options, includeNsfw: false }, { fetch: async () => json({ ...work, cover: { ...explicit, sexual: ' explicit ' }, covers: [{ ...explicit, sexual: ' explicit ' }, safe] }) })
  const detail = await malformed.getWork('86')
  assert.equal(detail.coverUrl, safe.url)
  assert.deepEqual(detail.covers?.map(cover => cover.id), ['2'])
})

test('malformed or oversized personal cover lists fail as invalid responses', async () => {
  for (const covers of [null, {}, Array.from({ length: 501 }, (_, id) => ({ id: String(id), url: `https://images.example/${id}.webp` }))]) {
    const client = new NextMoeClient(options, { fetch: async () => json({ ...work, covers }) })
    await assert.rejects(client.getWork('86'), errorCode('INVALID_RESPONSE'))
  }
})

test('collection search and reference reads keep upstream defaults and omit unavailable choice pools', async () => {
  const elected = { url: 'https://images.example/collection.webp' }
  const alternative = { id: '1', url: 'https://images.example/alternative.webp', vote_count: 99 }
  const calls: string[] = []
  const client = new NextMoeClient(options, { fetch: async input => {
    const url = new URL(String(input)); calls.push(url.pathname)
    if (url.pathname.endsWith('/catalog/search')) return json({ object: 'list', items: [hit('86')] })
    if (url.pathname.endsWith('/catalog/works')) return json({ object: 'list', items: [{ ...work, cover: elected, refs: [{ source: 'vndb', external_id: 'v4' }] }] })
    return json({ ...work, cover: elected, covers: [alternative] })
  } })
  const search = (await client.searchWorks('CLANNAD')).items[0]
  assert.equal(search.coverUrl, elected.url)
  assert.equal(search.covers, undefined)
  assert.equal((await client.resolveWorksByRefs(['vndb:v4']))[0].coverUrl, elected.url)
  const detail = await client.getWork('86')
  assert.equal(detail.coverUrl, elected.url)
  assert.equal(detail.covers?.[0].id, '1')
  assert.deepEqual(calls, ['/v2/catalog/search', '/v2/catalog/works', '/v2/catalog/works', '/v2/catalog/works/86'])
})

test('collection search and reference mapping hide explicit defaults when adult content is disabled', async () => {
  const client = new NextMoeClient({ ...options, includeNsfw: false }, { fetch: async input => {
    const url = new URL(String(input))
    assert.equal(url.searchParams.get('nsfw'), 'false')
    if (url.pathname.endsWith('/catalog/search')) return json({ object: 'list', items: [hit('86')] })
    return json({ object: 'list', items: [{ ...work, cover: { url: 'https://images.example/adult.webp', sexual: 'explicit' }, refs: [{ source: 'vndb', external_id: 'v4' }] }] })
  } })
  assert.equal((await client.searchWorks('CLANNAD')).items[0].coverUrl, null)
  assert.equal((await client.resolveWorksByRefs(['vndb:v4']))[0].coverUrl, null)
})
test('keeps elected collection romanization and full WorkTitle title/latin aliases', async () => {
  const calls: URL[] = []
  // WorkTitle uses title and latin; value belongs to LocalizedText, not titles[].
  const titleRows = [
    { lang: 'ja', title: 'クラナド', latin: 'CLANNAD', title_kind: 'official', is_machine: false },
    { lang: 'zh-Hans', title: '克兰纳德', latin: null, title_kind: 'official', is_machine: false },
    { lang: 'zh-Hant', title: '克蘭納德', latin: 'Ke Lan Na De', title_kind: 'official', is_machine: false },
    { lang: 'en', title: 'CLANNAD', latin: null, title_kind: 'alias', is_machine: false }
  ]
  const client = new NextMoeClient(options, { fetch: async input => {
    const url = new URL(String(input)); calls.push(url)
    if (url.pathname.endsWith('/catalog/search')) return json({ object: 'list', items: [hit('86')] })
    if (url.pathname.endsWith('/catalog/works')) {
      // The real collection contract omits titles[] even with include=titles.
      assert.ok(url.searchParams.get('include')?.split(',').includes('titles'))
      return json({ object: 'list', items: [{ ...work, latin: 'CLANNAD' }], missing: [] })
    }
    assert.ok(url.searchParams.get('include')?.split(',').includes('titles'))
    return json({ ...work, latin: 'CLANNAD', titles: titleRows })
  } })
  const search = await client.searchWorks('CLANNAD')
  assert.deepEqual(search.items[0].aliases, ['CLANNAD'])
  assert.deepEqual(search.items[0].titleEvidence, [{ text: 'CLANNAD', kind: 'latin', isMachine: false, language: null }])
  const detail = await client.getWork('86')
  assert.deepEqual(detail.aliases, ['CLANNAD', '克蘭納德', 'Ke Lan Na De'])
  assert.ok(detail.titleEvidence?.some(item => item.text === 'クラナド' && item.kind === 'official' && !item.isMachine && item.language === 'ja'))
  assert.ok(detail.titleEvidence?.some(item => item.text === 'Ke Lan Na De' && item.kind === 'official' && !item.isMachine && item.language === 'zh-Hant'))
  assert.equal(detail.title, '克兰纳德')
  assert.equal(detail.originalTitle, 'クラナド')
  assert.equal(calls.length, 3)
})

test('preserves official, abbreviation and machine title provenance without trusting missing fields', async () => {
  const client = new NextMoeClient(options, { fetch: async () => json({
    ...work,
    titles: [
      { lang: 'ja', title: '公式作品名', latin: 'Official Title', title_kind: 'official', is_machine: false },
      { lang: 'en', title: 'OT', latin: null, title_kind: 'abbreviation', is_machine: false },
      { lang: 'zh-Hans', title: '机器译名', latin: null, title_kind: 'alias', is_machine: true },
      { lang: 'en', title: 'Missing flag', latin: null, title_kind: 'official' },
      { lang: '', title: 'Unknown kind', latin: null, is_machine: false },
      { lang: 'en', title: 'Shared text', latin: null, title_kind: 'official', is_machine: false },
      { lang: 'zh-Hans', title: 'Shared text', latin: null, title_kind: 'official', is_machine: true },
      { lang: 'en', title: 'Shared text', latin: null, title_kind: 'abbreviation', is_machine: false },
      { lang: 'en', title: '   ', latin: null, title_kind: 'official', is_machine: false },
      { lang: 'en', title: 'x'.repeat(500), latin: null, title_kind: 'official', is_machine: false }
    ]
  }) })
  const result = await client.getWork('86')
  assert.deepEqual(result.titleEvidence, [
    { text: '公式作品名', kind: 'official', isMachine: false, language: 'ja' },
    { text: 'Official Title', kind: 'official', isMachine: false, language: 'ja' },
    { text: 'OT', kind: 'abbreviation', isMachine: false, language: 'en' },
    { text: '机器译名', kind: 'alias', isMachine: true, language: 'zh-Hans' },
    { text: 'Missing flag', kind: 'official', isMachine: true, language: 'en' },
    { text: 'Unknown kind', kind: 'unknown', isMachine: false, language: null },
    { text: 'Shared text', kind: 'official', isMachine: true, language: null },
    { text: 'Shared text', kind: 'abbreviation', isMachine: false, language: 'en' }
  ])
  assert.ok(result.aliases.includes('OT'))
  assert.ok(result.aliases.includes('机器译名'))
})

test('bounds optional title evidence from an oversized upstream title array', async () => {
  const titles = Array.from({ length: 600 }, (_, index) => ({ lang: 'en', title: `Title ${index}`, latin: null, title_kind: 'alias', is_machine: false }))
  const client = new NextMoeClient(options, { fetch: async () => json({ ...work, titles }) })
  const result = await client.getWork('86')
  assert.equal(result.titleEvidence?.length, 499)
})

test('reads chunked JSON with UTF-8 characters split across transport chunks', async () => {
  const bytes = new TextEncoder().encode(JSON.stringify(work))
  const stream = new ReadableStream<Uint8Array>({ start(controller) {
    for (let index = 0; index < bytes.length; index++) controller.enqueue(bytes.slice(index, index + 1))
    controller.close()
  } })
  const client = new NextMoeClient(options, { fetch: async () => new Response(stream, { headers: { 'content-type': 'application/json' } }) })
  assert.equal((await client.getWork('86')).title, '克兰纳德')
})

test('invalid or truncated JSON and unexpected envelopes fail explicitly', async () => {
  for (const body of ['{"object":"work",', '<html>bad gateway</html>', 'null', '{"object":"list","items":null}']) {
    const client = new NextMoeClient(options, { fetch: async () => new Response(body, { headers: { 'content-type': 'application/json' } }) })
    await assert.rejects(client.searchWorks('a'), error => error instanceof NextMoeError && ['INVALID_JSON', 'INVALID_RESPONSE'].includes(error.code))
  }
})

test('401, 403, 400 and quota errors are not retried and never reveal server text or keys', async () => {
  for (const [status, code] of [[401, 'UNAUTHORIZED'], [403, 'FORBIDDEN'], [400, 'INVALID_REQUEST'], [429, 'QUOTA_EXCEEDED']] as const) {
    let calls = 0
    const client = new NextMoeClient(options, { fetch: async () => {
      calls++
      return json({ status, code, detail: `secret ${options.apiKey} https://private.example/` }, status)
    } })
    await assert.rejects(client.getWork('86'), error => {
      assert.ok(error instanceof NextMoeError)
      assert.equal(error.status, status)
      assert.equal(error.code, code)
      assert.equal(JSON.stringify(error).includes(options.apiKey), false)
      assert.equal(error.message.includes('private.example'), false)
      return true
    })
    assert.equal(calls, 1)
  }
  const client = new NextMoeClient(options, { fetch: async () => json({ code: 'SENSITIVESECRET', detail: 'bad' }, 401) })
  await assert.rejects(client.getWork('86'), errorCode('HTTP_ERROR'))
})

test('transient failures back off and stop after two retries', async () => {
  let calls = 0
  const delays: number[] = []
  const client = new NextMoeClient(options, {
    fetch: async () => { calls++; return json({ code: 'SERVICE_UNAVAILABLE' }, 503) },
    delay: async milliseconds => { delays.push(milliseconds) }
  })
  await assert.rejects(client.getWork('86'), errorCode('SERVICE_UNAVAILABLE'))
  assert.equal(calls, 3)
  assert.deepEqual(delays, [500, 1000])
})

test('rate-limit retry respects Retry-After and succeeds without an early retry', async () => {
  let calls = 0
  let finishedWait = false
  const client = new NextMoeClient(options, {
    fetch: async () => {
      calls++
      if (calls === 1) return json({ code: 'RATE_LIMITED' }, 429, { 'retry-after': '2' })
      assert.equal(finishedWait, true)
      return json(work)
    },
    delay: async milliseconds => { assert.equal(milliseconds, 2000); finishedWait = true }
  })
  assert.equal((await client.getWork('86')).id, '86')
  assert.equal(calls, 2)
})

test('Retry-After beyond total budget fails without shortening the requested wait', async () => {
  let calls = 0
  let delays = 0
  const client = new NextMoeClient(options, {
    fetch: async () => { calls++; return json({ code: 'RATE_LIMITED' }, 429, { 'retry-after': '120' }) },
    delay: async () => { delays++ }
  })
  await assert.rejects(client.getWork('86'), errorCode('RATE_LIMITED'))
  assert.equal(calls, 1)
  assert.equal(delays, 0)
})

test('follows an entity merge using decimal IDs and detects loops', async () => {
  const urls: string[] = []
  const client = new NextMoeClient(options, { fetch: async input => {
    urls.push(new URL(String(input)).pathname)
    return urls.length === 1 ? json({ object: 'work', code: 'ENTITY_MERGED', current_id: '123' }, 404) : json({ ...work, id: '123' })
  } })
  assert.equal((await client.getWork('86')).id, '123')
  assert.deepEqual(urls, ['/v2/catalog/works/86', '/v2/catalog/works/123'])
  const looping = new NextMoeClient(options, { fetch: async () => json({ object: 'work', code: 'ENTITY_MERGED', current_id: '86' }, 404) })
  await assert.rejects(looping.getWork('86'), errorCode('MERGE_LOOP'))
  const malicious = new NextMoeClient(options, { fetch: async () => json({ object: 'work', code: 'ENTITY_MERGED', current_id: 'https://other.example/steal' }, 404) })
  await assert.rejects(malicious.getWork('86'), errorCode('INVALID_RESPONSE'))
})

test('merge chains are bounded and IDs cannot become paths', async () => {
  let calls = 0
  const client = new NextMoeClient(options, { fetch: async () => json({ object: 'work', code: 'ENTITY_MERGED', current_id: String(++calls) }, 404) })
  await assert.rejects(client.getWork('86'), errorCode('MERGE_LIMIT'))
  assert.equal(calls, 4)
  await assert.rejects(client.getWork('../catalog/keys'), errorCode('INVALID_ID'))
  assert.equal(calls, 4)
})

test('total timeout aborts stalled headers even if a transport ignores cancellation', async () => {
  let signal: AbortSignal | undefined
  const client = new NextMoeClient(options, { timeoutMs: 20, fetch: async (_, init) => {
    signal = init?.signal as AbortSignal
    return new Promise<Response>(() => {})
  } })
  await assert.rejects(client.getWork('86'), errorCode('TIMEOUT'))
  assert.equal(signal?.aborted, true)
})

test('total timeout covers a stalled response body and cancels its stream', async () => {
  let cancelled = false
  const stream = new ReadableStream<Uint8Array>({ start(controller) { controller.enqueue(new TextEncoder().encode('{')) }, cancel() { cancelled = true } })
  const client = new NextMoeClient(options, { timeoutMs: 20, fetch: async () => new Response(stream, { headers: { 'content-type': 'application/json' } }) })
  await assert.rejects(client.getWork('86'), errorCode('TIMEOUT'))
  assert.equal(cancelled, true)
})

test('redirects and network failures produce controlled errors', async () => {
  const redirected = new NextMoeClient(options, { fetch: async () => new Response(null, { status: 302, headers: { location: 'https://other.example' } }) })
  await assert.rejects(redirected.getWork('86'), errorCode('REDIRECT_BLOCKED'))
  const failed = new NextMoeClient(options, { fetch: async () => { throw new Error(`Failed ${options.apiKey}`) } })
  const result = await failed.testConnection()
  assert.equal(result.ok, false)
  assert.equal(result.message.includes(options.apiKey), false)
})

test('connection check reads the authenticated catalog and validates the response', async () => {
  const client = new NextMoeClient(options, { fetch: async input => {
    const url = new URL(String(input))
    assert.equal(url.pathname, '/v2/catalog/works')
    assert.equal(url.searchParams.get('limit'), '1')
    return json({ object: 'list', items: [] })
  } })
  assert.equal((await client.testConnection()).ok, true)
})
