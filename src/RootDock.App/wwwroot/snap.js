'use strict';
// ---------------------------------------------------------------- Print do código (estilo CodeSnap)
// Captura o trecho selecionado no terminal (com as cores da tela) ou no editor (com o destaque de
// sintaxe do Monaco); sem seleção, o que está visível. Monta uma imagem (moldura de janela, fundo,
// sombra) numa prévia com opções. O PNG é gerado aqui; copiar, salvar e PDF ficam com o C#.

const SNAP_STORAGE = 'rootdock.snap.v1';
const SNAP_MAX_ROWS = 1500;
const SNAP_WRAP_COLUMNS = 100;
const SNAP_MAX_PIXELS = 120e6;   // limite de área do canvas (memória)
const SNAP_MAX_SIDE = 16000;     // limite de lado do canvas
const SNAP_UI_FONT = "'Segoe UI Variable Text', 'Segoe UI', system-ui, sans-serif";
const SNAP_CAMERA = '<svg viewBox="0 0 16 16"><path d="M2.5 5.2h2.1l1.1-1.7h4.6l1.1 1.7h2.1a1 1 0 0 1 1 1v6.3a1 1 0 0 1-1 1h-11a1 1 0 0 1-1-1V6.2a1 1 0 0 1 1-1z"/><circle cx="8" cy="9.1" r="2.4"/></svg>';

const SNAP_BACKGROUNDS = [
  { id: 'rootdock', name: 'RootDock', stops: ['#4f46e5', '#9333ea'] },
  { id: 'aurora', name: 'Aurora', stops: ['#7f5af0', '#2cb67d'] },
  { id: 'oceano', name: 'Oceano', stops: ['#1c92d2', '#6dd5ed'] },
  { id: 'por-do-sol', name: 'Pôr do sol', stops: ['#f857a6', '#ff5858'] },
  { id: 'ambar', name: 'Âmbar', stops: ['#f7971e', '#ffd200'] },
  { id: 'menta', name: 'Menta', stops: ['#11998e', '#38ef7d'] },
  { id: 'rosa', name: 'Rosa', stops: ['#ee9ca7', '#ffdde1'] },
  { id: 'meia-noite', name: 'Meia-noite', stops: ['#232526', '#414345'] },
  { id: 'grafite', name: 'Grafite (cor sólida)', solid: '#1b1f27' },
  { id: 'transparente', name: 'Transparente', transparent: true },
];

const SNAP_DEFAULTS = {
  frame: 'mac', background: 'rootdock', padding: 56, fontSize: 14,
  lineNumbers: true, wrap: false, dedent: true, mask: false,
  shadow: true, rounded: true, watermark: false, scale: 2,
};

let snap = null; // diálogo aberto: { pane, model, opts, title, highlights, layout, fit }

function loadSnapOptions() {
  try {
    const saved = JSON.parse(localStorage.getItem(SNAP_STORAGE) || '{}');
    return { ...SNAP_DEFAULTS, ...saved };
  } catch {
    return { ...SNAP_DEFAULTS };
  }
}

function saveSnapOptions(opts) {
  try {
    localStorage.setItem(SNAP_STORAGE, JSON.stringify(opts));
  } catch {
    // sem armazenamento: as opções valem só agora
  }
}

// ------------------------------------------------ Captura: terminal
function snapRunStyle(el) {
  const s = el.style;
  const weight = s.fontWeight;
  return {
    color: s.color || null,
    bg: s.backgroundColor || null,
    bold: weight === 'bold' || Number(weight) >= 600,
    italic: s.fontStyle === 'italic',
    underline: (s.textDecoration || s.textDecorationLine || '').includes('underline'),
    dim: !!s.opacity && Number(s.opacity) < 1,
    hidden: s.visibility === 'hidden',
  };
}

function snapTrimEnd(runs) {
  while (runs.length) {
    const last = runs[runs.length - 1];
    const trimmed = last.text.replace(/\s+$/, '');
    if (trimmed.length && !last.bg) {
      last.text = trimmed;
      break;
    }
    if (trimmed.length) break; // fundo colorido: mantém (é parte do que aparece)
    runs.pop();
  }
  return runs;
}

function snapTrimEdges(lines) {
  const empty = (l) => !l.runs.some((r) => r.text.trim() || r.bg);
  while (lines.length && empty(lines[0])) lines.shift();
  while (lines.length && empty(lines[lines.length - 1])) lines.pop();
  return lines;
}

