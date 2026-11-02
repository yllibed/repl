const byId = id => document.getElementById(id);
const query = byId('query');
let searchRequest;
let sourceRequest;
let connection;
let connected = false;
let refreshing = false;
let indexActionPending = false;

async function getJson(url, signal) {
  const response = await fetch(url, { signal, cache: 'no-store' });
  const body = await response.json();
  if (!response.ok) throw new Error(body.detail || `Request failed (${response.status}).`);
  return body;
}

function announceIndexStatus(message) {
  const status = byId('index-status');
  if (status.textContent !== message) status.textContent = message;
}

async function refreshStatus() {
  if (document.hidden || refreshing) return;
  refreshing = true;
  try {
    const { index, sessions } = await getJson('/admin/status', AbortSignal.timeout(5000));
    byId('readiness').textContent = index.ready ? 'Ready' : 'Not ready';
    byId('readiness').classList.toggle('ready', index.ready);
    byId('phase').textContent = index.phase;
    byId('documents').textContent = index.generation?.documents.toLocaleString() ?? '—';
    byId('sections').textContent = index.generation?.sections.toLocaleString() ?? '—';
    byId('generation').textContent = index.generation?.id ?? 'No published generation';
    byId('published').textContent = index.generation ? new Date(index.generation.publishedAt).toLocaleString() : '—';
    byId('activity').classList.toggle('busy', index.rebuilding);
    byId('progress').textContent = index.nextRetryAt
      ? `Automatic retry after ${new Date(index.nextRetryAt).toLocaleString()}. You can request a rebuild now.`
      : index.rebuilding
      ? `${index.documentsProcessed} documents and ${index.sectionsProcessed} sections prepared. The previous generation stays available.`
      : index.ready ? 'Published index available. Documents are checked every 60 seconds.' : 'Search becomes available after the first successful build.';
    byId('index-error').hidden = !index.lastError;
    byId('index-error').textContent = index.lastError ?? '';
    document.querySelector('[data-index-action="rebuild"]').disabled = index.rebuilding || indexActionPending;
    document.querySelector('[data-index-action="cancel"]').disabled = !index.rebuilding || indexActionPending;
    byId('session-count').textContent = `${sessions.length} connection${sessions.length === 1 ? '' : 's'}`;
    byId('refresh-status').textContent = `Updated ${new Date().toLocaleTimeString()}`;
    announceIndexStatus(`${index.ready ? 'Index ready.' : 'Index not ready.'} ${index.generation ? `Generation ${index.generation.id}.` : 'No published generation.'}${index.rebuilding ? ' Rebuild in progress.' : ''}${index.lastError ? ` Last indexing failure: ${index.lastError}` : ''}`);
  } catch (error) {
    byId('refresh-status').textContent = `Connection lost. Displayed state may be stale. ${error.message}`;
    announceIndexStatus('Connection lost. Displayed state may be stale.');
    document.querySelectorAll('[data-index-action]').forEach(button => { button.disabled = true; });
  } finally { refreshing = false; }
}

function renderRanking(name, hits) {
  const list = document.querySelector(`[data-ranking="${name}"] ol`);
  list.replaceChildren();
  if (hits.length === 0) {
    const empty = document.createElement('li');
    empty.className = 'ranking-empty';
    empty.textContent = 'No matching sections.';
    list.append(empty);
  }
  hits.forEach((hit, index) => {
    const item = document.createElement('li');
    item.className = 'result';
    const rank = document.createElement('span');
    rank.className = 'rank';
    rank.textContent = `${String(index + 1).padStart(2, '0')} / ${name === 'hybrid' ? 'RRF ' : ''}${hit.score.toPrecision(3)}`;
    const button = document.createElement('button');
    button.type = 'button';
    button.textContent = hit.heading === hit.title ? hit.title : `${hit.title} · ${hit.heading}`;
    button.addEventListener('click', () => openSource(hit));
    const excerpt = document.createElement('p');
    const characters = Array.from(hit.excerpt);
    excerpt.textContent = characters.slice(0, 180).join('') + (characters.length > 180 ? '…' : '');
    const path = document.createElement('p');
    path.className = 'source-path';
    path.textContent = `${hit.source}:${hit.startLine}`;
    item.append(rank, button, excerpt, path);
    list.append(item);
  });
}

async function search() {
  searchRequest?.abort();
  sourceRequest?.abort();
  byId('source').hidden = true;
  const text = query.value.trim();
  if (!text) { byId('search-status').textContent = 'Enter a question or a few keywords.'; query.setAttribute('aria-invalid', 'true'); query.focus(); return; }
  query.removeAttribute('aria-invalid');
  const request = new AbortController();
  searchRequest = request;
  byId('search-button').disabled = true;
  byId('rankings').setAttribute('aria-busy', 'true');
  byId('search-status').textContent = 'Comparing keyword, vector and hybrid rankings…';
  byId('search-status').classList.remove('error');
  try {
    const result = await getJson(`/admin/compare?query=${encodeURIComponent(text)}`, request.signal);
    if (request.signal.aborted) return;
    for (const name of ['keyword', 'vector', 'hybrid']) renderRanking(name, result[name]);
    byId('search-status').textContent = `“${result.query}” · generation ${result.generation}. Scores are ranking signals, not confidence percentages.`;
    const url = new URL(location.href);
    url.searchParams.set('q', text);
    history.replaceState(null, '', url);
  } catch (error) {
    if (request.signal.aborted) return;
    document.querySelectorAll('.ranking ol').forEach(list => list.replaceChildren());
    byId('search-status').textContent = error.message;
    byId('search-status').classList.add('error');
  } finally {
    if (searchRequest === request) { byId('search-button').disabled = false; byId('rankings').removeAttribute('aria-busy'); }
  }
}

