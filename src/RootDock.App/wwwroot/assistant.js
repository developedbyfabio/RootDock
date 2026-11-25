// Assistente: a conversa com a IA que age na sessão SSH em foco.
// Esta página só desenha; quem fala com o provedor e executa as ferramentas é o aplicativo.
(() => {
  'use strict';

  const $ = (id) => document.getElementById(id);
  const send = (msg) => window.chrome.webview.postMessage(msg);

  const chat = $('chat');
  const messages = $('messages');
  const welcome = $('welcome');
  const input = $('input');
  const sendBtn = $('send');
  const modeBox = $('mode');
  const banner = $('banner');

  const TOOL_LABELS = {
    run_command: 'Comando',
    type_in_terminal: 'Digitar no terminal',
    read_terminal: 'Ler o terminal',
    read_file: 'Ler arquivo',
    write_file: 'Gravar arquivo',
    edit_file: 'Editar arquivo',
    list_directory: 'Listar pasta',
    search_files: 'Buscar no código',
    find_files: 'Achar arquivos',
    remember: 'Anotar',
    web_search: 'Buscar na web',
    web_fetch: 'Abrir página',
    list_macros: 'Ver macros',
    save_macro: 'Salvar macro',
  };

  let state = { busy: false, providers: [], provider: '', mode: 'ask', target: null };
  let replaying = false;       // redesenhando a conversa de outra aba (sem roubar foco nem animar)
  let segment = null;          // resposta em andamento: { el, text }
  let renderQueued = false;
  const tools = new Map();     // id → elemento do cartão
  const modelLists = {};       // provedor → nomes de modelo já listados
  const drafts = {};           // provedor → o que foi digitado e ainda não salvo
  const requested = {};        // provedor → a lista de modelos já foi pedida
  const loading = {};          // provedor → lista de modelos a caminho

  // ---------- Markdown (só o necessário; o texto é sempre escapado antes) ----------

  const escapeHtml = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

  function inline(text) {
    const codes = [];
    let s = escapeHtml(text).replace(/`([^`\n]+)`/g, (_, code) => {
      codes.push(code);
      return `\u0000${codes.length - 1}\u0000`;
    });
    s = s.replace(/\*\*([^*\n]+)\*\*/g, '<strong>$1</strong>')
      .replace(/(^|[\s(])\*([^*\s][^*\n]*)\*(?=[\s).,;:!?]|$)/g, '$1<em>$2</em>')
      .replace(/\[([^\]\n]+)\]\((https?:\/\/[^\s)]+)\)/g, '<a href="$2">$1</a>');
    return s.replace(/\u0000(\d+)\u0000/g, (_, i) => `<code>${codes[+i]}</code>`);
  }

  function markdown(text) {
    const lines = text.replace(/\r\n/g, '\n').split('\n');
    const out = [];
    let paragraph = [];
    let list = null; // { tag, items }
    const flushParagraph = () => {
      if (paragraph.length) { out.push(`<p>${inline(paragraph.join('\n')).replace(/\n/g, '<br>')}</p>`); paragraph = []; }
    };
    const flushList = () => {
      if (list) { out.push(`<${list.tag}>${list.items.map((i) => `<li>${inline(i)}</li>`).join('')}</${list.tag}>`); list = null; }
    };
    const flush = () => { flushParagraph(); flushList(); };

    for (let i = 0; i < lines.length; i++) {
      const line = lines[i];
      const fence = line.match(/^\s*```/);
      if (fence) {
        flush();
        const body = [];
        i++;
        while (i < lines.length && !/^\s*```\s*$/.test(lines[i])) { body.push(lines[i]); i++; }
        out.push(`<pre><code>${escapeHtml(body.join('\n'))}</code></pre>`);
        continue;
      }

      if (/^\s*\|.*\|\s*$/.test(line) && i + 1 < lines.length && /^\s*\|[\s:|-]+\|\s*$/.test(lines[i + 1])) {
        flush();
        const cells = (row) => row.trim().replace(/^\||\|$/g, '').split('|').map((c) => inline(c.trim()));
        const head = cells(line);
        const rows = [];
        i += 2;
        while (i < lines.length && /^\s*\|.*\|\s*$/.test(lines[i])) { rows.push(cells(lines[i])); i++; }
        i--;
        out.push(`<table><thead><tr>${head.map((c) => `<th>${c}</th>`).join('')}</tr></thead><tbody>${rows.map((r) => `<tr>${r.map((c) => `<td>${c}</td>`).join('')}</tr>`).join('')}</tbody></table>`);
        continue;
      }

      const heading = line.match(/^(#{1,6})\s+(.*)$/);
      if (heading) { flush(); out.push(`<h${Math.min(3, heading[1].length)}>${inline(heading[2])}</h${Math.min(3, heading[1].length)}>`); continue; }

      const bullet = line.match(/^\s*[-*•]\s+(.*)$/);
      const numbered = line.match(/^\s*\d+[.)]\s+(.*)$/);
      if (bullet || numbered) {
        flushParagraph();
        const tag = bullet ? 'ul' : 'ol';
        if (!list || list.tag !== tag) { flushList(); list = { tag, items: [] }; }
        list.items.push((bullet || numbered)[1]);
        continue;
      }

      if (list && /^\s{2,}\S/.test(line)) { list.items[list.items.length - 1] += ' ' + line.trim(); continue; }

      const quote = line.match(/^>\s?(.*)$/);
      if (quote) { flush(); out.push(`<blockquote>${inline(quote[1])}</blockquote>`); continue; }

      if (!line.trim()) { flush(); continue; }
      flushList();
      paragraph.push(line);
    }

    flush();
    return out.join('');
  }

  // ---------- Conversa ----------

  const nearBottom = () => chat.scrollHeight - chat.scrollTop - chat.clientHeight < 80;
  const scrollDown = () => { chat.scrollTop = chat.scrollHeight; };

  function add(el) {
    const stick = nearBottom();
    welcome.hidden = true;
    messages.appendChild(el);
    if (stick) scrollDown();
    return el;
  }

  function block(className, text) {
    const el = document.createElement('div');
    el.className = 'msg ' + className;
    if (text !== undefined) el.textContent = text;
    return el;
  }

  function endSegment() {
    if (!segment) return;
    segment.el.classList.remove('cursor');
    segment.el.innerHTML = markdown(segment.text);
    if (!segment.text.trim()) segment.el.remove();
    segment = null;
  }

  // Resposta longa: o que já está completo (até a última linha em branco fora de bloco de código) fica
  // desenhado e só o fim é redesenhado a cada pedaço. Sem isso o custo crescia com o tamanho da resposta.
  function settledLength(text) {
    let cut = 0, pos = 0, fenced = false;
    const lines = text.split('\n');
    for (let i = 0; i < lines.length - 1; i++) {
      const line = lines[i];
      pos += line.length + 1;
      if (fenced) { if (/^\s*```\s*$/.test(line)) fenced = false; }
      else if (/^\s*```/.test(line)) fenced = true;
      else if (!line.trim()) cut = pos;
    }
    return cut;
  }

  function renderSegment() {
    const s = segment;
    let rest = s.text.slice(s.settled);
    if (rest.length > 1500) {
      const cut = settledLength(rest);
      if (cut > 0) {
        s.tail.insertAdjacentHTML('beforebegin', markdown(rest.slice(0, cut)));
        s.settled += cut;
        rest = rest.slice(cut);
      }
    }
    s.tail.innerHTML = markdown(rest);
  }

  function onDelta(text) {
    if (!segment) {
      const el = add(block('md cursor'));
      const tail = document.createElement('div');
      tail.className = 'md-tail';
      el.appendChild(tail);
      segment = { el, tail, text: '', settled: 0 };
    }
    segment.text += text;
    if (renderQueued) return;
    renderQueued = true;
    requestAnimationFrame(() => {
      renderQueued = false;
      if (!segment) return;
      const stick = nearBottom();
      renderSegment();
      if (stick) scrollDown();
    });
  }

  // ---------- Lista de tarefas ----------

  const tasksPanel = $('tasks');
  let tasksDone = false;
  $('tasks-head').addEventListener('click', () => tasksPanel.classList.toggle('collapsed'));

  function onTasks(items) {
    const list = $('tasks-list');
    list.textContent = '';
    for (const item of items) {
      const li = document.createElement('li');
      li.className = item.status;
      const title = document.createElement('span');
      title.textContent = item.title;
      li.appendChild(title);
      list.appendChild(li);
    }
    const done = items.filter((i) => i.status === 'done').length;
    const current = items.find((i) => i.status === 'in_progress');
    tasksDone = items.length > 0 && done === items.length;
    tasksPanel.hidden = items.length === 0;
    $('tasks-title').textContent = current ? current.title : tasksDone ? 'Tarefas concluídas' : 'Tarefas';
    $('tasks-count').textContent = done + '/' + items.length;
    list.querySelector('.in_progress')?.scrollIntoView({ block: 'nearest' });
  }

  // ---------- Arquivos alterados por uma resposta (com desfazer) ----------

  const changeCards = new Map(); // id → cartão

  function onChanges(msg) {
    endSegment();
    const el = document.createElement('div');
    el.className = 'changes';
    const head = document.createElement('div');
    head.className = 'changes-head';
    const title = document.createElement('span');
    title.className = 'changes-title';
    const n = msg.files.length;
    title.textContent = n === 1 ? '1 arquivo alterado nesta resposta' : n + ' arquivos alterados nesta resposta';
    const undo = document.createElement('button');
    undo.className = 'btn';
    undo.textContent = 'Desfazer';
    undo.title = 'Volta os arquivos ao que eram antes desta resposta (e desfaz também as respostas seguintes). O que foi feito por comando de shell não volta.';
    let timer = 0;
    undo.addEventListener('click', () => {
      // Dois cliques: o primeiro só arma.
      if (!undo.classList.contains('sure')) {
        undo.classList.add('sure');
        undo.textContent = 'Desfazer mesmo?';
        timer = setTimeout(() => { undo.classList.remove('sure'); undo.textContent = 'Desfazer'; }, 4000);
        return;
      }
      clearTimeout(timer);
      undo.disabled = true;
      undo.textContent = 'Desfazendo...';
      send({ type: 'undo', id: msg.id });
    });
    head.append(title, undo);
    const list = document.createElement('ul');
    for (const file of msg.files) {
      const li = document.createElement('li');
      if (file.created) li.className = 'created';
      const kind = document.createElement('span');
      kind.textContent = file.created ? 'criado' : 'editado';
      const path = document.createElement('span');
      path.textContent = '‎' + file.path;
      path.title = file.path;
      li.append(kind, path);
      list.appendChild(li);
    }
    el.append(head, list);
    changeCards.set(msg.id, el);
    add(el);
  }

  function onUndone(msg) {
    for (const id of msg.ids || []) {
      const el = changeCards.get(id);
      if (!el) continue;
      el.classList.add('undone');
      el.querySelector('.changes-title').textContent = 'Alterações desfeitas';
      el.querySelector('.btn')?.remove();
    }
    const parts = [];
    if (msg.restored && msg.restored.length) parts.push('voltaram ao conteúdo anterior: ' + msg.restored.join(', '));
    if (msg.removed && msg.removed.length) parts.push('removidos (tinham sido criados): ' + msg.removed.join(', '));
    endSegment();
    add(block('note', 'Desfeito. ' + parts.join('; ') + '.'));
    if (msg.failed && msg.failed.length) add(block('error', 'Não foi possível desfazer: ' + msg.failed.join('; ')));
  }

  // A resposta em andamento falhou em desfazer: o botão volta.
  function resetUndoButtons() {
    for (const el of changeCards.values()) {
      const b = el.querySelector('.btn');
      if (b && b.disabled) { b.disabled = false; b.classList.remove('sure'); b.textContent = 'Desfazer'; }
    }
  }

  function userBlock(msg) {
    const el = block('user');
    if (msg.images && msg.images.length) {
      const row = document.createElement('div');
      row.className = 'user-images';
      for (const src of msg.images) {
        if (typeof src !== 'string' || !src.startsWith('data:image/')) continue;
        const img = document.createElement('img');
        img.src = src;
        img.alt = 'Imagem anexada';
        row.appendChild(img);
      }
      if (row.children.length) el.appendChild(row);
      else { const lost = document.createElement('div'); lost.className = 'note'; lost.textContent = '[imagem anexada]'; el.appendChild(lost); }
    }
    el.appendChild(document.createTextNode(msg.text || ''));
    return el;
  }

  function onTool(msg) {
    if (msg.name === 'update_tasks') return; // aparece na lista de tarefas, não como cartão
    endSegment();
    const el = document.createElement('div');
    el.className = 'tool running';
    // Cabeçalho (bolinha de estado, o que é, resumo) e, embaixo, a caixa cinza com o que entrou (IN)
    // e o que voltou (OUT). Comando de shell mostra a linha inteira na caixa; os outros, só a saída.
    el.innerHTML = '<button class="tool-head"><span class="tool-dot"></span><span class="tool-kind"></span><span class="tool-summary"></span><span class="tool-state">rodando</span></button>' +
      '<div class="tool-box"><div class="tool-row tool-in"><span class="tool-tag">IN</span><pre></pre></div>' +
      '<div class="tool-row tool-out"><span class="tool-tag">OUT</span><pre></pre></div></div>';
    el.querySelector('.tool-kind').textContent = TOOL_LABELS[msg.name] || msg.name;
    if (msg.name === 'run_command' || msg.name === 'type_in_terminal') {
      el.classList.add('shell');
      el.querySelector('.tool-in pre').textContent = msg.summary || '';
    }
    const isPath = /^(read|write|edit)_file$|_directory$/.test(msg.name);
    // Caminho comprido: corta no começo, para o nome do arquivo ficar à vista.
    el.querySelector('.tool-summary').classList.toggle('path', isPath);
    el.querySelector('.tool-summary').textContent = (isPath ? '‎' : '') + (msg.summary || '');
    el.querySelector('.tool-summary').title = msg.summary || '';
    el.querySelector('.tool-head').addEventListener('click', () => el.classList.toggle('open'));
    el.dataset.start = msg.start || Date.now();
    el.dataset.name = msg.name;
    tools.set(msg.id, el);
    add(el);
  }

  // Comando demorado: mostra há quanto tempo está rodando (ele vai até o fim; o botão vermelho interrompe).
  setInterval(() => {
    for (const el of tools.values()) {
      if (!el.classList.contains('running') || el.querySelector('.confirm')) continue;
      const seconds = Math.floor((Date.now() - Number(el.dataset.start)) / 1000);
      if (seconds < 5) continue;
      const m = Math.floor(seconds / 60);
      el.querySelector('.tool-state').textContent = 'rodando há ' + (m ? m + ' min ' : '') + (seconds % 60) + ' s';
    }
  }, 1000);

  function onToolEnd(msg) {
    const el = tools.get(msg.id);
    if (!el) return;
    el.querySelector('.confirm')?.remove();
    el.classList.remove('running', 'danger');
    el.classList.add(msg.ok ? 'ok' : 'fail');
    el.querySelector('.tool-state').textContent = msg.ok ? 'feito' : el.dataset.refused ? 'recusado' : 'erro';
    const output = (msg.output || '').replace(/\r/g, '').replace(/\s+$/, '');
    el.querySelector('.tool-out pre').textContent = output;
    el.classList.toggle('has-out', output.length > 0);
    if (!msg.ok && !el.dataset.refused) el.classList.add('open');
  }

  // A pessoa respondeu (aqui ou antes de trocar de aba): o pedido some do cartão.
  function onConfirmDone(msg) {
    const el = tools.get(msg.id);
    if (!el) return;
    el.querySelector('.confirm')?.remove();
    el.classList.remove('danger');
    el.querySelector('.tool-state').textContent = msg.allow ? 'rodando' : 'recusado';
    if (!msg.allow) el.dataset.refused = '1';
  }

  function onConfirm(msg) {
    const el = tools.get(msg.id);
    if (!el) { send({ type: 'confirmAnswer', id: msg.id, allow: false }); return; }
    el.querySelector('.tool-state').textContent = 'aguardando você';
    el.classList.toggle('danger', !!msg.destructive);
    const box = document.createElement('div');
    box.className = 'confirm';
    const text = document.createElement('p');
    text.className = 'confirm-text';
    text.textContent = msg.macro
      ? 'Salvar esta macro no RootDock? (fica em Comandos → Minhas macros; nada é executado agora)'
      : msg.web
      ? 'Abrir esta página da internet? (o pedido sai deste computador)'
      : (msg.destructive ? 'Regra da organização: este comando precisa do seu clique. Executar em ' : 'Executar em ') + msg.target + '?';
    box.appendChild(text);
    const pre = document.createElement('pre');
    pre.textContent = msg.detail ? msg.summary + '\n\n' + msg.detail : msg.summary;
    box.appendChild(pre);
    if (msg.reasons && msg.reasons.length) {
      const list = document.createElement('ul');
      for (const reason of msg.reasons) { const li = document.createElement('li'); li.textContent = reason; list.appendChild(li); }
      box.appendChild(list);
    }
    if (msg.production) {
      const prod = document.createElement('p');
      prod.className = 'confirm-text prod';
      prod.textContent = 'ATENÇÃO: este é um servidor de PRODUÇÃO.';
      box.appendChild(prod);
    }
    const buttons = document.createElement('div');
    buttons.className = 'confirm-buttons';
    const answer = (allow) => {
      box.remove();
      el.querySelector('.tool-state').textContent = allow ? 'rodando' : 'recusado';
      el.dataset.start = Date.now();
      if (!allow) el.dataset.refused = '1';
      send({ type: 'confirmAnswer', id: msg.id, allow });
      input.focus();
    };
    const yes = document.createElement('button');
    yes.className = 'btn ' + (msg.destructive ? 'danger' : 'primary');
    yes.textContent = msg.web ? 'Abrir' : msg.destructive ? 'Executar mesmo assim' : 'Executar';
    yes.addEventListener('click', () => answer(true));
    const no = document.createElement('button');
    no.className = 'btn';
    no.textContent = 'Recusar';
    no.addEventListener('click', () => answer(false));
    buttons.append(yes, no);
    box.appendChild(buttons);
    el.appendChild(box);
    const stick = nearBottom();
    if (stick) scrollDown(); else el.scrollIntoView({ block: 'nearest' });
    // O foco vai para Recusar no destrutivo: um Enter distraído não executa.
    if (!replaying) (msg.destructive ? no : yes).focus();
  }

  function clearChat() {
    segment = null;
    tools.clear();
    changeCards.clear();
    onTasks([]);
    messages.textContent = '';
    welcome.hidden = false;
  }

  // ---------- Imagens anexadas à mensagem ----------

  const MAX_IMAGES = 4;
  const attachments = [];      // { mime, data (base64), thumb (data URL) }
  const attachBox = $('attachments');

  function renderAttachments() {
    attachBox.textContent = '';
    attachBox.hidden = attachments.length === 0;
    attachments.forEach((a, index) => {
      const el = document.createElement('div');
      el.className = 'attachment';
      const img = document.createElement('img');
      img.src = a.thumb;
      img.alt = 'Imagem anexada';
      const remove = document.createElement('button');
      remove.textContent = '×';
      remove.title = 'Tirar esta imagem';
      remove.addEventListener('click', () => { attachments.splice(index, 1); renderAttachments(); input.focus(); });
      el.append(img, remove);
      attachBox.appendChild(el);
    });
  }

  // Reduz a imagem (lado maior até "side") e devolve em JPEG; fundo branco no lugar da transparência.
  function scaled(image, side, quality) {
    const ratio = Math.min(1, side / Math.max(image.naturalWidth, image.naturalHeight));
    const canvas = document.createElement('canvas');
    canvas.width = Math.max(1, Math.round(image.naturalWidth * ratio));
    canvas.height = Math.max(1, Math.round(image.naturalHeight * ratio));
    const context = canvas.getContext('2d');
    context.fillStyle = '#fff';
    context.fillRect(0, 0, canvas.width, canvas.height);
    context.drawImage(image, 0, 0, canvas.width, canvas.height);
    return canvas.toDataURL('image/jpeg', quality);
  }

  function attach(file) {
    if (!file || !/^image\/(png|jpeg|gif|webp|bmp)$/.test(file.type)) return;
    if (attachments.length >= MAX_IMAGES) { add(block('note', 'No máximo ' + MAX_IMAGES + ' imagens por mensagem.')); return; }
    const reader = new FileReader();
    reader.onload = () => {
      const image = new Image();
      image.onload = () => {
        if (attachments.length >= MAX_IMAGES) return;
        const full = scaled(image, 1568, 0.9);
        attachments.push({ mime: 'image/jpeg', data: full.slice(full.indexOf(',') + 1), thumb: scaled(image, 280, 0.75) });
        renderAttachments();
      };
      image.onerror = () => add(block('note', 'Não foi possível ler essa imagem.'));
      image.src = reader.result;
    };
    reader.readAsDataURL(file);
  }

  input.addEventListener('paste', (e) => {
    const files = [...(e.clipboardData?.items || [])].filter((i) => i.kind === 'file' && i.type.startsWith('image/')).map((i) => i.getAsFile());
    if (!files.length) return;
    e.preventDefault();
    files.forEach(attach);
  });
  const composer = $('composer');
  composer.addEventListener('dragover', (e) => { if ([...(e.dataTransfer?.types || [])].includes('Files')) { e.preventDefault(); composer.classList.add('dropping'); } });
  composer.addEventListener('dragleave', () => composer.classList.remove('dropping'));
  composer.addEventListener('drop', (e) => {
    composer.classList.remove('dropping');
    const files = [...(e.dataTransfer?.files || [])].filter((f) => f.type.startsWith('image/'));
    if (!files.length) return;
    e.preventDefault();
    files.forEach(attach);
  });
  // Arquivo solto fora da caixa de mensagem não pode fazer a página navegar para ele.
  document.addEventListener('dragover', (e) => e.preventDefault());
  document.addEventListener('drop', (e) => e.preventDefault());
  $('attach').addEventListener('click', () => $('attach-file').click());
  $('attach-file').addEventListener('change', (e) => { [...e.target.files].forEach(attach); e.target.value = ''; input.focus(); });

  // ---------- Envio ----------

  function autosize() {
    input.style.height = 'auto';
    input.style.height = Math.min(180, input.scrollHeight) + 'px';
  }

  function submit() {
    if (state.busy) { send({ type: 'stop' }); return; }
    const text = input.value.trim();
    if (!text && !attachments.length) return;
    input.value = '';
    autosize();
    send({ type: 'send', text, images: attachments.splice(0) });
    renderAttachments();
  }

  $('retry-btn').addEventListener('click', () => {
    if (state.busy) return;
    $('retry').hidden = true; // some na hora: um segundo clique não manda duas vezes
    send({ type: 'retry' });
  });

  input.addEventListener('input', autosize);
  input.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) { e.preventDefault(); if (!state.busy) submit(); }
    else if (e.key === 'Escape' && state.busy) { e.preventDefault(); send({ type: 'stop' }); }
  });
  sendBtn.addEventListener('click', submit);
  modeBox.addEventListener('change', () => send({ type: 'setMode', mode: modeBox.value }));
  $('show-terminal').addEventListener('click', () => send({ type: 'setShowInTerminal', on: !state.showInTerminal }));
  $('new-chat').addEventListener('click', () => send({ type: 'newChat' }));
  $('close').addEventListener('click', () => send({ type: 'close' }));
  for (const example of document.querySelectorAll('.example')) {
    example.addEventListener('click', () => { input.value = example.textContent; autosize(); input.focus(); });
  }

  // ---------- Histórico de conversas ----------

  const historyPanel = $('history');
  let historyItems = [];

  function ago(ms) {
    const minutes = Math.max(0, Math.floor((Date.now() - ms) / 60000));
    if (minutes < 1) return 'agora';
    if (minutes < 60) return minutes + ' min';
    const hours = Math.floor(minutes / 60);
    if (hours < 24) return hours + ' h';
    const days = Math.floor(hours / 24);
    if (days < 30) return days + ' d';
    return Math.floor(days / 30) + ' mes' + (days >= 60 ? 'es' : '');
  }

  function renderHistory() {
    const list = $('history-list');
    const filter = $('history-filter').value.trim().toLowerCase();
    list.textContent = '';
    const shown = historyItems.filter((i) => !filter || i.title.toLowerCase().includes(filter));
    if (!shown.length) {
      const empty = document.createElement('div');
      empty.className = 'history-empty';
      empty.textContent = historyItems.length ? 'Nenhuma conversa com esse texto.' : 'Ainda não há conversas guardadas deste servidor.';
      list.appendChild(empty);
      return;
    }
    for (const item of shown) {
      const row = document.createElement('div');
      row.tabIndex = 0;
      row.setAttribute('role', 'button');
      row.className = 'history-item' + (item.current ? ' current' : '');
      const title = document.createElement('span');
      title.className = 'history-title';
      title.textContent = item.title;
      title.title = item.title;
      const when = document.createElement('span');
      when.className = 'history-when';
      when.textContent = item.current ? 'aberta' : ago(item.updated);
      when.title = new Date(item.updated).toLocaleString('pt-BR');
      const remove = document.createElement('button');
      remove.className = 'history-delete';
      remove.title = 'Apagar esta conversa do histórico (vai para a lixeira na pasta de dados)';
      remove.setAttribute('aria-label', 'Apagar conversa');
      remove.innerHTML = '<svg viewBox="0 0 16 16"><path d="M3 4.5h10M6.5 4.5V3h3v1.5M4.5 4.5l.5 8.5h6l.5-8.5M6.8 7v3.5M9.2 7v3.5"/></svg>';
      remove.addEventListener('click', (e) => {
        e.stopPropagation();
        // Dois cliques: o primeiro só arma.
        if (!remove.classList.contains('sure')) {
          remove.classList.add('sure');
          remove.textContent = 'Apagar?';
          setTimeout(() => { if (remove.isConnected) renderHistory(); }, 4000);
          return;
        }
        send({ type: 'deleteHistory', id: item.id });
      });
      remove.addEventListener('keydown', (e) => e.stopPropagation());
      row.append(title, when, remove);
      const open = () => {
        closeHistory();
        if (!item.current) send({ type: 'openHistory', id: item.id });
        input.focus();
      };
      row.addEventListener('click', open);
      row.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); open(); } });
      list.appendChild(row);
    }
  }

  const historyButton = $('open-history');
  function closeHistory() {
    historyPanel.hidden = true;
    historyButton.classList.remove('active');
  }
  historyButton.addEventListener('click', () => {
    if (!historyPanel.hidden) { closeHistory(); input.focus(); return; }
    historyItems = [];
    $('history-filter').value = '';
    historyPanel.hidden = false;
    historyButton.classList.add('active');
    renderHistory();
    send({ type: 'history' });
    $('history-filter').focus();
  });
  // Clique fora ou Esc fecha, como um menu.
  document.addEventListener('mousedown', (e) => {
    if (!historyPanel.hidden && !historyPanel.contains(e.target) && !historyButton.contains(e.target)) closeHistory();
  }, true);
  window.addEventListener('blur', () => { if (!historyPanel.hidden) closeHistory(); });
  $('history-filter').addEventListener('input', renderHistory);
  historyPanel.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') { e.preventDefault(); closeHistory(); input.focus(); return; }
    // Setas andam pela lista a partir da busca.
    if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return;
    const rows = [...historyPanel.querySelectorAll('.history-item')];
    if (!rows.length) return;
    e.preventDefault();
    const at = rows.indexOf(document.activeElement);
    const next = e.key === 'ArrowDown' ? Math.min(rows.length - 1, at + 1) : at - 1;
    (next < 0 ? $('history-filter') : rows[next]).focus();
  });
  $('history-filter').addEventListener('keydown', (e) => {
    if (e.key === 'Enter') historyPanel.querySelector('.history-item')?.click();
  });

  // ---------- Configurações ----------

  const settings = $('settings');
  const openSettings = () => { settings.hidden = false; renderProviders(); send({ type: 'refreshClaude' }); send({ type: 'getMemory' }); };
  $('memory-save').addEventListener('click', () => {
    $('memory-state').className = 'status';
    $('memory-state').textContent = 'Salvando...';
    send({ type: 'saveMemory', text: $('memory-text').value });
  });
  $('memory-text').addEventListener('input', () => { $('memory-state').textContent = ''; });
  $('open-settings').addEventListener('click', openSettings);
  $('provider').addEventListener('click', openSettings);
  $('settings-close').addEventListener('click', () => { settings.hidden = true; input.focus(); });

  function button(label, onClick, className) {
    const b = document.createElement('button');
    b.className = 'btn' + (className ? ' ' + className : '');
    b.textContent = label;
    b.addEventListener('click', onClick);
    return b;
  }

  function field(labelText, inputEl, extra) {
    const wrap = document.createElement('div');
    wrap.className = 'field';
    const label = document.createElement('label');
    label.textContent = labelText;
    const row = document.createElement('div');
    row.className = 'field-row';
    row.appendChild(inputEl);
    if (extra) row.appendChild(extra);
    wrap.append(label, row);
    return wrap;
  }

  function renderProviders() {
    const host = $('providers');
    const focused = document.activeElement && document.activeElement.dataset ? document.activeElement.dataset.key : null;
    host.textContent = '';
    for (const p of state.providers) {
      const draft = drafts[p.id] || (drafts[p.id] = {});
      const card = document.createElement('div');
      card.className = 'provider' + (p.id === state.provider ? ' selected' : '');

      const head = document.createElement('label');
      head.className = 'provider-head';
      const radio = document.createElement('input');
      radio.type = 'radio';
      radio.name = 'provider';
      radio.checked = p.id === state.provider;
      radio.addEventListener('change', () => send({ type: 'setProvider', id: p.id }));
      const texts = document.createElement('div');
      const name = document.createElement('div');
      name.className = 'provider-name';
      name.textContent = p.label;
      const hint = document.createElement('div');
      hint.className = 'provider-hint';
      hint.textContent = p.hint;
      texts.append(name, hint);
      head.append(radio, texts);
      card.appendChild(head);

      const body = document.createElement('div');
      body.className = 'provider-body';

      if (p.id === 'claude-code') {
        const status = document.createElement('p');
        const c = state.claude;
        const actions = document.createElement('div');
        actions.className = 'buttons';
        if (!c) {
          status.className = 'status';
          status.textContent = 'Verificando o Claude Code...';
        } else if (!c.installed) {
          status.className = 'status bad';
          status.textContent = 'O Claude Code não está instalado nesta máquina.';
          actions.appendChild(button('Instalar o Claude Code', () => send({ type: 'claudeInstall' }), 'primary'));
        } else if (!c.loggedIn) {
          status.className = 'status warn';
          status.textContent = 'Claude Code instalado, mas sem login.';
          actions.appendChild(button('Entrar com a conta Claude', () => send({ type: 'claudeLogin' }), 'primary'));
        } else {
          status.className = 'status ok';
          status.textContent = 'Conectado' + (c.account ? ' como ' + c.account : '') + (c.plan ? ' (plano ' + c.plan + ')' : '') + '.';
          actions.appendChild(button('Trocar de conta', () => send({ type: 'claudeLogin' })));
        }
        actions.appendChild(button('Verificar de novo', () => send({ type: 'refreshClaude' })));
        body.append(status, actions);
        const space = document.createElement('div');
        space.style.height = '8px';
        body.appendChild(space);
      }

      if (p.custom) {
        const url = document.createElement('input');
        url.type = 'text';
        url.placeholder = 'https://api.exemplo.com/v1';
        url.value = draft.baseUrl ?? state.customBaseUrl ?? '';
        url.dataset.key = p.id + ':url';
        url.addEventListener('input', () => { draft.baseUrl = url.value; });
        body.appendChild(field('Endereço da API', url));
      }

      if (p.id !== 'claude-code') {
        if (p.hasKey && !draft.changingKey) {
          // Chave guardada: fica dito com todas as letras (o campo vazio parecia "não salvou").
          const saved = document.createElement('p');
          saved.className = 'status ok';
          saved.textContent = 'Chave salva neste computador. Não precisa digitar de novo.';
          const keyActions = document.createElement('div');
          keyActions.className = 'buttons';
          keyActions.appendChild(button('Trocar a chave', () => { draft.changingKey = true; renderProviders(); }));
          keyActions.appendChild(button('Apagar a chave', () => { delete modelLists[p.id]; delete requested[p.id]; send({ type: 'saveProvider', id: p.id, clearKey: true }); }));
          const wrap = document.createElement('div');
          wrap.className = 'field';
          const label = document.createElement('label');
          label.textContent = 'Chave de API';
          wrap.append(label, saved, keyActions);
          body.appendChild(wrap);
        } else {
          const key = document.createElement('input');
          key.type = 'password';
          key.autocomplete = 'off';
          key.placeholder = p.needsKey ? 'Cole a chave de API' : 'Chave de API (se o serviço pedir)';
          key.value = draft.key ?? '';
          key.dataset.key = p.id + ':key';
          key.addEventListener('input', () => { draft.key = key.value; });
          const saveKey = () => {
            if (!draft.key || !draft.key.trim()) return;
            delete draft.changingKey;
            delete modelLists[p.id];
            delete requested[p.id];
            save(p, draft);
          };
          key.addEventListener('keydown', (e) => { if (e.key === 'Enter') saveKey(); });
          body.appendChild(field('Chave de API', key, button('Salvar a chave', saveKey, 'primary')));
        }
      }

      // Modelo: lista pronta para escolher (carregada sozinha com a chave salva).
      const selected = p.id === state.provider;
      const canList = p.id === 'claude-code' || p.hasKey || (!p.needsKey && (!p.custom || (state.customBaseUrl || '').trim()));
      if (selected && canList && !modelLists[p.id] && !requested[p.id]) {
        requested[p.id] = true;
        loading[p.id] = true;
        send({ type: 'listModels', id: p.id });
      }

      const current = p.model || '';
      const items = modelLists[p.id] || [];
      if (draft.typingModel) {
        const model = document.createElement('input');
        model.type = 'text';
        model.placeholder = 'Nome exato do modelo';
        model.value = draft.model ?? current;
        model.dataset.key = p.id + ':model';
        model.addEventListener('input', () => { draft.model = model.value; });
        const apply = () => { delete draft.typingModel; save(p, draft); };
        model.addEventListener('keydown', (e) => { if (e.key === 'Enter') apply(); });
        body.appendChild(field('Modelo', model, button('Usar', apply, 'primary')));
      } else {
        const select = document.createElement('select');
        select.dataset.key = p.id + ':model';
        const option = (value, label) => { const o = document.createElement('option'); o.value = value; o.textContent = label; select.appendChild(o); return o; };
        if (loading[p.id]) {
          option(current, 'Carregando os modelos...');
          select.disabled = true;
        } else if (!canList) {
          option(current, current || (p.custom ? 'Informe o endereço da API para ver os modelos' : 'Salve a chave para ver os modelos'));
          select.disabled = true;
        } else {
          if (p.id === 'claude-code') option('', 'Padrão da conta');
          else if (p.defaultModel) option('', 'Padrão (' + p.defaultModel + ')');
          else if (!current) option('', 'Escolha um modelo');
          if (current && !items.includes(current)) option(current, current);
          for (const item of items) option(item, item);
          option('\u0000outro', 'Digitar outro nome...');
          select.value = current;
        }
        select.addEventListener('change', () => {
          if (select.value === '\u0000outro') { draft.typingModel = true; renderProviders(); return; }
          send({ type: 'saveProvider', id: p.id, model: select.value });
        });
        const reload = button('Atualizar', () => { loading[p.id] = true; requested[p.id] = true; delete draft.modelsError; renderProviders(); send({ type: 'listModels', id: p.id }); });
        reload.title = 'Buscar de novo a lista de modelos';
        body.appendChild(field('Modelo', select, canList ? reload : null));
      }
      if (draft.modelsError) {
        const err = document.createElement('p');
        err.className = 'status bad';
        err.textContent = draft.modelsError;
        body.appendChild(err);
      }

      const actions = document.createElement('div');
      actions.className = 'buttons';
      actions.appendChild(button('Concluir', () => { if (draft.baseUrl !== undefined) { delete modelLists[p.id]; delete requested[p.id]; save(p, draft); } settings.hidden = true; input.focus(); }, 'primary'));
      body.appendChild(actions);

      card.appendChild(body);
      host.appendChild(card);
    }

    if (focused) host.querySelector(`[data-key="${focused}"]`)?.focus();
  }

  function save(p, draft) {
    const msg = { type: 'saveProvider', id: p.id };
    if (draft.model !== undefined) msg.model = draft.model;
    if (draft.baseUrl !== undefined) msg.baseUrl = draft.baseUrl;
    delete draft.model;
    delete draft.baseUrl;
    if (draft.key) msg.key = draft.key;
    delete draft.key;        // a chave não fica na página depois de salva
    delete draft.modelsError;
    send(msg);
  }

  // ---------- Estado ----------

  // ---------- Sinal de atividade ----------
  // Enquanto a resposta está em andamento, a última linha da conversa diz o que está acontecendo:
  // pensando (com um verbo que vai trocando), executando uma ferramenta, ou esperando um clique.
  // O tempo corre junto, para ficar claro que não travou.

  const working = $('working');
  const THINKING = [
    'Pensando', 'Raciocinando', 'Analisando', 'Investigando', 'Matutando', 'Calculando', 'Masterizando',
    'Alucinando', 'Maquinando', 'Destrinchando', 'Farejando', 'Conferindo', 'Arquitetando',
    'Decifrando', 'Elucubrando', 'Processando', 'Ruminando', 'Tramando', 'Lapidando',
  ];
  const TOOL_VERBS = {
    run_command: 'Executando o comando',
    type_in_terminal: 'Digitando no terminal',
    read_terminal: 'Lendo o terminal',
    read_file: 'Lendo o arquivo',
    write_file: 'Gravando o arquivo',
    edit_file: 'Editando o arquivo',
    list_directory: 'Listando a pasta',
    search_files: 'Procurando no código',
    find_files: 'Procurando arquivos',
    remember: 'Anotando',
    web_search: 'Pesquisando na web',
    web_fetch: 'Abrindo a página',
    list_macros: 'Lendo as macros',
    save_macro: 'Salvando a macro',
  };
  const MARKS = ['·', '✢', '✳', '✶', '✻', '✽', '✻', '✶', '✳', '✢'];
  const calm = window.matchMedia('(prefers-reduced-motion: reduce)');
  let workingSince = 0;
  let workingTimer = 0;
  let workingTick = 0;
  let verb = THINKING[0];
  let verbAt = 0;

  function elapsed(ms) {
    const seconds = Math.max(0, Math.floor(ms / 1000));
    if (seconds < 60) return seconds + ' s';
    const minutes = Math.floor(seconds / 60);
    if (minutes < 60) return minutes + ' min ' + (seconds % 60) + ' s';
    return Math.floor(minutes / 60) + ' h ' + (minutes % 60) + ' min';
  }

  function nextVerb() {
    let pick = verb;
    while (pick === verb) pick = THINKING[Math.floor(Math.random() * THINKING.length)];
    verb = pick;
    verbAt = Date.now();
  }

  function updateWorking() {
    const now = Date.now();
    let text, waiting = false;
    if (messages.querySelector('.confirm')) {
      text = 'Aguardando a sua confirmação';
      waiting = true;
    } else {
      const running = [...tools.values()].filter((el) => el.classList.contains('running')).pop();
      if (running) {
        text = TOOL_VERBS[running.dataset.name] || 'Trabalhando';
      } else {
        if (now - verbAt > 3500) nextVerb(); // só troca enquanto pensa: verbo de ferramenta não pisca
        text = verb;
      }
    }

    const stick = nearBottom();
    if ($('working-verb').textContent !== text) $('working-verb').textContent = text;
    working.classList.toggle('waiting', waiting);
    $('working-dots').hidden = waiting;
    if (!waiting && !calm.matches) $('working-mark').textContent = MARKS[workingTick++ % MARKS.length];
    const info = waiting ? '' : '(' + elapsed(now - workingSince) + ' · Esc para parar)';
    if ($('working-info').textContent !== info) $('working-info').textContent = info;
    if (stick) scrollDown();
  }

  function setWorking(busy, since) {
    if (!busy) {
      if (workingTimer) { clearInterval(workingTimer); workingTimer = 0; }
      working.hidden = true;
      return;
    }
    // O relógio do aplicativo e o da página são o mesmo computador: "since" vale direto.
    workingSince = since > 0 ? since : workingSince || Date.now();
    if (workingTimer) return;
    nextVerb();
    const stick = nearBottom();
    working.hidden = false;
    updateWorking();
    if (stick) scrollDown();
    workingTimer = setInterval(updateWorking, 140);
  }

  function applyState(next) {
    state = next;
    const target = $('target');
    target.className = 'target' + (next.target ? (next.target.production ? ' prod' : '') : ' none');
    target.textContent = next.target
      ? next.target.label + ' · ' + next.target.name + (next.target.production ? ' · PRODUÇÃO' : '')
      : next.targetHint || '';
    target.title = target.textContent;

    $('show-terminal').setAttribute('aria-pressed', next.showInTerminal ? 'true' : 'false');
    modeBox.value = next.mode;
    modeBox.classList.toggle('auto', next.mode === 'auto');

    const current = next.providers.find((p) => p.id === next.provider);
    const model = current ? (current.model || current.defaultModel) : '';
    $('provider').textContent = current ? current.label.replace(/\s*\(.*\)$/, '') + (model ? ' · ' + model : '') : '';

    sendBtn.classList.toggle('stop', next.busy);
    setWorking(next.busy, next.busySince);
    const offer = !next.busy && !!next.canRetry;
    if ($('retry').hidden === offer) {
      const stick = nearBottom();
      $('retry').hidden = !offer;
      if (stick) scrollDown();
    }
    sendBtn.title = next.busy ? 'Parar (Esc)' : 'Enviar (Enter)';
    sendBtn.innerHTML = next.busy
      ? '<svg viewBox="0 0 16 16"><rect x="4.5" y="4.5" width="7" height="7" rx="1" fill="currentColor"/></svg>'
      : '<svg viewBox="0 0 16 16"><path d="M8 13V3M3.5 7.5L8 3l4.5 4.5"/></svg>';

    let warning = '';
    if (current && current.id === 'claude-code' && next.claude && !next.claude.installed) warning = 'O Claude Code não está instalado. Abra as configurações (botão de ajustes) para instalar ou escolher outro provedor.';
    else if (current && current.id === 'claude-code' && next.claude && !next.claude.loggedIn) warning = 'Falta entrar com a conta Claude. Abra as configurações (botão de ajustes).';
    else if (current && current.needsKey && !current.hasKey) warning = 'Falta a chave de API de ' + current.label + '. Abra as configurações (botão de ajustes).';
    banner.hidden = !warning;
    banner.textContent = warning;

    if (!settings.hidden) renderProviders();
  }

  function handle(msg) {
    switch (msg.type) {
      case 'replace':
        // Outra aba entrou em foco: a tela passa a mostrar a conversa dela, do começo.
        clearChat();
        closeHistory();
        replaying = true;
        try { for (const event of msg.events || []) handle(event); } finally { replaying = false; }
        if (segment) renderSegment();
        scrollDown();
        break;
      case 'confirmDone': onConfirmDone(msg); break;
      case 'memory':
        $('memory').hidden = !msg.available;
        $('memory-title').textContent = 'Anotações de ' + (msg.server || 'este servidor');
        if (document.activeElement !== $('memory-text') || $('memory-state').textContent === 'Salvando...') $('memory-text').value = msg.text || '';
        if ($('memory-state').textContent === 'Salvando...') { $('memory-state').className = 'status ok'; $('memory-state').textContent = 'Salvo.'; }
        break;
      case 'history':
        historyItems = msg.items || [];
        renderHistory();
        break;
      case 'state': applyState(msg); break;
      case 'user':
        endSegment();
        if (tasksDone) onTasks([]); // a lista concluída sai quando começa outro pedido
        add(userBlock(msg));
        if (!replaying) scrollDown();
        break;
      case 'tasks': onTasks(msg.items || []); break;
      case 'changes': onChanges(msg); break;
      case 'undone': onUndone(msg); break;
      case 'delta': onDelta(msg.text); break;
      case 'tool': onTool(msg); break;
      case 'toolEnd': onToolEnd(msg); break;
      case 'confirm': onConfirm(msg); break;
      case 'note': endSegment(); add(block('note', msg.text)); break;
      case 'error': endSegment(); add(block('error', msg.text)); resetUndoButtons(); break;
      case 'done':
        endSegment();
        for (const el of tools.values()) {
          if (el.classList.contains('running')) { el.classList.remove('running'); el.querySelector('.tool-state').textContent = 'interrompido'; el.querySelector('.confirm')?.remove(); }
        }
        break;
      case 'cleared': clearChat(); input.focus(); break;
      case 'models':
        loading[msg.provider] = false;
        if (msg.error) delete modelLists[msg.provider]; else modelLists[msg.provider] = msg.items || [];
        (drafts[msg.provider] || (drafts[msg.provider] = {})).modelsError = msg.error
          ? 'Não foi possível buscar os modelos: ' + msg.error
          : (msg.items && msg.items.length ? '' : 'O provedor não devolveu nenhum modelo. Use "Digitar outro nome...".');
        if (!settings.hidden) renderProviders();
        break;
      case 'appTheme':
        for (const [name, value] of Object.entries(msg.vars || {})) document.documentElement.style.setProperty(name, value);
        document.documentElement.style.colorScheme = msg.light ? 'light' : 'dark';
        break;
      case 'focus': input.focus(); break;
    }
  }

  window.chrome.webview.addEventListener('message', (e) => handle(e.data));

  // Links abrem no navegador padrão (o aplicativo intercepta a navegação).
  document.addEventListener('contextmenu', (e) => { if (!window.getSelection().toString() && e.target !== input) e.preventDefault(); });

  send({ type: 'ready' });
})();
