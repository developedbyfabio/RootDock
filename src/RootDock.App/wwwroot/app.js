'use strict';

// Ponte com o C#. Mensagens de texto: 'o'+id+dados (saída), 'i'+id+dados (entrada),
// 'b'+id+dados (entrada binária); as demais são JSON.
const host = window.chrome && window.chrome.webview;
const ID_LEN = 32;
const send = (msg) => host && host.postMessage(typeof msg === 'string' ? msg : JSON.stringify(msg));

// Controle de fluxo: acima de HIGH caracteres pendentes no xterm, pede ao C# para pausar a leitura.
const FLOW_HIGH = 2_000_000;
const FLOW_LOW = 500_000;

const THEME = {
  background: '#11141a',
  foreground: '#d7dae0',
  cursor: '#7aa2f7',
  cursorAccent: '#11141a',
  selectionBackground: '#33466b',
  selectionInactiveBackground: '#283246',
  overviewRulerBorder: '#1b1f27',
  black: '#1b1f27', red: '#f7768e', green: '#9ece6a', yellow: '#e0af68',
  blue: '#7aa2f7', magenta: '#bb9af7', cyan: '#7dcfff', white: '#c0caf5',
  brightBlack: '#5b6170', brightRed: '#ff8fa3', brightGreen: '#b5e08a', brightYellow: '#f0c88a',
  brightBlue: '#9ab8ff', brightMagenta: '#cdb2ff', brightCyan: '#a4ddff', brightWhite: '#e6ebff',
};

const DEFAULT_FONT_SIZE = 14;
const DEFAULT_FONT_FAMILY = '"Cascadia Mono", "Cascadia Code", Consolas, "Courier New", monospace';
// Preferências do terminal (o C# manda as salvas logo ao abrir; ver mensagem "settings").
const settings = {
  fontFamily: DEFAULT_FONT_FAMILY,
  fontSize: DEFAULT_FONT_SIZE,
  lineHeight: 1.1,
  cursorStyle: 'bar',
  cursorBlink: true,
  scrollback: 20000,
  theme: THEME,
  highlight: true,       // destacar ERROR, WARNING, FAILED... (terminal-pro.js)
  copyOnSelect: true,
  rightClickPaste: true,
};

// Funções chamadas para cada terminal novo (recursos extras em outros arquivos).
const paneHooks = [];

const $ = (id) => document.getElementById(id);
const el = (tag, cls) => {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  return e;
};

const panes = new Map(); // id -> Pane
const tabs = [];         // Tab[]
let activeTab = null;

// ---------------------------------------------------------------- Pane (um terminal)
class Pane {
  constructor(info) {
    this.id = info.id;
    this.title = info.title || 'Terminal';
    this.env = info.env || 'None';
    this.state = 'connecting';
    this.tab = null;
    this.pending = 0;
    this.paused = false;

    this.prompt = null; // pergunta de login em andamento (ver beginPrompt)
    this.marks = [];   // linhas de comando marcadas (ver noteSubmit)
    this.nav = null;   // posição da navegação Ctrl+↑/↓

    this.el = el('div', 'pane env-' + this.env);
    this.el.dataset.id = this.id;
    this.buildHead();
    this.termEl = el('div', 'pane-term');
    this.el.appendChild(this.termEl);
    this.el.style.display = 'none';
    stage.appendChild(this.el); // fica no palco para sempre (ver Tab.layout)

    const term = new Terminal({
      allowProposedApi: true,
      fontFamily: settings.fontFamily,
      fontSize: settings.fontSize,
      lineHeight: settings.lineHeight,
      theme: settings.theme,
      cursorBlink: settings.cursorBlink,
      cursorStyle: settings.cursorStyle,
      scrollback: settings.scrollback,
      smoothScrollDuration: 0,
      rightClickSelectsWord: false,
      drawBoldTextInBrightColors: true,
      minimumContrastRatio: 1,
      overviewRuler: { width: 10 },
    });
    this.term = term;

    this.fitAddon = new FitAddon.FitAddon();
    this.searchAddon = new SearchAddon.SearchAddon();
    term.loadAddon(this.fitAddon);
    term.loadAddon(this.searchAddon);
    term.loadAddon(new WebLinksAddon.WebLinksAddon((event, uri) => {
      if (event.ctrlKey) send({ type: 'openUrl', url: uri });
    }));
    const unicode = new Unicode11Addon.Unicode11Addon();
    term.loadAddon(unicode);
    this.serializer = new SerializeAddon.SerializeAddon(); // exportar como HTML com cores
    term.loadAddon(this.serializer);
    term.unicode.activeVersion = '11';

    term.open(this.termEl);
    this.loadWebgl();

    term.onData((data) => this.input(data));
    term.onBinary((data) => {
      if (this.state === 'connected') send('b' + this.id + data);
    });
    term.onResize(({ cols, rows }) => send({ type: 'resize', id: this.id, cols, rows }));
    term.onTitleChange((title) => {
      this.remoteTitle = title;
      this.renderHead();
      if (this.tab) this.tab.renderHeader();
    });

    let copyTimer = 0;
    term.onSelectionChange(() => {
      clearTimeout(copyTimer);
      if (!settings.copyOnSelect) return;
      copyTimer = setTimeout(() => {
        const text = term.getSelection();
        if (text) send({ type: 'copy', text });
      }, 120);
    });

    term.attachCustomKeyEventHandler((e) => handleTerminalKey(e, this));
    // Ao receber o foco de volta, o navegador devolve-o ao último terminal focado, mesmo que
    // ele já esteja escondido (outra aba): esse não pode virar o ativo.
    term.textarea.addEventListener('focus', () => {
      if (this.el.style.display !== 'none') setActivePane(this);
    });
    this.el.addEventListener('mousedown', () => setActivePane(this));
    this.el.addEventListener('contextmenu', (e) => {
      e.preventDefault();
      if (settings.rightClickPaste) requestPaste(this);
      else showTerminalMenu(this, e.clientX, e.clientY);
    });

    this.resizeObserver = new ResizeObserver(() => this.scheduleFit());
    this.resizeObserver.observe(this.termEl);
    this.renderHead();
    for (const hook of paneHooks) hook(this);
  }

  // Cabeçalho do painel: só aparece com a tela dividida (CSS .pane.multi). Arrastável:
  // solte em outro painel para trocar de lugar ou na barra de abas para separar.
  buildHead() {
    const head = el('div', 'pane-head');
    this.headDot = el('span', 'dot');
    this.headTitle = el('span', 'title');
    this.headInfo = el('span', 'info');
    this.headNote = el('span', 'note');
    this.zoomBtn = el('button', 'pane-btn');
    this.zoomBtn.addEventListener('click', (e) => {
      e.stopPropagation();
      if (this.tab) this.tab.toggleZoom(this);
    });
    const close = el('button', 'pane-btn close');
    close.innerHTML = '<svg viewBox="0 0 16 16"><path d="M4.5 4.5l7 7M11.5 4.5l-7 7"/></svg>';
    close.title = 'Fechar este painel (Ctrl+Shift+W)';
    close.setAttribute('aria-label', 'Fechar este painel');
    close.addEventListener('click', (e) => {
      e.stopPropagation();
      closePane(this);
    });
    head.append(this.headDot, this.headTitle, this.headInfo, el('span', 'spacer'), this.headNote, this.zoomBtn, close);
    head.addEventListener('click', (e) => {
      if (!e.target.closest('button')) this.focus();
    });
    head.addEventListener('dblclick', (e) => {
      if (!e.target.closest('button') && this.tab) this.tab.toggleZoom(this);
    });
    head.addEventListener('auxclick', (e) => {
      if (e.button === 1) closePane(this);
    });
    head.draggable = true;
    head.addEventListener('dragstart', (e) => beginDrag(e, { kind: 'pane', pane: this }, this.el));
    head.addEventListener('dragend', endDrag);
    this.head = head;
    this.el.appendChild(head);
  }

