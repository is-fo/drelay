package networking.web;

/**
 * The dashboard page: one HTML string, no build step, no CDN, no framework.
 *
 * <p>Kept as a Java constant on purpose. A separate resource file would have to be found relative to
 * the working directory at runtime, and the relay is started from a launcher script, from an IDE and
 * from a plain {@code java -cp target\classes} equally often - the page must not be the thing that
 * breaks when it is started from the wrong directory. It also means the page cannot drift from the API
 * it consumes without a compile-visible edit.
 *
 * <p>The layout is chosen for a second monitor next to the game: the HP panel and the nexus switch are
 * in the top-left where a glance lands, the escape conversation is next to them, and the filterable
 * packet stream takes the rest. Nothing auto-scrolls away from the newest event, and pausing the
 * stream never stops the relay from recording.
 */
final class DashboardPage {

    private DashboardPage() {
    }

    static final String HTML = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>drelay — observability</title>
<style>
  :root {
    --bg: #0d1117; --panel: #161b22; --panel2: #1c2129; --line: #2d333b;
    --fg: #e6edf3; --dim: #8b949e; --accent: #58a6ff; --good: #3fb950; --warn: #d29922;
    --bad: #f85149; --hp: #f85149; --ohter: #30363d;
  }
  * { box-sizing: border-box; }
  body { margin: 0; background: var(--bg); color: var(--fg);
         font: 13px/1.45 ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; }
  header { display: flex; align-items: center; gap: 14px; padding: 10px 14px;
           background: var(--panel); border-bottom: 1px solid var(--line); position: sticky; top: 0; z-index: 5; }
  header h1 { font-size: 14px; margin: 0; font-weight: 600; letter-spacing: .04em; }
  header .spacer { flex: 1; }
  .pill { padding: 2px 8px; border-radius: 10px; border: 1px solid var(--line); background: var(--panel2);
          color: var(--dim); font-size: 11px; }
  .pill.ok { color: var(--good); border-color: #1f6f3f; }
  .pill.warn { color: var(--warn); border-color: #7a5a12; }
  .pill.bad { color: var(--bad); border-color: #7d2b28; }
  main { display: grid; grid-template-columns: 340px 1fr; gap: 12px; padding: 12px; align-items: start; }
  @media (max-width: 900px) { main { grid-template-columns: 1fr; } }
  .panel { background: var(--panel); border: 1px solid var(--line); border-radius: 6px; margin-bottom: 12px; }
  .panel > h2 { margin: 0; padding: 8px 10px; font-size: 12px; font-weight: 600; color: var(--dim);
                border-bottom: 1px solid var(--line); text-transform: uppercase; letter-spacing: .06em; }
  .panel .body { padding: 10px; }
  .hp { font-size: 30px; font-weight: 700; letter-spacing: -.02em; }
  .hp small { font-size: 14px; color: var(--dim); font-weight: 400; }
  .bar { height: 12px; background: var(--ohter); border-radius: 3px; overflow: hidden; margin: 8px 0 4px; }
  .bar > span { display: block; height: 100%; background: var(--hp); transition: width .15s linear; }
  .kv { display: grid; grid-template-columns: 92px 1fr; gap: 2px 8px; margin-top: 8px; }
  .kv dt { color: var(--dim); }
  .kv dd { margin: 0; word-break: break-word; }
  table { width: 100%; border-collapse: collapse; }
  th, td { text-align: left; padding: 3px 6px; border-bottom: 1px solid var(--line); vertical-align: top; }
  th { color: var(--dim); font-weight: 500; font-size: 11px; text-transform: uppercase; }
  .stream { max-height: 66vh; overflow: auto; }
  .ev { cursor: default; }
  .ev:hover { background: #1a2029; }
  .ev td { font-size: 12px; }
  .kind { padding: 0 5px; border-radius: 3px; font-size: 10px; text-transform: uppercase; }
  .k-packet { background: #1f2d3d; color: #79c0ff; }
  .k-health { background: #3d1f22; color: #ff9492; }
  .k-nexus  { background: #3d331f; color: #e3b341; }
  .k-inject { background: #26331f; color: #7ee787; }
  .k-world  { background: #2b2440; color: #d2a8ff; }
  .k-session{ background: #1c2129; color: #8b949e; }
  .k-error  { background: #4d1f1c; color: #ff7b72; }
  .k-note   { background: #1c2129; color: #8b949e; }
  .dir-c2s { color: #7ee787; }
  .dir-s2c { color: #79c0ff; }
  .hex { color: var(--dim); word-break: break-all; font-size: 11px; }
  .hex.open { color: #a5b1bd; }
  .note { color: #c9d1d9; }
  .data { color: var(--dim); font-size: 11px; }
  button, input, select { background: var(--panel2); color: var(--fg); border: 1px solid var(--line);
                          border-radius: 4px; padding: 4px 8px; font: inherit; }
  button { cursor: pointer; }
  button:hover { border-color: var(--accent); }
  button.primary { background: #1f6feb; border-color: #1f6feb; }
  button.danger { background: #8b1f1c; border-color: #8b1f1c; }
  button.small { padding: 2px 6px; font-size: 11px; }
  .row { display: flex; gap: 6px; align-items: center; flex-wrap: wrap; }
  .row + .row { margin-top: 6px; }
  label.chk { display: flex; gap: 6px; align-items: center; cursor: pointer; }
  .filter { border: 1px solid var(--line); border-radius: 4px; padding: 6px; margin-bottom: 6px; background: var(--panel2); }
  .filter .name { font-weight: 600; }
  .filter .spec { color: var(--dim); font-size: 11px; word-break: break-word; }
  .grow { flex: 1; }
  .muted { color: var(--dim); }
  .flash { animation: flash .5s ease-out; }
  @keyframes flash { from { background: #1f6feb33; } to { background: transparent; } }
  .error { color: var(--bad); }
  details summary { cursor: pointer; color: var(--dim); }
</style>
</head>
<body>
<header>
  <h1>drelay observability</h1>
  <span id="conn" class="pill">connecting…</span>
  <span id="sess" class="pill">no session</span>
  <span id="arm" class="pill">injection disarmed</span>
  <span id="nexusPill" class="pill">auto-nexus off</span>
  <span id="stripPill" class="pill">strip off</span>
  <span class="spacer"></span>
  <span id="seq" class="pill">seq 0</span>
  <button id="pause" class="small">pause</button>
  <button id="clear" class="small">clear</button>
</header>

<main>
  <div>
    <section class="panel">
      <h2>character hp</h2>
      <div class="body">
        <div class="hp" id="hp">—<small id="hpMax"></small></div>
        <div class="bar"><span id="hpBar" style="width:0%"></span></div>
        <div class="row"><span class="muted" id="hpDetail">no HealthUpdate yet</span></div>
        <dl class="kv" id="hpKv"></dl>
      </div>
    </section>

    <section class="panel">
      <h2>auto nexus</h2>
      <div class="body">
        <div class="row">
          <label class="chk"><input type="checkbox" id="nxEnabled"> enabled</label>
          <label class="chk"><input type="checkbox" id="nxDry"> dry run</label>
          <label class="chk"><input type="checkbox" id="nxEff"> hp+shield+barrier</label>
          <label class="chk"><input type="checkbox" id="nxSafe"> skip safe area</label>
        </div>
        <div class="row">
          <label>threshold %</label><input type="number" id="nxThreshold" min="0" max="100" style="width:64px">
          <label>delay ms</label><input type="number" id="nxDelay" min="0" max="600000" style="width:80px">
          <label>max/world</label><input type="number" id="nxMax" min="0" max="1000" style="width:64px">
        </div>
        <div class="row">
          <input type="number" id="nxRearm" min="0" max="100" style="width:64px" title="re-arm percent">
          <span class="muted">re-arm %</span>
          <button id="nxApply" class="primary">apply</button>
          <button id="nxReload">reload</button>
        </div>
        <dl class="kv" id="nxKv"></dl>
        <div id="nxAcks" class="data"></div>
        <div id="nxSaved" class="data muted"></div>
      </div>
    </section>

    <section class="panel">
      <h2>status effect strip</h2>
      <div class="body">
        <div class="row">
          <label class="chk"><input type="checkbox" id="stEnabled"> enabled</label>
        </div>
        <div class="row" id="stEffects"></div>
        <div class="row">
          <label>other ids</label><input type="text" id="stExtra" placeholder="e.g. 32" style="width:96px"
                 title="status-effect ordinals to remove as well, comma or space separated">
          <label>min votes</label><input type="number" id="stVotes" min="1" max="64" style="width:64px">
          <button id="stApply" class="primary">apply</button>
          <button id="stReload">reload</button>
        </div>
        <dl class="kv" id="stKv"></dl>
        <div id="stSaved" class="data muted"></div>
      </div>
    </section>

    <section class="panel">
      <h2>sessions</h2>
      <div class="body"><table id="sessTable"><tbody></tbody></table></div>
    </section>

    <section class="panel">
      <h2>log</h2>
      <div class="body" id="logInfo"></div>
    </section>
  </div>

  <div>
    <section class="panel">
      <h2>filters <button id="filtersApply" class="small primary" style="float:right;margin-top:-2px">apply</button>
                     <button id="filtersReset" class="small" style="float:right;margin-right:6px;margin-top:-2px">reset</button></h2>
      <div class="body">
        <div id="filters"></div>
        <details>
          <summary>edit as JSON</summary>
          <textarea id="filtersJson" spellcheck="false"
             style="width:100%;height:150px;margin-top:6px;background:var(--panel2);color:var(--fg);
                    border:1px solid var(--line);border-radius:4px;font:inherit"></textarea>
        </details>
      </div>
    </section>

    <section class="panel">
      <h2>packets <span class="muted" id="streamInfo"></span></h2>
      <div class="stream">
        <table>
          <thead><tr><th style="width:52px">seq</th><th style="width:78px">time</th><th style="width:74px">kind</th>
                     <th style="width:112px">session</th><th style="width:44px">dir</th><th style="width:150px">packet</th>
                     <th>detail / hex</th></tr></thead>
          <tbody id="events"></tbody>
        </table>
      </div>
    </section>
  </div>
</main>

<script>
const $ = (id) => document.getElementById(id);
let cursor = 0;
let paused = false;
let filters = [];
let state = null;
let fullHex = new WeakSet();
let rowCount = 0;
// The last settings write, as the relay reported it. Held here rather than in the polled state: the
// state says what the configuration *is*, and this says whether it will still be there tomorrow.
let lastSave = null;

const esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, c =>
  ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));

function pill(el, text, cls) {
  el.textContent = text;
  el.className = 'pill' + (cls ? ' ' + cls : '');
}

async function getJson(url) {
  const r = await fetch(url, { cache: 'no-store' });
  if (!r.ok) throw new Error(url + ' -> ' + r.status);
  return r.json();
}

async function postJson(url, obj) {
  const r = await fetch(url, { method: 'POST', headers: { 'content-type': 'application/json' },
                               body: JSON.stringify(obj) });
  return r.json();
}

async function refreshState() {
  try {
    state = await getJson('/api/state');
    pill($('conn'), 'live', 'ok');
  } catch (e) {
    pill($('conn'), 'disconnected', 'bad');
    return;
  }
  renderHeader();
  renderHealth();
  renderNexus();
  renderStrip();
  renderSessions();
  renderLog();
  renderFilters();
}

function renderHeader() {
  const nexus = state.nexus.config;
  const enabled = nexus.enabled;
  const live = enabled && !nexus.dryRun;
  pill($('nexusPill'), live ? 'AUTO-NEXUS LIVE' : (enabled ? 'auto-nexus dry run' : 'auto-nexus off'),
       live ? 'bad' : (enabled ? 'warn' : ''));
  const strip = state.strip || { config: { effects: [] }, active: false };
  const armed = (strip.config.effects || []);
  pill($('stripPill'), strip.active ? 'strip ' + armed.join(',') : 'strip off',
       strip.active ? 'warn' : '');
  const sessions = state.sessions || [];
  const open = sessions.filter(s => !s.closed);
  const primary = sessions.find(s => s.tag === state.primary) || open[open.length - 1];
  pill($('sess'), primary ? primary.tag + ' · ' + primary.phase : 'no session', primary ? 'ok' : '');
  pill($('arm'), primary && primary.injectionReady ? 'injection armed' : 'injection disarmed',
       primary && primary.injectionReady ? 'ok' : 'warn');
  $('seq').textContent = 'seq ' + state.counters.eventsTotal + ' · shown ' + rowCount;
}

function renderHealth() {
  const h = state.health;
  const primary = (state.sessions || []).find(s => s.tag === state.primary);
  if (!h || h.health == null) {
    $('hp').innerHTML = '—<small></small>';
    $('hpBar').style.width = '0%';
    $('hpDetail').textContent = primary ? 'no health reading in ' + primary.tag : 'no session';
    $('hpKv').innerHTML = '';
    return;
  }
  $('hp').innerHTML = h.health + '<small> / ' + h.maxHealth + '</small>';
  const pct = h.hpPercent == null ? 0 : Math.max(0, Math.min(100, h.hpPercent));
  $('hpBar').style.width = pct + '%';
  $('hpBar').style.background = pct <= (state.nexus.config.thresholdPercent || 0) ? 'var(--bad)' : 'var(--hp)';
  $('hpDetail').textContent = pct + '% hp · effective ' + h.effectivePercent + '% · ' + h.ageMs + ' ms old';
  $('hpKv').innerHTML =
      row('shield', h.shield) + row('barrier', h.barrier) + row('samples', h.samples) +
      row('session', primary ? primary.tag : '') + row('world', primary ? (primary.worldName || '') : '') +
      row('safe area', primary ? primary.safeArea : '') + row('casting', primary ? primary.casting : '');
}

function row(k, v) {
  return '<dt>' + esc(k) + '</dt><dd>' + esc(v === null || v === undefined ? '—' : v) + '</dd>';
}

function renderNexus() {
  const c = state.nexus.config;
  if (document.activeElement.tagName !== 'INPUT') {
    $('nxEnabled').checked = !!c.enabled;
    $('nxDry').checked = !!c.dryRun;
    $('nxEff').checked = !!c.useEffectiveHp;
    $('nxThreshold').value = c.thresholdPercent;
    $('nxDelay').value = c.minIntervalMillis;
    $('nxMax').value = c.maxPerWorld;
    $('nxSafe').checked = !!c.skipInSafeArea;
    $('nxRearm').value = c.rearmPercent;
  }
  const n = state.nexus;
  $('nxKv').innerHTML =
      row('decisions', n.decisions) + row('nexused', n.fires) + row('dry runs', n.dryRuns) +
      row('declines', n.declines) + row('last ack', n.lastAckNote);
  const acks = (n.recentAcks || []).slice(-4).reverse();
  $('nxAcks').innerHTML = acks.map(a =>
      '<div>' + esc(a.at) + ' ' + (a.success ? '<span style="color:var(--good)">accepted</span>'
                                              : '<span style="color:var(--bad)">refused</span>') +
      ' ' + esc(a.note || '') + '</div>').join('');
  $('nxSaved').innerHTML = saveNote();
}

// The strip panel is built from the relay's own list of named effects, so a name and an ordinal are
// defined in exactly one place (StatusStrip.NAMED) and the page cannot disagree with the server about
// which effect is which. Only the boxes are rebuilt; typed input is never touched here.
function renderStrip() {
  const s = state.strip;
  if (!s) return;
  const named = s.named || [];
  const box = $('stEffects');
  const wanted = named.length ? named : [{ name: 'confused', effect: 11, armed: true }];
  box.innerHTML = wanted.map((e, i) =>
    '<label class="chk"><input type="checkbox" data-effect="' + e.effect + '" id="stFx' + i + '"' +
    (e.armed ? ' checked' : '') + '> ' + esc(e.name) + ' <span class="muted">(' + e.effect +
    ')</span></label>').join('');
  const c = s.config;
  if (document.activeElement.tagName !== 'INPUT') {
    $('stEnabled').checked = !!c.enabled;
    $('stVotes').value = c.minVotes;
    const known = wanted.map(e => e.effect);
    $('stExtra').value = (c.effects || []).filter(e => !known.includes(e)).join(', ');
  }
  $('stKv').innerHTML =
      row('packets rewritten', s.packetsStripped) + row('entries removed', s.entriesRemoved) +
      row('last strip', s.lastStrippedAt) +
      row('last object', s.lastObjectId) +
      row('last size', s.lastBytesBefore == null ? null
            : s.lastBytesBefore + ' -> ' + s.lastBytesAfter + ' bytes');
  $('stSaved').innerHTML = saveNote();
}

// The last settings write, if there was one. "saved to <file>" is the whole point of the line: a
// setting that is live but not saved looks identical to one that is, until the next restart.
function saveNote() {
  if (!lastSave) return '';
  if (lastSave.saved) {
    return '<span style="color:var(--good)">saved</span> to ' + esc(lastSave.file || '');
  }
  return '<span style="color:var(--bad)">not saved</span> to ' + esc(lastSave.file || '') +
         (lastSave.error ? ': ' + esc(lastSave.error) : '');
}

async function applyStrip() {
  const effects = [];
  document.querySelectorAll('#stEffects input[type=checkbox]').forEach(cb => {
    if (cb.checked) effects.push(Number(cb.dataset.effect));
  });
  ($('stExtra').value || '').split(/[\s,]+/).forEach(part => {
    if (!part) return;
    const value = Number(part);
    if (Number.isFinite(value) && value >= 0 && !effects.includes(value)) effects.push(value);
  });
  const r = await postJson('/api/strip', {
    enabled: $('stEnabled').checked,
    effects: effects,
    min_votes: Number($('stVotes').value)
  });
  if (r) lastSave = r.persistence || null;
  await refreshState();
}

function renderSessions() {
  const body = $('sessTable').querySelector('tbody');
  const rows = (state.sessions || []).slice().reverse();
  body.innerHTML = rows.map(s => {
    const h = s.health || {};
    return '<tr><td>' + esc(s.tag) + (s.closed ? ' <span class="muted">(closed)</span>' : '') +
      '<div class="muted">' + esc(s.phase) + ' · ' + esc(s.client || '') + ' → ' + esc(s.destination || '') +
      (s.injectionReady ? ' · <span style="color:var(--good)">armed</span>' : ' · disarmed') + '</div></td>' +
      '<td>' + (h.health == null ? '—' : esc(h.health + '/' + h.maxHealth + ' (' + h.hpPercent + '%)')) +
      '<div class="muted">sh ' + esc(h.shield) + ' ba ' + esc(h.barrier) + '</div></td></tr>';
  }).join('') || '<tr><td class="muted">no sessions yet</td></tr>';
}

function renderLog() {
  const l = state.log;
  $('logInfo').innerHTML =
    '<dl class="kv">' +
    row('events', l.eventsFile) + row('nexus', l.nexusFile) + row('run', l.run) +
    row('ring', l.ringCapacity + ' (dropped ' + l.dropped + ')') +
    row('last error', l.lastError || '—') + '</dl>';
}

function renderFilters() {
  const box = $('filters');
  const active = filters.filter(f => f.enabled).map(f => f.name).join(', ') || '(none — nothing is shown)';
  // A field may arrive as an array (what this page posts) or as a string (what the JSON box accepts),
  // and the panel must render both rather than throwing on one of them.
  const spec = (v) => Array.isArray(v) ? v.join(' ') : (v == null ? '' : String(v));
  box.innerHTML = filters.map((f, i) =>
    '<div class="filter"><div class="row"><label class="chk"><input type="checkbox" data-i="' + i + '"' +
    (f.enabled ? ' checked' : '') + '></label><span class="name">' + esc(f.name) + '</span>' +
    (f.builtIn ? '<span class="pill">built-in</span>' : '') + '</div>' +
    '<div class="spec">kinds: ' + esc(spec(f.kinds)) + '<br>packets: ' +
    esc(spec(f.packets)) + '</div></div>').join('');
  box.querySelectorAll('input[type=checkbox]').forEach(cb => cb.onchange = async () => {
    filters[Number(cb.dataset.i)].enabled = cb.checked;
    await applyFilters();
  });
  $('streamInfo').textContent = 'showing: ' + active;
  if (document.activeElement !== $('filtersJson')) {
    $('filtersJson').value = JSON.stringify({ filters: filters }, null, 1);
  }
}

async function applyFilters() {
  const r = await postJson('/api/filters', { filters: filters });
  if (r && r.filters) { filters = r.filters; renderFilters(); refreshEvents(true); }
}

function eventRow(e) {
  const kindCls = 'k-' + (e.kind || 'packet');
  const dirCls = e.dir === 'C->S' ? 'dir-c2s' : 'dir-s2c';
  const detail = [];
  if (e.note) detail.push('<span class="note">' + esc(e.note) + '</span>');
  if (e.data && Object.keys(e.data).length) {
    detail.push('<span class="data">' + esc(Object.entries(e.data).map(([k, v]) =>
      k + '=' + (typeof v === 'object' ? JSON.stringify(v) : v)).join('  ')) + '</span>');
  }
  const hex = e.hex ? '<div class="hex" data-hex="' + esc(e.hex) + '">' + esc(e.hex.slice(0, 128)) +
      (e.hex.length > 128 ? '…' : '') + '</div>' : '';
  return '<tr class="ev"><td>' + e.seq + '</td><td>' + esc(e.t) + '</td>' +
    '<td><span class="kind ' + kindCls + '">' + esc(e.kind) + '</span></td>' +
    '<td>' + esc(e.sess) + '</td>' +
    '<td class="' + dirCls + '">' + esc(e.dir || '') + '</td>' +
    '<td>' + esc(e.pkt || '') + (e.id != null ? ' <span class="muted">0x' +
        Number(e.id).toString(16).toUpperCase().padStart(2, '0') + '</span>' : '') +
        (e.len != null ? ' <span class="muted">' + e.len + 'B</span>' : '') + '</td>' +
    '<td>' + detail.join('<br>') + hex + '</td></tr>';
}

function appendEvents(events) {
  const body = $('events');
  const atBottom = body.parentElement.scrollTop + body.parentElement.clientHeight >=
                   body.parentElement.scrollHeight - 40;
  const html = events.map(eventRow).join('');
  body.insertAdjacentHTML('beforeend', html);
  rowCount += events.length;
  while (body.children.length > 800) { body.removeChild(body.firstChild); }
  if (atBottom) body.parentElement.scrollTop = body.parentElement.scrollHeight;
  body.parentElement.querySelectorAll('.hex').forEach(el => {
    if (el.dataset.bound) return;
    el.dataset.bound = '1';
    el.onclick = () => {
      const open = el.classList.toggle('open');
      el.textContent = open ? el.dataset.hex : el.dataset.hex.slice(0, 128) +
        (el.dataset.hex.length > 128 ? '…' : '');
    };
  });
}

async function refreshEvents(reset) {
  if (reset) {
    cursor = 0;
    $('events').innerHTML = '';
    rowCount = 0;
  }
  try {
    const data = await getJson('/api/events?after=' + cursor + '&limit=1000');
    cursor = data.lastSeq;
    if (data.gap) {
      appendEvents([{ seq: 0, t: '', kind: 'note', sess: 'relay', pkt: '',
        note: 'the ring rotated past the cursor; older events are only in the JSONL log' }]);
    }
    appendEvents(data.events || []);
    $('seq').textContent = 'seq ' + data.lastSeq + ' · shown ' + rowCount +
      (data.hidden ? ' · hidden ' + data.hidden : '');
  } catch (e) {
    pill($('conn'), 'events error', 'bad');
  }
}

async function reloadFilters() {
  const data = await getJson('/api/filters');
  filters = data.filters;
  renderFilters();
}

$('pause').onclick = () => {
  paused = !paused;
  $('pause').textContent = paused ? 'resume' : 'pause';
  $('pause').classList.toggle('primary', paused);
};
$('clear').onclick = () => { $('events').innerHTML = ''; rowCount = 0; };
$('nxApply').onclick = async () => {
  const body = {
    enabled: $('nxEnabled').checked,
    dry_run: $('nxDry').checked,
    use_effective_hp: $('nxEff').checked,
    threshold_percent: Number($('nxThreshold').value),
    min_interval_millis: Number($('nxDelay').value),
    max_per_world: Number($('nxMax').value),
    skip_in_safe_area: $('nxSafe').checked,
    rearm_percent: Number($('nxRearm').value)
  };
  const r = await postJson('/api/nexus', body);
  if (r) lastSave = r.persistence || null;
  refreshState();
};
$('nxReload').onclick = refreshState;
$('stApply').onclick = applyStrip;
$('stReload').onclick = refreshState;
$('filtersApply').onclick = async () => {
  try {
    const parsed = JSON.parse($('filtersJson').value);
    filters = parsed.filters || parsed;
    await applyFilters();
  } catch (e) { alert('filters JSON is not valid: ' + e.message); }
};
$('filtersReset').onclick = async () => {
  const data = await getJson('/api/packets');
  const defaults = [
    { name: 'character hp', enabled: true,
      kinds: 'health nexus inject world session error',
      packets: 'HealthUpdate MapInfo MapInfoAck Update Escape EscapeCastState EscapeAck ForcedEscape SafeAreaState Reconnect Ping',
      sessions: '' },
    { name: 'nexus only', enabled: false, kinds: 'nexus inject world error', packets: '', sessions: '' },
    { name: 'everything', enabled: false,
      kinds: 'session packet inject nexus health world note error', packets: '', sessions: '' }
  ];
  filters = defaults;
  await applyFilters();
  $('filtersJson').value = JSON.stringify({ filters: defaults,
    note: 'packet names come from /api/packets; a trailing * is a prefix match',
    gameIds: (data.game || []).slice(0, 12) }, null, 1);
};

(async function boot() {
  await reloadFilters();
  await refreshState();
  await refreshEvents(true);
  setInterval(refreshState, 1000);
  setInterval(() => { if (!paused) refreshEvents(false); }, 600);
})();
</script>
</body>
</html>
""";
}