function snapFromTerminal(pane) {
  const term = pane.term;
  const buf = term.buffer.active;
  const selected = term.hasSelection();
  const options = { includeGlobalBackground: true };
  if (selected) options.onlySelection = true;
  else options.range = { startLine: buf.viewportY, endLine: buf.viewportY + term.rows - 1, startCol: 0 };
  const html = pane.serializer.serializeAsHTML(options);
  const doc = new DOMParser().parseFromString(html, 'text/html');
  const outer = doc.querySelector('pre > div');
  const theme = term.options.theme || {};
  const lines = [];
  for (const row of outer ? outer.children : []) {
    const runs = [];
    for (const span of row.querySelectorAll('span')) {
      if (span.querySelector('span')) continue;
      const text = span.textContent.replace(/ /g, ' ');
      if (text) runs.push({ text, ...snapRunStyle(span) });
    }
    lines.push({ runs: snapTrimEnd(runs) });
  }
  return {
    kind: 'terminal',
    source: selected ? 'seleção do terminal' : 'tela visível do terminal',
    title: pane.remoteTitle || pane.title || 'Terminal',
    lines: snapTrimEdges(lines),
    background: theme.background || settings.theme.background || '#11141a',
    foreground: theme.foreground || '#d7dae0',
    fontFamily: term.options.fontFamily || settings.fontFamily,
  };
}

// ------------------------------------------------ Captura: editor (Monaco)
const snapTokenCache = new Map();

// Cor e estilo de uma classe de token do Monaco (mtk5, mtki...), lidas do tema aplicado.
function snapTokenStyle(className) {
  if (snapTokenCache.has(className)) return snapTokenCache.get(className);
  let probe = document.getElementById('snap-probe');
  if (!probe) {
    probe = document.createElement('div');
    probe.id = 'snap-probe';
    probe.className = 'monaco-editor';
    probe.style.cssText = 'position:absolute;left:-9999px;top:0;visibility:hidden;pointer-events:none';
    document.body.appendChild(probe);
  }
  const span = document.createElement('span');
  span.className = className;
  span.textContent = 'x';
  probe.appendChild(span);
  const cs = getComputedStyle(span);
  const style = {
    color: cs.color,
    bold: Number(cs.fontWeight) >= 600,
    italic: cs.fontStyle === 'italic',
    underline: (cs.textDecorationLine || '').includes('underline'),
  };
  span.remove();
  snapTokenCache.set(className, style);
  return style;
}

function snapFromEditor(pane) {
  const monaco = window.monaco;
  const ed = pane.diff ? pane.diff.getModifiedEditor() : pane.editor;
  const model = ed && ed.getModel();
  if (!monaco || !ed || !model) return null;
  const sel = ed.getSelection();
  let start;
  let end;
  let source;
  if (sel && !sel.isEmpty()) {
    start = sel.startLineNumber;
    end = sel.endLineNumber;
    if (sel.endColumn === 1 && end > start) end -= 1; // seleção que termina no começo da linha seguinte
    source = 'seleção do editor';
  } else {
    const visible = ed.getVisibleRanges()[0];
    start = visible ? visible.startLineNumber : 1;
    end = visible ? visible.endLineNumber : Math.min(model.getLineCount(), 40);
    source = 'trecho visível do editor';
  }
  const truncated = end - start + 1 > SNAP_MAX_ROWS;
  if (truncated) end = start + SNAP_MAX_ROWS - 1;
  const tabSize = model.getOptions().tabSize;
  const lines = [];
  for (let n = start; n <= end; n++) {
    const html = monaco.editor.colorizeModelLine(model, n, tabSize);
    const doc = new DOMParser().parseFromString(`<div>${html}</div>`, 'text/html');
    const runs = [];
    for (const span of doc.body.querySelectorAll('span[class]')) {
      if (span.querySelector('span')) continue;
      const text = span.textContent.replace(/ /g, ' ');
      if (text) runs.push({ text, ...snapTokenStyle(span.className) });
    }
    lines.push({ runs: snapTrimEnd(runs), number: n });
  }
  const bgEl = ed.getDomNode() && ed.getDomNode().querySelector('.monaco-editor-background');
  const bg = bgEl ? getComputedStyle(bgEl).backgroundColor : '#11141a';
  return {
    kind: 'editor',
    source,
    truncated,
    title: pane.title || 'arquivo',
    lines: snapTrimEdges(lines),
    background: bg,
    foreground: snapTokenStyle('mtk1').color || '#d7dae0',
    fontFamily: settings.fontFamily,
  };
}

