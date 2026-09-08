"""Initial source-inspected label authoring; never run to accept a ranking regression."""
import hashlib
import json
import pathlib
import zipfile

root = pathlib.Path(__file__).parent
repos = [
    ('repocontext', 'Berthapp/RepoContext', '806ba51e24aed14904b799dd01430c9f78672b47', 'v0.10.0 audit source', 'LICENSE'),
    ('p-limit', 'sindresorhus/p-limit', '923811344705a6604967540e19f7653752021438', 'v6.2.0', 'license'),
    ('p-retry', 'sindresorhus/p-retry', 'd067fea3c806351e4b1c3be90439bb0da638322c', 'v6.2.1', 'license'),
    ('p-queue', 'sindresorhus/p-queue', '6b84d8a4079fdc3f77a6eb8f2a985629990c731e', 'v8.1.0', 'license'),
]


def role(path):
    p = path.lower()
    if '/fixtures/' in p or p.startswith('fixtures/'):
        return 'fixture'
    if p.startswith(('tests/', 'test/', 'test-d/')) or p in ['test.js', 'index.test-d.ts']:
        return 'test'
    if p.startswith('docs/') or p.endswith('.md'):
        return 'documentation'
    if p.endswith(('.cs', '.ts', '.tsx', '.js', '.mjs')):
        return 'production'
    return 'configuration_or_other'


data = {
    'schema_version': 1,
    'id': 'retrieval-holdout-2026-09-07',
    'frozen_at': '2026-09-07',
    'label_method': 'Source-inspected retrieval questions authored before running this corpus. These are evidence-discovery tasks, not verified bug reports or completed coding tasks. The first six RepoContext queries are historical audit diagnostics, not unseen holdout tasks.',
    'settings': {'top': 8, 'response_budget_tokens': 2000, 'detail': 'slices', 'candidate_pool': 'All positive-scoring eligible paths from unbudgeted top=file_count; pre-score candidates are not observable.'},
    'repositories': [],
    'tasks': [],
}
archives = {}
for rid, upstream, commit, version, license_path in repos:
    path = root / 'sources' / f'{rid}.zip'
    z = zipfile.ZipFile(path)
    archives[rid] = z
    files = [{'path': n, 'role': role(n), 'sha256': hashlib.sha256(z.read(n)).hexdigest()} for n in sorted(z.namelist()) if not n.endswith('/')]
    assert license_path in z.namelist()
    data['repositories'].append({'id': rid, 'upstream': f'https://github.com/{upstream}', 'commit': commit, 'reference': version, 'archive': f'sources/{rid}.zip', 'archive_sha256': hashlib.sha256(path.read_bytes()).hexdigest(), 'scope': 'Complete git source snapshot; dependencies are not installed or executed.', 'license': 'MIT', 'license_path': license_path, 'files': files})


def task(rid, key, query, language, kind, ranges, rationale, relations=(), historical=False):
    labels = []
    for path, a, b in ranges:
        lines = archives[rid].read(path).decode('utf-8-sig').splitlines()
        assert 1 <= a <= b <= len(lines), (path, a, b, len(lines))
        labels.append({'path': path, 'start_line': a, 'end_line': b, 'sha256': hashlib.sha256(('\n'.join(lines[a - 1:b])).encode()).hexdigest()})
    data['tasks'].append({'id': f'{rid}-{key}', 'repository': rid, 'query': query, 'language': language, 'class': kind, 'historical_diagnostic': historical, 'rationale': rationale, 'required_spans': labels, 'required_relationships': [{'from': a, 'to': b, 'kind': 'import'} for a, b in relations]})


rc = 'src/RepoContext.Core/'
scan = rc + 'Scanning/FileScanner.cs'
matcher = rc + 'Scanning/GitignoreMatcher.cs'
engine = rc + 'Context/ContextEngine.cs'
receipt = rc + 'Identity/Receipt.cs'
graph = rc + 'Graph/GraphBuilder.cs'
refs = rc + 'Graph/ReferenceExtractor.cs'
session = rc + 'Context/SessionStore.cs'
cost = 'src/RepoContext.Cli/Output/ContextCostModel.cs'
for key, q in [('ignore-en', 'exclude generated files and respect gitignore'), ('ignore-de', 'Generierte Dateien ausschliessen und Gitignore beachten')]:
    task('repocontext', key, q, 'CSharp', 'fix', [(scan, 417, 434), (matcher, 23, 25)], 'Scanner loads scoped ignore files; matcher parses their rules.', [(scan, matcher)], True)
