'use strict';
// ---------------------------------------------------------------- Proteção de tela
// Terminal parado por alguns minutos (Aparência do terminal → Proteção de tela): uma cena aparece sobre ele e
// usa o próprio texto do terminal. Bichinhos andam por cima das linhas e comem letras, a chuva digital acende
// o texto, a TV de tubo mostra o terminal com defeito. O texto e as cores vêm do buffer do xterm; o terminal de
// verdade não muda (letra "comida" é só uma máscara no desenho). Cada terminal conta o próprio tempo: tecla,
// mouse ou rolagem nele zeram a contagem e tiram a animação, e a tecla que acorda não vai para o servidor (nada
// de Enter sem querer). Escondido (outra aba), o terminal não conta tempo.
// Cenas do protótipo screen/RootDock.Screensavers (web/turma.html).
const Screensaver = (() => {
const REDUCE = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
const TS = REDUCE ? 0.4 : 1;
const TAU = Math.PI * 2;
const PK = '#0d0b14';
const MONO = '"Cascadia Mono", Consolas, monospace';

const R = (a, b) => a + Math.random() * (b - a);
const C = (v, a, b) => Math.max(a, Math.min(b, v));
const pick = a => a[Math.random() * a.length | 0];
const approach = (cur, tgt, spd, dt) => { const d = tgt - cur, s = spd * dt; return Math.abs(d) <= s ? tgt : cur + Math.sign(d) * s; };
const lerp = (a, b, t) => a + (b - a) * t;
const ease = t => t < .5 ? 2 * t * t : 1 - Math.pow(-2 * t + 2, 2) / 2;
const mkCanvas = (w, h) => {
  w = Math.max(1, Math.ceil(w)); h = Math.max(1, Math.ceil(h));
  if (typeof OffscreenCanvas !== 'undefined') return new OffscreenCanvas(w, h);
  const c = document.createElement('canvas'); c.width = w; c.height = h; return c;
};

/* Fonte bitmap 3×5 (balões, relógio da chuva, "sem sinal"). */
const FONT = {
  A: '010101111101101', B: '110101110101110', C: '011100100100011', D: '110101101101110', E: '111100110100111', F: '111100110100100', G: '011100101101011', H: '101101111101101', I: '111010010010111', J: '001001001101010', K: '101101110101101', L: '100100100100111', M: '101111111101101', N: '110101101101101', O: '010101101101010', P: '110101110100100', Q: '010101101110011', R: '110101110101101', S: '011100010001110', T: '111010010010010', U: '101101101101111', V: '101101101101010', W: '101101111111101', X: '101101010101101', Y: '101101010010010', Z: '111001010100111',
  0: '111101101101111', 1: '010110010010111', 2: '110001010100111', 3: '110001010001110', 4: '101101111001001', 5: '111100110001110', 6: '011100111101111', 7: '111001010010010', 8: '111101111101111', 9: '111101111001110',
  '.': '000000000000010', '-': '000000111000000', ':': '000010000010000', '!': '010010010000010', '?': '110001010000010', ' ': '000000000000000'
};
const normTxt = s => String(s).normalize('NFD').replace(/[̀-ͯ]/g, '').toUpperCase();
const pxW = (s, sc = 1) => (normTxt(s).length * 4 - 1) * sc;
function pxText(p, str, x, y, col, sh, sc = 1) {
  str = normTxt(str); x = Math.round(x); y = Math.round(y);
  const pass = (c, ox) => { p.fillStyle = c; let cx = x; for (const ch of str) { const gl = FONT[ch]; if (gl) for (let i = 0; i < 15; i++) if (gl[i] === '1') p.fillRect(cx + (i % 3) * sc + ox, y + ((i / 3) | 0) * sc + ox, sc, sc); cx += 4 * sc; } };
  if (sh) pass(sh, sc); pass(col, 0);
}

/* ============ O TERMINAL DE VERDADE ============ */
// Cores 0–255 do xterm: as 16 do tema, o cubo 6×6×6 e os 24 cinzas.
function palette(theme) {
  const names = ['black', 'red', 'green', 'yellow', 'blue', 'magenta', 'cyan', 'white'];
  const p = [...names, ...names.map(n => 'bright' + n[0].toUpperCase() + n.slice(1))].map(n => theme[n] || '#888888');
  const hex = v => v.toString(16).padStart(2, '0'), L = [0, 95, 135, 175, 215, 255];
  for (let i = 0; i < 216; i++) p.push('#' + hex(L[(i / 36) | 0]) + hex(L[((i / 6) | 0) % 6]) + hex(L[i % 6]));
  for (let i = 0; i < 24; i++) { const v = hex(8 + i * 10); p.push('#' + v + v + v); }
  return p;
}
let palTheme = null, palCache = null;
const themePalette = () => { if (settings.theme !== palTheme) { palTheme = settings.theme; palCache = palette(palTheme || {}); } return palCache; };
const rgbHex = n => '#' + (n & 0xffffff).toString(16).padStart(6, '0');
const measure = mkCanvas(1, 1).getContext('2d');

// O que está visível no terminal agora: caractere, cor e negrito de cada célula, e as medidas da grade.
function readTerm(pane) {
  const term = pane.term, buf = term.buffer.active, theme = settings.theme || {};
  const screen = pane.termEl.querySelector('.xterm-screen');
  const box = pane.termEl.getBoundingClientRect(), sr = screen ? screen.getBoundingClientRect() : box;
  const cols = term.cols || 80, nrows = term.rows || 24;
  const cw = sr.width / cols || 8, lh = sr.height / nrows || 18;
  const fs = settings.fontSize || 14, font = `${fs}px ${settings.fontFamily || MONO}`;
  measure.font = font;
  const m = measure.measureText('H'), fA = m.fontBoundingBoxAscent || fs * .8, fD = m.fontBoundingBoxDescent || fs * .25, capA = m.actualBoundingBoxAscent || fs * .7;
  const base0 = (lh - (fA + fD)) / 2 + fA; // linha de base dentro da célula (o xterm centra o texto na altura da linha)
  const P = themePalette(), FG = theme.foreground || '#d0d0d0', BG = theme.background || '#000000';
  const ch = [], fg = [], bg = [], bold = [], cell = buf.getNullCell ? buf.getNullCell() : undefined;
  for (let i = 0; i < nrows; i++) {
    const line = buf.getLine(buf.viewportY + i), c1 = new Array(cols).fill(' '), f1 = new Array(cols).fill(FG), b1 = new Array(cols).fill(null), w1 = new Uint8Array(cols);
    if (line) for (let c = 0; c < cols; c++) {
      const k = line.getCell(c, cell); if (!k) break;
      let f = FG, b = null;
      if (k.isFgRGB()) f = rgbHex(k.getFgColor());
      else if (k.isFgPalette()) { const n = k.getFgColor(); f = P[k.isBold() && n < 8 ? n + 8 : n] || FG; }
      if (k.isBgRGB()) b = rgbHex(k.getBgColor()); else if (k.isBgPalette()) b = P[k.getBgColor()] || null;
      if (k.isInverse()) { const t = b || BG; b = f; f = t; }
      c1[c] = k.isInvisible() ? ' ' : (k.getWidth() === 0 ? '' : (k.getChars() || ' '));
      f1[c] = f; b1[c] = b; w1[c] = k.isBold() ? 1 : 0;
    }
    ch.push(c1); fg.push(f1); bg.push(b1); bold.push(w1);
  }
  const cr = buf.baseY + buf.cursorY - buf.viewportY;
  return {
    w: pane.termEl.clientWidth, h: pane.termEl.clientHeight, cols, nrows, cw, lh, fs, font, base0, capTop: base0 - capA,
    x0: sr.left - box.left, y0: sr.top - box.top, ch, fg, bg, bold, FG, BG, cursorColor: theme.cursor || FG,
    cursor: cr >= 0 && cr < nrows ? { r: cr, c: buf.cursorX } : null,
  };
}

// Assinatura barata do que está na tela, para notar saída nova ou rolagem enquanto a cena roda.
function termSig(pane) {
  const term = pane.term, buf = term.buffer.active;
  let s = `${buf.type}:${buf.viewportY}:${buf.cursorX},${buf.cursorY}\n`;
  for (let i = 0; i < term.rows; i++) { const l = buf.getLine(buf.viewportY + i); s += (l ? l.translateToString(true) : '') + '\n'; }
  return s;
}

// Redesenha o terminal inteiro (fundo, cores por célula, cursor). Usado pela TV de tubo.
function paintTerm(g, T, cursorOn) {
  g.fillStyle = T.BG; g.fillRect(0, 0, T.w, T.h);
  g.textBaseline = 'alphabetic'; g.textAlign = 'left';
  let curFont = '';
  for (let r = 0; r < T.nrows; r++) {
    const y = T.y0 + r * T.lh, chs = T.ch[r], fg = T.fg[r], bg = T.bg[r], bold = T.bold[r];
    for (let c = 0; c < T.cols;) { const b = bg[c]; let e = c + 1; while (e < T.cols && bg[e] === b) e++; if (b) { g.fillStyle = b; g.fillRect(T.x0 + c * T.cw, y, (e - c) * T.cw + .5, T.lh + .5); } c = e; }
    for (let c = 0; c < T.cols;) {
      if (chs[c] === ' ' || chs[c] === '') { c++; continue; }
      const f = fg[c], bd = bold[c]; let e = c + 1, s = chs[c];
      while (e < T.cols && fg[e] === f && bold[e] === bd && chs[e] !== ' ') s += chs[e++];
      const font = (bd ? 'bold ' : '') + T.font; if (font !== curFont) { g.font = font; curFont = font; }
      g.fillStyle = f; g.fillText(s, T.x0 + c * T.cw, y + T.base0);
      c = e;
    }
  }
  const k = T.cursor;
  if (cursorOn && k) {
    const x = T.x0 + k.c * T.cw, y = T.y0 + k.r * T.lh, style = settings.cursorStyle || 'block';
    g.fillStyle = T.cursorColor;
    if (style === 'bar') g.fillRect(x, y, Math.max(1, T.fs * .12), T.lh);
    else if (style === 'underline') g.fillRect(x, y + T.lh - 2, T.cw, 2);
    else { g.fillRect(x, y, T.cw, T.lh); const s = T.ch[k.r][k.c]; if (s && s !== ' ') { g.font = T.font; g.fillStyle = T.BG; g.fillText(s, x, y + T.base0); } }
  }
}

/* Cena sobre o texto do terminal: as linhas viram chão, parede e comida. */
function termScene(def) {
  return Object.assign({
    init(w, h, dpr = 1) {
      Object.assign(this, { w, h, dpr, t: 0, holes: new Map(), jig: new Map(), parts: [], syncT: .5 });
      this.read();
      this.setup();
    },
    read() {
      const T = this.T = readTerm(this.pane);
      Object.assign(this, { fs: T.fs, lh: T.lh, cw: T.cw, x0: T.x0, y0: T.y0, rows: T.ch, sig: termSig(this.pane) });
    },
    // Meio em meio segundo: chegou saída nova ou a tela rolou? Relê; letras mexidas em linhas que mudaram voltam ao normal.
    sync(dt) {
      this.syncT -= dt; if (this.syncT > 0) return; this.syncT = .5;
      const sig = termSig(this.pane); if (sig === this.sig) return;
      const old = this.rows; this.read();
      const same = r => old[r] && this.rows[r] && old[r].join('') === this.rows[r].join('');
      for (const map of [this.holes, this.jig]) for (const k of [...map.keys()]) if (!same(+k.split(',')[0])) map.delete(k);
      if (this.changed) this.changed();
    },
    ch(r, c) { const s = this.rows[r]; return s ? s[c] : undefined; },
    solid(r, c) { const ch = this.ch(r, c); if (!ch || ch === ' ') return false; const ho = this.holes.get(r + ',' + c); return !ho || ho.show; },
    colAt(x) { return Math.floor((x - this.x0) / this.cw); },
    cx(c) { return this.x0 + c * this.cw; },
    top(r) { return this.y0 + r * this.lh + this.T.capTop; }, // topo das maiúsculas: onde os bichinhos pisam
    mid(r) { return this.y0 + (r + .5) * this.lh; },
    lineEnd(r) { const s = this.rows[r]; if (!s) return 0; let e = s.length; while (e > 0 && (s[e - 1] === ' ' || s[e - 1] === '')) e--; return e; },
    rowColor(r, c = 0) { const f = this.T.fg[r]; return (f && f[c]) || this.T.FG; },
    floorY() { return this.h - 8; },
    cursor() { const k = this.T.cursor; return k ? { r: k.r, x: this.cx(k.c), y: this.y0 + k.r * this.lh } : { r: -9, x: -999, y: -999 }; },
    makeHole(r, c, heal) { this.holes.set(r + ',' + c, { age: 0, heal, show: false, flash: 0 }); },
    restore(r, c) { const ho = this.holes.get(r + ',' + c); if (ho) { ho.show = true; ho.flash = .35; ho.heal = 0; } },
    burst(x, y, n, col, sp = 60) { for (let i = 0; i < n; i++) this.parts.push({ x, y, vx: R(-sp, sp) * .5, vy: R(-sp, -sp * .2), t: 0, life: R(.4, .9), col, g: 400 }); },
    jiggle(r, c, dy, t = .12) { this.jig.set(r + ',' + c, { dy, t }); },
    frame(g, dt) {
      this.t += dt;
      this.sync(dt);
      for (const [k, ho] of this.holes) { ho.age += dt; if (ho.heal && !ho.show && ho.age > ho.heal) { ho.show = true; ho.flash = .35; } if (ho.show) { ho.flash -= dt; if (ho.flash <= 0) this.holes.delete(k); } }
      for (const [k, v] of this.jig) { if (v.t !== undefined) { v.t -= dt; if (v.t <= 0) this.jig.delete(k); } }
      this.update(dt);
      for (const q of this.parts) { q.t += dt; q.vy += q.g * dt; q.x += q.vx * dt; q.y += q.vy * dt; }
      this.parts = this.parts.filter(q => q.t < q.life);
      this.under(g); this.render(g);
      for (const q of this.parts) { g.globalAlpha = Math.max(0, 1 - q.t / q.life); g.fillStyle = q.col; g.fillRect(Math.round(q.x), Math.round(q.y), Math.max(1, this.ps - 1), Math.max(1, this.ps - 1)); }
      g.globalAlpha = 1;
    },
    // Letras comidas, devolvidas ou tremendo: tampa a célula com o fundo e redesenha a letra por cima.
    under(g) {
      if (!this.holes.size && !this.jig.size) return;
      const T = this.T; g.textBaseline = 'alphabetic'; g.textAlign = 'left';
      for (const k of new Set([...this.holes.keys(), ...this.jig.keys()])) {
        const [r, c] = k.split(',').map(Number), x = this.cx(c), y = this.y0 + r * this.lh, ho = this.holes.get(k), j = this.jig.get(k);
        if (!T.ch[r]) continue;
        g.fillStyle = T.bg[r][c] || T.BG; g.fillRect(x, y, this.cw, this.lh);
        if (ho && !ho.show) continue;
        g.font = (T.bold[r][c] ? 'bold ' : '') + T.font; g.fillStyle = ho && ho.show ? '#ffffff' : this.rowColor(r, c);
        g.fillText(T.ch[r][c], x, y + T.base0 + (j ? j.dy : 0));
      }
    },
  }, def);
}

/* ============ SPRITES "ASSADOS": formas vetoriais viram pixel art com paleta fechada e contorno ============ */
function psp(g, c, x, y, ps, flip, flipY) {
  const w = c.width * ps, h = c.height * ps, X = Math.round(x - w / 2), Y = Math.round(y - h);
  g.save(); g.imageSmoothingEnabled = false;
  g.translate(flip ? X + w : X, flipY ? Y + h : Y); g.scale(flip ? -1 : 1, flipY ? -1 : 1); g.drawImage(c, 0, 0, w, h);
  g.restore();
}
const hexRgb = h => [parseInt(h.slice(1, 3), 16), parseInt(h.slice(3, 5), 16), parseInt(h.slice(5, 7), 16)];
function bake(w, h, pal, draw, outline = PK) {
  const c = mkCanvas(w, h), g = c.getContext('2d');
  g.save(); draw(g); g.restore();
  const im = g.getImageData(0, 0, w, h), d = im.data, P = pal.map(hexRgb), op = new Uint8Array(w * h);
  for (let i = 0; i < w * h; i++) {
    const j = i * 4; if (d[j + 3] < 120) { d[j + 3] = 0; continue; }
    op[i] = 1; let best = 0, bd = 1e9;
    for (let k = 0; k < P.length; k++) { const a = d[j] - P[k][0], b = d[j + 1] - P[k][1], e = d[j + 2] - P[k][2], dd = a * a + b * b + e * e; if (dd < bd) { bd = dd; best = k; } }
    d[j] = P[best][0]; d[j + 1] = P[best][1]; d[j + 2] = P[best][2]; d[j + 3] = 255;
  }
  if (outline) { const O = hexRgb(outline); for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) { const i = y * w + x; if (op[i]) continue; if ((x > 0 && op[i - 1]) || (x < w - 1 && op[i + 1]) || (y > 0 && op[i - w]) || (y < h - 1 && op[i + w])) { const j = i * 4; d[j] = O[0]; d[j + 1] = O[1]; d[j + 2] = O[2]; d[j + 3] = 255; } } }
  g.putImageData(im, 0, 0); return c;
}
const E = (g, x, y, rx, ry, col, rot = 0) => { g.fillStyle = col; g.beginPath(); g.ellipse(x, y, rx, ry, rot, 0, TAU); g.fill(); };
const LN = (g, pts, col, w) => { g.strokeStyle = col; g.lineWidth = w; g.lineCap = 'round'; g.lineJoin = 'round'; g.beginPath(); pts.forEach((p, i) => i ? g.lineTo(p[0], p[1]) : g.moveTo(p[0], p[1])); g.stroke(); };
const TRI = (g, a, b, c, col) => { g.fillStyle = col; g.beginPath(); g.moveTo(...a); g.lineTo(...b); g.lineTo(...c); g.closePath(); g.fill(); };
const flipAt = (sx0, sy0, len, a) => [sx0 - Math.sin(a) * len, sy0 + Math.cos(a) * len];

/* Pinguim-saltador (rockhopper): costas azul-noite, barriga branca, bico e pés laranja, crista amarela, olho vermelho. */
function penguinSprite(o) {
  const pal = ['#1f2340', '#3a4170', '#121528', '#f4f1e6', '#c9c6d8', '#e8833a', '#b4562a', '#f2c84b', '#c4433b'];
  return bake(28, 30, pal, g => {
    if (o.slide) { g.translate(14, 22); g.rotate(1.3); g.translate(-14, -22); }
    g.translate(14, 28); g.rotate(o.tilt || 0); g.translate(-14, -28);
    const st = o.step || 0;
    E(g, 12 + st, 27.2, 2.8, 1.3, '#e8833a'); E(g, 16 - st, 27.2, 2.8, 1.3, '#e8833a');
    const fr = flipAt(17.5, 13.5, 4.6, -(o.fr ?? .2)); E(g, fr[0], fr[1], 1.7, 5, '#121528', -(o.fr ?? .2));
    E(g, 13.5, 17.5, 7.4, 9.6, '#1f2340');
    E(g, 11, 12, 3.2, 3.6, '#3a4170');
    E(g, 15.2, 19, 5, 7.2, '#f4f1e6'); E(g, 16.2, 22.6, 3.4, 3.6, '#c9c6d8');
    LN(g, [[16, 10.8], [11.5, 8.2]], '#f2c84b', 1.3); LN(g, [[16.5, 9.8], [12.5, 6.2]], '#f2c84b', 1.3); LN(g, [[15.6, 11.6], [11.5, 10.5]], '#f2c84b', 1);
    if (o.eye === 'closed') LN(g, [[15.8, 12.4], [18, 12.4]], '#121528', 1); else { E(g, 17, 12.2, 1.3, 1.3, '#c4433b'); }
    const bo = o.beak || 0;
    TRI(g, [18.6, 12], [23, 13.4 - bo * .6], [18.6, 14.2], '#e8833a'); TRI(g, [18.6, 13.6], [22, 13.8 + bo * 1.4], [18.6, 14.6 + bo], '#b4562a');
    const fl = flipAt(10.2, 13.8, 4.8, o.fl ?? .25); E(g, fl[0], fl[1], 1.8, 5.2, '#121528', o.fl ?? .25);
  });
}
function chuteSprite() {
  return bake(30, 16, ['#e5604d', '#f4f1e6', '#b7b8c9', '#a83a2e'], g => {
    g.save(); g.beginPath(); g.ellipse(15, 8, 13, 7, 0, Math.PI, TAU); g.closePath(); g.clip();
    for (let i = 0; i < 6; i++) { g.fillStyle = i % 2 ? '#f4f1e6' : '#e5604d'; g.fillRect(2 + i * 4.4, 0, 4.4, 9); }
    g.restore(); E(g, 15, 8.4, 13, 1.2, '#a83a2e');
    LN(g, [[3, 8.5], [14, 15.5]], '#b7b8c9', .8); LN(g, [[27, 8.5], [16, 15.5]], '#b7b8c9', .8); LN(g, [[15, 8.5], [15, 15.5]], '#b7b8c9', .8);
  }, null);
}
/* Ovelha: lã em nuvem com sombreado, cara escura, pernas finas. */
function sheepSprite(o) {
  const pal = ['#f4f1e6', '#d6d3e2', '#ffffff', '#3a3448', '#2a2638', '#1b1a2e', '#e06f8b', '#f2c84b'];
  return bake(32, 26, pal, g => {
    const sl = o.sleep, s = o.step || 0, drop = sl ? 4 : 0, bob = o.bob || 0;
    if (!sl) [[10, s], [13, -s], [19, -s], [22, s]].forEach(([x, k]) => { const lift = Math.max(0, k) * .8; LN(g, [[x, 16 + bob], [x + k * .6, 23.4 - lift]], '#3a3448', 1.7); E(g, x + k * .6, 24 - lift, 1.2, .7, '#1b1a2e'); });
    g.translate(0, drop + bob);
    const C0 = [[10, 12, 5.2], [14.5, 9.5, 5.4], [19.5, 11, 5.2], [14, 14, 5], [8.5, 14.5, 4.2], [20.5, 14.5, 4.4], [5.6, 12, 2.4]];
    C0.forEach(([x, y, r]) => E(g, x + .4, y + .7, r, r, '#d6d3e2'));
    C0.forEach(([x, y, r]) => E(g, x - .5, y - .6, r - .8, r - .8, '#f4f1e6'));
    E(g, 12.5, 7.5, 2, 1.4, '#ffffff'); E(g, 17.5, 8, 1.6, 1.2, '#ffffff');
    const hx = o.head === 'down' ? 25.5 : 26, hy = o.head === 'down' ? 17 - drop * .5 : 11.5 + (sl ? 3 : 0);
    E(g, hx - 2.8, hy - 2.4, 2, 1.1, '#2a2638', -.6);
    E(g, hx, hy, 3.4, 4, '#3a3448', .35);
    E(g, hx - 1.2, hy - 3.6, 2.4, 2, '#f4f1e6');
    if (o.eye === 'closed' || sl) LN(g, [[hx + .2, hy - .6], [hx + 1.8, hy - .4]], '#1b1a2e', .9); else E(g, hx + 1.1, hy - .8, .9, .9, '#f4f1e6');
    if (o.mouth) E(g, hx + 2.2, hy + 2.4, 1.1, 1.2, '#e06f8b');
  });
}
/* Gato rajado cinza com peito branco e olhos verdes. */
function catSprite(o) {
  const pal = ['#8a8fa8', '#6a7090', '#4e5470', '#e8e6f0', '#e06f8b', '#9ad37a', '#0d0b14', '#b9bdd0'];
  return bake(32, 24, pal, g => {
    const pose = o.pose || 'walk', s = o.step || 0, ta = o.tail || 0;
    const ear = (hx, hy) => { TRI(g, [hx - 3.6, hy - 1.8], [hx - 2.4, hy - 6.4], [hx - .6, hy - 3], '#8a8fa8'); TRI(g, [hx + .2, hy - 3.2], [hx + 2.4, hy - 6.6], [hx + 3.6, hy - 1.6], '#8a8fa8'); TRI(g, [hx + .9, hy - 3.4], [hx + 2.3, hy - 5.4], [hx + 2.9, hy - 2.6], '#e06f8b'); };
    const face = (hx, hy, closed) => { E(g, hx, hy, 4.4, 3.9, '#8a8fa8'); E(g, hx + 1.3, hy + 1.4, 2.4, 1.7, '#e8e6f0'); LN(g, [[hx - 1.5, hy - 3.4], [hx - 1.2, hy - 2]], '#4e5470', .9); LN(g, [[hx, hy - 3.7], [hx, hy - 2.2]], '#4e5470', .9);
      if (closed) { LN(g, [[hx - .1, hy - .4], [hx + 1.4, hy]], '#0d0b14', .8); LN(g, [[hx + 2.2, hy - .2], [hx + 3.4, hy - .5]], '#0d0b14', .8); } else { E(g, hx + .6, hy - .5, .95, 1.15, '#9ad37a'); E(g, hx + 2.9, hy - .6, .8, 1.05, '#9ad37a'); g.fillStyle = '#0d0b14'; g.fillRect(Math.round(hx + .4), Math.round(hy - 1), 1, 1); g.fillRect(Math.round(hx + 2.7), Math.round(hy - 1), 1, 1); }
      E(g, hx + 3.6, hy + .9, .7, .6, '#e06f8b'); };
    if (pose === 'sleep') {
      LN(g, [[7, 19], [4, 17], [5, 13]], '#6a7090', 2.2);
      E(g, 14, 17, 9, 5.2, '#8a8fa8'); [10, 13, 16].forEach(x => LN(g, [[x, 12.4], [x - .8, 15]], '#4e5470', 1));
      LN(g, [[6, 20], [14, 21.4], [22, 20.4]], '#6a7090', 2.2);
      ear(22, 16); face(22, 16.5, true);
      return;
    }
    if (pose === 'sit') {
      LN(g, [[9, 21], [5, 21.4], [3, 18 + Math.sin(ta) * 2]], '#6a7090', 2);
      E(g, 13.5, 13.5, 5, 6.8, '#8a8fa8', -.25); E(g, 11.5, 18, 4.6, 4, '#6a7090'); E(g, 16, 14.5, 2.4, 3.6, '#e8e6f0');
      LN(g, [[15.6, 15], [16, 21.6]], '#8a8fa8', 1.8); LN(g, [[17.6, 15], [18, 21.6]], '#b9bdd0', 1.8);
      ear(17.5, 7); face(17.5, 7.5, o.eye === 'closed');
      return;
    }
    const low = pose === 'crouch' ? 3 : 0;
    LN(g, [[6, 12 + low], [3, 9 + low - Math.cos(ta) * 3], [2.6 + Math.sin(ta) * 2, 4 + low]], '#6a7090', 2);
    [[9, -s], [11.5, s], [17, s], [19.5, -s]].forEach(([x, k], i) => { const lift = Math.max(0, k) * .9; LN(g, [[x, 14 + low], [x + k * .7, 21.4 - lift]], i % 2 ? '#b9bdd0' : '#8a8fa8', 1.8); });
    E(g, 14, 13 + low, 8, pose === 'crouch' ? 3.6 : 4.4, '#8a8fa8');
    [10.5, 13.5, 16.5].forEach(x => LN(g, [[x, 9.4 + low], [x - .9, 12.3 + low]], '#4e5470', 1.1));
    E(g, 20, 15 + low, 2.4, 2.6, '#e8e6f0');
    const hx = pose === 'crouch' ? 24 : 23.5, hy = pose === 'crouch' ? 12.5 : 9;
    ear(hx, hy); face(hx, hy, o.eye === 'closed');
  });
}
/* Bitinho: mascote estilo shimeji (cabeça grande, corpo fino), moletom roxo com capuz. */
function kidSprite(o) {
  const pal = ['#7b4a8f', '#5a3468', '#a46dba', '#f0c9a0', '#d9a07e', '#0d0b14', '#2a2a4a', '#e8e6f0', '#e06f8b', '#f2c84b'];
  return bake(22, 26, pal, g => {
    const p = o.pose || 'walk', s = o.step || 0;
    const legs = { walk: [[[9.5, 18], [9.5 + s, 24.3]], [[12, 18], [12 - s, 24.3]]], climb: [[[10, 18], [14, 20.5], [13.5, 23]], [[12, 18], [15.5, 18.5], [15.5, 21]]], hang: [[[10, 18], [9.6, 24.3]], [[12, 18], [12.4, 24.3]]], sit: [[[10, 18.5], [15.5, 19], [16, 23]], [[12, 18.5], [17, 19.2], [17.5, 23.2]]], fall: [[[10, 18], [7, 23]], [[12, 18], [15, 23]]] }[p === 'wave' ? 'walk' : p];
    legs.forEach(L => { LN(g, L, '#2a2a4a', 1.8); const e = L[L.length - 1]; E(g, e[0] + .6, e[1] + .2, 1.5, .9, '#e8e6f0'); });
    g.fillStyle = '#5a3468'; g.beginPath(); g.moveTo(8, 11.5); g.lineTo(14, 11.5); g.lineTo(14.6, 18.8); g.lineTo(7.4, 18.8); g.closePath(); g.fill();
    g.fillStyle = '#7b4a8f'; g.fillRect(8.4, 11.5, 4.6, 6.8);
    LN(g, [[10.2, 13.5], [10.2, 15.5]], '#f2c84b', .7);
    const arms = { walk: [[8.4, 12.6], [7.4 - s * .4, 17]], climb: [[8.4, 12.6], [15.4, 7 + (o.alt ? 3 : 0)]], hang: [[8.6, 12], [9.4, .8]], sit: [[8.4, 12.6], [8.6, 17.5]], fall: [[8.4, 12.4], [4.6, 7]], wave: [[8.4, 12.6], [7.4, 17]] }[p];
    const arms2 = { walk: [[13.4, 12.6], [14.6 + s * .4, 17]], climb: [[13.4, 12.6], [16, 10 - (o.alt ? 3 : 0)]], hang: [[13.2, 12], [12.4, .8]], sit: [[13.4, 12.6], [15, 17.4]], fall: [[13.4, 12.4], [17.4, 7]], wave: [[13.4, 12.6], [17 + (o.alt ? 1.2 : -.4), 6]] }[p];
    [arms, arms2].forEach(A => { LN(g, A, '#7b4a8f', 1.8); E(g, A[1][0], A[1][1], 1, 1, '#f0c9a0'); });
    E(g, 11, 7, 5.6, 5.6, '#7b4a8f'); E(g, 9.2, 5, 2.4, 2, '#a46dba');
    E(g, 12.4, 7.9, 3.7, 3.6, '#f0c9a0'); E(g, 11.2, 9.6, 1.4, .8, '#d9a07e');
    if (o.eye === 'closed') { LN(g, [[11.8, 7.8], [13, 7.8]], '#0d0b14', .8); LN(g, [[14.1, 7.8], [15.2, 7.8]], '#0d0b14', .8); }
    else { g.fillStyle = '#0d0b14'; g.fillRect(12, 6.6, 1, 2); g.fillRect(14.3, 6.6, 1, 2); g.fillStyle = '#e8e6f0'; g.fillRect(12, 6.6, 1, 1); }
    E(g, 15.2, 9.6, .9, .6, '#e06f8b');
  });
}
// Os sprites não dependem do terminal: assa uma vez e reaproveita em todos os painéis.
let turmaSprites = null;
function turmaSpriteSet() {
  if (turmaSprites) return turmaSprites;
  const P = {};
  P.peng = { walk: [0, 1, 2, 3].map(i => penguinSprite({ step: [1.4, 0, -1.4, 0][i], tilt: [.1, 0, -.1, 0][i], fl: .3, fr: .15 })), idle: penguinSprite({}), blink: penguinSprite({ eye: 'closed' }), wave: [penguinSprite({ fl: 2.5, beak: 1 }), penguinSprite({ fl: 2.0 })], slide: penguinSprite({ slide: true, fl: .9, fr: .7 }), fall: penguinSprite({ fl: 2.2, fr: -1.8 }) };
  P.chute = chuteSprite();
  P.sheep = { walk: [0, 1, 2, 3].map(i => sheepSprite({ step: [1.3, 0, -1.3, 0][i], bob: i % 2 ? 0 : -.5 })), idle: sheepSprite({}), blink: sheepSprite({ eye: 'closed' }), eat: [sheepSprite({ head: 'down' }), sheepSprite({ head: 'down', mouth: 1 })], baa: sheepSprite({ mouth: 1 }), sleep: sheepSprite({ sleep: true }) };
  P.cat = { walk: [0, 1, 2, 3].map(i => catSprite({ step: [1.3, 0, -1.3, 0][i], tail: i * .6 })), sit: [catSprite({ pose: 'sit', tail: 0 }), catSprite({ pose: 'sit', tail: 1.6 })], blink: catSprite({ pose: 'sit', eye: 'closed' }), sleep: catSprite({ pose: 'sleep' }), crouch: catSprite({ pose: 'crouch', tail: .3 }) };
  P.kid = { walk: [0, 1, 2, 3].map(i => kidSprite({ step: [1.6, 0, -1.6, 0][i] })), climb: [kidSprite({ pose: 'climb' }), kidSprite({ pose: 'climb', alt: 1 })], hang: kidSprite({ pose: 'hang' }), sit: kidSprite({ pose: 'sit' }), fall: kidSprite({ pose: 'fall' }), wave: [kidSprite({ pose: 'wave' }), kidSprite({ pose: 'wave', alt: 1 })], blink: kidSprite({ eye: 'closed' }) };
  return (turmaSprites = P);
}

/* ============ CENA 1: TURMA OCIOSA ============ */
const turmaScene = termScene({
  id: 'turma', name: 'Turma ociosa',
  desc: 'Na linha do XPenguins e dos Shimeji: pinguins de paraquedas, ovelhas que comem letras, um gato que caça o cursor e o Bitinho que escala a janela. Vão chegando aos poucos, no mínimo 4.',
  setup() {
    this.ps = Math.max(2, Math.round(this.fs / 5.2)); // tamanho do "pixel" dos bichinhos: ~3 px com fonte 14, como no protótipo
    this.spr = turmaSpriteSet();
    this.pets = []; this.spawnT = .2; this.txt = []; this.glyphs = []; this.cap = 4; this.capT = 14;
    this.SPEED = { peng: 7, sheep: 5, cat: 10, kid: 8 };
  },
  count(sp) { return this.pets.filter(p => p.sp === sp).length; },
  spawn() {
    const sps = ['peng', 'sheep', 'cat', 'kid'].sort((a, b) => this.count(a) - this.count(b) + R(-.6, .6));
    const sp = sps[0], ps = this.ps, w = this.w, e = { sp, dir: Math.random() < .5 ? 1 : -1, t: 0, ph: R(0, 4), act: null, think: R(2, 4), life: R(50, 110), row: -1, vx: 0, vy: 0 };
    const maxX = Math.min(w - 40, Math.max(...this.rows.map((_, r) => this.cx(this.lineEnd(r)))));
    if (sp === 'peng') Object.assign(e, { st: 'para', x: R(this.x0 + 30, Math.max(this.x0 + 60, maxX)), y: -10 });
    else if (sp === 'kid') { const left = Math.random() < .5; Object.assign(e, { st: 'wall', side: left ? -1 : 1, x: left ? 2 * ps : w - 2 * ps, y: this.h + 30 * ps, dir: left ? -1 : 1 }); }
    else if (sp === 'cat') { const left = Math.random() < .5, J = this.jumpPlan({ x: left ? -20 : w + 20, y: this.h * .35, row: -1 }, left ? 1 : -1, true); Object.assign(e, { x: left ? -20 : w + 20, y: this.h * .35, dir: left ? 1 : -1 }); if (J) { e.st = 'jump'; e.J = J; e.jt = 0; e.x0 = e.x; e.y0 = e.y; } else e.st = 'fall'; }
    else { const left = Math.random() < .5; Object.assign(e, { st: 'walk', row: -2, x: left ? -20 : w + 20, y: this.floorY(), dir: left ? 1 : -1, enter: true }); }
    this.pets.push(e);
  },
  supported(row, x, half) { if (row < 0) return row === -2; return this.solid(row, this.colAt(x)) || this.solid(row, this.colAt(x - half)) || this.solid(row, this.colAt(x + half)); },
  jumpPlan(e, dir, fromSide) {
    const cands = [];
    for (let r = 1; r < this.rows.length; r++) {
      if (!fromSide && e.row >= 0 && Math.abs(r - e.row) > 3) continue;
      if (!fromSide && e.row === -2 && r < this.rows.length - 4) continue;
      for (let k = 2; k <= 10; k++) { const x2 = fromSide ? (dir > 0 ? this.x0 + k * this.cw * 1.5 : this.w - 20 - k * this.cw * 1.5) : e.x + dir * this.cw * k; if (x2 < this.x0 + 10 || x2 > this.w - 20) continue; if (this.supported(r, x2, 0) && this.solid(r, this.colAt(x2))) cands.push({ r, x: x2 }); }
    }
    if (!cands.length) return null;
    const c = pick(cands), T = .5 + Math.abs(c.x - e.x) / 700;
    return { r: c.r, x: c.x, T };
  },
  say(e, txt, col = '#f4f1e6') { this.txt.push({ e, txt, col, t: 0 }); },
  update(dt) {
    const ps = this.ps, w = this.w;
    this.capT -= dt; if (this.capT <= 0 && this.cap < 7) { this.cap++; this.capT = R(9, 16); }
    this.spawnT -= dt;
    const alive = this.pets.filter(p => !p.leaving).length;
    if (this.spawnT <= 0 && alive < this.cap) { this.spawn(); this.spawnT = alive < 4 ? R(1.5, 3) : R(5, 10); }
    const cu = this.cursor();
    for (const e of this.pets) {
      e.t += dt; e.life -= dt; e.ph += dt;
      if (e.life <= 0 && !e.leaving && !e.act) { e.leaving = true; e.dir = e.x < w / 2 ? -1 : 1; }
      const sp = this.SPEED[e.sp] * ps, half = (e.sp === 'sheep' ? 7 : e.sp === 'cat' ? 6 : 3) * ps;
      switch (e.st) {
        case 'para': { e.y += 26 * dt * ps / 3; e.x += Math.sin(e.t * 1.3) * 8 * dt; for (let r = 0; r < this.rows.length; r++) { const ty = this.top(r); if (ty > e.y - 26 * dt && ty <= e.y && this.supported(r, e.x, 2 * ps)) { e.y = ty; e.row = r; e.st = 'walk'; this.burst(e.x, e.y, 4, '#7d7f99', 30); break; } } if (e.y >= this.floorY()) { e.y = this.floorY(); e.row = -2; e.st = 'walk'; } break; }
        case 'fall': { e.vy = Math.min(300, e.vy + 800 * dt); const ny = e.y + e.vy * dt; let landed = false; for (let r = 0; r < this.rows.length; r++) { const ty = this.top(r); if (ty > e.y + .5 && ty <= ny && this.supported(r, e.x, half * .6)) { e.y = ty; e.row = r; landed = true; break; } } if (!landed && ny >= this.floorY()) { e.y = this.floorY(); e.row = -2; landed = true; } if (landed) { e.st = 'walk'; e.vy = 0; this.burst(e.x, e.y, 4, '#7d7f99', 30); if (e.sp === 'peng' && Math.random() < .4) this.say(e, 'ufa'); } else { e.y = ny; if (e.sp === 'peng' && e.vy > 200 && !e.chute) { e.st = 'para'; } } break; }
        case 'jump': { e.jt += dt; const p = Math.min(1, e.jt / e.J.T), ty = this.top(e.J.r); e.x = lerp(e.x0, e.J.x, p); e.y = lerp(e.y0, ty, p) - Math.sin(p * Math.PI) * (2.5 * this.lh + Math.max(0, e.y0 - ty) * .4); if (p >= 1) { e.y = ty; e.row = e.J.r; e.st = 'walk'; this.burst(e.x, e.y, 3, '#7d7f99', 25); } break; }
        case 'wall': { e.y -= 9 * ps * dt; if (e.y < this.y0 + 25 * ps) { e.st = 'ceil'; e.y = this.y0; e.dir = -e.side; e.ceilT = R(3, 7); e.x = e.side < 0 ? 8 * ps : this.w - 8 * ps; } break; }
        case 'ceil': { e.x += e.dir * 5 * ps * dt; e.ceilT -= dt; if (e.x < 6 * ps || e.x > w - 6 * ps) e.dir *= -1; if (e.ceilT <= 0) { if (e.leaving) { e.gone = true; } else { e.st = 'fall'; e.vy = 0; this.say(e, 'opa!'); } } break; }
        case 'walk': {
          if (e.act) { this.doAct(e, dt, cu); break; }
          e.x += e.dir * sp * dt;
          if (e.enter && e.x > 30 && e.x < w - 30) e.enter = false;
          if (!e.enter && (e.x < this.x0 + half * .6 || e.x > w - half * .6 - 4)) {
            if (e.leaving) { if (e.x < -40 || e.x > w + 40) e.gone = true; }
            else if (e.sp === 'kid' && Math.random() < .7) { e.st = 'wall'; e.side = e.x < w / 2 ? -1 : 1; e.x = e.side < 0 ? 2 * ps : w - 2 * ps; e.row = -1; break; }
            else { e.dir *= -1; e.x = C(e.x, this.x0 + half * .6, w - half * .6 - 4); }
          }
          if (e.leaving && (e.x < -40 || e.x > w + 40)) e.gone = true;
          if (e.row !== -2 && !this.supported(e.row, e.x, half * .6)) { e.st = 'fall'; e.vy = 0; e.row = -1; break; }
          e.think -= dt;
          if (e.think <= 0 && !e.leaving && !e.enter) { e.think = R(2.5, 5); this.choose(e, cu); }
          break;
        }
      }
    }
    this.pets = this.pets.filter(e => !e.gone);
    for (const t of this.txt) t.t += dt; this.txt = this.txt.filter(t => t.t < 1.6);
    for (const q of this.glyphs) { q.t += dt; q.vy += 500 * dt; q.x += q.vx * dt; q.y += q.vy * dt; q.a += q.va * dt; } this.glyphs = this.glyphs.filter(q => q.y < this.h + 20);
  },
  // O texto mudou embaixo da turma: quem estava sentado ou dormindo numa linha que sumiu cai.
  changed() { for (const e of this.pets) if (e.st === 'walk' && e.row >= 0 && !this.supported(e.row, e.x, 2 * this.ps)) { e.act = null; e.st = 'fall'; e.vy = 0; e.row = -1; } },
  choose(e, cu) {
    const r = Math.random(), onRow = e.row >= 0;
    const go = (type, dur, extra = {}) => { e.act = Object.assign({ type, t: 0, dur }, extra); };
    if (r < .2) { const J = this.jumpPlan(e, e.dir, false); if (J) { e.st = 'jump'; e.J = J; e.jt = 0; e.x0 = e.x; e.y0 = e.y; return; } }
    if (e.sp === 'peng') { if (r < .45) go('slide', 1.4); else if (r < .65) go('wave', 1.6); else if (r < .8) go('idle', 1.5); }
    if (e.sp === 'sheep') { if (r < .5 && onRow) go('eat', 2.4); else if (r < .7) go('baa', 1.1); else if (r < .85) go('sleep', 7); }
    if (e.sp === 'cat') { if (r < .35 && e.row === cu.r && Math.abs(cu.x - e.x) < this.w * .4) go('stalk', 1.4, { tx: cu.x }); else if (r < .55 && onRow) go('swat', 1); else if (r < .75) go('sit', 3.5); else if (r < .9) go('sleep', 8); }
    if (e.sp === 'kid') { if (r < .4 && onRow) go('sit', 5); else if (r < .6) go('wave', 1.8); }
  },
  doAct(e, dt, cu) {
    const a = e.act, ps = this.ps; a.t += dt;
    if (a.type === 'slide') { e.x += e.dir * 22 * ps * dt; if (e.row >= 0) { const c = this.colAt(e.x); if (this.solid(e.row, c)) this.jiggle(e.row, c, 1, .1); } if (Math.random() < dt * 15) this.parts.push({ x: e.x - e.dir * 4 * ps, y: e.y - ps, vx: -e.dir * R(10, 30), vy: R(-20, -5), t: 0, life: .4, col: '#c9c6d8', g: 100 }); if (e.x < this.x0 + 10 || e.x > this.w - 14) { e.dir *= -1; } if (e.row >= 0 && !this.supported(e.row, e.x, ps)) { e.act = null; e.st = 'fall'; e.vy = 0; e.row = -1; return; } }
    if (a.type === 'wave' && a.t < .05) this.say(e, e.sp === 'peng' ? 'oi!' : 'eai!');
    if (a.type === 'baa' && a.t < .05) this.say(e, 'beee', '#fbe7a1');
    if (a.type === 'eat' && !a.done && a.t > .6) { a.done = true; const c = this.colAt(e.x + e.dir * 8 * ps); if (this.solid(e.row, c)) { this.makeHole(e.row, c, R(10, 18)); this.burst(this.cx(c) + this.cw / 2, this.mid(e.row), 5, this.rowColor(e.row, c), 30); this.say(e, 'nhom', '#9ad37a'); } }
    if (a.type === 'swat' && !a.done && a.t > .45) { a.done = true; const c = this.colAt(e.x + e.dir * 8 * ps); if (this.solid(e.row, c)) { this.glyphs.push({ ch: this.ch(e.row, c), col: this.rowColor(e.row, c), x: this.cx(c) + this.cw / 2, y: this.mid(e.row), vx: e.dir * R(60, 110), vy: -R(120, 180), a: 0, va: e.dir * R(4, 9), t: 0 }); this.makeHole(e.row, c, R(8, 14)); } }
    if (a.type === 'stalk') { if (a.t > 1.4 && !a.J) { a.J = { x0: e.x, tx: cu.x + this.fs * .3, t: 0 }; } if (a.J) { a.J.t += dt / .45; const p = Math.min(1, a.J.t); e.x = lerp(a.J.x0, a.J.tx, p); e.y = this.top(e.row) - Math.sin(p * Math.PI) * 2.2 * this.lh; if (p >= 1) { e.y = this.top(e.row); this.say(e, Math.random() < .5 ? 'peguei' : 'miau', '#9ad37a'); e.act = null; } } return; }
    if (a.t > a.dur) e.act = null;
  },
  sprite(e) {
    const P = this.spr[e.sp], f = (e.ph * 8 | 0) % 4, blink = (e.ph % 3.4) < .14, a = e.act;
    if (e.sp === 'peng') { if (e.st === 'para' || e.st === 'fall') return P.fall; if (a && a.type === 'slide') return P.slide; if (a && a.type === 'wave') return P.wave[(e.ph * 6 | 0) % 2]; if (a || e.st !== 'walk') return blink ? P.blink : P.idle; return P.walk[f]; }
    if (e.sp === 'sheep') { if (a && a.type === 'eat') return P.eat[(e.ph * 6 | 0) % 2]; if (a && a.type === 'baa') return P.baa; if (a && a.type === 'sleep') return P.sleep; if (a || e.st !== 'walk') return blink ? P.blink : P.idle; return P.walk[f]; }
    if (e.sp === 'cat') { if (a && a.type === 'sleep') return P.sleep; if (a && (a.type === 'sit')) return blink ? P.blink : P.sit[(e.ph * 1.5 | 0) % 2]; if (a && (a.type === 'stalk' || a.type === 'swat')) return P.crouch; if (e.st === 'jump' || e.st === 'fall') return P.walk[1]; return P.walk[f]; }
    if (e.st === 'wall') return P.climb[(e.ph * 5 | 0) % 2]; if (e.st === 'ceil') return P.hang; if (e.st === 'fall' || e.st === 'jump') return P.fall;
    if (a && a.type === 'sit') return P.sit; if (a && a.type === 'wave') return P.wave[(e.ph * 6 | 0) % 2]; if (a) return blink ? P.blink : P.walk[1];
    return P.walk[f];
  },
  render(g) {
    const ps = this.ps;
    for (const q of this.glyphs) { g.save(); g.translate(q.x, q.y); g.rotate(q.a); g.fillStyle = q.col; g.font = this.T.font; g.textAlign = 'center'; g.textBaseline = 'middle'; g.fillText(q.ch, 0, 0); g.restore(); }
    for (const e of this.pets.slice().sort((a, b) => a.y - b.y)) {
      const s = this.sprite(e);
      if (e.sp === 'peng' && e.st === 'para') { psp(g, this.spr.chute, e.x, e.y - s.height * ps + 3 * ps, ps); }
      if (e.st === 'wall') { psp(g, s, e.side < 0 ? e.x + 4 * ps : e.x - 4 * ps, e.y, ps, e.side < 0); continue; }
      if (e.st === 'ceil') { psp(g, s, e.x, this.y0 + s.height * ps - 2 * ps, ps, e.dir < 0); continue; }
      let fy = e.y; if (e.sp === 'kid' && e.act && e.act.type === 'sit') fy = e.y + 4 * ps;
      if (e.row === -2) { g.fillStyle = 'rgba(0,0,0,.35)'; g.beginPath(); g.ellipse(e.x, e.y + 1, s.width * ps * .3, ps, 0, 0, TAU); g.fill(); }
      psp(g, s, e.x, fy + ps, ps, e.dir < 0);
      if (e.act && e.act.type === 'sleep') { const k = (e.ph * 1.2) % 2; pxText(g, 'z', e.x + e.dir * 6 * ps + k * 4, e.y - s.height * ps * .8 - k * 10, '#c9c6e0', PK, Math.max(2, ps - 1)); }
    }
    for (const t of this.txt) { const e = t.e, sc = Math.max(2, ps - 1), tw = pxW(t.txt, sc); const y = Math.max(4, e.y - 9 * ps * 1.6 - t.t * 12); g.fillStyle = 'rgba(13,11,20,.75)'; g.fillRect(e.x - tw / 2 - 3, y - 3, tw + 6, 5 * sc + 6); pxText(g, t.txt, e.x - tw / 2, y, t.col, null, sc); }
  }
});

/* ============ CENA 2: CHUVA DIGITAL ============ */
const chuvaScene = termScene({
  id: 'chuva', name: 'Chuva digital',
  desc: 'Kana de meia largura espelhado com números, cabeça branca, rastro que troca de caractere e colunas mais lentas ao fundo. O terminal fica atrás e as letras dele acendem quando a chuva passa. O relógio se forma com a própria chuva.',
  setup() {
    const kana = []; for (let c = 0xFF66; c <= 0xFF9D; c++) kana.push(String.fromCharCode(c));
    this.GL = kana.concat('0123456789:.=*+-<>¦|'.split(''));
    this.mfs = Math.round(this.fs * 1.1); this.colW = Math.round(this.mfs * .78); this.rowH = Math.round(this.mfs * 1.05);
    this.cols = Math.ceil(this.w / this.colW); this.nrows = Math.ceil(this.h / this.rowH) + 1;
    this.atlas = this.buildAtlas();
    this.streams = []; for (let i = 0; i < this.cols; i++) this.newStream(i, true);
    this.grid = Array.from({ length: this.cols * this.nrows }, () => (Math.random() * this.GL.length) | 0);
    this.lit = new Float32Array(this.cols * this.nrows); this.dim = 0;
    this.clockKey = ''; this.clockCells = [];
  },
  buildAtlas() {
    const n = this.GL.length, cw = this.colW, ch = this.rowH, cols = ['#e6fff0', '#7dff9d', '#2fd060', '#167a36'];
    const cv = mkCanvas(cw * n, ch * cols.length), g = cv.getContext('2d');
    g.font = `${this.mfs}px "MS Gothic", "Yu Gothic", "Hiragino Kaku Gothic ProN", ${MONO}`; g.textAlign = 'center'; g.textBaseline = 'middle';
    cols.forEach((c, k) => { g.fillStyle = c; this.GL.forEach((s, i) => { g.save(); g.translate(i * cw + cw / 2, k * ch + ch / 2); g.scale(-1, 1); if (k === 0) { g.shadowColor = '#9dffbf'; g.shadowBlur = this.mfs * .5; } g.fillText(s, 0, 0); g.restore(); }); });
    return cv;
  },
  newStream(i, initial) {
    const far = Math.random() < .4;
    this.streams[i] = { y: initial ? R(-this.nrows, this.nrows) : R(-25, -2), v: far ? R(5, 9) : R(10, 20), len: (R(8, 28) | 0), far, wait: initial ? 0 : R(0, 2.5) };
  },
  update(dt) {
    this.dim = Math.min(1, this.dim + dt * .5);
    for (let i = 0; i < this.cols; i++) {
      const s = this.streams[i]; if (s.wait > 0) { s.wait -= dt; continue; }
      const prev = Math.floor(s.y); s.y += s.v * dt; const now = Math.floor(s.y);
      if (now !== prev && now >= 0 && now < this.nrows) { this.grid[now * this.cols + i] = (Math.random() * this.GL.length) | 0; this.lit[now * this.cols + i] = s.far ? .5 : 1; }
      if (s.y - s.len > this.nrows) this.newStream(i, false);
    }
    for (let k = 0; k < 40; k++) { const j = (Math.random() * this.grid.length) | 0; this.grid[j] = (Math.random() * this.GL.length) | 0; }
    for (let j = 0; j < this.lit.length; j++) if (this.lit[j] > 0) this.lit[j] = Math.max(0, this.lit[j] - dt * .7);
  },
  // O terminal escurece aos poucos (fica a 28%) para a chuva aparecer; o texto continua lá atrás.
  under(g) { g.fillStyle = `rgba(0,0,0,${(this.dim * .72).toFixed(3)})`; g.fillRect(0, 0, this.w, this.h); },
  // Células da grade que formam a hora (muda uma vez por minuto).
  clockSet() {
    const now = new Date(), txt = String(now.getHours()).padStart(2, '0') + ':' + String(now.getMinutes()).padStart(2, '0');
    if (txt === this.clockKey) return this.clockCells;
    const { cols, nrows } = this, cellsW = txt.length * 4 - 1, cellsH = 5, sc = Math.max(1, Math.floor(Math.min(cols * .5 / cellsW, nrows * .35 / cellsH))), cx0 = Math.floor(cols / 2 - cellsW * sc / 2), cy0 = Math.floor(nrows / 2 - cellsH * sc / 2);
    const out = new Set();
    [...txt].forEach((ch, k) => { const gl = FONT[ch]; if (!gl) return; for (let p = 0; p < 15; p++) if (gl[p] === '1') for (let a2 = 0; a2 < sc; a2++) for (let b2 = 0; b2 < sc; b2++) { const r = cy0 + ((p / 3) | 0) * sc + b2, i = cx0 + k * 4 * sc + (p % 3) * sc + a2; if (r >= 0 && r < nrows && i >= 0 && i < cols) out.add(r * cols + i); } });
    this.clockKey = txt; this.clockCells = [...out];
    return this.clockCells;
  },
  render(g) {
    const { cols, nrows, colW, rowH, atlas } = this, a = this.dim;
    for (let i = 0; i < cols; i++) {
      const s = this.streams[i], head = Math.floor(s.y);
      for (let r = Math.max(0, head - s.len); r <= Math.min(nrows - 1, head); r++) {
        const d = head - r, j = r * cols + i, gi = this.grid[j];
        let row = d === 0 ? 0 : d < 3 ? 1 : d < s.len * .6 ? 2 : 3, al = d === 0 ? 1 : Math.max(.15, 1 - d / s.len);
        if (s.far) { al *= .45; if (row === 0) row = 1; }
        g.globalAlpha = al * a; g.drawImage(atlas, gi * colW, row * rowH, colW, rowH, i * colW, r * rowH, colW, rowH);
      }
    }
    for (const j of this.clockSet()) { const i = j % cols, r = (j / cols) | 0, L = this.lit[j]; g.globalAlpha = (.35 + L * .65) * a; g.drawImage(atlas, this.grid[j] * colW, (L > .6 ? 0 : 1) * rowH, colW, rowH, i * colW, r * rowH, colW, rowH); }
    // Letras do terminal acendem quando a chuva passa por elas.
    g.font = this.T.font; g.textBaseline = 'alphabetic'; g.textAlign = 'left'; g.fillStyle = '#b8ffcf';
    this.rows.forEach((l, ri) => {
      const rr2 = Math.floor(this.mid(ri) / rowH), y = this.y0 + ri * this.lh + this.T.base0;
      if (rr2 < 0 || rr2 >= nrows) return;
      for (let c = 0; c < l.length; c++) {
        if (l[c] === ' ' || !l[c]) continue;
        const x = this.cx(c), ci = Math.floor((x + this.cw / 2) / colW); if (ci >= cols) break;
        const L = this.lit[rr2 * cols + ci];
        if (L > .3) { g.globalAlpha = (L - .3) * a; g.fillText(l[c], x, y); }
      }
    });
    g.globalAlpha = 1;
  }
});

/* ============ CENA 3: TV DE TUBO ============ */
const tvScene = termScene({
  id: 'tv', name: 'TV de tubo',
  desc: 'O terminal vira uma TV CRT velha: linhas de varredura, aberração cromática, barra rolando, chiado. De vez em quando a imagem rasga, perde o sincronismo vertical, entra chuvisco com barras de cor ou a TV desliga num ponto e volta.',
  setup() {
    const dpr = this.dpr, W = Math.round(this.w * dpr), H = Math.round(this.h * dpr);
    Object.assign(this, { W, H, tc: mkCanvas(W, H), cc: mkCanvas(W, H), bloom: mkCanvas(W, H), ch3: [0, 1, 2].map(() => mkCanvas(W, H)) });
    this.noise = [0, 1, 2].map(() => { const c = mkCanvas(Math.ceil(this.w / 2), Math.ceil(this.h / 2)), q = c.getContext('2d'), im = q.createImageData(c.width, c.height); for (let i = 0; i < im.data.length; i += 4) { const v = Math.random() * 255; im.data[i] = im.data[i + 1] = im.data[i + 2] = v; im.data[i + 3] = 255; } q.putImageData(im, 0, 0); return c; });
    this.scan = mkCanvas(4, 3); const sq = this.scan.getContext('2d'); sq.fillStyle = 'rgba(0,0,0,.42)'; sq.fillRect(0, 2, 4, 1);
    this.ev = null; this.evT = R(3, 6); this.roll = 0;
    this.dirty = true; this.blink = -1;
  },
  changed() { this.dirty = true; },
  update(dt) {
    this.evT -= dt;
    if (!this.ev && this.evT <= 0) { const r = Math.random(); this.ev = { type: r < .32 ? 'tear' : r < .55 ? 'vroll' : r < .72 ? 'wobble' : r < .9 ? 'static' : 'off', t: 0 }; this.ev.dur = { tear: .5, vroll: 1.4, wobble: 1.2, static: 2.2, off: 2.6 }[this.ev.type]; }
    if (this.ev) { this.ev.t += dt; if (this.ev.t > this.ev.dur) { this.ev = null; this.evT = R(4, 10); } }
    this.roll = (this.roll + dt * .08) % 1;
  },
  // Imagem do terminal, os três canais de cor e o brilho: só refaz quando o texto muda ou o cursor pisca.
  paint(cursorOn) {
    const { W, H, dpr } = this, tq = this.tc.getContext('2d');
    tq.setTransform(dpr, 0, 0, dpr, 0, 0); paintTerm(tq, this.T, cursorOn);
    const tint = ['#ff0000', '#00ff00', '#0000ff'];
    this.ch3.forEach((c, k) => { const q = c.getContext('2d'); q.setTransform(1, 0, 0, 1, 0, 0); q.globalCompositeOperation = 'source-over'; q.drawImage(this.tc, 0, 0); q.globalCompositeOperation = 'multiply'; q.fillStyle = tint[k]; q.fillRect(0, 0, W, H); q.globalCompositeOperation = 'source-over'; });
    const bq = this.bloom.getContext('2d'); bq.setTransform(1, 0, 0, 1, 0, 0); bq.clearRect(0, 0, W, H);
    if ('filter' in bq) { bq.filter = `blur(${2.5 * dpr}px)`; bq.drawImage(this.tc, 0, 0); bq.filter = 'none'; }
  },
  frame(g, dt) {
    this.t += dt; this.sync(dt); this.update(dt);
    const { dpr, W, H, ev, t } = this;
    const blink = settings.cursorBlink === false ? 0 : ((performance.now() / 530) | 0) % 2;
    if (this.dirty || blink !== this.blink) { this.blink = blink; this.dirty = false; this.paint(blink === 0); }
    const shift = (1.2 + (ev && ev.type === 'tear' ? 4 : 0) + Math.sin(t * 3) * .4) * dpr;
    const cq = this.cc.getContext('2d'); cq.setTransform(1, 0, 0, 1, 0, 0); cq.globalCompositeOperation = 'source-over'; cq.fillStyle = '#000'; cq.fillRect(0, 0, W, H);
    cq.globalCompositeOperation = 'lighter'; cq.drawImage(this.ch3[0], -shift, 0); cq.drawImage(this.ch3[1], 0, 0); cq.drawImage(this.ch3[2], shift, 0); cq.globalCompositeOperation = 'source-over';
    g.save(); g.setTransform(1, 0, 0, 1, 0, 0);
    g.fillStyle = '#000'; g.fillRect(0, 0, W, H);
    const flick = .93 + Math.random() * .07;
    if (ev && ev.type === 'off') {
      const p = ev.t;
      if (p < .5) { const k = ease(p / .5), sy = Math.max(.004, 1 - k), sxx = p > .35 ? Math.max(.01, 1 - (p - .35) / .15) : 1; g.globalAlpha = flick; g.drawImage(this.cc, W * (1 - sxx) / 2, H * (1 - sy) / 2, W * sxx, H * sy); g.globalCompositeOperation = 'lighter'; g.fillStyle = `rgba(220,240,255,${k * .8})`; g.fillRect(W * (1 - sxx) / 2, H / 2 - 1.5 * dpr, W * sxx, 3 * dpr); g.globalCompositeOperation = 'source-over'; }
      else if (p < 1.2) { const k = 1 - (p - .5) / .7, rr2 = Math.max(0, k) * 6 * dpr; g.fillStyle = `rgba(230,245,255,${k})`; g.beginPath(); g.arc(W / 2, H / 2, rr2, 0, TAU); g.fill(); }
      else if (p > 1.9) { const k = ease(C((p - 1.9) / .6, 0, 1)); g.globalAlpha = k * flick; g.drawImage(this.cc, 0, H * (1 - k) / 2, W, H * k); }
      g.globalAlpha = 1;
    } else if (ev && ev.type === 'static' && ev.t < 1.6) {
      if (ev.t < .5 || ev.t > 1.25) { const nz = this.noise[(t * 30 | 0) % 3]; g.drawImage(nz, R(-20, 0), R(-20, 0), W + 40, H + 40); }
      else { const bars = ['#c0c0c0', '#c0c000', '#00c0c0', '#00c000', '#c000c0', '#c00000', '#0000c0']; bars.forEach((c, i) => { g.fillStyle = c; g.fillRect(i * W / 7, 0, W / 7 + 1, H * .72); }); g.fillStyle = '#101010'; g.fillRect(0, H * .72, W, H * .28); const sc = Math.max(3, Math.round(4 * dpr)), tx = 'sem sinal'; g.fillStyle = 'rgba(0,0,0,.7)'; g.fillRect(W / 2 - pxW(tx, sc) / 2 - 3 * sc, H * .36 - 3 * sc, pxW(tx, sc) + 6 * sc, 11 * sc); pxText(g, tx, W / 2 - pxW(tx, sc) / 2, H * .36, '#f4f1e6', null, sc); g.globalAlpha = .25; g.drawImage(this.noise[(t * 30 | 0) % 3], 0, 0, W, H); g.globalAlpha = 1; }
    } else {
      let vy = 0; if (ev && ev.type === 'vroll') { vy = (ease(ev.t / ev.dur) * H * 1.0) % H; }
      const bands = ev && ev.type === 'tear' ? 18 : ev && ev.type === 'wobble' ? 60 : 1, bh = Math.ceil(H / bands);
      g.globalAlpha = flick;
      for (let b = 0; b < bands; b++) {
        let ox = 0;
        if (ev && ev.type === 'tear' && Math.random() < .4) ox = R(-1, 1) * W * .06;
        if (ev && ev.type === 'wobble') ox = Math.sin(b * .35 + t * 18) * 6 * dpr * Math.sin(ev.t / ev.dur * Math.PI);
        const y0 = b * bh, srcY = ((y0 + vy) % H + H) % H, hh = Math.min(bh, H - srcY);
        g.drawImage(this.cc, 0, srcY, W, hh, ox, y0, W, hh); if (hh < bh) g.drawImage(this.cc, 0, 0, W, bh - hh, ox, y0 + hh, W, bh - hh);
      }
      if (vy) { g.fillStyle = '#000'; g.fillRect(0, (H - vy) % H - 6 * dpr, W, 12 * dpr); }
      g.globalCompositeOperation = 'lighter'; g.globalAlpha = .28; g.drawImage(this.bloom, 0, 0); g.globalCompositeOperation = 'source-over';
      g.globalAlpha = .07 + (ev && ev.type === 'tear' ? .15 : 0); g.drawImage(this.noise[(t * 24 | 0) % 3], R(-30, 0), R(-30, 0), W + 30, H + 30); g.globalAlpha = 1;
    }
    const by = this.roll * (H + 200 * dpr) - 100 * dpr, bg = g.createLinearGradient(0, by - 60 * dpr, 0, by + 60 * dpr);
    bg.addColorStop(0, 'rgba(255,255,255,0)'); bg.addColorStop(.5, 'rgba(200,230,255,.05)'); bg.addColorStop(1, 'rgba(255,255,255,0)');
    g.fillStyle = bg; g.fillRect(0, by - 60 * dpr, W, 120 * dpr);
    g.fillStyle = g.createPattern(this.scan, 'repeat'); g.fillRect(0, 0, W, H);
    const vg = g.createRadialGradient(W / 2, H / 2, Math.min(W, H) * .35, W / 2, H / 2, Math.max(W, H) * .72);
    vg.addColorStop(0, 'rgba(0,0,0,0)'); vg.addColorStop(1, 'rgba(0,0,0,.72)'); g.fillStyle = vg; g.fillRect(0, 0, W, H);
    const rr3 = Math.min(W, H) * .06; g.fillStyle = '#000'; g.beginPath(); g.rect(0, 0, W, H); g.moveTo(rr3, 0); g.arcTo(W, 0, W, H, rr3); g.arcTo(W, H, 0, H, rr3); g.arcTo(0, H, 0, 0, rr3); g.arcTo(0, 0, W, 0, rr3); g.closePath(); g.fill('evenodd');
    g.restore();
  },
  render() {}
});

const SCENES = [turmaScene, chuvaScene, tvScene];

/* ---------- motor: ociosidade por terminal e desenho ---------- */
const config = { enabled: false, minutes: 10, scene: 'random' };
const savers = new Map(); // painel -> Saver
const FADE = 1.6;         // opacidade por segundo (entra e sai em ~0,6 s)
const FRAME_MS = 1000 / 30 - 2; // 30 quadros/s: suave e leve com vários painéis
let lastScene = null;

class Saver {
  constructor(pane) {
    this.pane = pane;
    this.last = performance.now();
    this.phase = 'off'; // off | in | on | out
    this.alpha = 0;
    this.scene = null;
    this.canvas = null;
    const el = pane.el;
    // Captura: roda antes do xterm. Com a animação na tela, a tecla só acorda e não chega ao servidor.
    el.addEventListener('keydown', (e) => {
      const showing = (this.phase === 'in' || this.phase === 'on') && this.alpha > .05;
      this.wake();
      if (showing) { e.preventDefault(); e.stopImmediatePropagation(); }
    }, true);
    el.addEventListener('pointermove', (e) => { if (Math.abs(e.movementX) + Math.abs(e.movementY) > 2) this.wake(); }, { passive: true });
    el.addEventListener('pointerdown', () => this.wake(), true);
    el.addEventListener('wheel', () => this.wake(), { passive: true, capture: true });
  }

  showing() { return this.phase !== 'off'; }

  visible() { return !document.hidden && this.pane.termEl.offsetWidth > 0 && this.pane.termEl.offsetHeight > 0; }

  wake() {
    this.last = performance.now();
    this.forced = false;
    if (this.phase === 'in' || this.phase === 'on') this.phase = 'out';
  }

  start(id) {
    const pool = SCENES.filter(s => s !== lastScene);
    const proto = SCENES.find(s => s.id === id) || pick(pool.length ? pool : SCENES);
    lastScene = proto;
    if (!this.canvas) {
      this.canvas = document.createElement('canvas');
      this.canvas.className = 'saver';
      this.canvas.setAttribute('aria-hidden', 'true');
      this.pane.termEl.appendChild(this.canvas);
    }
    this.scene = Object.create(proto);
    this.scene.pane = this.pane;
    this.w = 0; this.h = 0; // força o init no primeiro quadro, já com o tamanho certo
    this.alpha = 0;
    this.canvas.style.opacity = '0';
    this.canvas.hidden = false;
    this.phase = 'in';
    runLoop();
  }

  stop() {
    this.phase = 'off';
    this.forced = false;
    this.alpha = 0;
    this.scene = null;
    this.last = performance.now();
    if (this.canvas) {
      this.canvas.hidden = true;
      this.canvas.width = this.canvas.height = 0; // devolve a memória do quadro
    }
  }

  frame(dt, real) {
    const target = this.phase === 'in' || this.phase === 'on' ? 1 : 0;
    this.alpha = approach(this.alpha, target, FADE, real);
    if (this.phase === 'in' && this.alpha >= 1) this.phase = 'on';
    if (this.phase === 'out' && this.alpha <= 0) { this.stop(); return; }
    const cv = this.canvas, w = this.pane.termEl.clientWidth, h = this.pane.termEl.clientHeight;
    if (w < 40 || h < 40) return;
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    if (w !== this.w || h !== this.h || dpr !== this.dpr) {
      this.w = w; this.h = h; this.dpr = dpr;
      cv.width = Math.round(w * dpr); cv.height = Math.round(h * dpr);
      this.scene.init(w, h, dpr);
    }
    const g = cv.getContext('2d');
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    g.clearRect(0, 0, w, h);
    g.save();
    this.scene.frame(g, dt);
    g.restore();
    cv.style.opacity = String(Math.round(this.alpha * 100) / 100);
  }
}

let looping = false, lastNow = 0, lastFrame = 0;
function runLoop() {
  if (looping) return;
  looping = true;
  lastNow = lastFrame = performance.now();
  requestAnimationFrame(loop);
}

function loop(now) {
  let active = 0;
  for (const s of savers.values()) if (s.showing()) active++;
  if (!active) { looping = false; return; }
  requestAnimationFrame(loop);
  if (now - lastFrame < FRAME_MS) return;
  lastFrame = now;
  const real = Math.min(.05, (now - lastNow) / 1000); lastNow = now;
  for (const s of savers.values()) {
    if (!s.showing()) continue;
    try {
      s.frame(real * TS, real);
    } catch (err) {
      // Uma cena com defeito não pode travar o terminal: some e volta a contar o tempo.
      console.warn('proteção de tela', err);
      s.stop();
    }
  }
}

// A cada segundo: quem passou do tempo sem uso começa; escondido (outra aba, janela minimizada) não conta.
setInterval(() => {
  const now = performance.now(), limit = config.minutes * 60_000;
  for (const [pane, s] of savers) {
    if (pane.disposed) { savers.delete(pane); continue; }
    if (!s.visible()) { if (s.showing()) s.stop(); s.last = now; continue; }
    if (!config.enabled && !s.forced) { if (s.phase === 'in' || s.phase === 'on') s.phase = 'out'; s.last = now; continue; }
    if (s.phase === 'off' && now - s.last >= limit) s.start(config.scene);
  }
}, 1000);

// Voltar à janela minimizada é uso: ninguém quer encontrar a animação ao restaurar.
document.addEventListener('visibilitychange', () => {
  if (!document.hidden) for (const s of savers.values()) s.last = performance.now();
});

paneHooks.push((pane) => savers.set(pane, new Saver(pane)));

function configure(c) {
  config.enabled = !!c.enabled;
  config.minutes = C(Number(c.minutes) || 10, 1, 240);
  config.scene = typeof c.scene === 'string' ? c.scene : 'random';
  for (const s of savers.values()) s.last = performance.now();
}

// "Ver agora" em Aparência do terminal: no terminal ativo (ou no primeiro visível), mesmo desligada.
function preview(scene) {
  const visible = [...savers.values()].filter(s => s.visible());
  const active = activeTab && activeTab.activePane && savers.get(activeTab.activePane);
  const target = active && active.visible() ? active : visible[0];
  if (!target) return false;
  if (target.showing()) target.stop();
  target.start(scene || 'random');
  target.forced = true; // fica até mexer no terminal, mesmo com a proteção desligada
  return true;
}

function handle(msg) {
  if (msg.type !== 'screensaverPreview') return false;
  preview(msg.scene);
  return true;
}

return { configure, handle, preview, scenes: SCENES.map(s => ({ id: s.id, name: s.name })) };
})();

function handleScreensaverMessage(msg) {
  return Screensaver.handle(msg);
}