// ------------------------------------------------ Linhas prontas para desenhar
function snapMaskText(text) {
  return text
    .replace(/\b(?:\d{1,3}\.){3}\d{1,3}\b/g, (m) => m.replace(/\d/g, '•'))
    .replace(/[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+/g, (m) => m.replace(/[^@.]/g, '•'));
}

// Mascara na linha inteira (um IP pode estar dividido em trechos de cores diferentes).
function snapMaskLine(line) {
  const full = line.runs.map((r) => r.text).join('');
  const masked = snapMaskText(full);
  if (masked === full) return;
  let pos = 0;
  for (const r of line.runs) {
    r.text = masked.slice(pos, pos + r.text.length);
    pos += r.text.length;
  }
}

function snapDedent(lines) {
  let min = Infinity;
  for (const l of lines) {
    const text = l.runs.map((r) => r.text).join('');
    if (!text.trim()) continue;
    min = Math.min(min, text.length - text.trimStart().length);
  }
  if (!Number.isFinite(min) || min === 0) return;
  for (const l of lines) {
    let cut = min;
    for (const r of l.runs) {
      if (cut <= 0) break;
      const lead = r.text.length - r.text.trimStart().length;
      const take = Math.min(cut, lead, r.text.length);
      r.text = r.text.slice(take);
      cut -= take;
      if (lead < r.text.length + take) break; // encontrou texto
    }
    l.runs = l.runs.filter((r) => r.text.length);
  }
}

function snapWrap(line, columns) {
  const total = line.runs.reduce((n, r) => n + r.text.length, 0);
  if (total <= columns) return [line];
  const rows = [];
  let current = { number: line.number, runs: [] };
  let used = 0;
  for (const r of line.runs) {
    let text = r.text;
    while (text.length) {
      const room = columns - used;
      const part = text.slice(0, room);
      current.runs.push({ ...r, text: part });
      used += part.length;
      text = text.slice(part.length);
      if (used >= columns && text.length) {
        rows.push(current);
        current = { number: null, continuation: true, runs: [] };
        used = 0;
      }
    }
  }
  rows.push(current);
  return rows;
}

function snapRows(model, opts) {
  const lines = model.lines.map((l, i) => ({
    number: model.kind === 'editor' ? l.number : i + 1,
    runs: l.runs.map((r) => ({ ...r })),
  }));
  if (opts.mask) lines.forEach(snapMaskLine);
  if (model.kind === 'editor' && opts.dedent) snapDedent(lines);
  return opts.wrap ? lines.flatMap((l) => snapWrap(l, SNAP_WRAP_COLUMNS)) : lines;
}

// ------------------------------------------------ Desenho
function snapFont(run, size, family) {
  return `${run.italic ? 'italic ' : ''}${run.bold ? '600 ' : ''}${size}px ${family}`;
}

function snapLayout(ctx, model, opts, rows, title) {
  const size = opts.fontSize;
  const family = model.fontFamily || 'Consolas, monospace';
  ctx.font = snapFont({}, size, family);
  const charW = ctx.measureText('0').width;
  const lineH = Math.round(size * 1.6);
  let codeW = charW * 12;
  for (const row of rows) {
    let w = 0;
    for (const r of row.runs) {
      ctx.font = snapFont(r, size, family);
      r.width = ctx.measureText(r.text).width;
      w += r.width;
    }
    row.width = w;
    codeW = Math.max(codeW, w);
  }
  const maxNumber = rows.reduce((m, r) => Math.max(m, r.number || 0), 0);
  const gutter = opts.lineNumbers ? Math.ceil(String(maxNumber).length * charW) + 22 : 0;
  const titleH = opts.frame === 'none' ? 0 : 42;
  const padX = 24;
  const padTop = opts.frame === 'none' ? 20 : 6;
  const padBottom = 20;
  ctx.font = `500 13px ${SNAP_UI_FONT}`;
  const titleW = opts.frame !== 'none' && title ? ctx.measureText(title).width : 0;
  const minCard = opts.frame === 'none' ? 120 : titleW + 200;
  const cardW = Math.ceil(Math.max(minCard, padX * 2 + gutter + codeW));
  const cardH = Math.ceil(titleH + padTop + rows.length * lineH + padBottom);
  const pad = opts.padding;
  return {
    size, family, charW, lineH, gutter, titleH, padX, padTop, padBottom, cardW, cardH, pad,
    width: cardW + pad * 2,
    height: cardH + pad * 2,
  };
}

function snapRoundRect(ctx, x, y, w, h, r) {
  ctx.beginPath();
  if (ctx.roundRect) {
    ctx.roundRect(x, y, w, h, r);
    return;
  }
  ctx.moveTo(x + r, y);
  ctx.arcTo(x + w, y, x + w, y + h, r);
  ctx.arcTo(x + w, y + h, x, y + h, r);
  ctx.arcTo(x, y + h, x, y, r);
  ctx.arcTo(x, y, x + w, y, r);
  ctx.closePath();
}

function snapDraw(canvas, model, opts, rows, layout, scale, title, highlights) {
  const L = layout;
  canvas.width = Math.max(1, Math.round(L.width * scale));
  canvas.height = Math.max(1, Math.round(L.height * scale));
  const ctx = canvas.getContext('2d');
  ctx.setTransform(scale, 0, 0, scale, 0, 0);
  ctx.clearRect(0, 0, L.width, L.height);

  const bg = SNAP_BACKGROUNDS.find((b) => b.id === opts.background) || SNAP_BACKGROUNDS[0];
  if (bg.stops) {
    const g = ctx.createLinearGradient(0, 0, L.width, L.height);
    g.addColorStop(0, bg.stops[0]);
    g.addColorStop(1, bg.stops[1]);
    ctx.fillStyle = g;
    ctx.fillRect(0, 0, L.width, L.height);
  } else if (bg.solid) {
    ctx.fillStyle = bg.solid;
    ctx.fillRect(0, 0, L.width, L.height);
  }

  const x0 = L.pad;
  const y0 = L.pad;
  const radius = opts.rounded ? 12 : 0;
  const fg = model.foreground;

  // Janela com sombra
  ctx.save();
  if (opts.shadow) {
    ctx.shadowColor = 'rgba(0, 0, 0, 0.5)';
    ctx.shadowBlur = 44;
    ctx.shadowOffsetY = 18;
  }
  snapRoundRect(ctx, x0, y0, L.cardW, L.cardH, radius);
  ctx.fillStyle = model.background;
  ctx.fill();
  ctx.restore();

  ctx.save();
  snapRoundRect(ctx, x0, y0, L.cardW, L.cardH, radius);
  ctx.clip();

  // Moldura
  if (opts.frame !== 'none') {
    const cy = y0 + L.titleH / 2;
    ctx.textBaseline = 'middle';
    ctx.font = `500 13px ${SNAP_UI_FONT}`;
    ctx.fillStyle = fg;
    if (opts.frame === 'mac') {
      [['#ff5f57', 0], ['#febc2e', 1], ['#28c840', 2]].forEach(([color, i]) => {
        ctx.beginPath();
        ctx.arc(x0 + 20 + i * 20, cy, 6, 0, Math.PI * 2);
        ctx.fillStyle = color;
        ctx.fill();
      });
      if (title) {
        ctx.globalAlpha = 0.6;
        ctx.fillStyle = fg;
        ctx.textAlign = 'center';
        ctx.fillText(title, x0 + L.cardW / 2, cy, L.cardW - 160);
        ctx.textAlign = 'left';
        ctx.globalAlpha = 1;
      }
    } else {
      if (title) {
        ctx.globalAlpha = 0.7;
        ctx.fillText(title, x0 + 16, cy, L.cardW - 150);
        ctx.globalAlpha = 1;
      }
      // minimizar, maximizar, fechar (como no Windows)
      const right = x0 + L.cardW;
      ctx.strokeStyle = fg;
      ctx.globalAlpha = 0.6;
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(right - 118, cy + 0.5);
      ctx.lineTo(right - 108, cy + 0.5);
      ctx.stroke();
      ctx.strokeRect(right - 76.5, cy - 4.5, 9, 9);
      ctx.beginPath();
      ctx.moveTo(right - 36, cy - 5);
      ctx.lineTo(right - 26, cy + 5);
      ctx.moveTo(right - 26, cy - 5);
      ctx.lineTo(right - 36, cy + 5);
      ctx.stroke();
      ctx.globalAlpha = 1;
    }
  }

  // Linhas destacadas (clique na prévia)
  const top = y0 + L.titleH + L.padTop;
  for (const index of highlights) {
    if (index >= rows.length) continue;
    const y = top + index * L.lineH;
    ctx.fillStyle = 'rgba(255, 255, 255, 0.07)';
    ctx.fillRect(x0, y, L.cardW, L.lineH);
    ctx.fillStyle = '#7aa2f7';
    ctx.fillRect(x0, y, 3, L.lineH);
  }

  // Código
  ctx.textBaseline = 'middle';
  const codeX = x0 + L.padX + L.gutter;
  rows.forEach((row, i) => {
    const y = top + i * L.lineH;
    const mid = y + L.lineH / 2 + 0.5;
    if (opts.lineNumbers && row.number) {
      ctx.font = snapFont({}, L.size, L.family);
      ctx.fillStyle = fg;
      ctx.globalAlpha = highlights.has(i) ? 0.75 : 0.3;
      ctx.textAlign = 'right';
      ctx.fillText(String(row.number), codeX - 16, mid);
      ctx.textAlign = 'left';
      ctx.globalAlpha = 1;
    }
    let x = codeX;
    for (const r of row.runs) {
      const w = r.width || 0;
      if (r.bg && r.bg !== model.background) {
        ctx.fillStyle = r.bg;
        ctx.fillRect(x, y, w, L.lineH);
      }
      if (!r.hidden) {
        ctx.font = snapFont(r, L.size, L.family);
        ctx.fillStyle = r.color || fg;
        ctx.globalAlpha = r.dim ? 0.6 : 1;
        ctx.fillText(r.text, x, mid);
        if (r.underline) ctx.fillRect(x, mid + L.size * 0.55, w, 1);
        ctx.globalAlpha = 1;
      }
      x += w;
    }
  });
  ctx.restore();

  // Contorno sutil da janela
  snapRoundRect(ctx, x0 + 0.5, y0 + 0.5, L.cardW - 1, L.cardH - 1, radius);
  ctx.strokeStyle = 'rgba(255, 255, 255, 0.09)';
  ctx.lineWidth = 1;
  ctx.stroke();

  // Assinatura
  if (opts.watermark) {
    ctx.font = `600 12px ${SNAP_UI_FONT}`;
    ctx.textAlign = 'right';
    ctx.textBaseline = 'middle';
    if (L.pad >= 28 && !bg.transparent) {
      ctx.fillStyle = 'rgba(255, 255, 255, 0.8)';
      ctx.fillText('RootDock', L.width - 16, L.height - L.pad / 2);
    } else {
      ctx.fillStyle = fg;
      ctx.globalAlpha = 0.35;
      ctx.fillText('RootDock', x0 + L.cardW - 14, y0 + L.cardH - L.padBottom / 2);
      ctx.globalAlpha = 1;
    }
    ctx.textAlign = 'left';
  }
}

// ------------------------------------------------ Diálogo
function snapEl(tag, cls, text) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text !== undefined) e.textContent = text;
  return e;
}

