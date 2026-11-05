'use strict';
// ---------------------------------------------------------------- VNC numa aba (noVNC)
// O noVNC desenha a tela remota num canvas dentro da aba. A conexão passa por uma ponte local
// do RootDock (WebSocket em 127.0.0.1 com caminho secreto → TCP até o servidor VNC).

let rfbModule = null;
function loadNoVnc() {
  if (!rfbModule) {
    rfbModule = import('./vendor/novnc/core/rfb.js').then((m) => m.default);
    rfbModule.catch(() => {
      rfbModule = null;
    });
  }
  return rfbModule;
}

const VNC_ICON = '<svg viewBox="0 0 16 16"><rect x="1.5" y="2.5" width="13" height="9" rx="1.2"/><path d="M5.5 14h5M8 11.5V14M5 6.5l2 2 4-4"/></svg>';

class VncPane {
  constructor(info) {
    this.kind = 'vnc';
    this.id = info.id;
    this.title = info.title || info.host;
    this.host = info.host;
    this.saved = !!info.saved;
    this.env = info.env || 'None';
    this.remoteTitle = info.host;
    this.state = 'connecting';
    this.tab = null;
    this.rfb = null;
    this.scale = true;
    this.viewOnly = false;
    this.triedSaved = false;
    this.savedPassword = null;
    this.typedPassword = null;

    this.el = el('div', 'pane rdp-pane vnc-pane env-' + this.env);
    this.el.dataset.id = this.id;
    Pane.prototype.buildHead.call(this);
    this.buildToolbar();
    this.screen = el('div', 'rdp-screen vnc-screen');
    this.canvasHost = el('div', 'vnc-canvas');
    this.screen.appendChild(this.canvasHost);
    this.buildCard();
    this.screen.appendChild(this.card);
    this.el.append(this.toolbar, this.screen);
    this.el.style.display = 'none';
    stage.appendChild(this.el);
    this.el.addEventListener('mousedown', () => setActivePane(this));
    this.resizeObserver = new ResizeObserver(() => this.scheduleFit());
    this.resizeObserver.observe(this.screen);
    this.render();
    this.connect();
  }

  buildToolbar() {
    const bar = el('div', 'rdp-toolbar');
    const icon = el('span', 'rdp-icon');
    icon.innerHTML = VNC_ICON;
    const name = el('span', 'rdp-host');
    name.textContent = this.host;
    this.desktopName = el('span', 'rdp-user');
    this.stateChip = el('span', 'rdp-chip');
    const b = (text, title, fn) => {
      const button = el('button', 'rdp-btn');
      button.textContent = text;
      button.title = title;
      button.addEventListener('click', (e) => {
        e.stopPropagation();
        fn(button);
      });
      return button;
    };
    this.cadBtn = b('Ctrl+Alt+Del', 'Envia Ctrl+Alt+Del ao computador remoto', () => this.rfb && this.rfb.sendCtrlAltDel());
    this.pasteBtn = b('Colar texto', 'Envia o texto da área de transferência deste computador para a do computador remoto', () => send({ type: 'vncClipboardRequest', id: this.id }));
    this.scaleBtn = b('Ajustar à janela', 'Liga/desliga o encaixe da tela remota no tamanho da aba', (btn) => {
      this.scale = !this.scale;
      btn.classList.toggle('on', this.scale);
      if (this.rfb) this.rfb.scaleViewport = this.scale;
    });
    this.scaleBtn.classList.add('on');
    this.viewBtn = b('Só visualizar', 'Bloqueia teclado e mouse (assistir sem mexer)', (btn) => {
      this.viewOnly = !this.viewOnly;
      btn.classList.toggle('on', this.viewOnly);
      if (this.rfb) this.rfb.viewOnly = this.viewOnly;
    });
    this.fullBtn = b('Tela cheia', 'Tela cheia (Esc sai)', () => this.screen.requestFullscreen && this.screen.requestFullscreen());
    this.disconnectBtn = b('Desconectar', 'Encerra a conexão VNC', () => this.rfb && this.rfb.disconnect());
    this.reconnectBtn = b('Reconectar', 'Conectar de novo', () => this.connect());
    bar.append(icon, name, this.desktopName, this.stateChip, el('span', 'spacer'), this.cadBtn, this.pasteBtn, this.scaleBtn, this.viewBtn, this.fullBtn, this.disconnectBtn, this.reconnectBtn);
    this.toolbar = bar;
  }

