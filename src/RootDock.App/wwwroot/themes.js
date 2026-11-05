'use strict';
// ---------------------------------------------------------------- Temas da interface
// O C# manda o tema escolhido (Exibir → Tema): cores (variáveis CSS), efeitos (linhas de tubo,
// brilho do fósforo, chuva do Matrix, fonte de terminal na interface) e as cores do editor.

let appTheme = null;

function applyAppTheme(msg) {
  appTheme = msg;
  const root = document.documentElement;
  for (const [name, value] of Object.entries(msg.vars || {})) root.style.setProperty(name, value);
  const fx = msg.effects || {};
  const body = document.body;
  body.dataset.theme = msg.id || '';
  body.classList.toggle('theme-light', !!msg.light);
  body.classList.toggle('fx-scanlines', !!fx.scanlines);
  body.classList.toggle('fx-glow', !!fx.glow);
  body.classList.toggle('fx-mono', !!fx.mono);
  rain.color = getComputedStyle(document.documentElement).getPropertyValue('--accent').trim() || '#00ff41';
  setMatrixRain(!!fx.rain);
  if (window.monaco && typeof applyEditorTheme === 'function') applyEditorTheme(window.monaco);
}

// Tema da abertura (o C# injeta na criação do documento): classes e efeitos já no carregamento.
if (window.__bootTheme) document.addEventListener('DOMContentLoaded', () => { if (!appTheme) applyAppTheme(window.__bootTheme); });

function handleThemeMessage(msg) {
  if (msg.type !== 'appTheme') return false;
  applyAppTheme(msg);
  return true;
}

// ------------------------------------------------ Chuva do Matrix (só na tela inicial)
const rain = { canvas: null, running: false, last: 0, drops: [], columns: 0, size: 16, color: "#00ff41" };
const RAIN_CHARS = 'アイウエオカキクケコサシスセソタチツテトナニヌネノハヒフヘホマミムメモヤユヨラリルレロワン0123456789ABCDEF<>/{}$#';

function setMatrixRain(on) {
  const empty = $('empty');
  if (!on) {
    rain.running = false;
    if (rain.canvas) rain.canvas.remove();
    rain.canvas = null;
    return;
  }
  if (rain.canvas) return;
  if (window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;
  rain.canvas = document.createElement('canvas');
  rain.canvas.className = 'rain';
  rain.canvas.setAttribute('aria-hidden', 'true');
  empty.prepend(rain.canvas);
  rain.drops = [];
  resumeRain();
}

// Laço por quadro (~18 por segundo) que PARA de vez com abas abertas ou janela oculta: um
// setInterval continuaria acordando a página 18 vezes por segundo o dia inteiro.
function resumeRain() {
  if (rain.running || !rain.canvas) return;
  rain.running = true;
  requestAnimationFrame(rainFrame);
}

function rainFrame(t) {
  const empty = $('empty');
  if (!rain.canvas || empty.hidden || document.hidden) {
    rain.running = false;
    if (rain.canvas && empty.hidden) {
      rain.canvas.width = rain.canvas.height = 0; // libera o bitmap enquanto não aparece
      rain.drops = [];
    }
    return;
  }
  if (t - rain.last >= 55) {
    rain.last = t;
    drawRain();
  }
  requestAnimationFrame(rainFrame);
}

document.addEventListener('visibilitychange', () => { if (!document.hidden) resumeRain(); });

function drawRain() {
  const empty = $('empty');
  const c = rain.canvas;
  if (!c || empty.hidden || document.hidden) return; // pausa com abas abertas ou janela minimizada
  const dpr = window.devicePixelRatio || 1;
  const w = empty.clientWidth;
  const h = empty.clientHeight;
  if (c.width !== Math.round(w * dpr) || c.height !== Math.round(h * dpr)) {
    c.width = Math.round(w * dpr);
    c.height = Math.round(h * dpr);
    rain.columns = Math.ceil(w / rain.size);
    rain.drops = Array.from({ length: rain.columns }, () => Math.floor(Math.random() * -40));
  }
  const ctx = c.getContext('2d');
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.fillStyle = 'rgba(0, 0, 0, 0.09)'; // rastro que some aos poucos
  ctx.fillRect(0, 0, w, h);
  const color = rain.color;
  ctx.font = `${rain.size - 2}px "Cascadia Mono", Consolas, monospace`;
  for (let i = 0; i < rain.drops.length; i++) {
    const y = rain.drops[i] * rain.size;
    if (y > 0) {
      const ch = RAIN_CHARS[Math.floor(Math.random() * RAIN_CHARS.length)];
      ctx.fillStyle = Math.random() > 0.96 ? '#d8ffe0' : color; // de vez em quando um caractere mais claro
      ctx.fillText(ch, i * rain.size, y);
    }
    if (y > h && Math.random() > 0.975) rain.drops[i] = 0;
    rain.drops[i]++;
  }
}
