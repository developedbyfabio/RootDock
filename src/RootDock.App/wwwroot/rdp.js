'use strict';
// ---------------------------------------------------------------- Área de Trabalho Remota (RDP)
// A aba reserva um espaço na tela (.rdp-screen) e informa ao C# a posição e o tamanho; o controle
// de Área de Trabalho Remota do Windows (uma janela nativa) é posto exatamente ali. Janelas nativas
// ficam por cima da página: quando algo da página precisa aparecer por cima (diálogo, menu,
// arraste de abas), o controle é escondido até terminar.

const RDP_ICON = '<svg viewBox="0 0 16 16"><rect x="1.5" y="2.5" width="13" height="9" rx="1.2"/><path d="M5.5 14h5M8 11.5V14"/></svg>';

class RdpPane {
  constructor(info) {
    this.kind = 'rdp';
    this.id = info.id;
    this.title = info.title || info.host;
    this.host = info.host;
    this.user = info.user || '';
    this.env = info.env || 'None';
    this.remoteTitle = this.user ? `${this.user} @ ${this.host}` : this.host;
    this.state = 'connecting';
    this.message = `Conectando a ${this.host}…`;
    this.tab = null;

    this.el = el('div', 'pane rdp-pane env-' + this.env);
    this.el.dataset.id = this.id;
    Pane.prototype.buildHead.call(this);
    this.buildToolbar();
    this.screen = el('div', 'rdp-screen');
    this.buildCard();
    this.screen.appendChild(this.card);
    this.el.append(this.toolbar, this.screen);
    this.el.style.display = 'none';
    stage.appendChild(this.el);

    this.el.addEventListener('mousedown', () => setActivePane(this));
    this.resizeObserver = new ResizeObserver(() => this.scheduleFit());
    this.resizeObserver.observe(this.screen);
    this.render();
  }

  buildToolbar() {
    const bar = el('div', 'rdp-toolbar');
    const icon = el('span', 'rdp-icon');
    icon.innerHTML = RDP_ICON;
    const name = el('span', 'rdp-host');
    name.textContent = this.host;
    const user = el('span', 'rdp-user');
    user.textContent = this.user;
    this.stateChip = el('span', 'rdp-chip');
    const hint = el('span', 'rdp-hint');
    hint.textContent = 'Ctrl+Alt+End = Ctrl+Alt+Del';
    hint.title = 'Dentro da Área de Trabalho Remota, Ctrl+Alt+End faz o papel do Ctrl+Alt+Del (trocar senha, bloquear, Gerenciador de Tarefas).';

    this.filesBtn = this.button('Arquivos', 'Arquivos do servidor lado a lado com os deste computador, para copiar nos dois sentidos (compartilhamento de arquivos do Windows, com o mesmo login)', () => send({ type: 'rdpFiles', id: this.id }));
    this.fullBtn = this.button('Tela cheia', 'Tela cheia. Para voltar: botão Restaurar na barra azul do topo da tela, ou Ctrl+Alt+Break', () => send({ type: 'rdpFullscreen', id: this.id }));
    this.reconnectBtn = this.button('Reconectar', 'Conectar de novo', () => this.reconnect(false));
    this.disconnectBtn = this.button('Desconectar', 'Desconecta (os programas continuam abertos no servidor)', () => send({ type: 'rdpDisconnect', id: this.id }));
    bar.append(icon, name, user, this.stateChip, el('span', 'spacer'), hint, this.filesBtn, this.fullBtn, this.reconnectBtn, this.disconnectBtn);
    this.toolbar = bar;
  }

  button(text, title, onClick) {
    const b = el('button', 'rdp-btn');
    b.textContent = text;
    b.title = title;
    b.addEventListener('click', (e) => {
      e.stopPropagation();
      onClick();
    });
    return b;
  }

  buildCard() {
    const card = el('div', 'rdp-card');
    const icon = el('div', 'rdp-card-icon');
    icon.innerHTML = RDP_ICON;
    this.cardTitle = el('div', 'rdp-card-title');
    this.cardText = el('div', 'rdp-card-text');
    this.cardSpinner = el('div', 'rdp-spinner');
    this.cardActions = el('div', 'rdp-card-actions');
    const again = el('button', 'primary');
    again.textContent = 'Reconectar';
    again.addEventListener('click', () => this.reconnect(false));
    const other = el('button', '');
    other.textContent = 'Entrar com outro usuário';
    other.addEventListener('click', () => this.reconnect(true));
    const close = el('button', '');
    close.textContent = 'Fechar aba';
    close.addEventListener('click', () => closePane(this));
    this.cardActions.append(again, other, close);
    card.append(icon, this.cardTitle, this.cardText, this.cardSpinner, this.cardActions);
    this.card = card;
  }

  reconnect(askLogin) {
    this.setRdpState('connecting', `Conectando a ${this.host}…`);
    send({ type: 'rdpReconnect', id: this.id, login: !!askLogin });
  }