function snapSegmented(name, choices, onPick) {
  const group = snapEl('div', 'snap-seg');
  group.setAttribute('role', 'radiogroup');
  group.setAttribute('aria-label', name);
  for (const [value, label] of choices) {
    const b = snapEl('button', '', label);
    b.type = 'button';
    b.dataset.value = value;
    b.setAttribute('role', 'radio');
    b.addEventListener('click', () => onPick(value));
    group.appendChild(b);
  }
  return group;
}

function snapSetSegmented(group, value) {
  for (const b of group.children) {
    const on = b.dataset.value === String(value);
    b.classList.toggle('on', on);
    b.setAttribute('aria-checked', on ? 'true' : 'false');
  }
}

function snapSwitch(label, hint, key) {
  const row = snapEl('label', 'snap-switch');
  const input = snapEl('input');
  input.type = 'checkbox';
  input.dataset.key = key;
  const track = snapEl('span', 'track');
  const text = snapEl('span', 'label', label);
  row.append(input, track, text);
  if (hint) row.title = hint;
  input.addEventListener('change', () => snapSet(key, input.checked));
  return row;
}

function snapRange(label, key, min, max, step, unit) {
  const wrap = snapEl('div', 'snap-range');
  const head = snapEl('div', 'snap-range-head');
  const name = snapEl('span', '', label);
  const value = snapEl('span', 'value');
  head.append(name, value);
  const input = snapEl('input');
  input.type = 'range';
  input.min = min;
  input.max = max;
  input.step = step;
  input.dataset.key = key;
  input.setAttribute('aria-label', label);
  input.addEventListener('input', () => {
    value.textContent = input.value + unit;
    snapSet(key, Number(input.value));
  });
  wrap.append(head, input);
  wrap.update = (v) => {
    input.value = v;
    value.textContent = v + unit;
  };
  return wrap;
}

