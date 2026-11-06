'use strict';
// ---------------------------------------------------------------- Terminal profissional
// Destaque automático de erros e avisos, salvar o conteúdo (texto ou HTML com cores),
// menu do botão direito (opcional) e aplicação das preferências de aparência.

// ------------------------------------------------ Reabrir as abas da última vez
function showRestoreOffer(names) {
  const box = $('restore');
  if (!box || !names || names.length === 0) return;
  $('restore-btn').textContent = names.length === 1 ? 'Reabrir a aba da última vez' : `Reabrir as ${names.length} abas da última vez`;
  const shown = names.slice(0, 6).join(' · ') + (names.length > 6 ? ` e mais ${names.length - 6}` : '');
  $('restore-names').textContent = shown;
  box.hidden = false;
}

(() => {
  const btn = $('restore-btn');
  if (!btn) return;
  btn.addEventListener('click', () => {
    $('restore').hidden = true;
    send({ type: 'restoreWorkspace' });
  });
})();

// ------------------------------------------------ Preferências vindas do C#
function applySettings(msg) {
  const before = { ...settings };
  for (const key of ['fontFamily', 'lineHeight', 'cursorStyle', 'cursorBlink', 'scrollback', 'highlight', 'copyOnSelect', 'rightClickPaste']) {
    if (msg[key] !== undefined && msg[key] !== null) settings[key] = msg[key];
  }
  if (msg.theme) settings.theme = { ...THEME, ...msg.theme };
  document.documentElement.style.setProperty('--term-bg', settings.theme.background || THEME.background);

  for (const pane of panes.values()) {
    if (!pane.term) continue;
    const o = pane.term.options;
    if (o.fontFamily !== settings.fontFamily) o.fontFamily = settings.fontFamily;
    if (o.lineHeight !== settings.lineHeight) o.lineHeight = settings.lineHeight;
    if (o.cursorStyle !== settings.cursorStyle) o.cursorStyle = settings.cursorStyle;
    if (o.cursorBlink !== settings.cursorBlink) o.cursorBlink = settings.cursorBlink;
    if (o.scrollback !== settings.scrollback) o.scrollback = settings.scrollback;
    if (msg.theme) o.theme = settings.theme;
    if (before.highlight !== settings.highlight && pane.highlighter) pane.highlighter.reset(settings.highlight);
    pane.scheduleFit();
  }
  if (msg.fontSize && msg.fontSize !== settings.fontSize) setFontSize(msg.fontSize);
  if (msg.screensaver && typeof Screensaver !== 'undefined') Screensaver.configure(msg.screensaver);
}

// ------------------------------------------------ Destaque de ERROR / WARNING / OK
// Marca as palavras na tela sem mexer nos dados do servidor (programas como vim e htop não
// são afetados: a tela alternativa deles não é analisada).
const HIGHLIGHT_RULES = [
  {
    re: /\b(?:ERROR|ERRO|ERR|FATAL|CRITICAL|CRIT|FAILED|FAILURE|FALHA|FALHOU|EMERG|ALERT|PANIC)\b|\b(?:[Ee]rror|[Ff]atal|[Cc]ritical)(?=:)|\bFailed\b|\bTraceback\b|\b[A-Z]\w*Exception\b|[Pp]ermission denied|Connection refused|No such file or directory|command not found|Segmentation fault|Out of memory/g,
    fg: '#ff7a93',
    bg: '#351b24',
  },
  {
    re: /\b(?:WARN|WARNING|AVISO|DEPRECATED)\b|\b[Ww]arning(?=:)/g,
    fg: '#f2c374',
    bg: '#342a1a',
  },
  {
    re: /\[\s*OK\s*\]|\bSUCCESS(?:FUL)?\b|\bPASSED\b|\bactive \(running\)/g,
    fg: '#a6e07a',
    bg: null,
  },
];
const HIGHLIGHT_MAX = 4000;   // decorações vivas por terminal (as mais antigas saem)
const HIGHLIGHT_BATCH = 600;  // linhas analisadas por vez (saídas enormes não travam a tela)

class Highlighter {
  constructor(pane) {
    this.pane = pane;
    this.term = pane.term;
    this.next = 0;          // próxima linha (absoluta) a analisar
    this.decorations = [];
    this.timer = 0;
    this.enabled = settings.highlight;
    this.term.onWriteParsed(() => this.schedule());
  }