async function openSource(hit) {
  sourceRequest?.abort();
  const request = new AbortController();
  sourceRequest = request;
  byId('source').hidden = false;
  byId('close-source').disabled = false;
  byId('source-title').textContent = hit.heading;
  byId('source-meta').textContent = `${hit.source}:${hit.startLine}–${hit.endLine}`;
  byId('source-text').textContent = 'Loading source…';
  try {
    const source = await getJson(`/admin/fetch?id=${encodeURIComponent(hit.id)}`, request.signal);
    if (request.signal.aborted) return;
    byId('source-text').textContent = source.text;
    byId('source-meta').textContent += ` · generation ${source.generation}${source.truncated ? ' · truncated' : ''}`;
  } catch (error) { if (!request.signal.aborted) byId('source-text').textContent = error.message; }
  byId('source-title').focus();
}

byId('search-form').addEventListener('submit', event => { event.preventDefault(); search(); });
query.addEventListener('input', () => { byId('clear-search').hidden = query.value.length === 0; byId('clear-search').disabled = false; query.removeAttribute('aria-invalid'); });
byId('clear-search').addEventListener('click', () => {
  searchRequest?.abort(); sourceRequest?.abort(); query.value = ''; query.focus();
  byId('clear-search').hidden = true; byId('source').hidden = true; byId('search-button').disabled = false;
  byId('rankings').removeAttribute('aria-busy');
  document.querySelectorAll('.ranking ol').forEach(list => list.replaceChildren());
  byId('search-status').textContent = 'Enter a question or a few keywords.';
  byId('search-status').classList.remove('error');
  const url = new URL(location.href); url.searchParams.delete('q'); history.replaceState(null, '', url);
});
byId('close-source').addEventListener('click', () => { sourceRequest?.abort(); byId('source').hidden = true; query.focus(); });

const terminal = new Terminal({ screenReaderMode: true, cursorBlink: true, fontSize: 13, fontFamily: '"Cascadia Code", "SFMono-Regular", Consolas, monospace', theme: { background: '#142936', foreground: '#e1edf3', cursor: '#64d4cb', selectionBackground: '#315969' } });
const fit = new FitAddon.FitAddon();
terminal.loadAddon(fit);
terminal.open(byId('terminal'));
const observer = new ResizeObserver(() => fit.fit());
observer.observe(byId('terminal'));

function setConnected(value) {
  connected = value;
  byId('connection-status').textContent = value ? 'Console connected' : 'Console disconnected';
  byId('connect-console').textContent = value ? 'Disconnect' : 'Connect console';
  byId('connect-console').disabled = false;
  byId('interrupt-console').disabled = !value;
}

function connect() {
  fit.fit();
  byId('connection-status').textContent = 'Connecting to console…';
  byId('connect-console').disabled = true;
  const size = new URLSearchParams({ cols: terminal.cols, rows: terminal.rows });
  connection = ReplTerminalClient.connect({
    terminal,
    url: `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws/repl?${size}`,
    hello: `@@repl:hello ${JSON.stringify({ terminal: 'xterm-256color', cols: terminal.cols, rows: terminal.rows, ansi: true, capabilities: 'Ansi,VtInput,ResizeReporting' })}`,
    onOpen: () => setConnected(true),
    onOutput: text => terminal.write(text),
    onClose: () => { setConnected(false); terminal.writeln('\r\n[Session closed.]'); }
  });
}

byId('connect-console').addEventListener('click', () => { if (connected) connection.dispose(); else { connection?.dispose(); connect(); } });
byId('interrupt-console').addEventListener('click', () => { connection?.send('\x03'); terminal.focus(); });
document.querySelectorAll('[data-copy]').forEach(button => {
  button.disabled = false;
  button.addEventListener('click', async () => {
    try { await navigator.clipboard.writeText(button.dataset.copy); byId('console-hint').textContent = `Copied ${button.dataset.copy}. Paste it at the console prompt.`; }
    catch { byId('console-hint').textContent = `Type this command at the console prompt: ${button.dataset.copy}`; }
  });
});
document.querySelectorAll('[data-index-action]').forEach(button => button.addEventListener('click', async () => {
  if (indexActionPending) return;
  indexActionPending = true;
  document.querySelectorAll('[data-index-action]').forEach(action => { action.disabled = true; });
  const command = `index ${button.dataset.indexAction}`;
  byId('index-action-status').textContent = `Running ${command}…`;
  try {
    const response = await fetch(`/admin/index/${button.dataset.indexAction}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}', signal: AbortSignal.timeout(10000) });
    const result = await response.json();
    if (!response.ok) throw new Error(result.standardError || result.detail || 'Command failed. Check index status.');
    byId('index-action-status').textContent = `${command}: ${result.result ?? result.standardOutput}`;
  } catch (error) { byId('index-action-status').textContent = error.message; }
  finally { indexActionPending = false; refreshStatus(); }
}));
window.addEventListener('pagehide', () => { connection?.dispose(); observer.disconnect(); });
document.addEventListener('visibilitychange', refreshStatus);
refreshStatus();
setInterval(refreshStatus, 2000);
connect();
query.value = new URLSearchParams(location.search).get('q') ?? '';
if (query.value) { byId('clear-search').hidden = false; byId('clear-search').disabled = false; search(); }
