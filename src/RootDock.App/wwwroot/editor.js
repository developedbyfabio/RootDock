'use strict';
// ---------------------------------------------------------------- Editor de arquivos
// Editor do próprio RootDock (Monaco, o mesmo motor do VS Code) para arquivos do servidor.
// Cada arquivo abre numa aba, como os terminais: dá para arrastar para o lado de um terminal,
// maximizar, juntar em grade. O C# lê e grava pelo SFTP; aqui ficam a edição, o estado
// "não salvo", a comparação com a versão do servidor e os avisos.

const EDITOR_PREFS_KEY = 'rootdock.editor';
const editorPrefs = (() => {
  const defaults = { wrap: false, minimap: true };
  try {
    return { ...defaults, ...JSON.parse(localStorage.getItem(EDITOR_PREFS_KEY) || '{}') };
  } catch {
    return defaults;
  }
})();

function saveEditorPrefs() {
  try {
    localStorage.setItem(EDITOR_PREFS_KEY, JSON.stringify(editorPrefs));
  } catch {
    // sem armazenamento local: a preferência vale só até fechar o programa
  }
}

// ---------------------------------------------------------------- Carregar o Monaco (só na 1ª vez)
let monacoLoading = null;

function loadMonaco() {
  if (monacoLoading) return monacoLoading;
  monacoLoading = new Promise((resolve, reject) => {
    const script = document.createElement('script');
    script.src = 'vendor/monaco/vs/loader.js';
    script.onload = () => {
      // Sem pacote de idioma: o pt-br que vem com o Monaco 0.57 está vazio.
      window.require.config({ paths: { vs: 'vendor/monaco/vs' } });
      const timer = setTimeout(() => reject(new Error('o editor demorou demais para carregar')), 30000);
      window.require(['vs/editor/editor.main'], () => {
        clearTimeout(timer);
        setupMonaco(window.monaco);
        resolve(window.monaco);
      }, (err) => {
        clearTimeout(timer);
        reject(err);
      });
    };
    script.onerror = () => reject(new Error('arquivos do editor não encontrados'));
    document.head.appendChild(script);
  });
  monacoLoading.catch(() => {
    monacoLoading = null; // a próxima aba tenta de novo
  });
  return monacoLoading;
}

// Cores do editor no tema "Escuro" (as dos outros temas chegam do C#, em themes.js).
const EDITOR_THEME_DEFAULT = {
  light: false,
  background: '#11141a', foreground: '#d7dae0', surface: '#171b23', panel: '#13161d', border: '#262c38',
  selection: '#33466b', selectionInactive: '#283246', lineNumber: '#3b4252', lineNumberActive: '#8b93a5',
  accent: '#7aa2f7', guide: '#1f2430', guideActive: '#3b4252', inserted: '#9ece6a', removed: '#f7768e',
  syntax: {
    comment: '#5b6170', keyword: '#bb9af7', string: '#9ece6a', number: '#ff9e64', variable: '#7dcfff', type: '#7aa2f7',
    tag: '#f7768e', constant: '#ff9e64', delimiter: '#89ddff', warn: '#e0af68', error: '#f7768e', info: '#7aa2f7', dim: '#8b93a5',
  },
};

// Define (ou redefine) o tema "rootdock" do Monaco com as cores do tema da interface e aplica.
function applyEditorTheme(monaco) {
  const t = appTheme && appTheme.editor ? { light: !!appTheme.light, ...appTheme.editor } : EDITOR_THEME_DEFAULT;
  const s = t.syntax;
  const hex = (c) => c.replace('#', '');
  monaco.editor.defineTheme('rootdock', {
    base: t.light ? 'vs' : 'vs-dark',
    inherit: true,
    rules: [
      { token: 'comment', foreground: hex(s.comment), fontStyle: 'italic' },
      { token: 'keyword', foreground: hex(s.keyword) },
      { token: 'string', foreground: hex(s.string) },
      { token: 'number', foreground: hex(s.number) },
      { token: 'variable', foreground: hex(s.variable) },
      { token: 'type', foreground: hex(s.type) },
      { token: 'tag', foreground: hex(s.tag) },
      { token: 'constant', foreground: hex(s.constant) },
      { token: 'delimiter', foreground: hex(s.delimiter) },
      { token: 'log.error', foreground: hex(s.error), fontStyle: 'bold' },
      { token: 'log.warn', foreground: hex(s.warn), fontStyle: 'bold' },
      { token: 'log.info', foreground: hex(s.info) },
      { token: 'log.debug', foreground: hex(s.dim) },
      { token: 'log.date', foreground: hex(s.variable) },
      { token: 'log.ip', foreground: hex(s.keyword) },
    ],
    colors: {
      'editor.background': t.background,
      'editor.foreground': t.foreground,
      'editorLineNumber.foreground': t.lineNumber,
      'editorLineNumber.activeForeground': t.lineNumberActive,
      'editor.lineHighlightBackground': t.surface,
      'editor.lineHighlightBorder': '#00000000',
      'editor.selectionBackground': t.selection,
      'editor.inactiveSelectionBackground': t.selectionInactive,
      'editorCursor.foreground': t.accent,
      'editorIndentGuide.background1': t.guide,
      'editorIndentGuide.activeBackground1': t.guideActive,
      'editorWhitespace.foreground': t.guideActive,
      'editorWidget.background': t.surface,
      'editorWidget.border': t.border,
      'editorSuggestWidget.background': t.surface,
      'editorSuggestWidget.border': t.border,
      'editorStickyScroll.background': t.panel,
      'scrollbarSlider.background': t.lineNumberActive + '33',
      'scrollbarSlider.hoverBackground': t.lineNumberActive + '66',
      'minimap.background': t.background,
      'diffEditor.insertedTextBackground': t.inserted + '22',
      'diffEditor.removedTextBackground': t.removed + '22',
      'diffEditor.insertedLineBackground': t.inserted + '14',
      'diffEditor.removedLineBackground': t.removed + '14',
    },
  });
  monaco.editor.setTheme('rootdock');
  snapTokenCacheReset();
}

