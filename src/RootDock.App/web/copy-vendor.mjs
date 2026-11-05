// Copia os arquivos de distribuição do xterm.js e do Monaco (editor do VS Code) para wwwroot/vendor.
// Os arquivos copiados ficam versionados para que o build no Visual Studio não dependa do npm.
import { copyFileSync, cpSync, mkdirSync, rmSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const out = join(here, '..', 'wwwroot', 'vendor');
rmSync(out, { recursive: true, force: true });
mkdirSync(out, { recursive: true });

const files = [
  ['@xterm/xterm/lib/xterm.js', 'xterm.js'],
  ['@xterm/xterm/css/xterm.css', 'xterm.css'],
  ['@xterm/addon-fit/lib/addon-fit.js', 'addon-fit.js'],
  ['@xterm/addon-webgl/lib/addon-webgl.js', 'addon-webgl.js'],
  ['@xterm/addon-search/lib/addon-search.js', 'addon-search.js'],
  ['@xterm/addon-web-links/lib/addon-web-links.js', 'addon-web-links.js'],
  ['@xterm/addon-unicode11/lib/addon-unicode11.js', 'addon-unicode11.js'],
  ['@xterm/addon-serialize/lib/addon-serialize.js', 'addon-serialize.js'],
];
for (const [src, dst] of files) {
  copyFileSync(join(here, 'node_modules', src), join(out, dst));
  console.log(`vendor/${dst}`);
}

// Monaco: build AMD minificada (carregada sob demanda, só quando um editor abre).
const monaco = join(here, 'node_modules', 'monaco-editor');
cpSync(join(monaco, 'min', 'vs'), join(out, 'monaco', 'vs'), { recursive: true });
copyFileSync(join(monaco, 'LICENSE'), join(out, 'monaco', 'LICENSE'));
copyFileSync(join(monaco, 'ThirdPartyNotices.txt'), join(out, 'monaco', 'ThirdPartyNotices.txt'));
console.log('vendor/monaco/vs');

// noVNC (VNC numa aba): módulos ES carregados sob demanda por vnc.js.
const novnc = join(here, 'node_modules', '@novnc', 'novnc');
cpSync(join(novnc, 'core'), join(out, 'novnc', 'core'), { recursive: true });
cpSync(join(novnc, 'vendor'), join(out, 'novnc', 'vendor'), { recursive: true });
copyFileSync(join(novnc, 'LICENSE.txt'), join(out, 'novnc', 'LICENSE.txt'));
console.log('vendor/novnc');
