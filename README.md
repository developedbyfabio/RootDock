# RootDock

Cliente SSH e de terminais para Windows: sessões organizadas em pastas, abas, divisão de tela e terminal local.
Alternativa ao MobaXterm, feita do zero em C# (.NET 10) com WPF e xterm.js.

Criado por **Fabio Henrique de Almeida Lemes**.

## Instalar

Baixe o instalador na [página de versões](https://github.com/developedbyfabio/RootDock/releases/latest):

- **`RootDock-win.msi`** (recomendado): assistente de instalação em português, com boas-vindas, informações e a
  escolha entre instalar só para você ou para todos os usuários (este pede administrador, também nas atualizações).
- `RootDock-win-Setup.exe`: instalação de um clique, só para você, sem perguntas.
- `RootDock-win-Portable.zip`: descompacte e rode `RootDock.exe` (também se atualiza sozinho).

Os três criam atalhos na Área de Trabalho e no Menu Iniciar e recebem as atualizações automaticamente.
Desinstalar (Configurações → Aplicativos) remove o programa e o cache da tela; sessões, preferências
(`%APPDATA%\RootDock`) e senhas (Cofre do Windows) ficam, para uma reinstalação.

A arte e os textos do assistente ficam em `installer/` (`pwsh installer/gerar-recursos.ps1` gera de novo).

O instalador ainda não é assinado digitalmente: na primeira vez o Windows pode mostrar "O Windows protegeu o computador".
Clique em **Mais informações → Executar assim mesmo**.

### Atualização automática

Depois de instalado, o RootDock se atualiza sozinho pelas Releases deste repositório (Velopack):

- procura uma versão nova logo ao abrir (3 s) e a cada 6 horas;
- baixa em segundo plano só o que mudou (pacote de diferença, normalmente menos de 1 MB);
- terminado o download, **pergunta se quer atualizar agora**, com as novidades da versão. "Atualizar agora" fecha e abre de novo já atualizado (avisa antes se houver abas abertas); "Depois" deixa o aviso **Reiniciar para atualizar** na barra de título e instala quando o RootDock for fechado;
- o Enter escolhe "Depois", para uma tecla que ia para o terminal não reiniciar o programa sem querer.

**Ajuda → Verificar atualizações** mostra a situação, as novidades da versão e liga/desliga o modo automático.
Cópias executadas pelo Visual Studio ou por `dotnet run` não se atualizam (a janela explica isso).

## Publicar uma versão nova

O workflow `.github/workflows/release.yml` faz tudo quando recebe uma tag `vX.Y.Z`:

```powershell
git tag -a v0.2.0 -m "Novidades: ..."   # o texto da tag vira as novidades mostradas no RootDock
git push origin v0.2.0
```

Ele roda os testes, publica o app (win-x64, com o .NET embutido), gera o instalador, a versão portátil e os
pacotes de atualização (completo e diferença) e cria a Release. Os RootDock instalados encontram a versão sozinhos.
Sem texto na tag (`git tag v0.2.0`), as novidades são os títulos dos commits desde a versão anterior.
Tags com sufixo (`v0.2.0-beta.1`) viram pré-lançamento e não chegam a quem usa a versão estável.

Para testar uma versão antes de publicar: gere os pacotes com `vpk pack` numa pasta e abra o RootDock com a
variável `ROOTDOCK_UPDATE_URL` apontando para ela (serve também para um espelho interno via http).

## Como abrir e rodar (desenvolvimento)

1. Abra `RootDock.slnx` no Visual Studio 2026 (carga de trabalho "Desenvolvimento para desktop com .NET").
2. Defina `RootDock.App` como projeto de inicialização e pressione F5.

Pela linha de comando:

```powershell
dotnet build RootDock.slnx
dotnet test --project tests\RootDock.Core.Tests\RootDock.Core.Tests.csproj
dotnet run --project src\RootDock.App
```

Requisitos: Windows 10 1809+ ou Windows 11, SDK do .NET 10 e o runtime do WebView2 (já vem no Windows com o Edge).

O ícone do programa vem de `icone.png` (raiz). Ao trocá-lo, gere de novo `src/RootDock.App/Assets/rootdock.ico` (16 a 256 px, sem a margem transparente) e `rootdock-64.png`.

## Estrutura

| Pasta | Conteúdo |
|---|---|
| `src/RootDock.Core` | Sessões, importação do MobaXterm, SSH (SSH.NET), terminal local (ConPTY), cofre de senhas, chaves de servidores conhecidos. Sem interface. |
| `src/RootDock.App` | Aplicativo WPF (tema WPF-UI) e a área de terminais em `wwwroot` (xterm.js dentro de um WebView2). |
| `src/RootDock.App/web` | `package.json` usado só para atualizar o xterm.js: `npm ci && npm run vendor`. |
| `tests/RootDock.Core.Tests` | Testes (xUnit v3). |

## Onde ficam os dados

- `%APPDATA%\RootDock\sessions.json` — sessões e pastas (com `.bak` da versão anterior).
- `%APPDATA%\RootDock\known_hosts` — chaves de servidores confiáveis (formato OpenSSH). O `~\.ssh\known_hosts` também é consultado, só para leitura.
- Senhas: Gerenciador de Credenciais do Windows (entradas `RootDock:*`). Nenhuma senha é gravada em arquivo.
- `%APPDATA%\RootDock\logs` — registro de erros.
- `%APPDATA%\RootDock\history.jsonl` — histórico de comandos (senhas e tokens mascarados; comandos que começam com espaço não entram).
- `%APPDATA%\RootDock\transfers.jsonl` — histórico de transferências de arquivos.
- `%APPDATA%\RootDock\multiexec\<data>` — logs da execução em vários servidores (um arquivo por servidor e o resumo).
- `%APPDATA%\RootDock\recordings` — gravações de terminal (`.cast`, formato asciinema).

## Já funciona (versão 0.1)

- Árvore de sessões com pastas, busca, criar/editar/duplicar/excluir. "Nova sessão" tem as abas Simples (só servidor, porta e usuário) e Avançado (todas as opções; editar abre nela). Conectar salva e já abre o terminal (só o IP basta: o login é pedido no terminal); "Só salvar" apenas guarda.
- Organização por arrastar e soltar: sessões e pastas para dentro de pastas, entre pastas ou para a raiz. Destino destacado, pasta fechada abre ao parar o mouse sobre ela, rolagem automática nas bordas, Esc cancela. Não deixa colocar uma pasta dentro dela mesma; pergunta antes de mesclar pastas com o mesmo nome. Ctrl+Z desfaz (até 30 passos).
- Pelo teclado: menu de contexto com tecla Menu/Shift+F10, "Mover para", F2 renomeia pasta, Del exclui pasta vazia.
- Seleção múltipla: Ctrl+clique alterna, Shift+clique pega o intervalo, Ctrl+A todas as visíveis. Com várias marcadas: Del exclui, Enter abre (uma aba cada), arrastar move, botão direito tem "Abrir juntas na mesma tela" (grade), "Mover para" e "Excluir". Exclusões também se desfazem com Ctrl+Z (sem as senhas salvas).
- Sem duplicatas: "Nova sessão" com a mesma conexão de uma existente (servidor, porta, usuário, chave, ambiente, comando inicial) usa a existente. No menu de uma sessão repetida aparece "Excluir as cópias repetidas desta sessão".
- Importação do `MobaXterm.ini` (sessões SSH, pastas, usuário, porta, chave privada), oferecida na primeira execução.
- Importação do `~/.ssh/config` (Ferramentas → Importar sessões → OpenSSH): cada `Host` vira uma sessão com HostName, User, Port, IdentityFile e ProxyJump (ligado ao bastion quando ele também está no arquivo); `Host *` vale como padrão, `Include` é seguido, `Match` é ignorado com aviso.
- SSH com senha, chave (OpenSSH/PuTTY), keyboard-interactive/2FA; verificação da chave do servidor; reconexão com Enter.
- Vários logins por sessão (botão direito → "Logins salvos"): adicionar, trocar senha, definir o padrão, remover, parar de usar a chave SSH. Senhas só no Cofre do Windows. Ao abrir uma sessão com logins salvos, o terminal lista as opções (Enter = padrão, número, nome, ou "Outro usuário"); dá para desligar a pergunta por sessão. Dividir a tela reaproveita o login do painel original.
- Login no próprio terminal, como no MobaXterm/PuTTY: sessão (ou conexão rápida) só com o IP pede "Usuário:" e "Senha de usuario@host:" na tela; senha errada mostra "Acesso negado" e pergunta de novo; Ctrl+C cancela; colar funciona. Depois de entrar, um cartão no canto oferece lembrar usuário e senha (cofre do Windows).
- Senha salva no Cofre do Windows (opcional), apagada sozinha se deixar de funcionar.
- Chave SSH (Ferramentas → Chave SSH deste computador): mostra a chave deste computador ou cria uma Ed25519 em `%USERPROFILE%\.ssh\id_ed25519` (formato OpenSSH, só o usuário tem acesso ao arquivo; nunca substitui uma chave existente).
- "Instalar minha chave SSH" (botão direito numa sessão, ou em várias): adiciona a chave pública ao `~/.ssh/authorized_keys` do servidor pela conexão do terminal (sem duplicar, sem mexer nas linhas existentes, com SELinux), testa o login só com a chave e, se funcionar, a sessão passa a entrar sem senha.
- Terminal local: Windows PowerShell, PowerShell 7, CMD, WSL e Git Bash (ConPTY, sem Cygwin).
- Abas (arrastar para reordenar), divisão lateral e vertical, foco com Alt+setas, busca no histórico.
- Tela dividida: cada painel tem um cabeçalho com servidor, pasta atual, maximizar (ou duplo clique) e fechar (ou Ctrl+Shift+W / botão do meio).
- Juntar sessões na mesma tela: arraste uma aba para dentro do terminal e solte perto de uma borda (esquerda, direita, em cima, embaixo); aparece uma prévia de onde ela vai ficar. Dá para montar 2×2 ou mais.
- Arraste o cabeçalho de um painel para trocá-lo de lugar, ou para a barra de abas para separá-lo numa aba própria.
- Botão Organizar: grade (2×2, 3×2...), lado a lado, um embaixo do outro, juntar todas as abas na atual, separar em abas. Duplo clique na divisória divide meio a meio.
- Linhas de comando destacadas no histórico (faixa, prompt realçado e marca na barra de rolagem; vermelho em produção). Ctrl+↑/↓ pula entre os comandos. Não marca Enter vazio, senhas, respostas [Y/n] nem programas de tela cheia (vim, less, top, tmux).
- Copiar ao selecionar e colar com o botão direito; Ctrl+C copia só quando há seleção.
- Sessões de produção: aba vermelha com selo PROD e confirmação antes de colar várias linhas.
- Saída grande sem travar: envio em lote para a tela e contrapressão até o servidor.
- `RootDock.exe --open "Nome da sessão"` abre uma sessão direto (para atalhos).
- `RootDock.exe --data "D:\RootDock"` usa outra pasta de dados (modo portátil ou testes), inclusive para o cache do WebView2.

### Barra de menus (como o VS Code e o Visual Studio)

- Logo no canto da barra de título e menus ao lado: **Arquivo** (nova sessão, conexão rápida, terminal local, importar, arquivos do servidor, salvar o terminal, reabrir abas, sair), **Editar** (copiar, colar, selecionar tudo, buscar, limpar, buscar sessões, histórico), **Exibir** (painéis, dividir, organizar, fechar painel, abas, fonte, aparência), **Sessão** (conectar, editar, duplicar, logins, túneis, instalar chave, executar em vários, gravar), **Ferramentas** e **Ajuda** (atalhos de teclado com filtro, pasta de logs, Sobre com versão, ambiente e licenças).
- Cada menu mostra o atalho ao lado e desabilita o que não se aplica ao que está ativo. O resto da barra continua arrastável; o título da janela (barra de tarefas, Alt+Tab) mostra a sessão ativa.
- Atalhos que o terminal usa (Ctrl+N, Alt+letra, F1...) não foram tomados pelo RootDock.

### Temas (Exibir → Tema)

- **Escuro** (padrão), **Claro**, **Grafite** (estilo VS Code Dark Modern), **Black** (preto absoluto na janela e no terminal, bom em telas OLED; a separação vem de contornos finos), **Meia-noite**, **Matrix** (preto e verde neon, chuva de caracteres na tela inicial), **Computador antigo (âmbar)** e **Computador antigo (verde)** (fósforo com linhas de tubo, cantos escurecidos e brilho).
- O tema muda na hora a janela, os diálogos, os botões, as abas, o editor (cores de sintaxe próprias de cada tema) e o terminal (Matrix, fósforo âmbar, fósforo verde e claro entre os esquemas). **Galeria de temas** com miniatura de cada um; opção de manter as cores atuais do terminal.
- Implementação: os pincéis `Rd*` são usados com `DynamicResource` em todo o XAML e trocados por `Services/AppThemes.cs` (que também tinge os controles do WPF-UI); a página recebe variáveis CSS e efeitos (`wwwroot/themes.js`).

### Ícones de arquivo (como no VS Code)

- Painel de arquivos e gerenciador em duas colunas usam os ícones do **Material Icon Theme** (MIT), o mesmo do VS Code, com o mapeamento oficial: 1.389 extensões, 2.147 nomes de arquivo (`artisan`, `composer.json`, `Dockerfile`, `.env`...), 4.629 nomes de pasta (app, config, routes, vendor, node_modules...) e variantes para o tema claro.
- Os SVGs são convertidos por `web/build-file-icons.mjs` (`npm run icons`) em `Assets/FileIcons/file-icons.json` (embutido no executável); o WPF monta cada ícone sob demanda.
- Seleção na lista legível em qualquer tema: fundo suave com a barra de destaque à esquerda.

### SSH avançado

- **ProxyJump / bastion:** no editor da sessão, "Bastion (ProxyJump)" escolhe outra sessão salva para entrar primeiro. Vale bastion de bastion (até 5 saltos, ciclos são recusados). Cada bastion usa o próprio login (cofre, chave, agente ou pergunta no terminal). O terminal, o SFTP, o monitor e o teste da chave passam pelo mesmo caminho; o túnel interno escuta só em 127.0.0.1 e a chave de cada servidor é verificada pelo nome real.
- **Túneis (redirecionamento de portas):** botão direito na sessão → Túneis. Local (-L), remoto (-R) e dinâmico/SOCKS (-D), com nome, "abrir ao conectar", abrir/fechar na hora, erros explicados (porta em uso, servidor recusou). Rodam na conexão do terminal (sem novo login); dividir a tela não abre a mesma porta duas vezes e, se o terminal fechar, o túnel passa para outro terminal da mesma sessão. A barra de status mostra quantos estão abertos.
- **SSH Agent:** "Usar as chaves do agente SSH" na sessão tenta as chaves do ssh-agent do Windows (named pipe) ou do Pageant antes da senha. A janela "Chave SSH" lista as chaves carregadas no agente.
- **Grupos:** cada sessão pode ter empresa e filial; a lista pode ser agrupada por Pastas, Empresa › filial ou Ambiente (seletor ao lado da busca). Em grupo: "Abrir as N juntas na mesma tela", "Selecionar as N", "Definir empresa e filial" (também para várias selecionadas).

### Telnet, Serial e tags

- **Telnet** (switches, roteadores, sistemas legados): negociação de tamanho da tela e tipo de terminal; com usuário (e senha salva em Logins salvos) faz o login sozinho. Aviso de que não é criptografado. Conexão rápida com `telnet://equipamento`.
- **Console serial** (COM / adaptador USB-serial): porta, velocidade, bits, paridade, parada e controle de fluxo, com as portas do computador listadas.
- **Tags** nas sessões (PRODUÇÃO, BANCO, CRÍTICO, BACKUP...), com cores, mostradas na frente do nome na lista; agrupar por tags; buscar com `#tag`.

### Painel de arquivos (SFTP)

- Abre sozinho ao conectar e acompanha a pasta atual do terminal (inclusive após `sudo -i`, quando o usuário tem permissão).
- Filtro, arquivos ocultos, caminho editável, Backspace para subir, F5, F2, Del, Ctrl+C (copia caminhos), Ctrl+V (envia arquivos copiados no Explorer).
- Arrastar arquivos e pastas do Windows para enviar (soltar sobre uma pasta envia para dentro dela).
- Baixar para Downloads ou para outra pasta; pastas inteiras, sem sobrescrever arquivos locais.
- Arrastar do painel para uma pasta do Explorer ou para a área de trabalho: o Windows escolhe o destino, pergunta se substitui e mostra o progresso; o conteúdo vem do servidor enquanto é gravado (nada é baixado antes de soltar). Pastas vão inteiras; clicar num item de uma seleção múltipla e arrastar leva todos.
- Duplo clique abre o arquivo no VS Code (ou no programa padrão); cada salvamento é enviado de volta. Avisa se o arquivo mudou no servidor e pede confirmação em produção.
- Fila de transferências com progresso, velocidade, tempo restante e cancelamento (até 3 simultâneas).
- Nova pasta, novo arquivo, renomear, permissões (chmod), excluir com confirmação, "ir para esta pasta no terminal".
- Colunas **Nome, Tamanho, Modificado, Dono, Grupo e Permissões** com cabeçalho sempre visível (como no MobaXterm). Arrastar a divisória do cabeçalho muda a largura; dois cliques nela ajustam a coluna ao maior texto. Com o painel estreito a lista rola para o lado (barra embaixo ou Shift + roda do mouse) e o cabeçalho acompanha.
- Botão direito no cabeçalho (ou o botão "Colunas"): escolher quais colunas aparecem, ajustar todas ao conteúdo, restaurar o padrão e alargar o painel até caberem todas (de novo, volta). O Nome ocupa o espaço que sobra até ser redimensionado. Larguras, colunas e ordem ficam salvas.
- Clicar no título ordena (de novo inverte); pastas ficam sempre em cima. Dono e grupo vêm pelo nome (lidos de `/etc/passwd` e `/etc/group`), com o número quando não há nome.

### Biblioteca de comandos (painel direito, Ctrl+Shift+L)

- 361 comandos em 28 temas: primeiros passos, disco, memória/CPU, processos, serviços, logs, rede, usuários, pacotes, arquivos, tmux, Docker, Git, sistema, segurança, certificados SSL/TLS (validade, cadeia, certbot), Nginx/Apache/PHP, PHP-FPM e Composer, Laravel (deploy, fila, manutenção, logs do dia), banco de dados (MySQL/MariaDB e Redis: só consultas e backup com `mysqldump`, senha sempre pedida na hora), SAP (HANA, sapcontrol, SAP Business One) e Windows (inclusive AD: usuário bloqueado e desbloqueio). Cada um com explicação em português e dica.
- **Sistema** (no topo do painel): Automático, Ubuntu/Debian, RHEL/CentOS/Rocky/Alma, SUSE/SLES (SAP) ou Windows. No automático segue o terminal em foco: servidor SSH pelo `/etc/os-release` (ID e ID_LIKE, lido pelo monitor), PowerShell/CMD e Área de Trabalho Remota como Windows, WSL como Ubuntu. Os comandos mudam por sistema (`apt`/`dnf`/`zypper`, `ufw`/`firewalld`, `auth.log`/`secure`, `needs-restarting`, SELinux/AppArmor, `saptune`...); escolher um sistema fixa a lista e a escolha fica salva.
- **Windows:** atalhos Win+R (`ncpa.cpl`, `services.msc`, `eventvwr.msc`, `diskmgmt.msc`, `appwiz.cpl`, `%temp%`, `shell:startup`...) abrem neste computador; com uma Área de Trabalho Remota em foco, o atalho é copiado para colar lá. Comandos de rede, sistema, serviços, disco, logs e usuários vão para o PowerShell/CMD ativo (ou SSH num servidor Windows); os de PowerShell funcionam também no CMD (`powershell -NoProfile -Command ...`).
- Avisa quando a lista não combina com o terminal (comando de Linux num PowerShell, ou de Windows num servidor Linux) em vez de digitar.
- Clique executa no terminal ativo; comandos com campos abrem um formulário com prévia do comando final. Os valores são escapados para o shell e não podem começar com "-".
- Atalhos do tmux (Ctrl+B D, Ctrl+B %, ...) são enviados como teclas.
- Segurança: comandos que alteram algo pedem confirmação (mais forte em produção); nada destrutivo entra na biblioteca (garantido por teste, `CommandSafety`); se houver um programa de tela cheia aberto (vim, top, less), o painel avisa antes de digitar nele; valores dos comandos de Windows usam aspas do PowerShell ou do CMD (e recusam `%` e `"` no CMD).
- Busca sem acentos e por relevância (palavra no título vem antes da que só aparece na descrição; o Enter usa o primeiro), Favoritos, Usados recentemente, "Meus comandos" (com campos `{{nome}}`), "Minhas macros", copiar e colocar sem executar.
- O conteúdo fica em `src/RootDock.Core/Commands/commands.pt-BR.json`.

### Macros (Ferramentas → Macros)

- Sequência de passos executada no terminal ativo: **Comando**, **Esperar texto** (ex.: `password`, `[Y/n]`, com tempo máximo) e **Pausa**.
- Com "Esperar cada comando terminar" (padrão), o próximo comando só sai quando o prompt do shell volta: a saída é lida sem cores/controles e a linha atual precisa ter cara de prompt (`# $ > %`) ou ser igual ao prompt de antes. Comando seguido de "Esperar texto" não espera o prompt (é o caso das perguntas).
- **Gravar:** grava os comandos executados no prompt do terminal ativo (os mesmos do histórico) até clicar em "Gravando macro" no topo; ao parar, a janela abre com a macro pronta para nomear e editar.
- Andamento no topo da janela ("Macro … · 2/5"); clicar permite parar ou pular uma espera (útil quando o programa ficou esperando uma resposta). Para sozinha se o terminal cair ou se um texto esperado não aparecer.
- Confirmação antes de rodar se algum comando altera algo (`rm`, `kill`, `reboot`...) ou se o servidor é de produção, com a lista dos comandos.
- Também aparecem no painel de comandos, em "Minhas macros", para rodar com um clique. Ficam nas configurações (`settings.json`): não coloque senhas nelas.
- Lógica em `src/RootDock.Core/Macros` (testada com um shell simulado); janela em `Dialogs/MacrosWindow`.

### Barra de status do servidor

- Nome, CPU com gráfico, memória, rede (enviado/recebido) com gráfico, discos, tempo ligado e usuário (sessões abertas).
- Cores de alerta (amarelo/vermelho) para CPU, memória e disco.
- Clique para ver detalhes: sistema, IPs, carga, swap, todos os discos e os processos que mais usam CPU.
- Usa um canal da própria conexão do terminal (sem nova autenticação) e só lê `/proc`.

### Editor de arquivos (estilo VS Code)

- Botão direito num arquivo do painel de arquivos → **Editar no RootDock** (ou dois cliques). Abre numa aba, com o editor do VS Code (Monaco): destaque de sintaxe para PHP, Python, JavaScript/TypeScript, JSON, YAML, SQL, Shell, INI/.env/systemd, XML/HTML/CSS, Markdown, Dockerfile, Go, Java, C#, Ruby, Perl, PowerShell, Nginx, Apache e logs (ERROR/WARN coloridos). A linguagem é reconhecida pelo nome, pelo caminho (`/etc/nginx`, `apache2`) e pelo conteúdo.
- Ctrl+S salva direto no servidor pelo SFTP, no próprio arquivo (dono e permissões preservados). Mantém a codificação (UTF-8 com/sem BOM, UTF-16, Latin-1) e a quebra de linha (LF/CRLF, trocável na barra de status).
- Se o arquivo mudou no servidor depois de aberto, o salvamento para e oferece **Comparar** (lado a lado), **Sobrescrever** ou **Recarregar**. Em produção, pede confirmação ao salvar.
- Busca e substituição, ir para linha (Ctrl+G), minimapa, quebra de linha (Alt+Z), recuo detectado, multi-cursor e tudo do Monaco. A aba se comporta como as outras: arrastar para o lado de um terminal, dividir (Ctrl+Shift+D abre um terminal do mesmo servidor ao lado).
- Fechar com alterações pergunta Salvar / Não salvar / Cancelar; fechar o programa também avisa. Se a sessão cair, o texto continua na aba e dá para salvar uma cópia no computador.
- Arquivos binários ou maiores que 10 MB abrem no programa padrão. Também no menu: "Abrir no VS Code" e "Acompanhar no terminal (tail -f)".

### Área de Trabalho Remota (RDP)

- Sessão do tipo **Área de trabalho remota** no editor de sessões. Abre numa aba do RootDock com o componente oficial do Windows (o mesmo do mstsc), com NLA/CredSSP.
- Login pelos "Logins salvos" da sessão (senha no Cofre do Windows) ou numa janela própria, com "Lembrar a senha".
- Resolução automática: a área de trabalho acompanha o tamanho da aba (e da divisão de tela) e a escala de DPI deste monitor; ou resolução fixa. Opções: copiar e colar, discos, impressoras, som, sessão de administração (/admin).
- **Tela cheia** ocupa o monitor inteiro (a resolução do servidor passa a ser a do monitor e volta ao tamanho da aba ao sair). A barra azul fica fixa no topo: **Restaurar** volta para a aba, Minimizar minimiza o RootDock; também Ctrl+Alt+Break.
- Desconectar, reconectar, "entrar com outro usuário"; erros explicados com a mensagem do Windows. Ctrl+Alt+End faz o papel do Ctrl+Alt+Del.
- **Arquivos do servidor** (botão "Arquivos" na barra da aba, cartão no painel Arquivos, ou botão direito na sessão → "Arquivos do servidor"): abre o gerenciador em duas colunas pelo compartilhamento de arquivos do Windows (SMB, porta 445), com o mesmo login. Mostra as unidades (C$, D$) e as pastas compartilhadas; aceita digitar `C:\pasta` ou `\\servidor\C$\pasta`. Abrir um arquivo usa o programa do Windows e salvar grava direto no servidor (programas e scripts do servidor nunca são executados por aqui).
- **Atalhos do Windows no servidor** (ligado por padrão): com o foco na Área de Trabalho Remota, tecla Windows, Win+R, Alt+Tab e Ctrl+Esc vão para o servidor. Desmarcando na sessão, só em tela cheia (como o mstsc).
- A aba pode ser dividida com terminais (o controle é posicionado sobre o espaço dela e some quando um menu ou diálogo passa por cima).
- O WebView2 desenha a página por composição, por cima de qualquer janela nativa; por isso a janela dele ganha um recorte exatamente onde o controle RDP está visível (`Rdp/WebViewCutout.cs`). Sem o recorte, a sessão conectava mas aparecia a tela escura da aba.

### VNC

- Sessão do tipo **VNC** (Linux, Mac, Raspberry, equipamentos com tela): abre numa aba, com o noVNC desenhando a tela remota. Senha pedida na própria aba e, se quiser, salva no Cofre do Windows (apagada sozinha se for recusada depois).
- Barra: Ctrl+Alt+Del, **Colar texto** (manda a área de transferência deste computador para a remota), ajustar à janela, só visualizar, tela cheia, desconectar/reconectar. Texto copiado lá chega aqui sozinho.
- A conexão passa por uma ponte local do RootDock (WebSocket só em 127.0.0.1, com caminho secreto e origem conferida, uma conexão por ponte). Conexão rápida com `vnc://servidor:5901`.

### FTP e FTPS

- Sessão do tipo **FTP**: FTPS explícito (recomendado), FTPS implícito (990) ou FTP sem criptografia (com aviso). Abre direto no gerenciador de arquivos em duas colunas, com fila, pausar/retomar, comparar e sincronizar.
- Certificado autoassinado ou de CA interna: o RootDock mostra para quem é, quem emitiu, validade e impressão digital SHA-256 e pergunta; se aceito, fica lembrado na sessão (outro certificado volta a perguntar).
- Login pelos Logins salvos (Cofre do Windows) ou pela janela de login; servidores que derrubam conexão parada são reconectados sozinhos. Conexão rápida com `ftp://` ou `ftps://`.

### Executar em vários servidores (MultiExec)

- **Ferramentas → Executar em vários servidores** (ou botão direito numa pasta / em várias sessões marcadas). Escolha os servidores (filtro por nome, IP, empresa ou #tag; "Abertos agora"), escreva o comando (várias linhas viram script) e execute.
- Resultado individual por servidor: situação (OK, erro com código, sem conexão, tempo esgotado, cancelado), tempo, primeira linha da saída e a saída completa (stdout e stderr) ao clicar.
- Lotes (quantos ao mesmo tempo), intervalo entre servidores (deploy escalonado), tempo limite, "parar no primeiro erro" e **Cancelar execução**.
- Usa a conexão do terminal aberto quando houver; senão, o login salvo da sessão (senha do cofre, chave ou agente), sem perguntar nada no meio. Campo opcional de senha para os que não têm senha salva (não é gravada).
- **Proteção contra comandos perigosos:** `rm -r`, `mkfs`, `dd of=/dev/...`, `reboot/shutdown`, `chmod -R 777`, `DROP/TRUNCATE`, `DELETE`/`UPDATE` sem `WHERE`, firewall, parar o SSH, `git push --force`, `docker prune`, `crontab -r`, `migrate:fresh` e outros só liberam o botão depois de digitar **EXECUTAR**. Servidores de produção marcados pedem confirmação.
- Logs salvos por execução (um arquivo por servidor e o resumo), "Copiar relatório", histórico dos comandos usados.

### Terminal profissional

- **Destaque automático** de ERROR, FATAL, CRITICAL, FAILED, Permission denied, Connection refused, Traceback/Exception (vermelho), WARNING/AVISO (amarelo) e `[ OK ]`/`active (running)` (verde), sem alterar o que vem do servidor (vim/htop não são afetados).
- **Histórico de comandos de todas as sessões** (Ctrl+Shift+H ou Ferramentas): busca por palavras, por sessão, colar no terminal, copiar ou executar em vários. Senhas, tokens, `-pSENHA`, `PASSWORD=`, `Authorization:` e afins são mascarados antes de gravar.
- **Gravação e reprodução:** botão de gravação na barra das abas; Ferramentas → Gravações reproduz numa aba com play/pausa, linha do tempo, velocidade (0,5x a 8x) e "pular pausas longas". Formato asciinema (`.cast`).
- **Salvar o terminal** em texto ou HTML com cores (botão na barra das abas), ou copiar tudo.
- **Aparência** (Exibir → Aparência do terminal): 5 temas de cores, fonte (só as de largura fixa instaladas), tamanho, espaço entre linhas, cursor (barra, bloco, sublinhado, piscar), histórico de rolagem, destaque ligado/desligado, copiar ao selecionar e botão direito colar ou abrir menu (copiar, colar, buscar, limpar, salvar). Prévia ao vivo.
- **Proteção de tela** (Exibir → Aparência do terminal): com o terminal parado (10 minutos por padrão), uma cena sorteada aparece por cima dele usando o próprio texto do terminal: Turma ociosa (pinguins de paraquedas, ovelhas que comem letras, um gato que caça o cursor e o Bitinho que escala a janela), Chuva digital (as letras acendem quando a chuva passa) e TV de tubo (o terminal numa TV velha com defeito). O terminal de verdade não muda. Cada terminal conta o próprio tempo; a animação fica até um clique no terminal, e enquanto ela está na tela nada do que for digitado vai para o servidor (os atalhos do RootDock continuam). Botão "Ver agora" para testar.
- **Reabrir as abas da última vez** com um clique na tela inicial.
- **Print do código** (estilo CodeSnap): botão da câmera na barra das abas, Ctrl+Shift+S, botão direito no terminal ou no editor, ou Editar → Print do código. Usa a seleção (sem seleção, o que está na tela) com as cores do terminal ou o destaque de sintaxe do editor. Prévia com moldura macOS/Windows/nenhuma, título, 10 fundos (gradientes, sólido, transparente), espaço ao redor, fonte, números de linha (reais no editor), quebrar linhas longas, remover recuo comum, **ocultar IPs e e-mails**, sombra, cantos arredondados, assinatura e qualidade 1x/2x/3x; clique numa linha da prévia para destacá-la. **Copiar imagem** (PNG com transparência + bitmap para programas antigos), **Salvar PNG** e **Exportar PDF** (página do tamanho da imagem, sem perdas). As opções ficam lembradas.
- **Macros:** em "Meus comandos", um comando com várias linhas é digitado de uma vez; para esperar cada comando, gravar ou responder perguntas, use Ferramentas → Macros.

### Gerenciador de arquivos (duas colunas)

- Como o WinSCP: botão **Abrir lado a lado** no painel de arquivos (SSH/SFTP), FTP/FTPS, e servidores Windows pelo RDP (SMB). Este computador à esquerda, servidor à direita. Arraste entre os lados (ou do Explorer para o servidor) ou use as setas do meio.
- Nos dois lados: **nova pasta (F7)**, **renomear (F2)**, **excluir (Delete)**, atualizar (F5), Backspace sobe. No computador, excluir manda para a Lixeira; no servidor pede confirmação mostrando o que será apagado (com aviso extra em produção).
- Antes de substituir no servidor, mostra o que já existe (tamanho e data dos dois lados) e oferece **Substituir**, **Pular os que existem** ou Cancelar. No computador nada é sobrescrito (cria "nome (1)").
- **Comparar as pastas:** marca o que só existe de um lado, o que tem tamanho diferente (e qual é mais novo) e o que é igual.
- **Sincronizar** em uma direção (com subpastas): copia só o que falta ou mudou, mostra a lista antes e **nunca apaga nada** no destino.
- No servidor (botão direito): tamanho da pasta (`du`), compactar (`.tar.gz`, sem sobrescrever), descompactar numa pasta nova (tar.gz, tar.bz2, tar.xz, tar, zip, gz), trocar dono (`chown`, com dica de `sudo` se não houver permissão).
- Fila com **pausar/retomar** por item ou tudo: o download continua de onde parou; pastas retomadas pulam os arquivos já completos. **Histórico de transferências** (data, sentido, servidor, tamanho, resultado).

### Ferramentas de rede (Ferramentas → Ferramentas de rede)

- Ping, Traceroute, MTR contínuo (perda e tempos por salto), DNS (A, AAAA, MX, TXT, NS, CNAME, SOA, SRV, CAA, PTR, em qualquer servidor DNS), DNS reverso, scanner de portas (lista, faixas ou "comuns"; aberta x fechada x filtrada), calculadora IP (IPv4/IPv6, dividir em sub-redes), varredura de rede (quem responde, com nome e MAC), tabela ARP, WHOIS (segue a IANA até o registro certo, ex.: registro.br), certificado SSL (nomes, emissor, validade, cadeia, protocolo, problemas) e HTTP (status, redirecionamentos, tempos, cabeçalhos, cabeçalhos de segurança).
- "Do terminal" preenche com o servidor do terminal ativo; o botão direito numa sessão tem "Diagnóstico de rede". Resultados em texto para copiar.

### Outras melhorias

- Conexão SSH: o prazo de 15 s vale só até o servidor responder; conferir a chave do servidor ou digitar a senha/código 2FA não derruba mais a conexão por tempo.
- Fechar o RootDock com conexões abertas não dá mais erro ao confirmar "Fechar tudo".
- Downloads nunca substituem um arquivo do computador (nem dois downloads com o mesmo nome na fila): o novo vira "nome (1)". Só "Sincronizar ←", depois de confirmar a lista, atualiza arquivos existentes — e nomes vindos do servidor não conseguem gravar fora da pasta escolhida.
- Retomar continua só da mesma transferência pausada; cancelar ou limpar uma pausada apaga o parcial.
- Salvar no editor guarda a versão atual do servidor antes de gravar e a restaura se a gravação falhar no meio (disco cheio, queda de rede). Arquivo em Latin-1 com caractere que não cabe nele (€, emoji) avisa em vez de gravar "?".
- MultiExec: a confirmação EXECUTAR vale só para o comando que estava escrito; logs, relatório e recentes saem com senhas mascaradas.

## Desempenho e robustez

Medido numa máquina de escritório (i5-1334U, 16 GB, com Visual Studio e navegador abertos):

| | Antes | Agora |
|---|---|---|
| Janela aparece (20 sessões) | 2,1–3,6 s | 1,3 s |
| Terminal pronto para uso | 3,0–4,8 s | 2,0 s |
| Janela aparece com 2.000 sessões | 6,4 s | 1,2 s |
| Busca na árvore com 2.000 sessões (por letra) | até 18 s | 0,1–0,2 s |
| CPU com o programa parado | 0,5–14 % | 0,2 % |
| Memória extra durante saída de 60 MB no terminal | +310 MB | +30 MB |

Como foi feito:

- **Abertura:** o WebView2 começa a subir junto com o programa; preferências e sessões são lidas
  em paralelo, com JSON gerado na compilação (sem reflexão); a janela é desenhada antes de o WebView2 ficar pronto.
  O log registra os tempos de cada etapa a cada abertura ("Inicialização (ms): ...").
- **Terminal:** controle de fluxo por créditos entre o C# e a página: a página confirma o que o xterm já desenhou.
  No SSH, que não segura o servidor, a saída retida tem teto e o excesso vira o aviso "[N MB omitidos]".
  Se o processo de desenho do WebView2 cair, a página volta sozinha com os terminais conectados.
- **Listas:** a árvore de sessões e as filas de transferência são virtualizadas; a busca e o filtro esperam a pausa na digitação.
- **Rede e arquivos:** E/S de disco fora da thread da interface. A sincronização "→ servidor" é cancelada se a listagem
  do servidor ficar incompleta e nunca escreve através de links simbólicos.

Testes de integração opcionais: FTP (`ROOTDOCK_FTP_TEST=1`) e SSH (`ROOTDOCK_SSH_TEST=1` com as variáveis descritas em
`tests/RootDock.Core.Tests/MultiExecOutputTests.cs`).

## Próximas etapas

1. Editor do RootDock também para arquivos de FTP (hoje: abre uma cópia no programa padrão).