function snapSection(title, ...children) {
  const s = snapEl('section', 'snap-section');
  s.appendChild(snapEl('h4', '', title));
  s.append(...children);
  return s;
}

function buildSnapDialog() {
  const root = $('snap');
  if (root.dataset.built) return;
  root.dataset.built = '1';

  const dialog = snapEl('div', 'snap-dialog');
  dialog.setAttribute('role', 'dialog');
  dialog.setAttribute('aria-modal', 'true');
  dialog.setAttribute('aria-labelledby', 'snap-title');

  // Cabeçalho
  const head = snapEl('div', 'snap-head');
  const icon = snapEl('span', 'snap-head-icon');
  icon.innerHTML = SNAP_CAMERA;
  const titles = snapEl('div', 'snap-titles');
  const h = snapEl('div', 'snap-title', 'Print do código');
  h.id = 'snap-title';
  const sub = snapEl('div', 'snap-sub');
  titles.append(h, sub);
  const close = snapEl('button', 'snap-close', '×');
  close.type = 'button';
  close.title = 'Fechar (Esc)';
  close.setAttribute('aria-label', 'Fechar');
  close.addEventListener('click', closeSnap);
  head.append(icon, titles, snapEl('span', 'spacer'), close);

  // Prévia
  const stage = snapEl('div', 'snap-stage');
  const canvas = snapEl('canvas');
  canvas.setAttribute('aria-label', 'Prévia da imagem');
  const hint = snapEl('div', 'snap-hint', 'Clique numa linha da prévia para destacá-la');
  stage.append(canvas, hint);
  canvas.addEventListener('click', onSnapCanvasClick);

  // Opções
  const options = snapEl('div', 'snap-options');
  const frame = snapSegmented('Moldura', [['mac', 'macOS'], ['windows', 'Windows'], ['none', 'Nenhuma']], (v) => snapSet('frame', v));
  const titleInput = snapEl('input', 'snap-input');
  titleInput.type = 'text';
  titleInput.placeholder = 'Título da janela (opcional)';
  titleInput.setAttribute('aria-label', 'Título da janela');
  titleInput.addEventListener('input', () => {
    snap.title = titleInput.value;
    scheduleSnapRender();
  });
  const swatches = snapEl('div', 'snap-swatches');
  swatches.setAttribute('role', 'radiogroup');
  swatches.setAttribute('aria-label', 'Fundo');
  for (const b of SNAP_BACKGROUNDS) {
    const sw = snapEl('button', 'snap-swatch' + (b.transparent ? ' transparent' : ''));
    sw.type = 'button';
    sw.dataset.value = b.id;
    sw.title = b.name;
    sw.setAttribute('aria-label', b.name);
    sw.setAttribute('role', 'radio');
    if (b.stops) sw.style.background = `linear-gradient(135deg, ${b.stops[0]}, ${b.stops[1]})`;
    else if (b.solid) sw.style.background = b.solid;
    sw.addEventListener('click', () => snapSet('background', b.id));
    swatches.appendChild(sw);
  }
  const padding = snapRange('Espaço ao redor', 'padding', 0, 128, 8, ' px');
  const fontSize = snapRange('Tamanho da fonte', 'fontSize', 10, 24, 1, ' px');
  const scale = snapSegmented('Qualidade', [['1', '1x'], ['2', '2x'], ['3', '3x']], (v) => snapSet('scale', Number(v)));
  const reset = snapEl('button', 'snap-link', 'Restaurar o padrão');
  reset.type = 'button';
  reset.addEventListener('click', () => {
    snap.opts = { ...SNAP_DEFAULTS };
    saveSnapOptions(snap.opts);
    syncSnapControls();
    scheduleSnapRender();
  });

  const dedent = snapSwitch('Remover recuo comum', 'Tira os espaços à esquerda que todas as linhas têm', 'dedent');
  dedent.classList.add('editor-only');
  options.append(
    snapSection('Moldura', frame, titleInput),
    snapSection('Fundo', swatches, padding),
    snapSection('Código', fontSize,
      snapSwitch('Números de linha', '', 'lineNumbers'),
      snapSwitch('Quebrar linhas longas', `Quebra em ${SNAP_WRAP_COLUMNS} colunas`, 'wrap'),
      dedent,
      snapSwitch('Ocultar IPs e e-mails', 'Troca os números dos IPs e os e-mails por •••, para compartilhar com segurança', 'mask')),
    snapSection('Aparência',
      snapSwitch('Sombra', '', 'shadow'),
      snapSwitch('Cantos arredondados', '', 'rounded'),
      snapSwitch('Assinatura "RootDock"', '', 'watermark')),
    snapSection('Qualidade da imagem', scale, reset),
  );

  const body = snapEl('div', 'snap-body');
  body.append(stage, options);

  // Rodapé
  const foot = snapEl('div', 'snap-foot');
  const size = snapEl('span', 'snap-size');
  const status = snapEl('span', 'snap-status');
  status.setAttribute('role', 'status');
  const button = (label, action, cls, tip) => {
    const b = snapEl('button', 'snap-btn' + (cls ? ' ' + cls : ''), label);
    b.type = 'button';
    b.title = tip;
    b.addEventListener('click', () => snapExport(action));
    return b;
  };
  foot.append(size, status, snapEl('span', 'spacer'),
    button('Exportar PDF', 'pdf', '', 'Salvar como PDF (Ctrl+P)'),
    button('Salvar PNG', 'save', '', 'Salvar a imagem num arquivo (Ctrl+S)'),
    button('Copiar imagem', 'copy', 'primary', 'Copiar a imagem para colar no Teams, WhatsApp, e-mail... (Ctrl+C)'));

  dialog.append(head, body, foot);
  root.appendChild(dialog);
  root.addEventListener('mousedown', (e) => {
    if (e.target === root) closeSnap();
  });
  root.addEventListener('keydown', onSnapKey);
  new ResizeObserver(() => snap && scheduleSnapRender()).observe(stage);

  root.parts = { dialog, sub, canvas, stage, frame, titleInput, swatches, padding, fontSize, scale, options, size, status };
}

