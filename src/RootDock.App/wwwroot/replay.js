'use strict';
// ---------------------------------------------------------------- Gravações: gravar e reproduzir
// Gravar: o botão de gravação na barra das abas liga/desliga a gravação do terminal ativo (o C#
// grava em asciicast). Reproduzir: abre uma aba com um terminal só de leitura e controles de
// play/pausa, velocidade, linha do tempo e "pular pausas longas".

const RECORD_BUTTON = document.querySelector('[data-action="record"]');

function updateRecordButton() {
  if (!RECORD_BUTTON) return;
  const pane = activeTab && activeTab.activePane;
  const on = !!(pane && pane.recording);
  RECORD_BUTTON.classList.toggle('recording', on);
  RECORD_BUTTON.title = on ? 'Parar a gravação deste terminal' : 'Gravar este terminal (para assistir depois)';
  RECORD_BUTTON.disabled = !pane || !pane.term || pane.kind === 'replay';
}

if (RECORD_BUTTON) {
  RECORD_BUTTON.addEventListener('click', () => {
    const pane = activeTab && activeTab.activePane;
    if (pane && pane.term && !pane.kind) send({ type: 'record', id: pane.id });
  });
}

function setRecording(id, on) {
  const pane = panes.get(id);
  if (!pane) return;
  pane.recording = on;
  pane.el.classList.toggle('recording', on);
  if (pane.tab) pane.tab.renderHeader();
  updateRecordButton();
}

// O cabeçalho da aba mostra "REC" enquanto grava.
const renderHeaderWithoutRec = Tab.prototype.renderHeader;
Tab.prototype.renderHeader = function renderHeaderWithRec() {
  renderHeaderWithoutRec.call(this);
  this.header.classList.toggle('recording', this.panes().some((p) => p.recording));
  const lead = this.lead();
  if (lead && lead.kind === 'replay') this.dot.className = 'dot replay';
};

// O botão acompanha o painel ativo (troca de aba ou de painel na mesma aba).
const activateWithoutRec = activateTab;
activateTab = function activateTabWithRec(tab, pane) {
  activateWithoutRec(tab, pane);
  updateRecordButton();
};
const setActivePaneWithoutRec = setActivePane;
setActivePane = function setActivePaneWithRec(pane) {
  setActivePaneWithoutRec(pane);
  updateRecordButton();
};

// ------------------------------------------------ Reprodução
const REPLAY_SPEEDS = [0.5, 1, 2, 4, 8];

class ReplayPane {
  constructor(info) {
    this.kind = 'replay';
    this.id = info.id;
    this.title = info.title;
    this.env = 'None';
    this.state = 'replay';
    this.tab = null;
    this.events = info.events || [];
    this.duration = info.duration || (this.events.length ? this.events[this.events.length - 1][0] : 0);
    this.cols = info.width || 80;
    this.rows = info.height || 24;
    this.speed = 1;
    this.skipIdle = true;
    this.position = 0;      // segundos (tempo da gravação)
    this.index = 0;         // próximo evento
    this.playing = false;

    this.el = el('div', 'pane replay-pane');
    this.el.dataset.id = this.id;
    Pane.prototype.buildHead.call(this);
    this.buildControls();
    this.termEl = el('div', 'replay-term');
    this.el.append(this.controls, this.termEl);
    this.el.style.display = 'none';
    stage.appendChild(this.el);

    this.term = new Terminal({
      fontFamily: settings.fontFamily,
      fontSize: settings.fontSize,
      lineHeight: settings.lineHeight,
      theme: settings.theme,
      cols: this.cols,
      rows: this.rows,
      scrollback: 5000,
      disableStdin: true,
      cursorBlink: false,
      allowProposedApi: true,
    });
    this.term.loadAddon(new Unicode11AddonShim());
    this.term.open(this.termEl);
    this.el.addEventListener('mousedown', () => setActivePane(this));
    this.renderTime();
  }

  buildControls() {
    const bar = el('div', 'replay-controls');
    this.playBtn = el('button', 'replay-play');
    this.playBtn.addEventListener('click', () => (this.playing ? this.pause() : this.play()));
    this.slider = document.createElement('input');
    this.slider.type = 'range';
    this.slider.min = '0';
    this.slider.max = String(Math.max(0.1, this.duration));
    this.slider.step = '0.1';
    this.slider.value = '0';
    this.slider.className = 'replay-slider';
    this.slider.addEventListener('input', () => this.seek(parseFloat(this.slider.value)));
    this.timeEl = el('span', 'replay-time');
    this.speedSel = document.createElement('select');
    this.speedSel.className = 'replay-speed';
    for (const s of REPLAY_SPEEDS) {
      const o = document.createElement('option');
      o.value = String(s);
      o.textContent = `${String(s).replace('.', ',')}x`;
      this.speedSel.appendChild(o);
    }
    this.speedSel.value = '1';
    this.speedSel.title = 'Velocidade';
    this.speedSel.addEventListener('change', () => {
      this.speed = parseFloat(this.speedSel.value);
    });
    const idle = document.createElement('label');
    idle.className = 'replay-idle';
    const box = document.createElement('input');
    box.type = 'checkbox';
    box.checked = true;
    box.addEventListener('change', () => {
      this.skipIdle = box.checked;
    });
    idle.append(box, document.createTextNode(' pular pausas longas'));
    const copy = el('button', 'replay-btn');
    copy.textContent = 'Copiar tela';
    copy.title = 'Copia o texto que está na tela da reprodução';
    copy.addEventListener('click', () => send({ type: 'copy', text: terminalText(this) }));
    bar.append(this.playBtn, this.slider, this.timeEl, this.speedSel, idle, copy);
    this.controls = bar;
    this.renderPlay();
  }

