using Microsoft.Web.WebView2.Core;

namespace RootDock.App.Services;

/// <summary>Ambiente do WebView2 (terminais, editor, VNC, RDP): as mesmas opções em todo o app.</summary>
internal static class WebViewRuntime
{
    /// <summary>
    /// Desliga o "ocultar ponteiro ao digitar" do WebView2 (versão 152 em diante, quando a opção do Windows está ligada).
    /// Ele esconde o ponteiro pelo contador de exibição do cursor da thread da interface. Se uma janela do RootDock
    /// abre antes de o mouse passar de novo sobre o terminal (chave do servidor, confirmações...), o ponteiro fica
    /// invisível no app inteiro, mesmo clicando. Defeito da Microsoft em aberto:
    /// https://github.com/MicrosoftEdge/WebView2Feedback/issues/5687. Sem o recurso, o ponteiro continua visível
    /// enquanto se digita no terminal, como era até o WebView2 151.
    /// </summary>
    public const string BrowserArguments = "--disable-features=HideCursorWhileTyping";

    /// <summary>HRESULT_FROM_WIN32(ERROR_INVALID_STATE): o navegador da pasta já roda com outras opções.</summary>
    private const int OptionsMismatch = unchecked((int)0x8007139F);

    public static Task<CoreWebView2Environment> CreateAsync(string userDataFolder) =>
        CoreWebView2Environment.CreateAsync(null, userDataFolder, new CoreWebView2EnvironmentOptions(BrowserArguments));

    /// <summary>
    /// Cópias abertas ao mesmo tempo dividem o navegador da pasta do WebView2, e ele recusa opções diferentes:
    /// uma cópia de versão anterior (sem as opções) aberta junto impede esta de montar os terminais.
    /// </summary>
    public static bool IsOptionsMismatch(Exception ex) => ex.HResult == OptionsMismatch;
}
