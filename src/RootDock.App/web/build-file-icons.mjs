// Gera Assets/file-icons.json a partir do Material Icon Theme (MIT, o tema de ícones do VS Code):
// o mapeamento oficial (extensões, nomes de arquivo, nomes de pasta, linguagens, variantes do tema
// claro) e cada ícone SVG convertido em formas prontas para o WPF (caminhos no formato do WPF,
// cores, gradientes lineares, contornos e transformações). Rode: npm run icons
import { readFileSync, writeFileSync, copyFileSync, mkdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const pkg = join(here, 'node_modules', 'material-icon-theme');
const outDir = join(here, '..', 'Assets', 'FileIcons');
const manifest = JSON.parse(readFileSync(join(pkg, 'dist', 'material-icons.json'), 'utf8'));

// ------------------------------------------------ XML mínimo (os SVGs do tema são simples)
function parseXml(src) {
  const root = { name: '#root', attrs: {}, children: [] };
  const stack = [root];
  const re = /<!--[\s\S]*?-->|<\?[\s\S]*?\?>|<(\/?)([a-zA-Z][\w:.-]*)((?:\s+[\w:.-]+\s*=\s*(?:"[^"]*"|'[^']*'))*)\s*(\/?)>/g;
  let m;
  while ((m = re.exec(src))) {
    if (!m[2]) continue;
    const [, close, name, attrText, selfClose] = m;
    if (close) {
      stack.pop();
      continue;
    }
    const attrs = {};
    for (const a of attrText.matchAll(/([\w:.-]+)\s*=\s*(?:"([^"]*)"|'([^']*)')/g)) attrs[a[1]] = a[2] ?? a[3];
    if (attrs.style) {
      for (const decl of attrs.style.split(';')) {
        const [k, v] = decl.split(':').map((x) => x && x.trim());
        if (k && v) attrs[k] = v;
      }
    }
    const node = { name, attrs, children: [] };
    stack[stack.length - 1].children.push(node);
    if (!selfClose) stack.push(node);
  }
  return root.children[0];
}

// ------------------------------------------------ Cores
const NAMED = { white: '#FFFFFF', black: '#000000', red: '#FF0000', green: '#008000', blue: '#0000FF', yellow: '#FFFF00', gray: '#808080', grey: '#808080', orange: '#FFA500', purple: '#800080', transparent: null, none: null };

function color(value) {
  if (value == null) return undefined;
  const v = value.trim().toLowerCase();
  if (v in NAMED) return NAMED[v];
  if (/^#[0-9a-f]{3}$/.test(v)) return '#' + v.slice(1).split('').map((c) => c + c).join('').toUpperCase();
  if (/^#[0-9a-f]{6}$/.test(v)) return v.toUpperCase();
  const rgb = /^rgba?\(([^)]+)\)$/.exec(v);
  if (rgb) {
    const [r, g, b] = rgb[1].split(',').map((x) => Math.round(parseFloat(x)));
    return '#' + [r, g, b].map((n) => n.toString(16).padStart(2, '0')).join('').toUpperCase();
  }
  return undefined; // desconhecida: herda
}

// ------------------------------------------------ Transformações (matriz [a b c d e f] do SVG)
const IDENTITY = [1, 0, 0, 1, 0, 0];

function multiply(m, n) {
  return [
    m[0] * n[0] + m[2] * n[1], m[1] * n[0] + m[3] * n[1],
    m[0] * n[2] + m[2] * n[3], m[1] * n[2] + m[3] * n[3],
    m[0] * n[4] + m[2] * n[5] + m[4], m[1] * n[4] + m[3] * n[5] + m[5],
  ];
}

function parseTransform(text) {
  let m = IDENTITY;
  for (const t of (text || '').matchAll(/(\w+)\s*\(([^)]*)\)/g)) {
    const a = t[2].split(/[\s,]+/).filter(Boolean).map(Number);
    let n;
    switch (t[1]) {
      case 'translate': n = [1, 0, 0, 1, a[0] || 0, a[1] || 0]; break;
      case 'scale': n = [a[0], 0, 0, a.length > 1 ? a[1] : a[0], 0, 0]; break;
      case 'rotate': {
        const r = (a[0] * Math.PI) / 180;
        const cos = Math.cos(r);
        const sin = Math.sin(r);
        n = [cos, sin, -sin, cos, 0, 0];
        if (a.length === 3) n = multiply(multiply([1, 0, 0, 1, a[1], a[2]], n), [1, 0, 0, 1, -a[1], -a[2]]);
        break;
      }
      case 'matrix': n = a.slice(0, 6); break;
      case 'skewX': n = [1, 0, Math.tan((a[0] * Math.PI) / 180), 1, 0, 0]; break;
      case 'skewY': n = [1, Math.tan((a[0] * Math.PI) / 180), 0, 1, 0, 0]; break;
      default: continue;
    }
    m = multiply(m, n);
  }
  return m;
}

const isIdentity = (m) => m.every((v, i) => Math.abs(v - IDENTITY[i]) < 1e-9);

// ------------------------------------------------ Caminhos: SVG → mini-linguagem do WPF
const fmt = (n) => {
  const r = Math.round(n * 1000) / 1000;
  return Object.is(r, -0) ? '0' : String(r);
};

function toWpfPath(d) {
  const s = d;
  let pos = 0;
  let cmd = null;
  const out = [];
  const argc = { m: 2, l: 2, h: 1, v: 1, c: 6, s: 4, q: 4, t: 2, a: 7, z: 0 };
  const skip = () => { while (pos < s.length && /[\s,]/.test(s[pos])) pos++; };
  const num = () => {
    skip();
    const m = /^[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?/.exec(s.slice(pos));
    if (!m) throw new Error(`número esperado em "${s.slice(pos, pos + 16)}"`);
    pos += m[0].length;
    return parseFloat(m[0]);
  };
  const flag = () => {
    skip();
    const c = s[pos];
    if (c !== '0' && c !== '1') throw new Error(`flag de arco inválida em "${s.slice(pos, pos + 16)}"`);
    pos++;
    return c === '1' ? 1 : 0;
  };
  for (;;) {
    skip();
    if (pos >= s.length) break;
    if (/[MmZzLlHhVvCcSsQqTtAa]/.test(s[pos])) cmd = s[pos++];
    else if (!cmd) throw new Error('caminho sem comando inicial');
    else if (cmd === 'M') cmd = 'L'; // pares depois de um moveto são linhas
    else if (cmd === 'm') cmd = 'l';
    else if (cmd === 'z' || cmd === 'Z') throw new Error('número depois de Z');
    const lower = cmd.toLowerCase();
    if (lower === 'z') {
      out.push(cmd);
      continue;
    }
    const args = lower === 'a' ? [num(), num(), num(), flag(), flag(), num(), num()] : Array.from({ length: argc[lower] }, num);
    out.push(cmd + args.map(fmt).join(' ').replace(/^/, ' '));
  }
  return out.join(' ');
}

function shapePath(node) {
  const a = node.attrs;
  const n = (k, def = 0) => (a[k] === undefined ? def : parseFloat(a[k]));
  switch (node.name) {
    case 'path': return a.d || '';
    case 'circle': {
      const cx = n('cx'); const cy = n('cy'); const r = n('r');
      return `M${cx - r} ${cy}A${r} ${r} 0 1 0 ${cx + r} ${cy}A${r} ${r} 0 1 0 ${cx - r} ${cy}Z`;
    }
    case 'ellipse': {
      const cx = n('cx'); const cy = n('cy'); const rx = n('rx'); const ry = n('ry');
      return `M${cx - rx} ${cy}A${rx} ${ry} 0 1 0 ${cx + rx} ${cy}A${rx} ${ry} 0 1 0 ${cx - rx} ${cy}Z`;
    }
    case 'rect': {
      const x = n('x'); const y = n('y'); const w = n('width'); const h = n('height');
      let rx = a.rx !== undefined ? n('rx') : a.ry !== undefined ? n('ry') : 0;
      let ry = a.ry !== undefined ? n('ry') : rx;
      rx = Math.min(rx, w / 2);
      ry = Math.min(ry, h / 2);
      if (!rx && !ry) return `M${x} ${y}H${x + w}V${y + h}H${x}Z`;
      return `M${x + rx} ${y}H${x + w - rx}A${rx} ${ry} 0 0 1 ${x + w} ${y + ry}V${y + h - ry}A${rx} ${ry} 0 0 1 ${x + w - rx} ${y + h}`
        + `H${x + rx}A${rx} ${ry} 0 0 1 ${x} ${y + h - ry}V${y + ry}A${rx} ${ry} 0 0 1 ${x + rx} ${y}Z`;
    }
    case 'polygon':
    case 'polyline': {
      const pts = (a.points || '').trim().split(/[\s,]+/).map(Number);
      if (pts.length < 4) return '';
      let d = `M${pts[0]} ${pts[1]}`;
      for (let i = 2; i + 1 < pts.length; i += 2) d += `L${pts[i]} ${pts[i + 1]}`;
      return node.name === 'polygon' ? d + 'Z' : d;
    }
    case 'line': return `M${n('x1')} ${n('y1')}L${n('x2')} ${n('y2')}`;
    default: return '';
  }
}

// ------------------------------------------------ Gradientes
function collectGradients(node, map = {}) {
  if (node.name === 'linearGradient' || node.name === 'radialGradient') map[node.attrs.id] = node;
  for (const c of node.children) collectGradients(c, map);
  return map;
}

function gradientFill(id, gradients) {
  let node = gradients[id];
  if (!node) return null;
  let stopsNode = node;
  const seen = new Set();
  while (!stopsNode.children.some((c) => c.name === 'stop')) {
    const href = (stopsNode.attrs['xlink:href'] || stopsNode.attrs.href || '').replace('#', '');
    if (!href || seen.has(href) || !gradients[href]) break;
    seen.add(href);
    stopsNode = gradients[href];
  }
  const stops = stopsNode.children.filter((c) => c.name === 'stop').map((st) => {
    const off = st.attrs.offset || '0';
    const offset = off.endsWith('%') ? parseFloat(off) / 100 : parseFloat(off);
    const op = st.attrs['stop-opacity'] !== undefined ? parseFloat(st.attrs['stop-opacity']) : 1;
    return [Math.round(offset * 1000) / 1000, color(st.attrs['stop-color'] || '#000') || '#000000', op];
  });
  if (!stops.length) return null;
  if (node.name === 'radialGradient') return stops[0][1]; // raro: usa a primeira cor
  const a = node.attrs;
  const user = a.gradientUnits === 'userSpaceOnUse';
  const p = (k, def) => {
    if (a[k] === undefined) return def;
    return a[k].endsWith('%') ? parseFloat(a[k]) / 100 : parseFloat(a[k]);
  };
  return { u: user ? 1 : 0, x1: p('x1', 0), y1: p('y1', 0), x2: p('x2', user ? 0 : 1), y2: p('y2', 0), st: stops };
}

// ------------------------------------------------ Ícone
function convertIcon(svgText) {
  const svg = parseXml(svgText);
  if (!svg || svg.name !== 'svg') throw new Error('não é SVG');
  const vb = (svg.attrs.viewBox || `0 0 ${svg.attrs.width || 24} ${svg.attrs.height || 24}`).split(/[\s,]+/).map(Number);
  const gradients = collectGradients(svg);
  const ids = {};
  const index = (node) => {
    if (node.attrs.id) ids[node.attrs.id] = node;
    node.children.forEach(index);
  };
  index(svg);
  const shapes = [];
  const walk = (node, st) => {
    const a = node.attrs;
    if (a['clip-path'] || a.mask) throw new Error('não suportado: clip/mask');
    if (node.name === 'use') {
      // <use href="#id" x y>: desenha o elemento referenciado, deslocado.
      const target = ids[(a['xlink:href'] || a.href || '').replace('#', '')];
      if (!target || target === node) throw new Error('não suportado: use sem destino');
      const moved = { ...st, m: multiply(multiply(st.m, parseTransform(a.transform)), [1, 0, 0, 1, parseFloat(a.x || 0), parseFloat(a.y || 0)]) };
      const inherited = { name: 'g', attrs: Object.fromEntries(Object.entries(a).filter(([k]) => !['href', 'xlink:href', 'x', 'y', 'transform', 'id'].includes(k))), children: [target] };
      walk(inherited, moved);
      return;
    }
    const next = { ...st };
    if (a.fill !== undefined) {
      const url = /url\(#([^)]+)\)/.exec(a.fill);
      next.fill = url ? gradientFill(url[1], gradients) : a.fill === 'none' ? null : (color(a.fill) ?? st.fill);
    }
    if (a.stroke !== undefined) next.stroke = a.stroke === 'none' ? null : (color(a.stroke) ?? st.stroke);
    if (a['stroke-width'] !== undefined) next.sw = parseFloat(a['stroke-width']);
    if (a['stroke-linecap']) next.cap = a['stroke-linecap'];
    if (a['stroke-linejoin']) next.join = a['stroke-linejoin'];
    if (a['fill-rule']) next.rule = a['fill-rule'];
    if (a.opacity !== undefined) next.opacity = st.opacity * parseFloat(a.opacity);
    if (a['fill-opacity'] !== undefined) next.fillOpacity = parseFloat(a['fill-opacity']);
    if (a.transform) next.m = multiply(st.m, parseTransform(a.transform));
    if (['defs', 'linearGradient', 'radialGradient', 'clipPath', 'mask', 'symbol', 'title', 'desc', 'style', 'metadata'].includes(node.name)) return;
    if (node.name === 'svg' || node.name === 'g') {
      for (const c of node.children) walk(c, next);
      return;
    }
    const raw = shapePath(node);
    if (!raw) return;
    if (next.fill == null && next.stroke == null) return; // invisível (moldura de tamanho)
    const shape = { d: (next.rule === 'evenodd' ? 'F0 ' : 'F1 ') + toWpfPath(raw) };
    if (next.fill != null) shape.f = next.fill;
    const alpha = next.opacity * (next.fill != null ? next.fillOpacity : 1);
    if (alpha < 0.999) shape.o = Math.round(alpha * 1000) / 1000;
    if (next.stroke != null) {
      shape.s = next.stroke;
      shape.sw = next.sw;
      if (next.cap) shape.cap = next.cap;
      if (next.join) shape.join = next.join;
    }
    if (!isIdentity(next.m)) shape.m = next.m.map((v) => Math.round(v * 10000) / 10000);
    shapes.push(shape);
  };
  walk(svg, { fill: '#000000', stroke: null, sw: 1, opacity: 1, fillOpacity: 1, m: IDENTITY, rule: 'nonzero' });
  return { v: vb, sh: shapes };
}

// ------------------------------------------------ Ícones usados pelo mapeamento
const used = new Set([manifest.file, manifest.folder]);
const tables = ['fileExtensions', 'fileNames', 'folderNames', 'languageIds'];
for (const t of tables) for (const v of Object.values(manifest[t] || {})) used.add(v);
for (const t of tables) for (const v of Object.values((manifest.light || {})[t] || {})) used.add(v);
if (manifest.light && manifest.light.file) used.add(manifest.light.file);
if (manifest.light && manifest.light.folder) used.add(manifest.light.folder);

const icons = {};
const failed = [];
for (const name of [...used].sort()) {
  const def = manifest.iconDefinitions[name];
  if (!def) continue;
  try {
    icons[name] = convertIcon(readFileSync(resolve(join(pkg, 'dist'), def.iconPath), 'utf8'));
  } catch (err) {
    failed.push(`${name}: ${err.message}`);
  }
}

// Mapeamento só com os ícones convertidos (os que falharam caem no ícone padrão).
const keep = (table) => Object.fromEntries(Object.entries(table || {}).filter(([, v]) => icons[v]));
const light = manifest.light || {};
const out = {
  source: 'material-icon-theme 5.40.0 (MIT) — https://github.com/material-extensions/vscode-material-icon-theme',
  file: manifest.file,
  folder: manifest.folder,
  fileExtensions: keep(manifest.fileExtensions),
  fileNames: keep(manifest.fileNames),
  folderNames: keep(manifest.folderNames),
  languageIds: keep(manifest.languageIds),
  light: {
    fileExtensions: keep(light.fileExtensions),
    fileNames: keep(light.fileNames),
    folderNames: keep(light.folderNames),
    languageIds: keep(light.languageIds),
  },
  icons,
};

mkdirSync(outDir, { recursive: true });
writeFileSync(join(outDir, 'file-icons.json'), JSON.stringify(out));
copyFileSync(join(pkg, 'LICENSE'), join(outDir, 'LICENSE-material-icon-theme.txt'));
console.log(`${Object.keys(icons).length} ícones convertidos, ${failed.length} ignorados`);
if (failed.length) console.log('  ' + failed.slice(0, 30).join('\n  '));