  renderPlay() {
    this.playBtn.innerHTML = this.playing
      ? '<svg viewBox="0 0 16 16"><rect x="4" y="3" width="3" height="10" rx=".6"/><rect x="9" y="3" width="3" height="10" rx=".6"/></svg>'
      : '<svg viewBox="0 0 16 16"><path d="M5 3.2v9.6L12.5 8z"/></svg>';
    this.playBtn.title = this.playing ? 'Pausar' : 'Reproduzir';
  }

  renderTime() {
    const fmt = (s) => {
      const t = Math.max(0, Math.floor(s));
      return `${String(Math.floor(t / 60)).padStart(2, '0')}:${String(t % 60).padStart(2, '0')}`;
    };
    this.timeEl.textContent = `${fmt(this.position)} / ${fmt(this.duration)}`;
    if (document.activeElement !== this.slider) this.slider.value = String(this.position);
  }

  play() {
    if (this.position >= this.duration) this.seek(0);
    this.playing = true;
    this.renderPlay();
    this.last = performance.now();
    this.tick();
  }

  pause() {
    this.playing = false;
    this.renderPlay();
    cancelAnimationFrame(this.frame);
  }

  tick() {
    if (!this.playing || this.disposed) return;
    const now = performance.now();
    let dt = ((now - this.last) / 1000) * this.speed;
    this.last = now;
    // Pausa longa (gente pensando, comando demorado): pula para o próximo evento.
    const next = this.events[this.index];
    if (this.skipIdle && next && next[0] - this.position > 2) dt = Math.max(dt, next[0] - this.position - 0.5);
    this.position = Math.min(this.duration, this.position + dt);
    this.applyUntil(this.position);
    this.renderTime();
    if (this.position >= this.duration) {
      this.pause();
      return;
    }
    this.frame = requestAnimationFrame(() => this.tick());
  }

  applyUntil(time) {
    let chunk = '';
    while (this.index < this.events.length && this.events[this.index][0] <= time) {
      const [, kind, data] = this.events[this.index++];
      if (kind === 'o') chunk += data;
      else if (kind === 'r') {
        if (chunk) {
          this.term.write(chunk);
          chunk = '';
        }
        const m = /^(\d+)x(\d+)$/.exec(data);
        if (m) this.term.resize(parseInt(m[1], 10), parseInt(m[2], 10));
      }
    }
    if (chunk) this.term.write(chunk);
  }

  seek(time) {
    this.position = Math.max(0, Math.min(this.duration, time));
    this.term.reset();
    this.term.resize(this.cols, this.rows);
    this.index = 0;
    this.applyUntil(this.position);
    this.renderTime();
  }

  // Integração com o palco
  renderHead() {
    Pane.prototype.renderHead.call(this);
    if (!this.head) return;
    this.headInfo.textContent = 'reprodução';
  }

  displayTitle() {
    return this.title;
  }

  scheduleFit() {}

  fitNow() {}

  focus() {
    this.playBtn.focus();
  }

  setState() {}

  setFontSize(size) {
    this.term.options.fontSize = size;
  }

  dispose() {
    this.disposed = true;
    cancelAnimationFrame(this.frame);
    this.term.dispose();
    this.el.remove();
  }
}

// Unicode 11 (larguras corretas de emoji/CJK) sem depender da instância do Pane.
function Unicode11AddonShim() {
  const addon = new Unicode11Addon.Unicode11Addon();
  return {
    activate(term) {
      addon.activate(term);
      term.unicode.activeVersion = '11';
    },
    dispose() {
      addon.dispose();
    },
  };
}

function openReplay(info) {
  const pane = new ReplayPane(info);
  panes.set(pane.id, pane);
  const tab = new Tab(pane);
  tabs.push(tab);
  activateTab(tab, pane);
  updateEmptyState();
  pane.play();
}

function handleReplayMessage(msg) {
  switch (msg.type) {
    case 'openReplay':
      openReplay(msg);
      return true;
    case 'recording':
      setRecording(msg.id, !!msg.on);
      return true;
    default:
      return false;
  }
}