  renderHead() {
    if (!this.head) return;
    const state = this.state;
    this.headDot.className = 'dot' + (state === 'connected' ? ' connected' : state === 'closed' ? ' closed' : '');
    if (this.headTitle.textContent !== this.title) this.headTitle.textContent = this.title;
    const info = state === 'connecting' ? 'conectando…'
      : state === 'closed' ? 'desconectado · Enter reconecta'
        : (this.remoteTitle && this.remoteTitle !== this.title ? this.remoteTitle : '');
    if (this.headInfo.textContent !== info) this.headInfo.textContent = info;
    this.head.title = this.remoteTitle ? this.title + '\n' + this.remoteTitle : this.title;

    const tab = this.tab;
    const zoomed = !!tab && tab.zoomed === this.id;
    const hidden = zoomed ? tab.panes().length - 1 : 0;
    this.headNote.textContent = hidden > 0 ? (hidden === 1 ? '1 painel oculto' : `${hidden} painéis ocultos`) : '';
    const icon = zoomed ? 'restore' : 'max';
    if (this.zoomBtn.dataset.icon !== icon) {
      this.zoomBtn.dataset.icon = icon;
      this.zoomBtn.innerHTML = zoomed
        ? '<svg viewBox="0 0 16 16"><rect x="2.5" y="5.5" width="8" height="8" rx="1"/><path d="M5.5 5.5v-2a1 1 0 0 1 1-1h6a1 1 0 0 1 1 1v6a1 1 0 0 1-1 1h-2"/></svg>'
        : '<svg viewBox="0 0 16 16"><rect x="2.5" y="2.5" width="11" height="11" rx="1.5"/></svg>';
      const label = zoomed ? 'Restaurar a divisão (duplo clique no cabeçalho)' : 'Maximizar este painel (duplo clique no cabeçalho)';
      this.zoomBtn.title = label;
      this.zoomBtn.setAttribute('aria-label', label);
    }
  }

  loadWebgl() {
    try {
      const webgl = new WebglAddon.WebglAddon();
      webgl.onContextLoss(() => webgl.dispose()); // volta ao renderizador DOM
      this.term.loadAddon(webgl);
    } catch (err) {
      console.warn('WebGL indisponível, usando renderizador DOM', err);
    }
  }

  scheduleFit() {
    if (this.fitQueued) return;
    this.fitQueued = true;
    requestAnimationFrame(() => {
      this.fitQueued = false;
      this.fitNow();
    });
  }

  fitNow() {
    if (!this.termEl.isConnected || this.termEl.clientWidth === 0 || this.termEl.clientHeight === 0) return;
    try {
      this.fitAddon.fit();
    } catch (err) {
      console.warn('fit falhou', err);
    }
    if (!this.sizeSent) {
      // Primeiro tamanho real: o C# só conecta depois disso.
      this.sizeSent = true;
      send({ type: 'resize', id: this.id, cols: this.term.cols, rows: this.term.rows });
    }
  }

  write(data) {
    this.pending += data.length;
    this.term.write(data, () => {
      this.pending -= data.length;
      if (this.paused && this.pending < FLOW_LOW) {
        this.paused = false;
        send({ type: 'resume', id: this.id });
      }
    });
    if (!this.paused && this.pending > FLOW_HIGH) {
      this.paused = true;
      send({ type: 'pause', id: this.id });
    }
  }

  input(data) {
    if (this.prompt) {
      this.promptInput(data);
      return;
    }
    if (this.state === 'closed') {
      if (data === '\r') send({ type: 'reconnect', id: this.id });
      return;
    }
    if (this.state !== 'connected') return;
    this.nav = null;
    if (data.includes('\r')) this.noteSubmit();
    send('i' + this.id + data);
  }

  setState(state) {
    this.state = state;
    this.el.classList.toggle('closed', state === 'closed');
    this.renderHead();
    if (this.tab) this.tab.renderHeader();
  }

  // ------------------------------------------------ Login no terminal
  // O C# escreve a pergunta ("Usuário: ", "Senha de root@srv: ") e pede a linha. A edição é
  // local: Backspace, Ctrl+U limpa, Ctrl+C cancela, Enter envia. A senha aparece como "•"
  // (um por caractere), para saber quanto foi digitado sem mostrar o conteúdo.
  beginPrompt(echo) {
    this.prompt = { echo: !!echo, text: '' };
    if (activeTab && activeTab.activePane === this) this.focus();
  }

  endPrompt() {
    this.prompt = null;
  }

