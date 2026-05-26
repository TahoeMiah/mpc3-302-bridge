namespace Mpc3TcpBridge.Web
{
    // Static HTML served at GET /. Inlined as a verbatim string so the
    // SIMPL# Pro plugin packs it into the .cpz without extra build steps
    // (embedded resources are awkward to drive from VS 2008 DTE builds).
    //
    // Convention: single quotes in HTML/CSS/JS so we don't have to double
    // the C# verbatim-string quote chars. The CSS/JS is small enough to
    // keep inline; if it grows much we'll split into /static/app.css etc.
    internal static class Static
    {
        public const string IndexHtml = @"<!DOCTYPE html>
<html lang='en'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>MPC3 Panel</title>
<style>
  :root {
    --bg: #07080a;
    --panel: #111317;
    --panel-2: #15181d;
    --btn: #1a1d23;
    --btn-hover: #20242b;
    --border: #23272e;
    --text: #e6e8eb;
    --muted: #6b7280;
    --accent: #4aa3ff;
    --accent-glow: rgba(74, 163, 255, 0.55);
    --led-off: #2a2f37;
    --led-on: #4aa3ff;
    --ok: #34d058;
  }
  * { box-sizing: border-box; }
  html, body {
    margin: 0; padding: 0;
    background: var(--bg);
    color: var(--text);
    font-family: 'Inter', -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;
    min-height: 100vh;
    -webkit-font-smoothing: antialiased;
  }
  body {
    display: flex; flex-direction: column;
    align-items: center;
    padding: 32px 24px;
  }
  .wrap {
    width: 100%;
    max-width: 920px;
  }
  header {
    display: flex; align-items: center; justify-content: space-between;
    margin-bottom: 18px;
    padding: 0 8px;
  }
  header h1 {
    font-size: 18px; font-weight: 600; margin: 0;
    letter-spacing: 0.2px;
  }
  .status {
    display: flex; align-items: center; gap: 14px;
    font-size: 13px; color: var(--muted);
  }
  .dot {
    display: inline-block;
    width: 8px; height: 8px; border-radius: 50%;
    background: #555;
    margin-right: 6px;
    transition: background 0.2s, box-shadow 0.2s;
  }
  .dot.on {
    background: var(--ok);
    box-shadow: 0 0 8px rgba(52, 208, 88, 0.55);
  }
  .gear {
    background: transparent; border: none; color: var(--muted);
    cursor: pointer; padding: 4px;
    width: 28px; height: 28px;
    display: inline-flex; align-items: center; justify-content: center;
  }
  .gear:hover { color: var(--text); }
  .panel {
    background: var(--panel);
    border-radius: 22px;
    padding: 36px 40px 28px;
    border: 1px solid var(--border);
    box-shadow: 0 24px 60px rgba(0,0,0,0.5);
  }
  .layout {
    display: grid;
    grid-template-columns: 1fr 240px;
    gap: 40px;
    align-items: start;
  }
  .grid {
    display: grid;
    grid-template-columns: 1fr 1fr;
    grid-template-rows: repeat(5, auto);
    grid-auto-flow: column;
    gap: 14px;
  }
  .btn {
    display: flex; align-items: center;
    gap: 16px;
    background: var(--btn);
    color: var(--text);
    border: 1px solid var(--border);
    border-radius: 14px;
    padding: 18px 22px;
    font-size: 15px; font-weight: 500;
    cursor: pointer;
    transition: background 0.12s, transform 0.06s, border-color 0.12s;
    user-select: none;
    text-align: left;
    min-height: 56px;
  }
  .btn:hover { background: var(--btn-hover); border-color: #2c313a; }
  .btn:active { transform: translateY(1px); }
  /* Visual feedback when the *physical* panel button is held down,
     driven by SSE `{event:'button',pressed:true|false}` messages. Kept
     distinct from `.on` (LED state) so a held key with the LED off
     still looks pressed, and a lit LED with no key down looks lit. */
  .btn.pressing {
    background: rgba(74, 163, 255, 0.18);
    border-color: rgba(74, 163, 255, 0.7);
    box-shadow: 0 0 18px rgba(74, 163, 255, 0.35),
                inset 0 0 0 1px rgba(74, 163, 255, 0.25);
    transform: translateY(1px);
  }
  .btn .led {
    width: 9px; height: 9px; border-radius: 50%;
    background: var(--led-off);
    flex-shrink: 0;
    transition: background 0.15s, box-shadow 0.15s;
  }
  .btn.on .led {
    background: var(--led-on);
    box-shadow: 0 0 10px var(--accent-glow);
  }
  .controls {
    display: flex; flex-direction: column;
    align-items: center;
    gap: 20px;
  }
  .square-btn {
    width: 64px; height: 56px;
    background: var(--btn);
    border: 1px solid var(--border);
    border-radius: 12px;
    color: var(--text);
    display: flex; align-items: center; justify-content: center;
    cursor: pointer;
    transition: background 0.12s, color 0.12s, box-shadow 0.12s;
  }
  .square-btn:hover { background: var(--btn-hover); }
  .square-btn.on {
    color: var(--accent);
    box-shadow: 0 0 14px rgba(74, 163, 255, 0.25);
    border-color: rgba(74, 163, 255, 0.4);
  }
  .square-btn.pressing {
    background: rgba(74, 163, 255, 0.18);
    color: var(--accent);
    border-color: rgba(74, 163, 255, 0.7);
    box-shadow: 0 0 18px rgba(74, 163, 255, 0.4);
    transform: translateY(1px);
  }
  .square-btn svg { width: 20px; height: 20px; stroke: currentColor; fill: none; stroke-width: 2; }
  .dial {
    position: relative;
    width: 200px; height: 200px;
    display: flex; align-items: center; justify-content: center;
  }
  .dial svg.ring {
    position: absolute; inset: 0;
    width: 100%; height: 100%;
    transform: rotate(135deg);
  }
  .ring-bg, .ring-fg {
    fill: none;
    stroke-linecap: round;
  }
  .ring-bg { stroke: #1a1d23; stroke-width: 6; }
  .ring-fg {
    stroke: var(--accent);
    stroke-width: 6;
    filter: drop-shadow(0 0 6px var(--accent-glow));
    transition: stroke-dasharray 0.15s ease;
  }
  .ring-tick { stroke: #2a2f37; stroke-width: 1; }
  .vol-knob {
    width: 110px; height: 110px;
    border-radius: 50%;
    background: radial-gradient(circle at 50% 35%, #1d2028, #0d0f13 80%);
    border: 1px solid var(--border);
    display: flex; align-items: center; justify-content: center;
    box-shadow: inset 0 -6px 18px rgba(0,0,0,0.5), 0 4px 12px rgba(0,0,0,0.3);
  }
  .vol-text {
    display: flex; align-items: baseline;
    font-weight: 600;
    color: var(--text);
  }
  .vol-text .num { font-size: 28px; line-height: 1; }
  .vol-text .pct { font-size: 11px; color: var(--muted); margin-left: 2px; }
  .vminus, .vplus {
    position: absolute;
    width: 30px; height: 30px;
    border-radius: 50%;
    background: var(--btn);
    color: var(--muted);
    border: 1px solid var(--border);
    cursor: pointer;
    font-size: 16px; line-height: 1;
    display: flex; align-items: center; justify-content: center;
  }
  .vminus:hover, .vplus:hover { color: var(--text); background: var(--btn-hover); }
  .vminus { left: -6px; top: 50%; transform: translateY(-50%); }
  .vplus  { right: -6px; top: 50%; transform: translateY(-50%); }
  .sp-low, .sp-high {
    position: absolute; bottom: 18px;
    color: var(--muted);
    font-size: 11px;
  }
  .sp-low { left: 28px; }
  .sp-high { right: 28px; }
  footer {
    display: flex; justify-content: space-between;
    margin-top: 24px;
    font-size: 11px; letter-spacing: 1.5px;
    color: #3a3f48;
    text-transform: uppercase;
  }
  .url-hint {
    margin-top: 10px;
    font-family: 'JetBrains Mono', 'SF Mono', Consolas, monospace;
    font-size: 11px;
    color: #3a3f48;
    padding: 0 8px;
  }
  @media (max-width: 760px) {
    .layout { grid-template-columns: 1fr; gap: 28px; }
    .controls { flex-direction: row; justify-content: center; }
  }
</style>
</head>
<body>
<div class='wrap'>
  <header>
    <h1>MPC3 Panel</h1>
    <div class='status'>
      <span><span class='dot' id='dot'></span><span id='conn-text'>connecting…</span></span>
      <button class='gear' aria-label='settings' title='settings'>
        <svg width='18' height='18' viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='1.8' stroke-linecap='round' stroke-linejoin='round'>
          <circle cx='12' cy='12' r='3'/>
          <path d='M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 1 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 1 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 1 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 1 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z'/>
        </svg>
      </button>
    </div>
  </header>

  <div class='panel'>
    <div class='layout'>
      <section class='grid' id='grid'></section>
      <section class='controls'>
        <button class='square-btn power' data-name='power' title='power'>
          <svg viewBox='0 0 24 24'><path d='M12 2v10'/><path d='M18.4 6.6a9 9 0 1 1-12.8 0'/></svg>
        </button>
        <div class='dial'>
          <button class='vminus' aria-label='volume down'>−</button>
          <svg class='ring' viewBox='0 0 200 200'>
            <circle class='ring-bg' cx='100' cy='100' r='85' pathLength='100' stroke-dasharray='75 100' stroke-dashoffset='0'/>
            <circle class='ring-fg' id='ring' cx='100' cy='100' r='85' pathLength='100' stroke-dasharray='37.5 100' stroke-dashoffset='0'/>
          </svg>
          <div class='vol-knob'>
            <div class='vol-text'><span class='num' id='volnum'>50</span><span class='pct'>%</span></div>
          </div>
          <button class='vplus' aria-label='volume up'>+</button>
          <svg class='sp-low' width='12' height='12' viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2'><path d='M11 5L6 9H2v6h4l5 4z'/></svg>
          <svg class='sp-high' width='12' height='12' viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2'><path d='M11 5L6 9H2v6h4l5 4z'/><path d='M15.5 8.5a5 5 0 0 1 0 7'/><path d='M19 5a9 9 0 0 1 0 14'/></svg>
        </div>
        <button class='square-btn mute' data-name='mute' title='mute'>
          <svg viewBox='0 0 24 24'><path d='M11 5L6 9H2v6h4l5 4z'/><line x1='23' y1='9' x2='17' y2='15'/><line x1='17' y1='9' x2='23' y2='15'/></svg>
        </button>
      </section>
    </div>
    <footer>
      <span>Crestron</span>
      <span>MPC3-302</span>
    </footer>
  </div>

  <div class='url-hint' id='hint'></div>
</div>

<script>
(function(){
  var state = { leds: {}, volume: 50, muted: false };
  var $ = function(id){ return document.getElementById(id); };

  // Build the 10 programmable buttons
  var grid = $('grid');
  for (var i = 1; i <= 10; i++) {
    var n = 'btn' + (i < 10 ? '0' + i : i);
    var b = document.createElement('button');
    b.className = 'btn';
    b.setAttribute('data-name', n);
    b.innerHTML = ""<span class='led'></span><span class='num'>"" + i + ""</span>"";
    b.addEventListener('click', (function(name){
      return function(){
        var on = !state.leds[name];
        send({ cmd: 'led', name: name, on: on });
      };
    })(n));
    grid.appendChild(b);
  }

  document.querySelector('.power').addEventListener('click', function(){
    send({ cmd: 'led', name: 'power', on: !state.leds['power'] });
  });
  document.querySelector('.mute').addEventListener('click', function(){
    send({ cmd: 'mute', on: !state.muted });
  });
  document.querySelector('.vminus').addEventListener('click', function(){
    send({ cmd: 'vol', level: Math.max(0, state.volume - 5) });
  });
  document.querySelector('.vplus').addEventListener('click', function(){
    send({ cmd: 'vol', level: Math.min(100, state.volume + 5) });
  });

  function send(cmd) {
    return fetch('/api/cmd', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(cmd)
    }).catch(function(e){ console.error('send failed', e); });
  }

  function setConn(ok) {
    $('dot').classList.toggle('on', ok);
    $('conn-text').textContent = ok ? 'connected' : 'offline';
  }

  function render() {
    var els = document.querySelectorAll('[data-name]');
    for (var i = 0; i < els.length; i++) {
      var name = els[i].getAttribute('data-name');
      var on = !!state.leds[name];
      if (name === 'mute') on = state.muted;
      els[i].classList.toggle('on', on);
    }
    $('volnum').textContent = state.volume;
    var ring = $('ring');
    // 270deg arc (= 75 / 100 pathLength); fill ratio = volume/100 * 75
    var filled = (state.volume / 100) * 75;
    ring.setAttribute('stroke-dasharray', filled + ' 100');
  }

  function applyEvent(m) {
    if (!m || !m.event) return;
    switch (m.event) {
      case 'state':
        if (m.leds) state.leds = m.leds;
        if (typeof m.volume === 'number') state.volume = m.volume;
        if (typeof m.muted === 'boolean') state.muted = m.muted;
        break;
      case 'led':    state.leds[m.name] = !!m.on; break;
      case 'volume': state.volume = m.level | 0; break;
      case 'mute':   state.muted = !!m.on; break;
      case 'button': {
        // Physical press/release on the MPC3 panel. Show a transient
        // 'pressing' highlight on the matching tile. LED state is a
        // separate signal (driven by `led` events), so don't touch it.
        var el = document.querySelector(""[data-name='"" + (m.name || '') + ""']"");
        if (el) el.classList.toggle('pressing', !!m.pressed);
        return;
      }
      case 'hello':  break;
      default: return;
    }
    render();
  }

  // Initial paint with sane defaults; then fetch real state.
  render();
  fetch('/api/state').then(function(r){ return r.json(); }).then(applyEvent).catch(function(){});

  // Live updates via SSE.
  var es = null;
  function connectSse() {
    try { if (es) es.close(); } catch (_) {}
    es = new EventSource('/api/events');
    es.onopen = function(){ setConn(true); };
    es.onerror = function(){
      setConn(false);
      setTimeout(connectSse, 2000);
    };
    es.onmessage = function(ev){
      try { applyEvent(JSON.parse(ev.data)); } catch(_){}
    };
  }
  connectSse();

  // Show the URL hint below the panel.
  $('hint').textContent = 'http://' + location.host;
})();
</script>
</body>
</html>";
    }
}
