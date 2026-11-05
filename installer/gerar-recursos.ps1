# Gera os recursos do instalador MSI: a arte (Windows Installer exige BMP nos tamanhos fixos abaixo) a partir do ícone.
# Uso: pwsh installer/gerar-recursos.ps1   (rode de novo se trocar o ícone ou os textos)
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$logo = [System.Drawing.Image]::FromFile((Join-Path $root 'src/RootDock.App/Assets/rootdock-256.png'))
$dark1 = [System.Drawing.Color]::FromArgb(255, 24, 18, 52)    # roxo bem escuro
$dark2 = [System.Drawing.Color]::FromArgb(255, 74, 38, 150)   # roxo do ícone
$accent = [System.Drawing.Color]::FromArgb(255, 122, 162, 247)

function New-Canvas($w, $h) {
  $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.TextRenderingHint = 'AntiAliasGridFit'
  return $bmp, $g
}

# Faixa do topo das telas internas (493x58): o Windows escreve título e descrição à esquerda, em preto.
$bmp, $g = New-Canvas 493 58
$g.Clear([System.Drawing.Color]::White)
$fade = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Rectangle 290, 0, 204, 58), ([System.Drawing.Color]::White), ([System.Drawing.Color]::FromArgb(255, 238, 233, 252)), 0.0
$g.FillRectangle($fade, 300, 0, 193, 58)
$g.DrawImage($logo, 437, 7, 44, 44)
$line = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Rectangle 0, 55, 493, 3), $dark2, $accent, 0.0
$g.FillRectangle($line, 0, 55, 493, 3)
$g.Dispose(); $bmp.Save((Join-Path $PSScriptRoot 'msi-banner.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp); $bmp.Dispose()

# Fundo das telas de boas-vindas e conclusão (493x312): coluna da marca à esquerda (164 px), texto à direita.
$bmp, $g = New-Canvas 493 312
$g.Clear([System.Drawing.Color]::White)
$side = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Rectangle 0, 0, 164, 312), $dark2, $dark1, 90.0
$g.FillRectangle($side, 0, 0, 164, 312)
$glow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(40, 122, 162, 247))
$g.FillEllipse($glow, 12, 46, 140, 140)
$g.DrawImage($logo, 34, 68, 96, 96)
$font = New-Object System.Drawing.Font 'Segoe UI Semibold', 17
$small = New-Object System.Drawing.Font 'Segoe UI', 8.5
$center = New-Object System.Drawing.StringFormat; $center.Alignment = 'Center'
$g.DrawString('RootDock', $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, 178, 164, 34), $center)
$soft = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 196, 186, 236))
$g.DrawString("SSH · RDP · VNC · SFTP`nnuma janela só", $small, $soft, (New-Object System.Drawing.RectangleF 0, 214, 164, 40), $center)
$g.FillRectangle((New-Object System.Drawing.SolidBrush $accent), 0, 309, 164, 3)
$g.Dispose(); $bmp.Save((Join-Path $PSScriptRoot 'msi-dialog.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp); $bmp.Dispose()
$logo.Dispose()
"arte gerada em $PSScriptRoot"

# Tela "Informações importantes" (RTF: controle de fonte e marcadores, que o Markdown não dá).
function Rtf([string]$text) {
  $sb = New-Object System.Text.StringBuilder
  foreach ($ch in $text.ToCharArray()) {
    $code = [int]$ch
    if ($ch -eq '\' -or $ch -eq '{' -or $ch -eq '}') { [void]$sb.Append('\').Append($ch) }
    elseif ($code -gt 127) { [void]$sb.Append('\u').Append($code).Append('?') } # "$code?" seria outra variável no PowerShell
    else { [void]$sb.Append($ch) }
  }
  $sb.ToString()
}
$title = { param($t) "{\pard\sb120\sa60\b\fs22 $(Rtf $t)\par}" }
$para = { param($t) "{\pard\sa80 $t\par}" }
$item = { param($t) "{\pard\li160\sa40 \bullet\~\~$t\par}" } # sem tabulação: o controle do instalador usa uma parada larga
$b = { param($t) "{\b $(Rtf $t)}" }
$lines = @(
  "{\rtf1\ansi\ansicpg1252\deff0\uc1{\fonttbl{\f0\fswiss Segoe UI;}}\f0\fs18",
  "{\pard\sa60\b\fs28 RootDock\par}",
  (& $para ((Rtf 'Cliente de terminais e acesso remoto para Windows, criado por ') + (& $b 'Fabio Henrique de Almeida Lemes') + '.')),
  (& $title 'O que vem no RootDock'),
  (& $item ((Rtf 'Terminais ') + (& $b 'SSH, Telnet, Serial') + (Rtf ' e terminal local, em abas e telas divididas'))),
  (& $item ((& $b 'Área de Trabalho Remota (RDP)') + (Rtf ' e ') + (& $b 'VNC') + (Rtf ' dentro de uma aba'))),
  (& $item ((& $b 'Arquivos lado a lado') + (Rtf ': SFTP, FTP/FTPS e pastas de servidores Windows (SMB)'))),
  (& $item ((Rtf 'Editor de arquivos no estilo VS Code e ') + (& $b 'print do código') + (Rtf ' (imagem ou PDF)'))),
  (& $item (Rtf 'Comandos em vários servidores, biblioteca de comandos e ferramentas de rede')),
  (& $item (Rtf 'Temas claro, escuro e especiais (inclusive Matrix)')),
  (& $title 'Onde ficam seus dados'),
  (& $item ((Rtf 'Sessões e preferências: ') + (& $b '%APPDATA%\RootDock'))),
  (& $item (Rtf 'Senhas: Cofre de Credenciais do Windows (nunca em arquivo)')),
  (& $title 'Atualizações'),
  (& $para (Rtf 'O RootDock procura versões novas sozinho e instala quando você fecha o programa. Em Ajuda > Verificar atualizações dá para ver as novidades ou desligar o modo automático.')),
  (& $para (Rtf 'Instalando "para todos os usuários", o Windows pede permissão de administrador também nas atualizações.')),
  "}"
)
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'leia-me.rtf'), ($lines -join "`r`n"), [Text.Encoding]::ASCII)
"leia-me.rtf gerado"