  promptInput(data) {
    const p = this.prompt;
    if (data.startsWith('\x1b[200~')) data = data.slice(6).replace(/\x1b\[201~$/, '');
    else if (data.charCodeAt(0) === 0x1b) return; // setas, F1... não fazem sentido aqui
    for (const ch of data) {
      if (ch === '\r' || ch === '\n') {
        this.finishPrompt(p.text, false);
        return;
      }
      if (ch === '\x03') {
        this.term.write('^C\r\n');
        this.finishPrompt(null, true);
        return;
      }
      if (ch === '\x7f' || ch === '\b') {
        if (p.text) {
          p.text = [...p.text].slice(0, -1).join('');
          this.term.write('\b \b');
        }
        continue;
      }
      if (ch === '\x15') {
        this.term.write('\b \b'.repeat([...p.text].length));
        p.text = '';
        continue;
      }
      if (ch < ' ') continue;
      p.text += ch;
      this.term.write(p.echo ? ch : PASSWORD_MASK);
    }
  }

  finishPrompt(value, newlineWritten) {
    if (!this.prompt) return;
    this.prompt = null;
    if (!newlineWritten) this.term.write('\r\n');
    send({ type: 'promptAnswer', id: this.id, value: value === null ? '' : value, cancel: value === null });
  }

  // Cartão no canto do painel com botões (ex.: lembrar usuário e senha). Some sozinho em 1 min.
  showOffer(msg) {
    const card = el('div', 'offer');
    card.setAttribute('role', 'dialog');
    const icon = el('span', 'offer-icon');
    icon.innerHTML = '<svg viewBox="0 0 16 16"><circle cx="5.5" cy="10.5" r="3"/><path d="M7.6 8.4 13.5 2.5M11 5l1.8 1.8M12.5 3.5l1.5 1.5"/></svg>';
    const text = el('span', 'offer-text');
    text.textContent = msg.text;
    card.append(icon, text);
    let timer = 0;
    const done = (choice) => {
      if (!card.isConnected) return;
      clearTimeout(timer);
      card.remove();
      send({ type: 'offerAnswer', offerId: msg.offerId, choice });
    };
    for (const action of msg.actions || []) {
      const b = el('button', action.primary ? 'primary' : '');
      b.textContent = action.label;
      b.addEventListener('click', (e) => {
        e.stopPropagation();
        done(action.id);
        this.focus();
      });
      card.appendChild(b);
    }
    const close = el('button', 'offer-close');
    close.textContent = '×';
    close.title = 'Dispensar';
    close.setAttribute('aria-label', 'Dispensar');
    close.addEventListener('click', (e) => {
      e.stopPropagation();
      done(null);
      this.focus();
    });
    card.appendChild(close);
    this.el.appendChild(card);
    timer = setTimeout(() => done(null), 60000);
  }

  // ------------------------------------------------ Linhas de comando
  // Ao enviar Enter no prompt, marca a linha (faixa colorida, prompt em destaque e marca na
  // barra de rolagem). Só marca o que parece "prompt + comando": Enter vazio, senha, respostas
  // como [Y/n] e programas de tela cheia (vim, less, top, tmux) ficam de fora.
  noteSubmit() {
    const buffer = this.term.buffer.active;
    if (buffer.type !== 'normal') return;
    const cursor = buffer.baseY + buffer.cursorY;
    let y = cursor;
    while (y > 0 && buffer.getLine(y) && buffer.getLine(y).isWrapped) y--;
    if (this.marks.some((m) => m.marker.line === y)) return;
    const marker = this.term.registerMarker(y - cursor);
    if (!marker) return;
    const mark = { marker, deco: null, el: null, chip: null, cols: 0 };
    this.marks.push(mark);
    marker.onDispose(() => {
      const i = this.marks.indexOf(mark);
      if (i >= 0) this.marks.splice(i, 1);
    });
    this.confirmMark(mark, 0);
  }

  // O eco do comando pode chegar depois do Enter (rede lenta ou comando vindo do painel):
  // confere a linha algumas vezes antes de desistir.
  confirmMark(mark, attempt) {
    setTimeout(() => {
      if (this.disposed || mark.marker.isDisposed) return;
      const line = readLogicalLine(this.term.buffer.normal, mark.marker.line);
      const parsed = line && parseCommandLine(line.text);
      if (parsed) {
        this.decorateMark(mark);
        send({ type: 'commandRan', id: this.id, command: parsed.command }); // histórico (Ctrl+Shift+H)
      } else if (attempt + 1 < MARK_CHECKS.length) this.confirmMark(mark, attempt + 1);
      else mark.marker.dispose();
    }, MARK_CHECKS[attempt]);
  }

  decorateMark(mark) {
    mark.cols = this.term.cols;
    const deco = this.term.registerDecoration({
      marker: mark.marker,
      x: 0,
      width: mark.cols,
      layer: 'bottom',
    });
    if (!deco) {
      mark.marker.dispose();
      return;
    }
    mark.deco = deco;
    deco.onRender((elem) => this.renderMark(mark, elem));
  }

  // Chamado pelo xterm a cada desenho, logo depois de posicionar o elemento (1 linha de altura).
  renderMark(mark, elem) {
    if (elem.style.display === 'none' || mark.marker.isDisposed) return;
    const line = readLogicalLine(this.term.buffer.normal, mark.marker.line);
    const parsed = line && parseCommandLine(line.text);
    if (!parsed) {
      // A linha foi apagada ou reescrita (clear, Ctrl+L, programa que redesenha a tela).
      elem.style.display = 'none';
      setTimeout(() => mark.marker.dispose(), 0);
      return;
    }
    if (mark.el !== elem) {
      elem.classList.add('cmd-mark');
      mark.chip = el('span', 'cmd-chip');
      elem.replaceChildren(mark.chip);
      mark.el = elem;
    }
    const cellH = parseFloat(elem.style.height) || 0;
    const cellW = (parseFloat(elem.style.width) || 0) / mark.cols;
    elem.style.height = line.rows * cellH + 'px';
    const cells = Math.min(cellCount(parsed.prompt), this.term.cols);
    mark.chip.style.width = Math.round(cells * cellW) + 4 + 'px';
    mark.chip.style.height = cellH + 'px';
    elem.style.setProperty('--cmd-row', cellH + 'px');
  }

  // Ctrl+↑ / Ctrl+↓: rola até o comando anterior / seguinte. Retorna false fora do buffer normal.
  jumpCommand(step) {
    const buffer = this.term.buffer.active;
    if (buffer.type !== 'normal') return false;
    const lines = this.marks.filter((m) => m.deco && !m.marker.isDisposed)
      .map((m) => m.marker.line)
      .sort((a, b) => a - b);
    const top = buffer.viewportY;
    const nav = this.nav && this.nav.viewportY === top ? this.nav : null;
    let target;
    if (step < 0) {
      const ref = nav ? nav.line : top + this.term.rows;
      for (let i = lines.length - 1; i >= 0; i--) {
        if (lines[i] < ref) { target = lines[i]; break; }
      }
    } else {
      const ref = nav ? nav.line : top;
      target = lines.find((l) => l > ref);
    }
    if (target === undefined) {
      if (step > 0) {
        this.term.scrollToBottom();
        this.nav = null;
      }
      return true;
    }
    this.term.scrollToLine(target);
    this.nav = { line: target, viewportY: this.term.buffer.active.viewportY };
    const mark = this.marks.find((m) => m.marker.line === target);
    setTimeout(() => {
      if (!mark || !mark.el) return;
      mark.el.classList.remove('flash');
      void mark.el.offsetWidth; // reinicia a animação
      mark.el.classList.add('flash');
    }, 30);
    return true;
  }

  displayTitle() {
    return this.title;
  }

  focus() {
    this.term.focus();
  }

  dispose() {
    this.disposed = true;
    this.resizeObserver.disconnect();
    this.term.dispose();
    this.el.remove();
  }
}

// Mostrado no lugar de cada caractere da senha (ocupa uma célula).
const PASSWORD_MASK = '•';

// ---------------------------------------------------------------- Detecção de prompt
// "root@srv:~# ls", "[user@srv ~]$ ls", "PS C:\> ls", "mysql> select 1;", "$ ls", "C:\>dir".
// O primeiro espaço separa o prompt; espaços a mais ficam no comando (" comando" = não guardar no histórico).
const PROMPT_RE = /^(.{0,200}?[#$%>❯➜»λ])\s(\s*\S.*)$/;
// cmd.exe: "C:\pasta>dir", também com prefixo (ex.: "dominio\usuario@PC C:\pasta>dir").
const CMD_PROMPT_RE = /^(.{0,120}?[A-Za-z]:\\[^>]{0,200}>)(\S.*)$/;
const MARK_CHECKS = [40, 300, 1200, 4000];

function parseCommandLine(text) {
  const m = PROMPT_RE.exec(text) || CMD_PROMPT_RE.exec(text);
  return m ? { prompt: m[1], command: m[2] } : null;
}

// Junta a linha com as continuações quebradas pela largura do terminal.
function readLogicalLine(buffer, y) {
  let line = buffer.getLine(y);
  if (!line) return null;
  let text = '';
  let rows = 1;
  for (;;) {
    const next = buffer.getLine(y + rows);
    const wraps = !!next && next.isWrapped;
    text += line.translateToString(!wraps);
    if (!wraps || rows >= 50) break;
    line = next;
    rows++;
  }
  return { text, rows };
}

// Largura em células (caracteres CJK e de largura total ocupam duas).
const WIDE_RE = /[\u1100-\u115f\u2e80-\ua4cf\uac00-\ud7a3\uf900-\ufaff\uff00-\uff60\uffe0-\uffe6]/;
function cellCount(text) {
  let n = 0;
  for (const ch of text) n += WIDE_RE.test(ch) ? 2 : 1;
  return n;
}


// ---------------------------------------------------------------- Abas, layout e palco
// Layout de uma aba: { kind: 'pane', id } | { kind: 'split', dir: 'v'|'h', ratio, a, b }
// Todos os painéis e divisórias vivem num único palco (#stage), em posição absoluta, e nunca
// mudam de lugar no DOM: dividir, trocar de aba, juntar abas ou mover painéis só muda
// coordenadas e visibilidade (mover um terminal no DOM zera a rolagem dele).
const GUTTER = 4;
const stage = $('stage');
let shownTab = null;     // aba desenhada no palco (igual a activeTab, exceto durante um arraste)
const tabHistory = [];   // abas da usada mais recentemente para a mais antiga

class Tab {
  constructor(firstPane) {
    this.root = { kind: 'pane', id: firstPane.id };
    this.activePane = firstPane;
    this.zoomed = null;
    this.gutters = new Set();
    firstPane.tab = this;
    this.buildHeader();
  }

  // Cabeçalho criado uma vez e só atualizado (estável para cliques e acessibilidade).
  buildHeader() {
    const header = el('div', 'tab');
    header.draggable = true;
    this.dot = el('span', 'dot');
    this.titleEl = el('span', 'title');
    this.badge = el('span', 'badge');
    this.badge.textContent = 'PROD';
    this.count = el('span', 'count');
    const close = el('button', 'close');
    close.textContent = '×';
    close.title = 'Fechar aba';
    close.setAttribute('aria-label', 'Fechar aba');
    close.addEventListener('click', (e) => {
      e.stopPropagation();
      closeTab(this);
    });
    header.append(this.dot, this.titleEl, this.badge, this.count, close);
    $('tabs').appendChild(header);
    this.header = header;

    // Ativa no clique, não ao apertar o botão: assim dá para arrastar uma aba para dentro da
    // que está aberta sem trocar a tela no caminho.
    header.addEventListener('click', (e) => {
      if (!e.target.closest('.close')) activateTab(this);
    });
    header.addEventListener('auxclick', (e) => {
      if (e.button === 1) closeTab(this);
    });
    header.addEventListener('dragstart', (e) => beginDrag(e, { kind: 'tab', tab: this }, header));
    header.addEventListener('dragend', endDrag);
    header.addEventListener('dragover', (e) => onHeaderDragOver(e, this));
  }

  panes() {
    const out = [];
    const walk = (n) => (n.kind === 'pane' ? out.push(panes.get(n.id)) : (walk(n.a), walk(n.b)));
    if (this.root) walk(this.root);
    return out.filter(Boolean);
  }

  lead() {
    const list = this.panes();
    return list.includes(this.activePane) ? this.activePane : list[0];
  }

  label() {
    const list = this.panes();
    const lead = this.lead();
    if (!lead) return '';
    return list.length > 1 ? `${lead.title} +${list.length - 1}` : lead.title;
  }

  render() {
    if (this.zoomed && (this.root.kind === 'pane' || !panes.has(this.zoomed))) this.zoomed = null;
    this.layout();
    this.renderHeader();
    for (const p of this.panes()) {
      p.el.classList.toggle('active', p === this.activePane);
      p.renderHead();
    }
  }

  // Maximizar: um painel ocupa a aba inteira; os outros continuam conectados, só escondidos.
  toggleZoom(pane) {
    if (this.root.kind === 'pane') return;
    this.zoomed = this.zoomed === pane.id ? null : pane.id;
    activateTab(this, pane);
  }

  // Posiciona painéis e divisórias desta aba. Se ela não está no palco, só esconde.
  layout() {
    if (!this.root) return;
    const shown = this === shownTab;
    const W = stage.clientWidth;
    const H = stage.clientHeight;
    const multi = this.root.kind !== 'pane';
    const zoomed = this.zoomed ? panes.get(this.zoomed) : null;
    const visible = new Set();
    const inTree = new Set();

    const collect = (node) => {
      if (node.kind !== 'split') return;
      if (!node.gutter) node.gutter = createGutter();
      if (node.gutter.parentNode !== stage) stage.appendChild(node.gutter);
      node.gutter.style.display = 'none';
      this.gutters.add(node.gutter);
      inTree.add(node.gutter);
      collect(node.a);
      collect(node.b);
    };
    collect(this.root);

    const place = (node, x, y, w, h) => {
      if (node.kind === 'pane') {
        const pane = panes.get(node.id);
        if (!pane) return;
        visible.add(pane);
        Object.assign(pane.el.style, {
          left: x + 'px', top: y + 'px', width: Math.max(0, w) + 'px', height: Math.max(0, h) + 'px',
        });
        return;
      }
      const g = node.gutter;
      g.node = node;
      g.tab = this;
      g.rect = { x, y, w, h };
      g.classList.toggle('v', node.dir === 'v');
      g.classList.toggle('h', node.dir === 'h');
      g.style.display = '';
      if (node.dir === 'v') {
        const aw = Math.round((w - GUTTER) * node.ratio);
        place(node.a, x, y, aw, h);
        Object.assign(g.style, { left: x + aw + 'px', top: y + 'px', width: GUTTER + 'px', height: h + 'px' });
        place(node.b, x + aw + GUTTER, y, w - aw - GUTTER, h);
      } else {
        const ah = Math.round((h - GUTTER) * node.ratio);
        place(node.a, x, y, w, ah);
        Object.assign(g.style, { left: x + 'px', top: y + ah + 'px', width: w + 'px', height: GUTTER + 'px' });
        place(node.b, x, y + ah + GUTTER, w, h - ah - GUTTER);
      }
    };
    if (shown && W && H) {
      if (zoomed) place({ kind: 'pane', id: zoomed.id }, 0, 0, W, H);
      else place(this.root, 0, 0, W, H);
    }

    // Divisórias de nós que saíram da árvore (painel fechado ou movido, nova organização).
    for (const g of this.gutters) {
      if (!inTree.has(g)) {
        g.remove();
        this.gutters.delete(g);
      }
    }
    for (const p of this.panes()) {
      if (!visible.has(p) && p.el.contains(document.activeElement)) document.activeElement.blur();
      p.el.style.display = visible.has(p) ? '' : 'none';
      p.el.classList.toggle('multi', multi);
      p.el.classList.toggle('zoomed', p === zoomed);
    }
  }

  renderHeader() {
    const list = this.panes();
    const lead = this.lead();
    if (!lead) return;
    const terms = list.filter((p) => p.kind !== 'editor');
    const anyConnected = terms.some((p) => p.state === 'connected');
    const allClosed = terms.length > 0 && terms.every((p) => p.state === 'closed');
    // Basta um painel de produção para a aba inteira avisar.
    const prod = list.some((p) => p.env === 'Production');

    const h = this.header.classList;
    for (const c of [...h]) {
      if (c.startsWith('env-')) h.remove(c);
    }
    h.add('env-' + (prod ? 'Production' : lead.env));
    h.toggle('active', this === activeTab);
    this.header.title = list.length > 1
      ? list.map((p) => '• ' + p.title).join('\n') + '\n\nArraste para dentro de outro terminal para dividir a tela.'
      : (lead.remoteTitle ? lead.title + '\n' + lead.remoteTitle : lead.title)
        + (tabs.length > 1 ? '\n\nArraste para dentro do terminal aberto para dividir a tela.' : '');
    this.dot.className = lead.kind === 'editor'
      ? 'dot file'
      : 'dot' + (allClosed ? ' closed' : anyConnected ? ' connected' : '');
    h.toggle('dirty', list.some((p) => p.dirty));
    const title = lead.displayTitle();
    if (this.titleEl.textContent !== title) this.titleEl.textContent = title;
    this.badge.hidden = !prod;
    this.count.hidden = list.length < 2;
    this.count.textContent = String(list.length);
  }

  dispose() {
    for (const g of this.gutters) g.remove();
    this.gutters.clear();
    this.header.remove();
  }
}

function createGutter() {
  const gutter = el('div', 'gutter');
  gutter.title = 'Arraste para redimensionar · duplo clique divide meio a meio';
  gutter.addEventListener('pointerdown', (e) => {
    const tab = gutter.tab;
    if (!tab) return;
    e.preventDefault();
    gutter.setPointerCapture(e.pointerId);
    gutter.classList.add('dragging');
    const box = stage.getBoundingClientRect();
    const move = (ev) => {
      const node = gutter.node;
      const r = gutter.rect;
      if (!node || !r) return;
      const ratio = node.dir === 'v'
        ? (ev.clientX - box.left - r.x) / r.w
        : (ev.clientY - box.top - r.y) / r.h;
      node.ratio = Math.min(0.9, Math.max(0.1, ratio));
      tab.layout();
    };
    const up = () => {
      gutter.classList.remove('dragging');
      gutter.removeEventListener('pointermove', move);
      gutter.removeEventListener('pointerup', up);
    };
    gutter.addEventListener('pointermove', move);
    gutter.addEventListener('pointerup', up);
  });
  gutter.addEventListener('dblclick', () => {
    if (!gutter.node || !gutter.tab) return;
    gutter.node.ratio = 0.5;
    gutter.tab.layout();
  });
  return gutter;
}

function syncTabOrder() {
  const order = [...$('tabs').children];
  tabs.sort((a, b) => order.indexOf(a.header) - order.indexOf(b.header));
}

// ---------------------------------------------------------------- Árvore de layout
function replaceNode(node, id, replacement) {
  if (node.kind === 'pane') return node.id === id ? replacement : node;
  node.a = replaceNode(node.a, id, replacement);
  node.b = replaceNode(node.b, id, replacement);
  return node;
}

function removeNode(node, id) {
  if (node.kind === 'pane') return node.id === id ? null : node;
  const a = removeNode(node.a, id);
  const b = removeNode(node.b, id);
  if (!a) return b;
  if (!b) return a;
  node.a = a;
  node.b = b;
  return node;
}

// Coloca `subtree` ao lado do painel `targetId` (side: left, right, top, bottom).
function insertBeside(root, targetId, subtree, side) {
  const dir = side === 'left' || side === 'right' ? 'v' : 'h';
  const target = { kind: 'pane', id: targetId };
  const first = side === 'left' || side === 'top';
  return replaceNode(root, targetId, {
    kind: 'split', dir, ratio: 0.5, a: first ? subtree : target, b: first ? target : subtree,
  });
}

// Fileira de nós do mesmo tamanho: 1/n para o primeiro, o resto divide o que sobra.
function chainNodes(nodes, dir) {
  if (nodes.length === 1) return nodes[0];
  return { kind: 'split', dir, ratio: 1 / nodes.length, a: nodes[0], b: chainNodes(nodes.slice(1), dir) };
}

// ---------------------------------------------------------------- Abas e painéis
function updateEmptyState() {
  $('empty').hidden = tabs.length > 0;
}

function layoutStage() {
  for (const t of tabs) t.layout();
}

// Troca só o que está desenhado (usado durante um arraste); a aba ativa continua a mesma.
function showTab(tab) {
  if (shownTab === tab) return;
  shownTab = tab;
  layoutStage();
  if (tab) requestAnimationFrame(() => tab.panes().forEach((p) => p.fitNow()));
}

function activateTab(tab, pane) {
  if (!tab) return;
  const before = activeTab && activeTab.activePane;
  activeTab = tab;
  shownTab = tab;
  if (pane && pane.tab === tab) tab.activePane = pane;
  if (!tab.activePane || tab.activePane.tab !== tab) tab.activePane = tab.panes()[0];
  const h = tabHistory.indexOf(tab);
  if (h >= 0) tabHistory.splice(h, 1);
  tabHistory.unshift(tab);
  for (const t of tabs) {
    if (t === tab) continue;
    t.layout();
    t.renderHeader();
  }
  tab.render();
  tab.header.scrollIntoView({ block: 'nearest', inline: 'nearest' });
  // Avisa o C# na hora (a barra de status e o painel de comandos seguem o terminal ativo).
  if (tab.activePane && tab.activePane !== before) send({ type: 'activeChanged', id: tab.activePane.id });
  requestAnimationFrame(() => {
    tab.panes().forEach((p) => p.fitNow());
    if (activeTab === tab && tab.activePane) {
      tab.activePane.focus();
      send({ type: 'activeChanged', id: tab.activePane.id });
    }
  });
}

function setActivePane(pane) {
  const tab = pane.tab;
  if (!tab) return;
  if (activeTab !== tab) {
    activateTab(tab, pane);
    return;
  }
  const changed = tab.activePane !== pane;
  tab.activePane = pane;
  for (const p of tab.panes()) p.el.classList.toggle('active', p === pane);
  tab.renderHeader();
  if (changed) send({ type: 'activeChanged', id: pane.id });
}

function openPane(info) {
  if (panes.has(info.id)) return;
  const pane = new Pane(info);
  panes.set(pane.id, pane);

  const target = info.splitOf && panes.get(info.splitOf);
  if (target && target.tab) {
    const tab = target.tab;
    pane.tab = tab;
    tab.zoomed = null;
    tab.root = replaceNode(tab.root, target.id, {
      kind: 'split',
      dir: info.dir === 'h' ? 'h' : 'v',
      ratio: 0.5,
      a: { kind: 'pane', id: target.id },
      b: { kind: 'pane', id: pane.id },
    });
    activateTab(tab, pane);
  } else {
    const tab = new Tab(pane);
    tabs.push(tab);
    activateTab(tab, pane);
  }
  updateEmptyState();
}

function closePane(pane, notify = true) {
  if (pane.kind === 'editor' && notify) {
    pane.requestClose(); // pergunta se há alterações e volta aqui com notify = false
    return;
  }
  const tab = pane.tab;
  panes.delete(pane.id);
  if (notify && !pane.kind) send({ type: 'closeRequest', id: pane.id }); // editores e RDP avisam no dispose
  pane.dispose();
  if (!tab) return;

  tab.root = removeNode(tab.root, pane.id);
  if (!tab.root) {
    removeTab(tab);
    return;
  }
  if (tab.activePane === pane) tab.activePane = tab.panes()[0];
  tab.render();
  if (tab === activeTab && tab.activePane) {
    // O C# limpou o painel ativo ao fechar este; informa explicitamente o novo.
    send({ type: 'activeChanged', id: tab.activePane.id });
    requestAnimationFrame(() => tab.activePane && tab.activePane.focus());
  }
}

async function closeTab(tab) {
  if (tab.closing) return;
  tab.closing = true;
  try {
    for (const pane of tab.panes()) {
      if (pane.kind === 'editor' && !(await pane.confirmClose())) return;
    }
  } finally {
    tab.closing = false;
  }
  if (!tab.root) return; // fechada enquanto perguntava
  for (const pane of tab.panes()) {
    panes.delete(pane.id);
    if (!pane.kind) send({ type: 'closeRequest', id: pane.id });
    pane.dispose();
  }
  tab.root = null;
  removeTab(tab);
}

// Tira a aba da barra sem fechar terminais (eles já foram fechados ou para outra aba).
function detachTab(tab) {
  const i = tabs.indexOf(tab);
  if (i >= 0) tabs.splice(i, 1);
  const h = tabHistory.indexOf(tab);
  if (h >= 0) tabHistory.splice(h, 1);
  tab.dispose();
}

function removeTab(tab) {
  const index = tabs.indexOf(tab);
  detachTab(tab);
  if (activeTab === tab) {
    activeTab = null;
    shownTab = null;
    activateTab(tabHistory[0] || tabs[Math.min(index, tabs.length - 1)]);
  }
  if (!activeTab) send({ type: 'activeChanged', id: null });
  updateEmptyState();
}

function cycleTab(step) {
  if (tabs.length < 2 || !activeTab) return;
  const index = (tabs.indexOf(activeTab) + step + tabs.length) % tabs.length;
  activateTab(tabs[index]);
}

// Junta a aba `src` inteira (com as divisões dela) ao lado de um painel da aba `dst`.
function mergeTab(src, dst, targetPane, side) {
  if (!src || !dst || src === dst || !targetPane || targetPane.tab !== dst) return;
  const moved = src.panes();
  const focus = src.lead();
  for (const p of moved) p.tab = dst;
  for (const g of src.gutters) dst.gutters.add(g);
  src.gutters.clear();
  dst.root = insertBeside(dst.root, targetPane.id, src.root, side);
  dst.zoomed = null;
  src.root = null;
  detachTab(src);
  activateTab(dst, focus);
}

// Move um painel para o lado de outro (na mesma aba ou em outra).
function movePane(pane, dst, targetPane, side) {
  const src = pane.tab;
  if (!src || !dst || pane === targetPane || !targetPane || targetPane.tab !== dst) return;
  src.root = removeNode(src.root, pane.id);
  if (src.zoomed === pane.id) src.zoomed = null;
  pane.tab = dst;
  dst.root = insertBeside(dst.root, targetPane.id, { kind: 'pane', id: pane.id }, side);
  dst.zoomed = null;
  if (src !== dst) {
    if (!src.root) detachTab(src);
    else if (src.activePane === pane) src.activePane = src.panes()[0];
  }
  activateTab(dst, pane);
}

// Tira um painel da divisão e abre numa aba própria (antes de `beforeHeader`, ou no fim).
function detachPane(pane, beforeHeader) {
  const src = pane.tab;
  if (!src || src.root.kind === 'pane') return;
  src.root = removeNode(src.root, pane.id);
  if (src.zoomed === pane.id) src.zoomed = null;
  if (src.activePane === pane) src.activePane = src.panes()[0];
  const tab = new Tab(pane);
  $('tabs').insertBefore(tab.header, beforeHeader && beforeHeader.parentNode ? beforeHeader : null);
  tabs.push(tab);
  syncTabOrder();
  activateTab(tab, pane);
  src.render();
}

// Reorganiza todos os painéis da aba: grade (2×2, 3×2...), colunas ou linhas.
function arrangeTab(tab, mode) {
  const list = tab.panes();
  if (list.length >= 2) {
    const leaves = list.map((p) => ({ kind: 'pane', id: p.id }));
    if (mode === 'cols') tab.root = chainNodes(leaves, 'v');
    else if (mode === 'rows') tab.root = chainNodes(leaves, 'h');
    else {
      const cols = Math.ceil(Math.sqrt(leaves.length));
      const rows = [];
      for (let i = 0; i < leaves.length; i += cols) rows.push(chainNodes(leaves.slice(i, i + cols), 'v'));
      tab.root = chainNodes(rows, 'h');
    }
    tab.zoomed = null;
  }
  activateTab(tab, tab.activePane);
}

// Traz os terminais de todas as abas para esta e organiza em grade.
function gatherTabs(dst) {
  for (const src of tabs.filter((t) => t !== dst)) {
    for (const p of src.panes()) p.tab = dst;
    dst.root = { kind: 'split', dir: 'v', ratio: 0.5, a: dst.root, b: src.root };
    for (const g of src.gutters) dst.gutters.add(g);
    src.gutters.clear();
    src.root = null;
    detachTab(src);
  }
  arrangeTab(dst, 'grid');
}

// Desfaz a divisão: cada painel volta a ter a sua aba, na mesma ordem.
function separateTab(tab) {
  const list = tab.panes();
  if (list.length < 2) return;
  const keep = tab.lead();
  const after = tab.header.nextSibling;
  for (const p of list) {
    if (p === keep) continue;
    tab.root = removeNode(tab.root, p.id);
    const t = new Tab(p);
    $('tabs').insertBefore(t.header, after);
    tabs.push(t);
  }
  tab.zoomed = null;
  syncTabOrder();
  for (const t of tabs) t.renderHeader();
  activateTab(tab, keep);
}

function focusNeighbor(direction) {
  const tab = activeTab;
  if (!tab || !tab.activePane) return;
  if (tab.zoomed) {
    tab.zoomed = null;
    tab.render();
  }
  const from = tab.activePane.el.getBoundingClientRect();
  const cx = from.left + from.width / 2;
  const cy = from.top + from.height / 2;
  let best = null;
  let bestScore = Infinity;
  for (const p of tab.panes()) {
    if (p === tab.activePane) continue;
    const r = p.el.getBoundingClientRect();
    const px = r.left + r.width / 2;
    const py = r.top + r.height / 2;
    const ok = (direction === 'ArrowLeft' && r.right <= from.left + 1)
      || (direction === 'ArrowRight' && r.left >= from.right - 1)
      || (direction === 'ArrowUp' && r.bottom <= from.top + 1)
      || (direction === 'ArrowDown' && r.top >= from.bottom - 1);
    if (!ok) continue;
    const score = Math.hypot(px - cx, py - cy);
    if (score < bestScore) {
      bestScore = score;
      best = p;
    }
  }
  if (best) {
    setActivePane(best);
    best.focus();
  }
}

// ---------------------------------------------------------------- Arrastar abas e painéis
// Arraste uma aba (ou o cabeçalho de um painel) para dentro do terminal e solte perto de uma
// borda: ele se encaixa daquele lado. Soltar um painel na barra de abas o separa numa aba.
const DRAG_TYPE = 'application/x-rootdock';
const SIDE_LABEL = { left: 'à esquerda', right: 'à direita', top: 'em cima', bottom: 'embaixo' };
let drag = null; // { kind: 'tab', tab } | { kind: 'pane', pane }, com source, target e insertBefore

function beginDrag(e, info, source) {
  drag = { ...info, source, target: null, insertBefore: undefined };
  e.dataTransfer.effectAllowed = 'move';
  e.dataTransfer.setData(DRAG_TYPE, info.kind);
  source.classList.add('dragging');
  // Mexer no DOM dentro do dragstart pode cancelar o arraste: a camada entra no quadro seguinte.
  requestAnimationFrame(() => {
    if (drag) $('dock').hidden = false;
  });
}

function endDrag() {
  if (!drag) return;
  drag.source.classList.remove('dragging');
  markTabDrop(undefined);
  hideDockPreview();
  $('dock').hidden = true;
  drag = null;
  if (shownTab !== activeTab) showTab(activeTab);
}

// Aba que recebe o que está sendo arrastado.
function dockHost() {
  if (!drag) return null;
  if (drag.kind === 'pane') return drag.pane.tab && drag.pane.tab.root.kind !== 'pane' ? drag.pane.tab : null;
  if (drag.tab !== activeTab) return activeTab;
  // Arrastando a própria aba aberta: o destino é a usada antes dela.
  return tabHistory.find((t) => t !== drag.tab) || tabs.find((t) => t !== drag.tab) || null;
}

function dockTargetAt(x, y) {
  const host = dockHost();
  if (!host) return null;
  if (shownTab !== host) showTab(host);
  for (const pane of host.panes()) {
    if (pane.el.style.display === 'none') continue;
    const r = pane.el.getBoundingClientRect();
    if (x < r.left || x >= r.right || y < r.top || y >= r.bottom) continue;
    if (drag.kind === 'pane' && pane === drag.pane) return null;
    const fx = (x - r.left) / r.width;
    const fy = (y - r.top) / r.height;
    const dist = { left: fx, right: 1 - fx, top: fy, bottom: 1 - fy };
    const side = Object.keys(dist).reduce((a, b) => (dist[b] < dist[a] ? b : a));
    return { host, pane, side, rect: r };
  }
  return null;
}

function showDockPreview(t) {
  const box = $('workspace').getBoundingClientRect();
  const r = t.rect;
  let x = r.left - box.left;
  let y = r.top - box.top;
  let w = r.width;
  let h = r.height;
  if (t.side === 'left') w /= 2;
  else if (t.side === 'right') { x += w / 2; w /= 2; }
  else if (t.side === 'top') h /= 2;
  else { y += h / 2; h /= 2; }
  const preview = $('dock-preview');
  Object.assign(preview.style, { left: x + 4 + 'px', top: y + 4 + 'px', width: w - 8 + 'px', height: h - 8 + 'px' });
  preview.hidden = false;
  const name = drag.kind === 'tab' ? drag.tab.label() : drag.pane.title;
  $('dock-label').textContent = `${name} ${SIDE_LABEL[t.side]} de ${t.pane.title}`;
}

function hideDockPreview() {
  $('dock-preview').hidden = true;
}

function onHeaderDragOver(e, tab) {
  if (!drag) return;
  e.preventDefault();
  const rect = tab.header.getBoundingClientRect();
  const before = e.clientX < rect.left + rect.width / 2;
  if (drag.kind === 'tab') {
    if (drag.tab !== tab) $('tabs').insertBefore(drag.tab.header, before ? tab.header : tab.header.nextSibling);
    return;
  }
  e.stopPropagation();
  markTabDrop(before ? tab.header : tab.header.nextSibling);
}

// Marca na barra onde o painel solto vira aba (null = no fim; undefined = limpa).
function markTabDrop(beforeEl) {
  for (const h of $('tabs').querySelectorAll('.drop-before, .drop-after')) h.classList.remove('drop-before', 'drop-after');
  if (!drag) return;
  drag.insertBefore = beforeEl;
  if (beforeEl === undefined) return;
  if (beforeEl) beforeEl.classList.add('drop-before');
  else if ($('tabs').lastElementChild) $('tabs').lastElementChild.classList.add('drop-after');
}

function wireDragAndDrop() {
  const dock = $('dock');
  dock.addEventListener('dragover', (e) => {
    if (!drag) return;
    e.preventDefault();
    const t = dockTargetAt(e.clientX, e.clientY);
    drag.target = t;
    e.dataTransfer.dropEffect = t ? 'move' : 'none';
    if (t) showDockPreview(t);
    else hideDockPreview();
  });
  dock.addEventListener('dragleave', (e) => {
    if (!drag || (e.relatedTarget && dock.contains(e.relatedTarget))) return;
    drag.target = null;
    hideDockPreview();
    if (shownTab !== activeTab) showTab(activeTab);
  });
  dock.addEventListener('drop', (e) => {
    e.preventDefault();
    const t = drag && drag.target;
    if (t) {
      if (drag.kind === 'tab') mergeTab(drag.tab, t.host, t.pane, t.side);
      else movePane(drag.pane, t.host, t.pane, t.side);
    }
    endDrag();
  });

  const bar = $('tabbar');
  bar.addEventListener('dragover', (e) => {
    if (!drag) return;
    e.preventDefault();
    if (drag.kind === 'pane' && !e.target.closest('.tab')) markTabDrop(null);
  });
  bar.addEventListener('dragleave', (e) => {
    if (drag && drag.kind === 'pane' && !(e.relatedTarget && bar.contains(e.relatedTarget))) markTabDrop(undefined);
  });
  bar.addEventListener('drop', (e) => {
    if (!drag) return;
    e.preventDefault();
    if (drag.kind === 'tab') syncTabOrder();
    else if (drag.insertBefore !== undefined) detachPane(drag.pane, drag.insertBefore);
    endDrag();
  });
}

// ---------------------------------------------------------------- Menu "Organizar"
function wireLayoutMenu() {
  const button = document.querySelector('[data-action="layout"]');
  const menu = $('layout-menu');
  const item = (name) => menu.querySelector(`[data-layout="${name}"]`);
  const onOutside = (e) => {
    if (!menu.contains(e.target) && !button.contains(e.target)) close();
  };
  const onKey = (e) => {
    if (e.key !== 'Escape') return;
    e.preventDefault();
    close();
    if (activeTab && activeTab.activePane) activeTab.activePane.focus();
  };
  function close() {
    menu.hidden = true;
    button.classList.remove('active');
    document.removeEventListener('mousedown', onOutside, true);
    document.removeEventListener('keydown', onKey, true);
  }
  button.addEventListener('click', () => {
    if (!menu.hidden) {
      close();
      return;
    }
    const n = activeTab ? activeTab.panes().length : 0;
    const others = activeTab ? tabs.length - 1 : 0;
    for (const name of ['grid', 'cols', 'rows', 'separate']) item(name).disabled = n < 2;
    item('gather').disabled = others < 1;
    item('gather').querySelector('.hint').textContent = others < 1
      ? 'abra outra sessão para juntar'
      : `traz ${others === 1 ? 'a outra aba' : `as outras ${others} abas`} para esta, em grade`;
    const r = button.getBoundingClientRect();
    menu.style.top = r.bottom + 4 + 'px';
    menu.style.right = Math.max(8, window.innerWidth - r.right) + 'px';
    menu.hidden = false;
    button.classList.add('active');
    document.addEventListener('mousedown', onOutside, true);
    document.addEventListener('keydown', onKey, true);
  });
  menu.addEventListener('click', (e) => {
    const choice = e.target.closest('[data-layout]');
    if (!choice || choice.disabled || !activeTab) return;
    close();
    const tab = activeTab;
    switch (choice.dataset.layout) {
      case 'gather': gatherTabs(tab); break;
      case 'separate': separateTab(tab); break;
      default: arrangeTab(tab, choice.dataset.layout);
    }
  });
}

function setFontSize(size) {
  settings.fontSize = Math.min(32, Math.max(8, size));
  for (const pane of panes.values()) {
    if (pane.kind === 'editor') pane.setFontSize(settings.fontSize);
    else if (pane.term) pane.term.options.fontSize = settings.fontSize;
    pane.scheduleFit();
  }
  send({ type: 'fontSize', size: settings.fontSize });
}

// ---------------------------------------------------------------- Copiar, colar, confirmar
function requestPaste(pane) {
  if (pane.state === 'connected' || pane.prompt) send({ type: 'pasteRequest', id: pane.id });
}

function confirmDialog({ title, text, preview, okText }) {
  return new Promise((resolve) => {
    $('modal-title').textContent = title;
    $('modal-text').textContent = text;
    $('modal-preview').textContent = preview || '';
    $('modal-ok').textContent = okText || 'Confirmar';
    $('modal').hidden = false;
    $('modal-cancel').focus();
    const done = (value) => {
      $('modal').hidden = true;
      $('modal-ok').onclick = null;
      $('modal-cancel').onclick = null;
      document.removeEventListener('keydown', onKey, true);
      resolve(value);
    };
    const onKey = (e) => {
      if (e.key === 'Escape') {
        e.preventDefault();
        done(false);
      }
    };
    $('modal-ok').onclick = () => done(true);
    $('modal-cancel').onclick = () => done(false);
    document.addEventListener('keydown', onKey, true);
  });
}

async function pasteInto(pane, text) {
  if (text && pane.prompt) {
    // Senha ou usuário colado: só a primeira linha, sem enviar (Enter confirma).
    pane.promptInput(text.replace(/\r\n/g, '\n').split('\n')[0]);
    pane.focus();
    return;
  }
  if (!text || pane.state !== 'connected') return;
  const lines = text.replace(/\r\n/g, '\n').split('\n').filter((l) => l.length > 0).length;
  if (pane.env === 'Production' && lines > 1) {
    const ok = await confirmDialog({
      title: 'Colar várias linhas em PRODUÇÃO?',
      text: `${lines} linhas serão enviadas para ${pane.title}. Cada linha pode ser executada como comando.`,
      preview: text.length > 4000 ? text.slice(0, 4000) + '\n…' : text,
      okText: 'Colar mesmo assim',
    });
    if (!ok) {
      pane.focus();
      return;
    }
  }
  pane.term.paste(text);
  pane.focus();
}

// ---------------------------------------------------------------- Busca
const searchOptions = {
  decorations: {
    matchBackground: '#e0af6844',
    matchOverviewRuler: '#e0af68',
    activeMatchBackground: '#e0af68',
    activeMatchColorOverviewRuler: '#ff9e64',
  },
};

function openSearch() {
  if (!activeTab || !activeTab.activePane) return;
  if (activeTab.activePane.kind === 'editor') {
    activeTab.activePane.find();
    return;
  }
  if (activeTab.activePane.kind === 'rdp') return;
  $('searchbar').hidden = false;
  const input = $('search-input');
  const selection = activeTab.activePane.term.getSelection();
  if (selection && !selection.includes('\n')) input.value = selection;
  input.focus();
  input.select();
  runSearch(true);
}

function closeSearch() {
  $('searchbar').hidden = true;
  const pane = activeTab && activeTab.activePane;
  if (pane && pane.searchAddon) {
    pane.searchAddon.clearDecorations();
    pane.focus();
  }
}

function runSearch(forward, incremental = false) {
  const pane = activeTab && activeTab.activePane;
  const term = $('search-input').value;
  if (!pane || !pane.searchAddon) return;
  if (!term) {
    pane.searchAddon.clearDecorations();
    $('search-count').textContent = '';
    return;
  }
  const opts = { ...searchOptions, incremental };
  if (forward) pane.searchAddon.findNext(term, opts);
  else pane.searchAddon.findPrevious(term, opts);
}

function wireSearch() {
  const input = $('search-input');
  input.addEventListener('input', () => runSearch(true, true));
  input.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') {
      e.preventDefault();
      runSearch(!e.shiftKey);
    } else if (e.key === 'Escape') {
      e.preventDefault();
      closeSearch();
    }
  });
  $('search-next').onclick = () => runSearch(true);
  $('search-prev').onclick = () => runSearch(false);
  $('search-close').onclick = closeSearch;
}