task('repocontext', 'receipts', 'avoid sending the same source spans twice using receipts', 'CSharp', 'fix', [(engine, 1324, 1342), (receipt, 63, 79)], 'Slice delivery suppresses exact seen receipts; Receipt.For binds source identity and delivered text.', [(engine, receipt)], True)
task('repocontext', 'packing', 'fix response token budget packing', 'CSharp', 'fix', [(engine, 655, 678), (cost, 51, 62)], 'Packing checks the proposed complete response through the rendering and tokenizer cost oracle.', historical=True)
task('repocontext', 'imports', 'resolve TypeScript module imports to source files', 'CSharp', 'fix', [(graph, 367, 395), (refs, 135, 142)], 'Reference extraction records module specifiers and the graph resolves those specifiers to source paths.', [(graph, refs)], True)
task('repocontext', 'session', 'persist and load agent session receipts', 'CSharp', 'explain', [(session, 53, 75), (session, 127, 143)], 'LoadState reads session state; Save accumulates exact receipts and atomically writes them.', historical=True)
task('repocontext', 'config-defaults', 'preserve default exclusions when configuration fields are missing', 'CSharp', 'fix', [(rc + 'Configuration/ConfigStore.cs', 14, 17), (rc + 'Configuration/RepoctxConfig.cs', 18, 29), (rc + 'Configuration/RepoctxConfig.cs', 46, 60)], 'The pinned source has empty property defaults versus explicit factory defaults; deserialization is the entry point.')
task('repocontext', 'hash-layout', 'make canonical hashes unambiguous for Unicode text containing separators', 'CSharp', 'explain', [(rc + 'Identity/Canonical.cs', 25, 33), (rc + 'Identity/Canonical.cs', 59, 63)], 'Hash fields are length-prefixed with their UTF-8 byte counts.')
task('repocontext', 'sensitive-prune', 'exclude sensitive files before reading their content', 'CSharp', 'locate', [(scan, 306, 317)], 'Sensitive and ordinary ignore checks precede FileInfo and content reads.')
task('repocontext', 'binary-unreadable', 'distinguish an unreadable file from binary content during scanning', 'CSharp', 'explain', [(scan, 570, 585)], 'Sniff classifies readable bytes and maps access failures to the separate Unreadable value.')
task('repocontext', 'session-merge', 'prevent concurrent agents from overwriting each other in a saved session', 'CSharp', 'fix', [(session, 101, 116), (session, 127, 143)], 'Save holds a path mutex across reading, merging and atomic replacement.')
task('repocontext', 'savings', 'do not credit a partial receipt as avoiding a full file read in usage statistics', 'CSharp', 'explain', [(rc + 'Stats/UsageMeter.cs', 25, 45)], 'The accounting credits delivered content and explicit whole-file reuse, with per-path deduplication.')

for key, q, a, b, why in [
    ('validation', 'validate the concurrency limit and allow an unlimited value', 100, 103, 'Validation admits positive integers or positive infinity.'),
    ('release', 'release a concurrency slot after a task rejects', 23, 32, 'run awaits either fulfillment or rejection before next decrements activeCount.'),
    ('microtask', 'preserve asynchronous context while queuing a function', 35, 53, 'Internal promise resolution queues work and waits a microtask before checking capacity.'),
    ('resize', 'start waiting tasks when the concurrency limit increases', 73, 85, 'The concurrency setter validates and resumes queued work in a microtask.'),
    ('counts', 'report running and waiting task counts without removing queued tasks', 61, 67, 'Getter descriptors expose activeCount and queue.size independently.'),
    ('wrapper', 'wrap a single asynchronous function with its own concurrency limit', 93, 97, 'limitFunction creates one limiter and forwards each call through it.'),
]:
    task('p-limit', key, q, 'JavaScript', 'explain', [('index.js', a, b)], why)