  buildCard() {
    const card = el('div', 'rdp-card');
    const icon = el('div', 'rdp-card-icon');
    icon.innerHTML = VNC_ICON;
    this.cardTitle = el('div', 'rdp-card-title');
    this.cardText = el('div', 'rdp-card-text');
    this.cardSpinner = el('div', 'rdp-spinner');

    this.passwordForm = el('form', 'vnc-password');
    this.passwordInput = document.createElement('input');
    this.passwordInput.type = 'password';
    this.passwordInput.placeholder = 'Senha do VNC';
    this.passwordInput.autocomplete = 'off';
    const remember = document.createElement('label');
    this.rememberBox = document.createElement('input');
    this.rememberBox.type = 'checkbox';
    this.rememberBox.checked = true;
    remember.append(this.rememberBox, document.createTextNode(' Lembrar (Cofre do Windows)'));
    remember.hidden = !this.saved;
    const go = el('button', 'primary');
    go.type = 'submit';
    go.textContent = 'Entrar';
    this.passwordForm.append(this.passwordInput, go, remember);
    this.passwordForm.addEventListener('submit', (e) => {
      e.preventDefault();
      const password = this.passwordInput.value;
      this.typedPassword = password;
      this.passwordInput.value = '';
      this.setState('connecting', 'Verificando a senha…');
      if (this.rfb) this.rfb.sendCredentials({ password });
    });

    this.cardActions = el('div', 'rdp-card-actions');
    const again = el('button', 'primary');
    again.textContent = 'Reconectar';
    again.addEventListener('click', () => this.connect());
    const close = el('button', '');
    close.textContent = 'Fechar aba';
    close.addEventListener('click', () => closePane(this));
    this.cardActions.append(again, close);
    card.append(icon, this.cardTitle, this.cardText, this.cardSpinner, this.passwordForm, this.cardActions);
    this.card = card;
  }

  // ------------------------------------------------ Conexão
  connect() {
    if (this.rfb) {
      try {
        this.rfb.disconnect();
      } catch {
        // já desconectado
      }
      this.rfb = null;
    }
    this.triedSaved = false;
    this.setState('connecting', `Conectando a ${this.host}…`);
    send({ type: 'vncConnect', id: this.id }); // o C# responde com o endereço da ponte (vncUrl)
  }

  async start(url, password) {
    this.savedPassword = password || null;
    let RFB;
    try {
      RFB = await loadNoVnc();
    } catch (err) {
      this.setState('closed', 'Não foi possível carregar o VNC: ' + (err && err.message ? err.message : err));
      return;
    }
    if (this.disposed) return;
    this.canvasHost.replaceChildren();
    const rfb = new RFB(this.canvasHost, url, { wsProtocols: ['binary'] });
    rfb.scaleViewport = this.scale;
    rfb.resizeSession = false;
    rfb.viewOnly = this.viewOnly;
    rfb.focusOnClick = true;
    rfb.showDotCursor = true;
    rfb.background = '#0c0f14';
    rfb.qualityLevel = 6;
    rfb.compressionLevel = 2;
    rfb.addEventListener('connect', () => {
      this.setState('connected');
      if (this.typedPassword && this.rememberBox.checked && this.saved) send({ type: 'vncRemember', id: this.id, password: this.typedPassword });
      this.typedPassword = null;
      if (activeTab && activeTab.activePane === this) this.focus();
    });
    rfb.addEventListener('disconnect', (e) => {
      if (this.rfb !== rfb) return;
      const clean = e.detail && e.detail.clean;
      if (this.state !== 'closed') this.setState('closed', clean ? 'Conexão encerrada.' : (this.lastError || `Não foi possível conectar a ${this.host} (servidor desligado, porta errada ou firewall).`));
      this.lastError = null;
    });
    rfb.addEventListener('credentialsrequired', () => {
      if (this.savedPassword && !this.triedSaved) {
        this.triedSaved = true;
        rfb.sendCredentials({ password: this.savedPassword });
        return;
      }
      this.askPassword();
    });
    rfb.addEventListener('securityfailure', (e) => {
      const reason = e.detail && e.detail.reason;
      // A senha salva não é apagada: o servidor pode só estar bloqueando tentativas por um tempo.
      // Se a pessoa digitar outra com "Lembrar", ela substitui a salva.
      this.lastError = this.triedSaved && !this.typedPassword
        ? 'A senha salva foi recusada (ou o servidor bloqueou novas tentativas por um tempo).'
        : reason ? `Acesso recusado: ${reason}` : 'Senha incorreta ou acesso recusado pelo servidor VNC.';
    });
    rfb.addEventListener('desktopname', (e) => {
      const name = e.detail && e.detail.name;
      this.desktopName.textContent = name || '';
      this.remoteTitle = name ? `${name} (${this.host})` : this.host;
      this.renderHead();
    });
    rfb.addEventListener('clipboard', (e) => {
      if (e.detail && e.detail.text) send({ type: 'copy', text: e.detail.text });
    });
    this.rfb = rfb;
  }