function syncSnapControls() {
  const p = $('snap').parts;
  const o = snap.opts;
  snapSetSegmented(p.frame, o.frame);
  snapSetSegmented(p.scale, o.scale);
  p.titleInput.value = snap.title;
  for (const sw of p.swatches.children) {
    const on = sw.dataset.value === o.background;
    sw.classList.toggle('on', on);
    sw.setAttribute('aria-checked', on ? 'true' : 'false');
  }
  p.padding.update(o.padding);
  p.fontSize.update(o.fontSize);
  for (const input of p.options.querySelectorAll('input[type=checkbox]')) input.checked = !!o[input.dataset.key];
  p.options.querySelector('.editor-only').hidden = snap.model.kind !== 'editor';
}

function snapSet(key, value) {
  if (!snap) return;
  snap.opts[key] = value;
  if (key === 'wrap' || key === 'dedent') snap.highlights.clear(); // as linhas mudam de posição
  saveSnapOptions(snap.opts);
  syncSnapControls();
  scheduleSnapRender();
}

function scheduleSnapRender() {
  if (!snap || snap.renderQueued) return;
  snap.renderQueued = true;
  requestAnimationFrame(() => {
    if (!snap) return;
    snap.renderQueued = false;
    renderSnapPreview();
  });
}

function renderSnapPreview() {
  const p = $('snap').parts;
  const ctx = p.canvas.getContext('2d');
  const rows = snapRows(snap.model, snap.opts);
  const layout = snapLayout(ctx, snap.model, snap.opts, rows, snap.title);
  const room = { w: Math.max(80, p.stage.clientWidth - 48), h: Math.max(80, p.stage.clientHeight - 64) };
  const fit = Math.min(1, room.w / layout.width, room.h / layout.height);
  const dpr = window.devicePixelRatio || 1;
  snapDraw(p.canvas, snap.model, snap.opts, rows, layout, fit * dpr, snap.title, snap.highlights);
  p.canvas.style.width = Math.round(layout.width * fit) + 'px';
  p.canvas.style.height = Math.round(layout.height * fit) + 'px';
  snap.rows = rows;
  snap.layout = layout;
  snap.fit = fit;
  const out = snapExportScale(layout, snap.opts.scale);
  p.size.textContent = `${Math.round(layout.width * out)} × ${Math.round(layout.height * out)} px`
    + (out < snap.opts.scale ? ' (reduzida: seleção grande)' : '');
}