function bindSearchCounter(pane) {
  pane.searchAddon.onDidChangeResults(({ resultIndex, resultCount }) => {
    if (activeTab && activeTab.activePane === pane) {
      $('search-count').textContent = resultCount === 0 ? 'nada' : `${resultIndex + 1} de ${resultCount}`;
    }
  });
}

// ---------------------------------------------------------------- Teclado
function splitActive(dir) {
  const pane = activeTab && activeTab.activePane;
  if (pane) send({ type: 'split', id: pane.id, dir });
}

// Retorna false quando o atalho é do RootDock (o xterm não recebe a tecla).
function handleTerminalKey(e, pane) {
  if (e.type !== 'keydown') return true;
  const ctrl = e.ctrlKey && !e.altKey;
  const key = e.key.length === 1 ? e.key.toLowerCase() : e.key;

  if (ctrl && e.shiftKey) {
    switch (key) {
      case 'c': { const t = pane.term.getSelection(); if (t) send({ type: 'copy', text: t }); return false; }
      case 'v': requestPaste(pane); return false;
      case 'd': splitActive('v'); return false;
      case 'e': splitActive('h'); return false;
      case 'w': closePane(pane); return false;
      case 't': send({ type: 'newLocal' }); return false;
      case 'f': openSearch(); return false;
      case 's': openSnap(pane); return false;
      case 'k': send({ type: 'command', name: 'quickConnect' }); return false;
      case 'h': send({ type: 'command', name: 'history' }); return false;
      case 'l': send({ type: 'toggleCommands' }); return false;
      case 'Tab': cycleTab(-1); return false;
      default: break;
    }
  }
  if (ctrl && !e.shiftKey) {
    switch (key) {
      case 'c':
        // Com texto selecionado, Ctrl+C copia; sem seleção, vai para o terminal (interromper).
        if (pane.term.hasSelection()) {
          send({ type: 'copy', text: pane.term.getSelection() });
          pane.term.clearSelection();
          return false;
        }
        return true;
      case 'v': requestPaste(pane); return false;
      case 'ArrowUp': return !pane.jumpCommand(-1);
      case 'ArrowDown': return !pane.jumpCommand(1);
      case 'Tab': cycleTab(1); return false;
      case 'PageDown': cycleTab(1); return false;
      case 'PageUp': cycleTab(-1); return false;
      case '=': case '+': setFontSize(settings.fontSize + 1); return false;
      case '-': setFontSize(settings.fontSize - 1); return false;
      case '0': setFontSize(DEFAULT_FONT_SIZE); return false;
      default: break;
    }
  }
  if (e.altKey && !e.ctrlKey && !e.shiftKey && key.startsWith('Arrow') && activeTab && activeTab.root.kind !== 'pane') {
    focusNeighbor(key);
    return false;
  }
  return true;
}