  askPassword() {
    this.setState('password');
    setTimeout(() => this.passwordInput.focus(), 30);
  }

  setState(state, message) {
    this.state = state;
    this.message = message || '';
    this.render();
    if (this.tab) this.tab.renderHeader();
    send({ type: 'vncState', id: this.id, connected: state === 'connected' });
  }

  render() {
    const s = this.state;
    this.el.classList.toggle('closed', s === 'closed');
    this.stateChip.textContent = s === 'connected' ? 'conectado' : s === 'closed' ? 'desconectado' : s === 'password' ? 'aguardando senha' : 'conectando…';
    this.stateChip.className = 'rdp-chip ' + (s === 'password' ? 'connecting' : s);
    for (const b of [this.cadBtn, this.pasteBtn, this.scaleBtn, this.viewBtn, this.fullBtn, this.disconnectBtn]) b.hidden = s !== 'connected';
    this.reconnectBtn.hidden = s !== 'closed';
    this.card.hidden = s === 'connected';
    this.canvasHost.style.visibility = s === 'connected' ? 'visible' : 'hidden';
    this.cardTitle.textContent = s === 'closed' ? `Desconectado de ${this.host}`
      : s === 'password' ? `Senha do VNC de ${this.host}`
        : `Conectando a ${this.host}…`;
    this.cardText.textContent = s === 'password'
      ? (this.lastError ? this.lastError + ' ' : '') + 'O VNC pede só a senha (sem usuário).'
      : this.message;
    this.cardSpinner.hidden = s !== 'connecting';
    this.passwordForm.hidden = s !== 'password';
    this.cardActions.hidden = s !== 'closed';
    this.renderHead();
  }

  renderHead() {
    Pane.prototype.renderHead.call(this);
    if (!this.head) return;
    this.headInfo.textContent = this.state === 'connected' ? this.remoteTitle : this.state === 'closed' ? 'desconectado' : 'conectando…';
  }

  displayTitle() {
    return this.title;
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
    // O noVNC recalcula a escala quando a opção é aplicada de novo.
    if (this.rfb && this.state === 'connected' && this.scale) this.rfb.scaleViewport = true;
  }

  focus() {
    if (this.rfb && this.state === 'connected') this.rfb.focus();
    else if (this.state === 'password') this.passwordInput.focus();
  }

  setFontSize() {}

  dispose() {
    this.disposed = true;
    this.resizeObserver.disconnect();
    if (this.rfb) {
      try {
        this.rfb.disconnect();
      } catch {
        // já desconectado
      }
    }
    send({ type: 'vncClosed', id: this.id });
    this.el.remove();
  }
}

function openVnc(info) {
  if (panes.has(info.id)) return;
  const pane = new VncPane(info);
  panes.set(pane.id, pane);
  const tab = new Tab(pane);
  tabs.push(tab);
  activateTab(tab, pane);
  updateEmptyState();
}

function handleVncMessage(msg) {
  switch (msg.type) {
    case 'openVnc':
      openVnc(msg);
      return true;
    case 'vncUrl': {
      const pane = panes.get(msg.id);
      if (pane && pane.kind === 'vnc') pane.start(msg.url, msg.password);
      return true;
    }
    case 'vncClipboard': {
      const pane = panes.get(msg.id);
      if (pane && pane.kind === 'vnc' && pane.rfb && msg.text) {
        pane.rfb.clipboardPasteFrom(msg.text);
        pane.focus();
      }
      return true;
    }
    default:
      return false;
  }
}