function snapExportScale(layout, wanted) {
  const bySide = SNAP_MAX_SIDE / Math.max(layout.width, layout.height);
  const byArea = Math.sqrt(SNAP_MAX_PIXELS / (layout.width * layout.height));
  return Math.max(0.5, Math.min(wanted, bySide, byArea));
}

function onSnapCanvasClick(e) {
  if (!snap || !snap.layout) return;
  const L = snap.layout;
  const r = e.currentTarget.getBoundingClientRect();
  const y = (e.clientY - r.top) / snap.fit;
  const index = Math.floor((y - L.pad - L.titleH - L.padTop) / L.lineH);
  if (index < 0 || index >= snap.rows.length) return;
  if (snap.highlights.has(index)) snap.highlights.delete(index);
  else snap.highlights.add(index);
  scheduleSnapRender();
}

function onSnapKey(e) {
  if (!snap) return;
  const typing = e.target && e.target.classList && e.target.classList.contains('snap-input');
  if (e.key === 'Escape') {
    e.preventDefault();
    closeSnap();
  } else if (e.ctrlKey && !e.shiftKey && !e.altKey) {
    const key = e.key.toLowerCase();
    if (key === 'c' && !typing) {
      e.preventDefault();
      snapExport('copy');
    } else if (key === 's') {
      e.preventDefault();
      snapExport('save');
    } else if (key === 'p') {
      e.preventDefault();
      snapExport('pdf');
    }
  } else if (e.key === 'Tab') {
    // mantém o foco dentro do diálogo
    const items = [...$('snap').querySelectorAll('button:not([disabled]), input:not([hidden]), canvas')]
      .filter((x) => x.offsetParent !== null);
    if (!items.length) return;
    const i = items.indexOf(document.activeElement);
    const next = e.shiftKey ? (i <= 0 ? items.length - 1 : i - 1) : (i === items.length - 1 ? 0 : i + 1);
    e.preventDefault();
    items[next].focus();
  }
}

function setSnapStatus(text, kind) {
  if (!snap) return;
  const s = $('snap').parts.status;
  s.textContent = text || '';
  s.className = 'snap-status' + (kind ? ' ' + kind : '');
  if (s.reveal) {
    s.reveal.remove();
    s.reveal = null;
  }
}