for key, q, ranges, why in [
    ('abort-error', 'preserve the original error when explicitly aborting retries', [('index.js', 8, 17), ('index.js', 64, 66)], 'AbortError stores its original error and the retry catch path unwraps it.'),
    ('counts', 'report attempt number and remaining retries to failure hooks', [('index.js', 21, 27), ('index.js', 72, 79)], 'The first attempt is excluded from retriesLeft before invoking the hooks.'),
    ('undefined-hooks', 'apply default callbacks when retry options are explicitly undefined', [('index.js', 30, 37)], 'Nullish assignments provide the hooks and retry count defaults.'),
    ('abort-signal', 'stop a retry operation when its abort signal fires and remove the listener', [('index.js', 39, 50)], 'The abort handler stops/rejects; cleanup unregisters the listener.'),
    ('type-errors', 'retry network errors but reject ordinary TypeError failures', [('index.js', 60, 70)], 'The catch branch filters non-errors, explicit aborts and non-network TypeErrors.'),
    ('retry-hook', 'honor an asynchronous decision about whether a failed attempt should retry', [('index.js', 72, 87)], 'The retry decision and failed-attempt hook are awaited before retry or final error cleanup.'),
]:
    task('p-retry', key, q, 'JavaScript', 'explain', ranges, why)

pq = 'source/index.ts'
priority = 'source/priority-queue.ts'
lower = 'source/lower-bound.ts'
options = 'source/options.ts'
queue = 'source/queue.ts'
for key, q, ranges, why, relations in [
    ('sorted-insert', 'insert higher priority tasks before lower priority tasks while keeping equal priorities in order', [(priority, 24, 33), (lower, 3, 19)], 'PriorityQueue uses a binary insertion point after equal-priority elements.', [(priority, lower)]),
    ('reprioritize', 'change the priority of a queued task by its identifier', [(pq, 270, 271), (priority, 36, 43)], 'The public method delegates to a remove-and-reinsert operation that preserves the task id.', [(pq, priority)]),
    ('missing-id', 'report an error when reprioritizing a task whose identifier is not queued', [(priority, 36, 39)], 'setPriority throws ReferenceError if the id cannot be found.', []),
    ('filtered-size', 'count waiting tasks with a particular priority', [(pq, 448, 450), (priority, 51, 54)], 'sizeBy uses the queue filter whose implementation selects matching priorities.', [(pq, priority)]),
    ('timeout', 'decide whether a timed out queued operation rejects or fulfills', [(pq, 299, 300), (pq, 310, 319), (options, 3, 14)], 'Timeout wrapping and catch behavior depend on timeout and throwOnTimeout options.', [(pq, options)]),
    ('cancellation', 'cancel a queued operation before it starts or while it is running', [(pq, 226, 230), (pq, 294, 305)], 'add first checks an aborted signal and then races active work against an abort listener.', []),
    ('idle-empty', 'distinguish an empty queue from completion of all running work', [(pq, 382, 388), (pq, 412, 418), (pq, 148, 151)], 'onEmpty observes waiting work only; onIdle also waits for pending jobs.', []),
    ('backpressure', 'wait until the number of waiting tasks falls below a limit', [(pq, 398, 404), (pq, 421, 432)], 'onSizeLessThan waits for a next event whose filtered size satisfies the bound.', []),
    ('pause-resume', 'pause queue execution and resume pending tasks later', [(pq, 352, 367), (pq, 157, 166)], 'Pause changes the flag checked during scheduling; start clears it and processes the queue.', []),
    ('carryover', 'count unfinished tasks against the next rate limit interval', [(pq, 194, 201), (options, 57, 62)], 'The interval counter resets to pending when carryoverConcurrencyCount is enabled.', [(pq, options)]),
    ('custom-queue', 'implement a custom queue class accepted by the concurrency controller', [(queue, 1, 8), (pq, 79, 85), (options, 34, 37)], 'The queue contract declares required operations and options select the instantiated class.', [(pq, queue), (pq, options)]),
    ('clear', 'discard waiting tasks without changing the running task count', [(pq, 373, 374), (pq, 456, 457)], 'clear replaces the backing queue while pending remains a separate counter.', []),
]:
    task('p-queue', key, q, 'TypeScript', 'explain', ranges, why, relations)

assert len(data['tasks']) == 36
payload = (json.dumps(data, indent=2, ensure_ascii=False) + '\n').encode()
(root / 'manifest.json').write_bytes(payload)
print('Frozen tasks:', len(data['tasks']), 'manifest SHA-256:', hashlib.sha256(payload).hexdigest())