document.addEventListener('keydown', (e) => {
  // Atalhos que valem mesmo sem terminal em foco (tela inicial).
  if (e.ctrlKey && e.shiftKey && !e.altKey) {
    const key = e.key.toLowerCase();
    if (key !== 't' && e.target.closest && e.target.closest('.editor-pane')) return;
    if (key === 't') { e.preventDefault(); send({ type: 'newLocal' }); }
    else if (key === 'k') { e.preventDefault(); send({ type: 'command', name: 'quickConnect' }); }
    else if (key === 'l') { e.preventDefault(); send({ type: 'toggleCommands' }); }
    else if (key === 'h') { e.preventDefault(); send({ type: 'command', name: 'history' }); }
  }
});

// ---------------------------------------------------------------- Barra de menus (Editar, Exibir...)
// Mesmas ações dos atalhos, pedidas pelos menus da janela.
function runMenuAction(action, arg) {
  const pane = activeTab && activeTab.activePane;
  const term = pane && pane.term && !pane.kind ? pane : null;
  switch (action) {
    case 'copy':
      if (term && term.term.hasSelection()) send({ type: 'copy', text: term.term.getSelection() });
      break;
    case 'paste': if (term) requestPaste(term); break;
    case 'selectAll': if (term) term.term.selectAll(); break;
    case 'clear': if (term) term.term.clear(); break;
    case 'search': openSearch(); return;
    case 'splitV': splitActive('v'); break;
    case 'splitH': splitActive('h'); break;
    case 'close': if (pane) closePane(pane); return;
    case 'nextTab': cycleTab(1); break;
    case 'prevTab': cycleTab(-1); break;
    case 'arrange':
      if (!activeTab) break;
      if (arg === 'gather') gatherTabs(activeTab);
      else if (arg === 'separate') separateTab(activeTab);
      else arrangeTab(activeTab, arg || 'grid');
      break;
    case 'fontUp': setFontSize(settings.fontSize + 1); break;
    case 'fontDown': setFontSize(settings.fontSize - 1); break;
    case 'fontReset': setFontSize(DEFAULT_FONT_SIZE); break;
    case 'export': if (term) exportTerminal(term, arg === 'html' ? 'html' : 'txt'); return;
    case 'record': if (term) send({ type: 'record', id: term.id }); break;
    case 'snap': openSnap(); return;
    default: console.warn('ação de menu desconhecida', action); return;
  }
  const now = activeTab && activeTab.activePane;
  if (now) requestAnimationFrame(() => now.focus());
}