  schedule() {
    if (!this.enabled || this.timer) return;
    this.timer = setTimeout(() => {
      this.timer = 0;
      this.scan();
    }, 90);
  }

  reset(enabled) {
    this.enabled = enabled;
    for (const d of this.decorations) d.dispose();
    this.decorations = [];
    const buf = this.term.buffer.active;
    this.next = enabled ? Math.max(0, buf.baseY + buf.cursorY - this.term.rows * 2) : 0;
    if (enabled) this.scan();
  }

  scan() {
    const term = this.term;
    const buf = term.buffer.active;
    if (!this.enabled || buf.type !== 'normal' || this.pane.disposed) return;
    const cursorLine = buf.baseY + buf.cursorY;
    if (this.next > cursorLine) this.next = cursorLine; // tela limpa (clear)
    // A linha do cursor ainda pode mudar: é analisada quando o cursor passar dela.
    let start = this.next;
    if (cursorLine - start > HIGHLIGHT_BATCH * 4) start = cursorLine - HIGHLIGHT_BATCH * 4;
    const end = Math.min(cursorLine, start + HIGHLIGHT_BATCH);
    for (let y = start; y < end; y++) this.scanLine(buf, y, cursorLine);
    this.next = end;
    if (end < cursorLine) this.schedule();
    if (this.decorations.length > HIGHLIGHT_MAX) {
      for (const d of this.decorations.splice(0, this.decorations.length - HIGHLIGHT_MAX + 500)) d.dispose();
    }
  }

  scanLine(buf, y, cursorLine) {
    const line = buf.getLine(y);
    if (!line) return;
    const text = line.translateToString(true);
    if (text.length < 2) return;
    for (const rule of HIGHLIGHT_RULES) {
      rule.re.lastIndex = 0;
      let m;
      while ((m = rule.re.exec(text)) !== null) {
        if (m[0].length === 0) {
          rule.re.lastIndex++;
          continue;
        }
        const marker = this.term.registerMarker(y - cursorLine);
        if (!marker) return;
        const x = cellCount(text.slice(0, m.index));
        const width = Math.max(1, cellCount(m[0]));
        const options = { marker, x, width, foregroundColor: rule.fg, layer: 'top' };
        if (rule.bg) options.backgroundColor = rule.bg;
        const deco = this.term.registerDecoration(options);
        if (deco) {
          this.decorations.push(deco);
          marker.onDispose(() => deco.dispose());
        } else {
          marker.dispose();
        }
      }
    }
  }
}

paneHooks.push((pane) => {
  pane.highlighter = new Highlighter(pane);
});

// ------------------------------------------------ Salvar o conteúdo do terminal
function terminalText(pane) {
  const buf = pane.term.buffer.active;
  const lines = [];
  let current = '';
  for (let y = 0; y < buf.length; y++) {
    const line = buf.getLine(y);
    if (!line) continue;
    const next = buf.getLine(y + 1);
    const wraps = !!next && next.isWrapped;
    current += line.translateToString(!wraps);
    if (!wraps) {
      lines.push(current);
      current = '';
    }
  }
  while (lines.length && lines[lines.length - 1].trim() === '') lines.pop();
  return lines.join('\n') + '\n';
}

function terminalHtml(pane) {
  const body = pane.serializer.serializeAsHTML({ includeGlobalBackground: true });
  const title = pane.title.replace(/[<&>]/g, (c) => ({ '<': '&lt;', '>': '&gt;', '&': '&amp;' }[c]));
  return `<!doctype html><html lang="pt-BR"><head><meta charset="utf-8"><title>${title}</title>`
    + `<style>body{margin:0;background:${settings.theme.background};}pre{margin:0;padding:16px;}</style></head>`
    + `<body>${body}</body></html>`;
}

function exportTerminal(pane, format) {
  if (!pane || !pane.term) return;
  const content = format === 'html' ? terminalHtml(pane) : terminalText(pane);
  send({ type: 'exportBuffer', id: pane.id, title: pane.title, format, content });
}

// ------------------------------------------------ Menus pequenos (exportar, botão direito)
let popupMenu = null;

function closePopupMenu() {
  if (!popupMenu) return;
  popupMenu.el.remove();
  document.removeEventListener('mousedown', popupMenu.outside, true);
  document.removeEventListener('keydown', popupMenu.key, true);
  if (popupMenu.button) popupMenu.button.classList.remove('active');
  popupMenu = null;
  syncRdpObstructionSafe();
}