// As cores do print do código (snap.js) vêm do tema do editor: limpa o que foi lido do tema anterior.
function snapTokenCacheReset() {
  if (typeof snapTokenCache !== 'undefined') snapTokenCache.clear();
}

function setupMonaco(monaco) {
  applyEditorTheme(monaco);

  // Nginx: a primeira palavra de cada instrução é a diretiva; ";" e "{" encerram.
  monaco.languages.register({ id: 'nginx', aliases: ['Nginx'] });
  monaco.languages.setLanguageConfiguration('nginx', {
    comments: { lineComment: '#' },
    brackets: [['{', '}']],
    autoClosingPairs: [{ open: '{', close: '}' }, { open: '"', close: '"' }, { open: "'", close: "'" }],
  });
  monaco.languages.setMonarchTokensProvider('nginx', {
    blocks: ['server', 'location', 'upstream', 'http', 'events', 'stream', 'if', 'map', 'geo', 'types', 'limit_except', 'split_clients', 'mail'],
    tokenizer: {
      root: [
        [/\s+/, ''],
        [/#.*$/, 'comment'],
        [/[{}]/, 'delimiter'],
        [/[a-zA-Z_][\w.-]*/, { cases: { '@blocks': { token: 'keyword', next: '@args' }, '@default': { token: 'type', next: '@args' } } }],
      ],
      args: [
        [/;/, 'delimiter', '@pop'],
        [/\{/, 'delimiter', '@pop'],
        [/#.*$/, 'comment'],
        [/"([^"\\]|\\.)*"/, 'string'],
        [/'([^'\\]|\\.)*'/, 'string'],
        [/\$\{?\w+\}?/, 'variable'],
        [/\b(on|off)\b/, 'constant'],
        [/\b\d+(\.\d+)*[kKmMgGsShHdDwWyY]?\b/, 'number'],
        [/[~^=*]+/, 'delimiter'],
        [/[^\s;{"'$#]+/, ''],
        [/\s+/, ''],
      ],
    },
  });

  // Apache: diretiva no começo da linha, blocos <VirtualHost ...> e variáveis ${X} %{X}.
  monaco.languages.register({ id: 'apache', aliases: ['Apache'] });
  monaco.languages.setLanguageConfiguration('apache', { comments: { lineComment: '#' } });
  const eol = (token) => ({ cases: { '@eos': { token, next: '@pop' }, '@default': token } });
  monaco.languages.setMonarchTokensProvider('apache', {
    ignoreCase: true,
    tokenizer: {
      root: [
        [/\s+/, ''],
        [/#.*$/, 'comment'],
        [/<\/?[\w]+/, { token: 'tag', next: '@tagArgs' }],
        [/[a-zA-Z][\w]*/, { cases: { '@eos': 'type', '@default': { token: 'type', next: '@args' } } }],
      ],
      args: [
        [/"([^"\\]|\\.)*"/, eol('string')],
        [/[$%]\{[^}]*\}/, eol('variable')],
        [/\b(on|off|all|none|granted|denied)\b/, eol('constant')],
        [/\b\d+\b/, eol('number')],
        [/[^\s"$%]+/, eol('')],
        [/[$%]/, eol('')],
        [/\s+/, eol('')],
      ],
      tagArgs: [
        [/>/, 'tag', '@pop'],
        [/"([^"\\]|\\.)*"/, 'string'],
        [/[$%]\{[^}]*\}/, 'variable'],
        [/[^>"$%]+/, 'attribute.value'],
        [/[$%]/, ''],
      ],
    },
  });

  // Logs: níveis em destaque, datas, IPs e códigos HTTP.
  monaco.languages.register({ id: 'log', aliases: ['Log'] });
  monaco.languages.setMonarchTokensProvider('log', {
    tokenizer: {
      root: [
        [/\b(EMERG(ENCY)?|ALERT|CRIT(ICAL)?|FATAL|ERROR|ERR|FAIL(ED|URE)?|SEVERE|PANIC|Exception|Traceback)\b/, 'log.error'],
        [/\b(error|failed|failure|fatal|denied|refused|exception)\b/, 'log.error'],
        [/\b(WARN(ING)?|NOTICE)\b/, 'log.warn'],
        [/\b(warn(ing)?|timeout|timed out)\b/, 'log.warn'],
        [/\b(INFO|OK|SUCCESS)\b/, 'log.info'],
        [/\b(DEBUG|TRACE|VERBOSE)\b/, 'log.debug'],
        [/\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}([.,]\d+)?(Z|[+-]\d{2}:?\d{2})?/, 'log.date'],
        [/\d{1,2}\/\w{3}\/\d{4}:\d{2}:\d{2}:\d{2}( [+-]\d{4})?/, 'log.date'],
        [/\b(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) +\d{1,2} \d{2}:\d{2}:\d{2}\b/, 'log.date'],
        [/\b\d{1,3}(\.\d{1,3}){3}(:\d+)?\b/, 'log.ip'],
        [/"[^"]*"/, 'string'],
        [/\b[1-5]\d{2}\b/, 'number'],
      ],
    },
  });
}

// ---------------------------------------------------------------- Linguagem pelo nome/caminho
const EXT_LANG = {
  php: 'php', phtml: 'php', inc: 'php', py: 'python', pyw: 'python', js: 'javascript', mjs: 'javascript', cjs: 'javascript',
  jsx: 'javascript', ts: 'typescript', tsx: 'typescript', json: 'json', jsonc: 'json', lock: 'json', yml: 'yaml', yaml: 'yaml',
  sql: 'sql', sh: 'shell', bash: 'shell', zsh: 'shell', ksh: 'shell', ini: 'ini', cnf: 'ini', cfg: 'ini', toml: 'ini',
  properties: 'ini', env: 'ini', service: 'ini', timer: 'ini', socket: 'ini', mount: 'ini', xml: 'xml', xsd: 'xml',
  svg: 'xml', html: 'html', htm: 'html', vue: 'html', twig: 'twig', css: 'css', scss: 'scss', less: 'less', md: 'markdown',
  markdown: 'markdown', go: 'go', java: 'java', cs: 'csharp', c: 'c', h: 'c', cpp: 'cpp', hpp: 'cpp', rb: 'ruby',
  rs: 'rust', pl: 'perl', pm: 'perl', lua: 'lua', bat: 'bat', cmd: 'bat', ps1: 'powershell', psm1: 'powershell',
  kt: 'kotlin', swift: 'swift', r: 'r', graphql: 'graphql', tf: 'hcl', hcl: 'hcl', log: 'log', dockerfile: 'dockerfile',
  csv: 'plaintext', txt: 'plaintext', redis: 'redis',
};

const NAME_LANG = {
  dockerfile: 'dockerfile', '.htaccess': 'apache', '.bashrc': 'shell', '.bash_profile': 'shell', '.profile': 'shell',
  '.zshrc': 'shell', '.bash_aliases': 'shell', '.bash_logout': 'shell', crontab: 'shell', '.env': 'ini',
  'nginx.conf': 'nginx', 'httpd.conf': 'apache', 'apache2.conf': 'apache', '.gitignore': 'shell', 'authorized_keys': 'plaintext',
  'sshd_config': 'shell', 'ssh_config': 'shell', 'hosts': 'plaintext', 'fstab': 'shell', 'php.ini': 'ini', 'my.cnf': 'ini',
  'docker-compose.yml': 'yaml', 'composer.json': 'json', 'package.json': 'json', 'makefile': 'shell', 'syslog': 'log',
  'messages': 'log', 'auth.log': 'log', 'secure': 'log', 'kern.log': 'log', 'access_log': 'log', 'error_log': 'log',
};

const LANGUAGES = [
  ['plaintext', 'Texto'], ['shell', 'Shell (bash)'], ['php', 'PHP'], ['python', 'Python'], ['javascript', 'JavaScript'],
  ['typescript', 'TypeScript'], ['json', 'JSON'], ['yaml', 'YAML'], ['sql', 'SQL'], ['mysql', 'MySQL'], ['pgsql', 'PostgreSQL'],
  ['nginx', 'Nginx'], ['apache', 'Apache'], ['ini', 'INI / .env / systemd'], ['xml', 'XML'], ['html', 'HTML'], ['css', 'CSS'],
  ['scss', 'SCSS'], ['markdown', 'Markdown'], ['dockerfile', 'Dockerfile'], ['log', 'Log'], ['go', 'Go'], ['java', 'Java'],
  ['csharp', 'C#'], ['c', 'C'], ['cpp', 'C++'], ['ruby', 'Ruby'], ['perl', 'Perl'], ['rust', 'Rust'], ['lua', 'Lua'],
  ['powershell', 'PowerShell'], ['bat', 'Batch'], ['hcl', 'Terraform (HCL)'], ['twig', 'Twig'], ['redis', 'Redis'],
];

function languageFor(path, content) {
  const name = path.split('/').pop().toLowerCase();
  const lowerPath = path.toLowerCase();
  if (NAME_LANG[name]) return NAME_LANG[name];
  if (name.startsWith('.env')) return 'ini';
  if (name.startsWith('dockerfile')) return 'dockerfile';
  if (/^docker-compose.*\.ya?ml$/.test(name)) return 'yaml';
  if (/\.blade\.php$/.test(name)) return 'php';
  if (lowerPath.includes('/nginx/')) return 'nginx';
  if (/\/(apache2?|httpd)\//.test(lowerPath)) return 'apache';
  if (/^\/var\/log\//.test(lowerPath) || /\.log(\.\d+)?$/.test(name)) return 'log';

  const dot = name.lastIndexOf('.');
  const ext = dot > 0 ? name.slice(dot + 1) : '';
  if (ext === 'conf' || ext === 'cnf' || ext === '') {
    const head = content.slice(0, 20000);
    if (/^\s*(server|location|upstream|http|events)\s*[^\n{]*\{/m.test(head)) return 'nginx';
    if (/<VirtualHost|<Directory|^\s*(ServerName|DocumentRoot|LoadModule|RewriteEngine)\b/mi.test(head)) return 'apache';
    const shebang = /^#!.*\b(python3?|php|node|perl|ruby|bash|sh|zsh)\b/.exec(head);
    if (shebang) {
      return { python: 'python', python3: 'python', php: 'php', node: 'javascript', perl: 'perl', ruby: 'ruby' }[shebang[1]] || 'shell';
    }
    if (/^\s*\[[^\]\n]+\]\s*$/m.test(head)) return 'ini';
    if (ext === 'conf' || ext === 'cnf') return 'shell'; // comentários "#" e textos destacados
  }
  return EXT_LANG[ext] || 'plaintext';
}

// ---------------------------------------------------------------- Ícones (mesmo traço dos outros)
const ICONS = {
  save: '<svg viewBox="0 0 16 16"><path d="M3 2.5h8l2.5 2.5v8.5a.5.5 0 0 1-.5.5H3a.5.5 0 0 1-.5-.5V3a.5.5 0 0 1 .5-.5z"/><path d="M5 2.5v3.5h5V2.5M5 13.5V9.5h6v4"/></svg>',
  reload: '<svg viewBox="0 0 16 16"><path d="M13 8a5 5 0 1 1-1.5-3.6"/><path d="M13 2.5v3h-3"/></svg>',
  compare: '<svg viewBox="0 0 16 16"><rect x="1.5" y="2.5" width="5.5" height="11" rx="1"/><rect x="9" y="2.5" width="5.5" height="11" rx="1"/><path d="M3 6h2.5M3 8.5h2.5M10.5 6h2.5M10.5 8.5h1.5"/></svg>',
  wrap: '<svg viewBox="0 0 16 16"><path d="M2 4h12M2 8h9.5a2 2 0 0 1 0 4H8"/><path d="M9.5 10.5L8 12l1.5 1.5M2 12h3.5"/></svg>',
  minimap: '<svg viewBox="0 0 16 16"><rect x="1.5" y="2.5" width="13" height="11" rx="1.5"/><path d="M10.5 2.5v11M12 5h1M12 7h1M12 9h1"/></svg>',
  find: '<svg viewBox="0 0 16 16"><circle cx="7" cy="7" r="4.5"/><line x1="10.3" y1="10.3" x2="14" y2="14"/></svg>',
  download: '<svg viewBox="0 0 16 16"><path d="M8 2.5v8M4.5 7.5L8 11l3.5-3.5M2.5 13.5h11"/></svg>',
  file: '<svg viewBox="0 0 16 16"><path d="M4 1.5h5l3.5 3.5v9a.5.5 0 0 1-.5.5H4a.5.5 0 0 1-.5-.5V2a.5.5 0 0 1 .5-.5z"/><path d="M9 1.5V5h3.5"/></svg>',
};

function toolButton(icon, label, onClick, cls) {
  const b = el('button', 'ed-btn' + (cls ? ' ' + cls : ''));
  b.innerHTML = ICONS[icon];
  b.title = label;
  b.setAttribute('aria-label', label);
  b.addEventListener('click', (e) => {
    e.stopPropagation();
    onClick();
  });
  return b;
}

// ---------------------------------------------------------------- Painel de editor
class EditorPane {
  constructor(info) {
    this.kind = 'editor';
    this.id = info.id;
    this.title = info.name;
    this.name = info.name;
    this.path = info.path;
    this.host = info.host;
    this.env = info.env || 'None';
    this.remoteTitle = `${info.host}:${info.path}`;
    this.state = 'editor';
    this.tab = null;
    this.dirty = false;
    this.saving = false;
    this.orphaned = false;
    this.encoding = info.encoding || 'UTF-8';
    this.language = languageFor(info.path, info.content || '');
    this.pendingSave = null; // resolve(ok) de quem espera o resultado do salvamento

    this.el = el('div', 'pane editor-pane env-' + this.env);
    this.el.dataset.id = this.id;
    Pane.prototype.buildHead.call(this);
    this.buildToolbar();
    this.banner = el('div', 'ed-banner');
    this.banner.hidden = true;
    this.body = el('div', 'ed-body');
    this.loading = el('div', 'ed-loading');
    this.loading.textContent = 'Abrindo o editor…';
    this.body.appendChild(this.loading);
    this.buildStatus();
    this.el.append(this.toolbar, this.banner, this.body, this.status);
    this.el.style.display = 'none';
    stage.appendChild(this.el);

    this.el.addEventListener('mousedown', () => setActivePane(this));
    this.el.addEventListener('focusin', () => {
      if (this.el.style.display !== 'none') setActivePane(this);
    });

    this.resizeObserver = new ResizeObserver(() => this.scheduleFit());
    this.resizeObserver.observe(this.body);
    this.renderHead();

    loadMonaco()
      .then((monaco) => this.mount(monaco, info.content || '', info.eol))
      .catch((err) => {
        this.loading.textContent = 'Não foi possível abrir o editor: ' + (err && err.message ? err.message : err);
        this.loading.classList.add('error');
      });
  }

  buildToolbar() {
    const bar = el('div', 'ed-toolbar');
    const crumbs = el('div', 'ed-crumbs');
    crumbs.title = `${this.host}:${this.path}
Clique para copiar o caminho`;
    crumbs.addEventListener('click', () => {
      send({ type: 'copy', text: this.path });
      this.renderStatus({ text: 'Caminho copiado' });
    });
    const icon = el('span', 'ed-file-icon');
    icon.innerHTML = ICONS.file;
    const hostEl = el('span', 'ed-host');
    hostEl.textContent = this.host;
    crumbs.append(icon, hostEl);
    if (this.env === 'Production') {
      const badge = el('span', 'ed-prod');
      badge.textContent = 'PRODUÇÃO';
      crumbs.appendChild(badge);
    }
    // Caminhos longos: primeira pasta, "…" e as três últimas partes (o caminho inteiro fica na dica).
    const all = this.path.split('/').filter(Boolean);
    const parts = all.length > 5 ? [all[0], '…', ...all.slice(-3)] : all;
    parts.forEach((part, i) => {
      const sep = el('span', 'ed-sep');
      sep.textContent = '/';
      const seg = el('span', i === parts.length - 1 ? 'ed-seg last' : part === '…' ? 'ed-seg more' : 'ed-seg');
      seg.textContent = part;
      crumbs.append(sep, seg);
    });

    this.saveBtn = el('button', 'ed-save');
    this.saveBtn.innerHTML = ICONS.save + '<span>Salvar</span>';
    this.saveBtn.title = 'Salvar no servidor (Ctrl+S)';
    this.saveBtn.addEventListener('click', (e) => {
      e.stopPropagation();
      this.save();
    });

    this.wrapBtn = toolButton('wrap', 'Quebra de linha automática (Alt+Z)', () => this.toggleWrap(), editorPrefs.wrap ? 'on' : '');
    this.minimapBtn = toolButton('minimap', 'Minimapa (visão geral do arquivo)', () => this.toggleMinimap(), editorPrefs.minimap ? 'on' : '');
    bar.append(
      crumbs,
      el('span', 'spacer'),
      toolButton('find', 'Buscar e substituir (Ctrl+F / Ctrl+H)', () => this.find()),
      toolButton('compare', 'Comparar com a versão do servidor', () => this.requestCompare()),
      toolButton('reload', 'Recarregar do servidor (descarta o que não foi salvo)', () => this.requestReload()),
      toolButton('download', 'Salvar uma cópia no computador', () => this.saveLocalCopy()),
      el('span', 'ed-divider'),
      this.wrapBtn,
      this.minimapBtn,
      el('span', 'ed-divider'),
      this.saveBtn,
    );
    this.toolbar = bar;
  }

  buildStatus() {
    const bar = el('div', 'ed-status');
    this.stateEl = el('span', 'ed-state');
    this.cursorEl = el('button', 'ed-item');
    this.cursorEl.title = 'Ir para a linha (Ctrl+G)';
    this.cursorEl.addEventListener('click', () => this.run('editor.action.gotoLine'));
    this.indentEl = el('button', 'ed-item');
    this.indentEl.title = 'Recuo: clique para alternar entre espaços e tabulação';
    this.indentEl.addEventListener('click', () => this.toggleIndent());
    this.encodingEl = el('span', 'ed-item static');
    this.encodingEl.title = 'Codificação do arquivo (mantida ao salvar)';
    this.eolEl = el('button', 'ed-item');
    this.eolEl.title = 'Quebra de linha: LF (Linux) ou CRLF (Windows). Clique para trocar.';
    this.eolEl.addEventListener('click', () => this.toggleEol());
    this.langSelect = el('select', 'ed-lang');
    this.langSelect.title = 'Linguagem (destaque de sintaxe)';
    for (const [id, label] of LANGUAGES) {
      const opt = document.createElement('option');
      opt.value = id;
      opt.textContent = label;
      this.langSelect.appendChild(opt);
    }
    if (!LANGUAGES.some(([id]) => id === this.language)) {
      const opt = document.createElement('option');
      opt.value = this.language;
      opt.textContent = this.language;
      this.langSelect.appendChild(opt);
    }
    this.langSelect.value = this.language;
    this.langSelect.addEventListener('change', () => {
      this.language = this.langSelect.value;
      if (this.model) window.monaco.editor.setModelLanguage(this.model, this.language);
      if (this.editor) this.editor.focus();
    });
    bar.append(this.stateEl, el('span', 'spacer'), this.cursorEl, this.indentEl, this.encodingEl, this.eolEl, this.langSelect);
    this.status = bar;
  }

  mount(monaco, content, eol) {
    if (this.disposed) return;
    this.loading.remove();
    this.model = monaco.editor.createModel(content, this.language);
    this.model.detectIndentation(true, 4);
    if (eol === 'CRLF') this.model.setEOL(monaco.editor.EndOfLineSequence.CRLF);
    this.savedVersion = this.model.getAlternativeVersionId();
    this.serverText = content;

    this.editorHost = el('div', 'ed-monaco');
    this.body.appendChild(this.editorHost);
    this.editor = monaco.editor.create(this.editorHost, {
      model: this.model,
      theme: 'rootdock',
      fontFamily: settings.fontFamily,
      fontSize: settings.fontSize,
      lineHeight: Math.round(settings.fontSize * 1.5),
      automaticLayout: false,
      minimap: { enabled: editorPrefs.minimap, renderCharacters: false, maxColumn: 100 },
      wordWrap: editorPrefs.wrap ? 'on' : 'off',
      renderWhitespace: 'selection',
      scrollBeyondLastLine: false,
      smoothScrolling: true,
      cursorBlinking: 'smooth',
      cursorSmoothCaretAnimation: 'on',
      bracketPairColorization: { enabled: true },
      guides: { bracketPairs: 'active', indentation: true },
      stickyScroll: { enabled: true },
      unicodeHighlight: { ambiguousCharacters: false },
      padding: { top: 8, bottom: 8 },
      fixedOverflowWidgets: true,
      contextmenu: true,
      mouseWheelZoom: false,
      renderLineHighlight: 'all',
      scrollbar: { verticalScrollbarSize: 12, horizontalScrollbarSize: 12 },
    });

    registerEditorKeys(monaco, this.editor);
    this.model.onDidChangeContent(() => this.updateDirty());
    this.editor.onDidChangeCursorPosition(() => this.renderStatus());
    this.model.onDidChangeOptions(() => this.renderStatus());
    this.editor.onDidFocusEditorText(() => {
      if (this.el.style.display !== 'none') setActivePane(this);
    });

    this.renderStatus();
    this.fitNow();
    if (activeTab && activeTab.activePane === this) this.editor.focus();
  }

  // ------------------------------------------------ Estado
  updateDirty() {
    const dirty = !!this.model && this.model.getAlternativeVersionId() !== this.savedVersion;
    if (dirty === this.dirty) return;
    this.dirty = dirty;
    send({ type: 'editorDirty', id: this.id, dirty });
    this.renderHead();
    if (this.tab) this.tab.renderHeader();
    this.renderStatus();
  }

  markSaved() {
    this.savedVersion = this.model.getAlternativeVersionId();
    this.updateDirty();
  }

  renderHead() {
    Pane.prototype.renderHead.call(this);
    if (!this.head) return;
    this.headDot.className = 'dot file' + (this.dirty ? ' dirty' : '');
    this.headTitle.textContent = this.title;
    this.headInfo.textContent = this.dirty ? 'não salvo' : this.host;
    this.head.title = `${this.host}:${this.path}`;
  }

  renderStatus(message) {
    if (message !== undefined) this.message = message;
    let text;
    let cls = '';
    if (this.orphaned) {
      text = 'Sessão encerrada: salve uma cópia no computador';
      cls = 'warn';
    } else if (this.saving) {
      text = 'Salvando…';
    } else if (this.message && this.message.error) {
      text = this.message.text;
      cls = 'error';
    } else if (this.dirty) {
      text = 'Não salvo · Ctrl+S salva no servidor';
      cls = 'dirty';
    } else {
      text = this.message && this.message.text ? this.message.text : 'Sem alterações';
      cls = 'ok';
    }
    this.stateEl.textContent = text;
    this.stateEl.className = 'ed-state ' + cls;
    this.saveBtn.disabled = this.saving || this.orphaned;
    this.saveBtn.classList.toggle('ready', this.dirty && !this.orphaned);

    if (!this.model || !this.editor) return;
    const pos = this.editor.getPosition();
    const sel = this.editor.getSelection();
    const selected = sel && !sel.isEmpty() ? this.model.getValueInRange(sel).length : 0;
    this.cursorEl.textContent = `Ln ${pos ? pos.lineNumber : 1}, Col ${pos ? pos.column : 1}` + (selected ? ` (${selected} selecionados)` : '');
    const opts = this.model.getOptions();
    this.indentEl.textContent = opts.insertSpaces ? `Espaços: ${opts.indentSize || opts.tabSize}` : `Tabulação: ${opts.tabSize}`;
    this.encodingEl.textContent = this.encoding;
    this.eolEl.textContent = this.model.getEOL() === '\r\n' ? 'CRLF' : 'LF';
  }

  displayTitle() {
    return this.title;
  }

  // ------------------------------------------------ Ações
  save(force = false) {
    if (!this.model || this.saving || this.orphaned) return Promise.resolve(false);
    this.saving = true;
    this.savingVersion = this.model.getAlternativeVersionId();
    this.hideBanner();
    this.renderStatus({ text: '' });
    send({ type: 'editorSave', id: this.id, content: this.model.getValue(), force });
    return new Promise((resolve) => {
      if (this.pendingSave) this.pendingSave(false);
      this.pendingSave = resolve;
    });
  }

  onSaved(msg) {
    this.saving = false;
    if (msg.ok) {
      // Só marca como salvo o que foi enviado (o usuário pode ter digitado durante o envio).
      this.savedVersion = this.savingVersion;
      this.serverText = null;
      this.updateDirty();
      this.renderStatus({ text: msg.message || 'Salvo' });
    } else {
      this.renderStatus({ text: msg.message || 'Não salvo', error: true });
    }
    this.resolveSave(!!msg.ok);
  }

  resolveSave(ok) {
    const done = this.pendingSave;
    this.pendingSave = null;
    if (done) done(ok);
  }

  onConflict(msg) {
    this.saving = false;
    this.renderStatus({ text: 'Não salvo: o arquivo mudou no servidor', error: true });
    this.showBanner(
      `O arquivo foi alterado no servidor (${msg.modified}) depois que você abriu. Salvar agora apagaria essas mudanças.`,
      [
        { text: 'Comparar', primary: true, run: () => this.requestCompare() },
        { text: 'Sobrescrever mesmo assim', danger: true, run: () => this.save(true) },
        { text: 'Recarregar do servidor', run: () => this.requestReload() },
      ],
    );
    this.resolveSave(false);
  }

  requestCompare() {
    if (this.orphaned) return;
    this.renderStatus({ text: 'Buscando a versão do servidor…' });
    send({ type: 'editorCompare', id: this.id });
  }

  async requestReload() {
    if (this.orphaned) return;
    if (this.dirty) {
      const ok = await confirmDialog({
        title: 'Recarregar do servidor?',
        text: `As alterações não salvas em ${this.name} serão descartadas.`,
        okText: 'Descartar e recarregar',
      });
      if (!ok) {
        this.focus();
        return;
      }
    }
    this.renderStatus({ text: 'Recarregando…' });
    send({ type: 'editorReload', id: this.id });
  }

  onReloaded(msg) {
    this.closeDiff();
    this.hideBanner();
    // pushEditOperations mantém o desfazer (Ctrl+Z volta ao texto anterior).
    this.model.pushEditOperations([], [{ range: this.model.getFullModelRange(), text: msg.content }], () => null);
    this.model.pushStackElement();
    this.markSaved();
    this.renderStatus({ text: 'Recarregado do servidor' });
    this.focus();
  }

  // Lado a lado: servidor (esquerda, só leitura) x o que está no editor (direita, editável).
  onCompare(msg) {
    const monaco = window.monaco;
    this.closeDiff();
    this.renderStatus({ text: '' });
    if (msg.content === this.model.getValue()) {
      this.showBanner('A versão do servidor é igual ao texto do editor.', [], 'info', 5000);
      return;
    }
    this.serverModel = monaco.editor.createModel(msg.content, this.language);
    this.editorHost.style.display = 'none';
    this.diffHost = el('div', 'ed-monaco');
    this.body.appendChild(this.diffHost);
    this.diff = monaco.editor.createDiffEditor(this.diffHost, {
      theme: 'rootdock',
      fontFamily: settings.fontFamily,
      fontSize: settings.fontSize,
      automaticLayout: false,
      renderSideBySide: true,
      originalEditable: false,
      readOnly: false,
      minimap: { enabled: false },
      scrollBeyondLastLine: false,
      renderOverviewRuler: true,
      ignoreTrimWhitespace: false,
    });
    this.diff.setModel({ original: this.serverModel, modified: this.model });
    this.showBanner(
      'Comparando: à esquerda a versão atual do servidor, à direita o seu texto (editável).',
      [
        { text: 'Salvar o meu (sobrescrever)', primary: true, run: () => this.save(true).then((ok) => ok && this.closeDiff()) },
        { text: 'Usar a do servidor', run: () => {
          this.closeDiff();
          send({ type: 'editorReload', id: this.id });
        } },
        { text: 'Fechar comparação', run: () => this.closeDiff() },
      ],
      'info',
    );
    this.fitNow();
  }

  closeDiff() {
    if (!this.diff) return;
    this.diff.dispose();
    this.diff = null;
    this.diffHost.remove();
    this.diffHost = null;
    if (this.serverModel) {
      this.serverModel.dispose();
      this.serverModel = null;
    }
    this.editorHost.style.display = '';
    this.hideBanner();
    this.fitNow();
    this.focus();
  }

  onOrphaned() {
    this.orphaned = true;
    this.closeDiff();
    this.showBanner(
      'A sessão deste arquivo foi fechada, então não dá mais para salvar no servidor. Salve uma cópia no computador para não perder o texto.',
      [{ text: 'Salvar cópia no computador', primary: true, run: () => this.saveLocalCopy() }],
      'warn',
    );
    this.renderStatus();
  }

  saveLocalCopy() {
    if (!this.model) return;
    send({ type: 'editorSaveLocal', id: this.id, name: this.name, content: this.model.getValue() });
  }

  find() {
    this.run('actions.find');
  }

  run(action) {
    const target = this.diff ? this.diff.getModifiedEditor() : this.editor;
    if (!target) return;
    target.focus();
    const a = target.getAction(action);
    if (a) a.run();
  }

  toggleWrap() {
    editorPrefs.wrap = !editorPrefs.wrap;
    saveEditorPrefs();
    for (const p of panes.values()) {
      if (p.kind !== 'editor') continue;
      p.wrapBtn.classList.toggle('on', editorPrefs.wrap);
      if (p.editor) p.editor.updateOptions({ wordWrap: editorPrefs.wrap ? 'on' : 'off' });
    }
  }

  toggleMinimap() {
    editorPrefs.minimap = !editorPrefs.minimap;
    saveEditorPrefs();
    for (const p of panes.values()) {
      if (p.kind !== 'editor') continue;
      p.minimapBtn.classList.toggle('on', editorPrefs.minimap);
      if (p.editor) p.editor.updateOptions({ minimap: { enabled: editorPrefs.minimap } });
    }
  }

  toggleIndent() {
    if (!this.model) return;
    const opts = this.model.getOptions();
    this.model.updateOptions({ insertSpaces: !opts.insertSpaces });
    this.renderStatus();
    this.focus();
  }

  toggleEol() {
    if (!this.model) return;
    const monaco = window.monaco;
    const crlf = this.model.getEOL() === '\r\n';
    this.model.pushEOL(crlf ? monaco.editor.EndOfLineSequence.LF : monaco.editor.EndOfLineSequence.CRLF);
    this.renderStatus();
    this.focus();
  }

  setFontSize(size) {
    const opts = { fontSize: size, lineHeight: Math.round(size * 1.5) };
    if (this.editor) this.editor.updateOptions(opts);
    if (this.diff) this.diff.updateOptions({ fontSize: size });
  }

  // ------------------------------------------------ Faixa de aviso (conflito, comparação...)
  showBanner(text, actions, tone = 'warn', autoHideMs = 0) {
    clearTimeout(this.bannerTimer);
    this.banner.replaceChildren();
    this.banner.className = 'ed-banner ' + tone;
    const p = el('span', 'text');
    p.textContent = text;
    this.banner.appendChild(p);
    for (const a of actions) {
      const b = el('button', a.primary ? 'primary' : a.danger ? 'danger' : '');
      b.textContent = a.text;
      b.addEventListener('click', (e) => {
        e.stopPropagation();
        a.run();
      });
      this.banner.appendChild(b);
    }
    this.banner.hidden = false;
    if (autoHideMs) this.bannerTimer = setTimeout(() => this.hideBanner(), autoHideMs);
    this.scheduleFit();
  }

  hideBanner() {
    if (this.banner.hidden) return;
    this.banner.hidden = true;
    this.scheduleFit();
  }

  // ------------------------------------------------ Fechar
  // true = pode fechar (salvou ou o usuário quis descartar).
  async confirmClose() {
    if (!this.dirty) return true;
    if (this.tab) activateTab(this.tab, this);
    const choice = await choiceDialog({
      title: `Salvar as alterações em ${this.name}?`,
      text: this.orphaned
        ? 'A sessão foi encerrada e o texto não pode mais ser salvo no servidor. Fechar descarta as alterações.'
        : 'Se não salvar, as alterações serão perdidas.',
      buttons: this.orphaned
        ? [{ id: 'cancel', text: 'Cancelar' }, { id: 'copy', text: 'Salvar cópia no computador' }, { id: 'discard', text: 'Fechar sem salvar', danger: true }]
        : [{ id: 'cancel', text: 'Cancelar' }, { id: 'discard', text: 'Não salvar' }, { id: 'save', text: 'Salvar', primary: true }],
    });
    if (choice === 'save') return this.save();
    if (choice === 'copy') {
      this.saveLocalCopy();
      return false;
    }
    if (choice !== 'discard') this.focus();
    return choice === 'discard';
  }

  async requestClose() {
    if (this.closing) return;
    this.closing = true;
    try {
      if (await this.confirmClose()) closePane(this, false);
    } finally {
      this.closing = false;
    }
  }

  // ------------------------------------------------ Integração com o palco (igual a Pane)
  scheduleFit() {
    if (this.fitQueued) return;
    this.fitQueued = true;
    requestAnimationFrame(() => {
      this.fitQueued = false;
      this.fitNow();
    });
  }

  fitNow() {
    if (!this.body.isConnected || this.body.clientWidth === 0 || this.body.clientHeight === 0) return;
    if (this.diff) this.diff.layout();
    else if (this.editor) this.editor.layout();
  }

  focus() {
    if (this.diff) this.diff.getModifiedEditor().focus();
    else if (this.editor) this.editor.focus();
    else this.el.focus();
  }

  setState() {
    // editores não têm estado de conexão
  }

  dispose() {
    this.disposed = true;
    clearTimeout(this.bannerTimer);
    this.resolveSave(false);
    send({ type: 'editorClosed', id: this.id });
    this.resizeObserver.disconnect();
    if (this.diff) this.diff.dispose();
    if (this.serverModel) this.serverModel.dispose();
    if (this.editor) this.editor.dispose();
    if (this.model) this.model.dispose();
    this.el.remove();
  }
}

// Atalhos do editor. O Monaco registra atalhos de forma global (o último editor criado
// recebe a tecla), então cada ação procura o editor que está com o foco.
function focusedEditorPane() {
  for (const p of panes.values()) {
    if (p.kind !== 'editor' || !p.editor) continue;
    if (p.editor.hasWidgetFocus() || (p.diff && p.diff.getModifiedEditor().hasWidgetFocus())) return p;
  }
  const active = activeTab && activeTab.activePane;
  return active && active.kind === 'editor' ? active : null;
}

function registerEditorKeys(monaco, editor) {
  const K = monaco.KeyMod;
  const C = monaco.KeyCode;
  const onPane = (fn) => () => {
    const p = focusedEditorPane();
    if (p) fn(p);
  };
  // Na comparação, Ctrl+S grava o texto da direita por cima da versão do servidor (é o que se vê).
  editor.addCommand(K.CtrlCmd | C.KeyS, onPane((p) => p.save(!!p.diff)));
  editor.addCommand(K.CtrlCmd | C.KeyW, onPane((p) => closePane(p)));
  editor.addCommand(K.CtrlCmd | K.Shift | C.KeyW, onPane((p) => closePane(p)));
  editor.addCommand(K.Alt | C.KeyZ, onPane((p) => p.toggleWrap()));
  editor.addCommand(K.CtrlCmd | C.Tab, () => cycleTab(1));
  editor.addCommand(K.CtrlCmd | K.Shift | C.Tab, () => cycleTab(-1));
  editor.addCommand(K.CtrlCmd | C.PageDown, () => cycleTab(1));
  editor.addCommand(K.CtrlCmd | C.PageUp, () => cycleTab(-1));
  editor.addCommand(K.CtrlCmd | C.Equal, () => setFontSize(settings.fontSize + 1));
  editor.addCommand(K.CtrlCmd | C.Minus, () => setFontSize(settings.fontSize - 1));
  editor.addCommand(K.CtrlCmd | C.Digit0, () => setFontSize(DEFAULT_FONT_SIZE));
  editor.addCommand(K.CtrlCmd | K.Shift | C.KeyT, () => send({ type: 'newLocal' }));
  // Print do código: no menu do botão direito do editor e em Ctrl+Shift+S.
  editor.addAction({
    id: 'rootdock.snap',
    label: 'Print do código (imagem)...',
    keybindings: [K.CtrlCmd | K.Shift | C.KeyS],
    contextMenuGroupId: '9_cutcopypaste',
    contextMenuOrder: 9,
    run: () => {
      const p = [...panes.values()].find((x) => x.editor === editor) || focusedEditorPane();
      if (p && typeof openSnap === 'function') openSnap(p);
    },
  });
}

// ---------------------------------------------------------------- Escolha com 3 botões
function choiceDialog({ title, text, buttons }) {
  return new Promise((resolve) => {
    const box = $('modal');
    const row = box.querySelector('.modal-buttons');
    const original = [...row.children];
    $('modal-title').textContent = title;
    $('modal-text').textContent = text;
    $('modal-preview').textContent = '';
    box.classList.add('neutral');
    const made = buttons.map((b) => {
      const button = el('button', b.primary ? 'primary' : b.danger ? 'danger' : '');
      button.textContent = b.text;
      button.addEventListener('click', () => done(b.id));
      return button;
    });
    row.replaceChildren(...made);
    box.hidden = false;
    (made.find((b) => b.classList.contains('primary')) || made[0]).focus();
    function done(value) {
      box.hidden = true;
      box.classList.remove('neutral');
      row.replaceChildren(...original);
      document.removeEventListener('keydown', onKey, true);
      resolve(value);
    }
    function onKey(e) {
      if (e.key === 'Escape') {
        e.preventDefault();
        done('cancel');
      }
    }
    document.addEventListener('keydown', onKey, true);
  });
}

// ---------------------------------------------------------------- Abrir e mensagens do C#
function openEditor(info) {
  const existing = panes.get(info.id);
  if (existing) {
    setActivePane(existing);
    existing.focus();
    return;
  }
  const pane = new EditorPane(info);
  panes.set(pane.id, pane);
  const tab = new Tab(pane);
  // Ao lado da aba do terminal ativo (o arquivo é dele), não no fim da barra.
  if (activeTab && activeTab.header.nextSibling) {
    $('tabs').insertBefore(tab.header, activeTab.header.nextSibling);
  }
  tabs.push(tab);
  syncTabOrder();
  activateTab(tab, pane);
  updateEmptyState();
}

function editorFor(msg) {
  const pane = panes.get(msg.id);
  return pane && pane.kind === 'editor' ? pane : null;
}

// true quando a mensagem é do editor.
function handleEditorMessage(msg) {
  switch (msg.type) {
    case 'openEditor':
      openEditor(msg);
      return true;
    case 'editorSaved': {
      const pane = editorFor(msg);
      if (pane) pane.onSaved(msg);
      return true;
    }
    case 'editorConflict': {
      const pane = editorFor(msg);
      if (pane) pane.onConflict(msg);
      return true;
    }
    case 'editorReloaded': {
      const pane = editorFor(msg);
      if (pane) pane.onReloaded(msg);
      return true;
    }
    case 'editorCompare': {
      const pane = editorFor(msg);
      if (pane) pane.onCompare(msg);
      return true;
    }
    case 'editorOrphaned': {
      const pane = editorFor(msg);
      if (pane) pane.onOrphaned();
      return true;
    }
    default:
      return false;
  }
}