// ---------------------------------------------------------------- Mensagens do C#
function onHostMessage(raw) {
  if (raw.charCodeAt(0) === 111 /* 'o' */) {
    const pane = panes.get(raw.substr(1, ID_LEN));
    if (pane) pane.write(raw.substring(1 + ID_LEN));
    return;
  }

  const msg = JSON.parse(raw);
  switch (msg.type) {
    case 'open':
      openPane(msg);
      bindSearchCounter(panes.get(msg.id));
      break;
    case 'state': {
      const pane = panes.get(msg.id);
      if (pane) pane.setState(msg.state);
      break;
    }
    case 'paste': {
      const pane = panes.get(msg.id);
      if (pane) pasteInto(pane, msg.text);
      break;
    }
    case 'input': {
      // Texto vindo do C# (biblioteca de comandos, painel de arquivos): entra como se fosse
      // digitado, para a linha do comando ser marcada igual às outras.
      const pane = panes.get(msg.id);
      if (pane) pane.term.input(msg.data, true);
      break;
    }
    case 'arrange': {
      const pane = panes.get(msg.id);
      if (pane && pane.tab) arrangeTab(pane.tab, msg.mode || 'grid');
      break;
    }
    case 'prompt': {
      const pane = panes.get(msg.id);
      if (pane) pane.beginPrompt(msg.echo);
      break;
    }
    case 'promptEnd': {
      const pane = panes.get(msg.id);
      if (pane) pane.endPrompt();
      break;
    }
    case 'offer': {
      const pane = panes.get(msg.id);
      if (pane) pane.showOffer(msg);
      else send({ type: 'offerAnswer', offerId: msg.offerId, choice: null });
      break;
    }
    case 'focus': {
      const pane = panes.get(msg.id);
      if (pane) {
        setActivePane(pane);
        pane.focus();
      }
      break;
    }
    case 'close': {
      const pane = panes.get(msg.id);
      if (pane) closePane(pane, false);
      break;
    }
    case 'settings':
      applySettings(msg);
      break;
    case 'commandsPanel':
      document.querySelector('[data-action="commands"]').classList.toggle('active', !!msg.open);
      break;
    case 'menu':
      runMenuAction(msg.action, msg.arg);
      break;
    default:
      if (msg.type === 'restoreOffer') showRestoreOffer(msg.names);
      else if (!handleEditorMessage(msg) && !handleRdpMessage(msg) && !handleReplayMessage(msg) && !handleVncMessage(msg) && !handleSnapMessage(msg) && !handleThemeMessage(msg)) console.warn('mensagem desconhecida', msg);
  }
}

if (host) {
  host.addEventListener('message', (e) => onHostMessage(e.data));
}

$('new-tab').addEventListener('click', () => send({ type: 'newLocal' }));
document.querySelector('[data-action="split-v"]').addEventListener('click', () => splitActive('v'));
document.querySelector('[data-action="split-h"]').addEventListener('click', () => splitActive('h'));
document.querySelector('[data-action="search"]').addEventListener('click', openSearch);
document.querySelector('[data-action="commands"]').addEventListener('click', () => send({ type: 'toggleCommands' }));
new ResizeObserver(() => shownTab && shownTab.layout()).observe(stage);
wireSearch();
wireDragAndDrop();
wireLayoutMenu();
updateEmptyState();
// "Pronto" só depois de todos os scripts da página (temas, editor, print...): as mensagens que o
// C# guardou chegam com todas as funções já definidas.
if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', () => send({ type: 'ready' }));
else send({ type: 'ready' });