function snapExport(action) {
  if (!snap || snap.busy) return;
  const rows = snapRows(snap.model, snap.opts);
  const work = document.createElement('canvas');
  const layout = snapLayout(work.getContext('2d'), snap.model, snap.opts, rows, snap.title);
  const scale = snapExportScale(layout, snap.opts.scale);
  snapDraw(work, snap.model, snap.opts, rows, layout, scale, snap.title, snap.highlights);
  let png;
  try {
    png = work.toDataURL('image/png').split(',')[1];
  } catch {
    setSnapStatus('Não foi possível gerar a imagem (seleção grande demais).', 'error');
    return;
  }
  snap.busy = true;
  setSnapStatus(action === 'copy' ? 'Copiando…' : action === 'pdf' ? 'Gerando o PDF…' : 'Salvando…');
  send({ type: 'snapExport', action, png, width: work.width, height: work.height, scale, title: snap.title || snap.model.title });
}

function openSnap(pane) {
  pane = pane || (activeTab && activeTab.activePane);
  let model = null;
  try {
    if (pane && pane.kind === 'editor' && pane.editor) model = snapFromEditor(pane);
    else if (pane && pane.term && !pane.kind) model = snapFromTerminal(pane);
  } catch (err) {
    console.error('print do código', err);
  }
  if (!model) {
    snapToast('Para o print, use um terminal ou um arquivo aberto no editor (selecione o trecho ou use o que está na tela).');
    return;
  }
  if (!model.lines.some((l) => l.runs.some((r) => r.text.trim()))) {
    snapToast('Não há texto para o print: selecione um trecho do terminal ou do editor.');
    return;
  }
  if (model.lines.length > SNAP_MAX_ROWS) {
    model.lines = model.lines.slice(0, SNAP_MAX_ROWS);
    model.truncated = true;
  }
  buildSnapDialog();
  snap = { pane, model, opts: loadSnapOptions(), title: model.title, highlights: new Set() };
  const root = $('snap');
  const count = model.lines.length;
  root.parts.sub.textContent = `${count} ${count === 1 ? 'linha' : 'linhas'} · ${model.source}`
    + (model.truncated ? ` (só as primeiras ${SNAP_MAX_ROWS})` : '');
  syncSnapControls();
  setSnapStatus('');
  root.hidden = false;
  renderSnapPreview();
  requestAnimationFrame(() => root.querySelector('.snap-btn.primary').focus());
}

function closeSnap() {
  if (!snap) return;
  const pane = snap.pane;
  snap = null;
  $('snap').hidden = true;
  if (pane && panes.has(pane.id)) requestAnimationFrame(() => pane.focus());
}

// Resposta do C# (copiado, salvo em..., erro).
function handleSnapMessage(msg) {
  if (msg.type !== 'snapResult') return false;
  if (!snap) return true;
  snap.busy = false;
  setSnapStatus(msg.message || '', msg.ok ? 'ok' : (msg.cancelled ? '' : 'error'));
  if (msg.ok && msg.path) {
    const s = $('snap').parts.status;
    const link = snapEl('button', 'snap-link', 'Mostrar na pasta');
    link.type = 'button';
    link.addEventListener('click', () => send({ type: 'snapReveal', path: msg.path }));
    s.after(link);
    s.reveal = link;
  }
  return true;
}

// Aviso rápido no canto (quando não há o que capturar).
function snapToast(text) {
  let t = document.getElementById('snap-toast');
  if (!t) {
    t = snapEl('div', 'snap-toast');
    t.id = 'snap-toast';
    t.setAttribute('role', 'status');
    document.body.appendChild(t);
  }
  t.textContent = text;
  t.classList.add('show');
  clearTimeout(t.timer);
  t.timer = setTimeout(() => t.classList.remove('show'), 4200);
}

// Botão da câmera na barra das abas (ao lado da gravação): só com terminal ou editor ativo.
const SNAP_BUTTON = document.querySelector('[data-action="snap"]');

function updateSnapButton() {
  if (!SNAP_BUTTON) return;
  const p = activeTab && activeTab.activePane;
  SNAP_BUTTON.disabled = !(p && (p.kind === 'editor' || (p.term && !p.kind)));
}

if (SNAP_BUTTON) {
  SNAP_BUTTON.addEventListener('click', () => openSnap());
  const activateWithoutSnap = activateTab;
  activateTab = function activateTabWithSnap(tab, pane) {
    activateWithoutSnap(tab, pane);
    updateSnapButton();
  };
  const setActivePaneWithoutSnap = setActivePane;
  setActivePane = function setActivePaneWithSnap(pane) {
    setActivePaneWithoutSnap(pane);
    updateSnapButton();
  };
  const removeTabWithoutSnap = removeTab;
  removeTab = function removeTabWithSnap(tab) {
    removeTabWithoutSnap(tab);
    updateSnapButton();
  };
  updateSnapButton();
}