  setRdpState(state, message) {
    this.state = state === 'waiting' ? 'connecting' : state;
    if (message) this.message = message;
    this.render();
    if (this.tab) this.tab.renderHeader();
    if (this.state === 'connected' && activeTab && activeTab.activePane === this) this.focus();
  }

  render() {
    const state = this.state;
    this.el.classList.toggle('closed', state === 'closed');
    this.el.classList.toggle('connected', state === 'connected');
    this.stateChip.textContent = state === 'connected' ? 'conectado' : state === 'closed' ? 'desconectado' : 'conectando…';
    this.stateChip.className = 'rdp-chip ' + state;
    this.fullBtn.hidden = state !== 'connected';
    this.disconnectBtn.hidden = state !== 'connected';
    this.reconnectBtn.hidden = state !== 'closed';
    this.card.hidden = state === 'connected';
    this.cardTitle.textContent = state === 'closed' ? `Desconectado de ${this.host}` : `Conectando a ${this.host}…`;
    this.cardText.textContent = state === 'closed' ? this.message || '' : 'Se aparecer um aviso de certificado ou de credenciais do Windows, responda a ele para continuar.';
    this.cardSpinner.hidden = state !== 'connecting';
    this.cardActions.hidden = state !== 'closed';
    this.renderHead();
  }

  renderHead() {
    Pane.prototype.renderHead.call(this);
    if (!this.head) return;
    this.headInfo.textContent = this.state === 'connecting' ? 'conectando…' : this.state === 'closed' ? 'desconectado' : this.remoteTitle;
  }

  displayTitle() {
    return this.title;
  }

  // ------------------------------------------------ Posição do controle nativo
  scheduleFit() {
    if (this.fitQueued) return;
    this.fitQueued = true;
    requestAnimationFrame(() => {
      this.fitQueued = false;
      this.fitNow();
    });
  }

  fitNow() {
    if (this.disposed) return;
    const r = this.screen.getBoundingClientRect();
    const visible = this.el.style.display !== 'none' && this.screen.isConnected && r.width > 0 && r.height > 0;
    const key = visible ? `${Math.round(r.left)},${Math.round(r.top)},${Math.round(r.width)},${Math.round(r.height)}` : 'hidden';
    if (key === this.lastBounds) return;
    this.lastBounds = key;
    send({ type: 'rdpBounds', id: this.id, x: r.left, y: r.top, w: r.width, h: r.height, visible });
  }

  focus() {
    send({ type: 'rdpFocus', id: this.id });
  }

  setState() {
    // estado vem do C# (rdpState)
  }

  dispose() {
    this.disposed = true;
    this.resizeObserver.disconnect();
    send({ type: 'rdpClosed', id: this.id });
    this.el.remove();
  }
}

// Mover painéis (organizar, trocar de lugar) nem sempre muda o tamanho: depois de cada
// reorganização, confere a posição de todas as Áreas de Trabalho Remotas.
const layoutWithoutRdp = Tab.prototype.layout;
Tab.prototype.layout = function layoutWithRdp() {
  layoutWithoutRdp.call(this);
  for (const p of this.panes()) {
    if (p.kind === 'rdp') p.scheduleFit();
  }
};

// Diálogo, menu "Organizar" ou arraste de abas aberto: esconde os controles nativos.
let rdpObstructed = false;
function syncRdpObstruction() {
  const now = !!popupMenu || !$('modal').hidden || !$('layout-menu').hidden || !$('dock').hidden || !$('snap').hidden;
  if (now === rdpObstructed) return;
  rdpObstructed = now;
  if ([...panes.values()].some((p) => p.kind === 'rdp')) send({ type: 'rdpObstructed', value: now });
}
const rdpOverlayWatcher = new MutationObserver(syncRdpObstruction);
for (const id of ['modal', 'layout-menu', 'dock', 'snap']) rdpOverlayWatcher.observe($(id), { attributes: true, attributeFilter: ['hidden'] });

function openRdp(info) {
  if (panes.has(info.id)) return;
  const pane = new RdpPane(info);
  panes.set(pane.id, pane);
  const target = info.splitOf && panes.get(info.splitOf);
  if (target && target.tab) {
    const tab = target.tab;
    pane.tab = tab;
    tab.zoomed = null;
    tab.root = insertBeside(tab.root, target.id, { kind: 'pane', id: pane.id }, 'right');
    activateTab(tab, pane);
  } else {
    const tab = new Tab(pane);
    tabs.push(tab);
    activateTab(tab, pane);
  }
  updateEmptyState();
}

// true quando a mensagem é da Área de Trabalho Remota.
function handleRdpMessage(msg) {
  switch (msg.type) {
    case 'openRdp':
      openRdp(msg);
      return true;
    case 'rdpState': {
      const pane = panes.get(msg.id);
      if (pane && pane.kind === 'rdp') pane.setRdpState(msg.state, msg.message);
      return true;
    }
    default:
      return false;
  }
}