// items: [{ label, hint, run, disabled }] ou 'sep'
function openPopupMenu(items, x, y, button) {
  closePopupMenu();
  const el = document.createElement('div');
  el.className = 'popup-menu';
  el.setAttribute('role', 'menu');
  for (const item of items) {
    if (item === 'sep') {
      el.appendChild(Object.assign(document.createElement('div'), { className: 'sep' }));
      continue;
    }
    const b = document.createElement('button');
    b.setAttribute('role', 'menuitem');
    b.disabled = !!item.disabled;
    const label = document.createElement('span');
    label.textContent = item.label;
    b.appendChild(label);
    if (item.hint) {
      const hint = document.createElement('span');
      hint.className = 'hint';
      hint.textContent = item.hint;
      b.appendChild(hint);
    }
    b.addEventListener('click', () => {
      closePopupMenu();
      item.run();
    });
    el.appendChild(b);
  }
  document.body.appendChild(el);
  const r = el.getBoundingClientRect();
  el.style.left = Math.max(8, Math.min(x, window.innerWidth - r.width - 8)) + 'px';
  el.style.top = Math.max(8, Math.min(y, window.innerHeight - r.height - 8)) + 'px';
  const outside = (e) => {
    if (!el.contains(e.target) && !(button && button.contains(e.target))) closePopupMenu();
  };
  const key = (e) => {
    if (e.key === 'Escape') {
      e.preventDefault();
      closePopupMenu();
    }
  };
  document.addEventListener('mousedown', outside, true);
  document.addEventListener('keydown', key, true);
  if (button) button.classList.add('active');
  popupMenu = { el, outside, key, button };
  syncRdpObstructionSafe();
  const first = el.querySelector('button:not([disabled])');
  if (first) first.focus();
}

// rdp.js observa os menus fixos; este é criado na hora, então avisa diretamente.
function syncRdpObstructionSafe() {
  if (typeof rdpObstructed === 'undefined') return;
  const now = !!popupMenu || !$('modal').hidden || !$('layout-menu').hidden || !$('dock').hidden || !$('snap').hidden;
  if (now === rdpObstructed) return;
  rdpObstructed = now;
  if ([...panes.values()].some((p) => p.kind === 'rdp')) send({ type: 'rdpObstructed', value: now });
}

function activeTerminal() {
  const pane = activeTab && activeTab.activePane;
  return pane && pane.term && !pane.kind ? pane : null;
}

function exportMenuItems(pane) {
  return [
    { label: 'Salvar como texto (.txt)', hint: 'todo o histórico deste terminal', run: () => exportTerminal(pane, 'txt'), disabled: !pane },
    { label: 'Salvar como HTML (com cores)', hint: 'abre em qualquer navegador', run: () => exportTerminal(pane, 'html'), disabled: !pane },
    'sep',
    { label: 'Copiar todo o conteúdo', run: () => send({ type: 'copy', text: terminalText(pane) }), disabled: !pane },
  ];
}

function showTerminalMenu(pane, x, y) {
  const selection = pane.term.getSelection();
  openPopupMenu([
    { label: 'Copiar', hint: 'Ctrl+Shift+C', run: () => send({ type: 'copy', text: selection }), disabled: !selection },
    { label: 'Colar', hint: 'Ctrl+V', run: () => requestPaste(pane) },
    { label: 'Selecionar tudo', run: () => pane.term.selectAll() },
    'sep',
    { label: 'Buscar...', hint: 'Ctrl+Shift+F', run: () => openSearch() },
    { label: selection ? 'Print da seleção (imagem)...' : 'Print da tela (imagem)...', hint: 'Ctrl+Shift+S', run: () => openSnap(pane) },
    { label: 'Limpar a tela', hint: 'mantém a conexão', run: () => pane.term.clear() },
    'sep',
    ...exportMenuItems(pane),
  ], x, y, null);
}

(() => {
  const button = document.querySelector('[data-action="export"]');
  if (!button) return;
  button.addEventListener('click', () => {
    if (popupMenu && popupMenu.button === button) {
      closePopupMenu();
      return;
    }
    const r = button.getBoundingClientRect();
    openPopupMenu(exportMenuItems(activeTerminal()), r.right - 280, r.bottom + 4, button);
  });
})();
