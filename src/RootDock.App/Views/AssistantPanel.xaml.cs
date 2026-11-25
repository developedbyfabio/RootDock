using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RootDock.App.Assistant;
using RootDock.App.Services;

namespace RootDock.App.Views;

/// <summary>Painel lateral direito do assistente: hospeda a página do chat e a liga ao serviço.</summary>
public partial class AssistantPanel : UserControl
{
    private const string VirtualHost = "rootdock.example";

    private WebView2? _web;
    private bool _ready;
    private string? _theme;

    public AssistantPanel()
    {
        InitializeComponent();
    }

    /// <summary>Cria a página na primeira abertura (quem nunca usa o assistente não paga por ela).</summary>
    public async Task EnsureStartedAsync(Task<CoreWebView2Environment> environment, AssistantService service, System.Windows.Media.Color background)
    {
        if (_web is not null)
        {
            return;
        }

        var web = _web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(background.A, background.R, background.G, background.B) };
        Host.Children.Add(web);
        try
        {
            await web.EnsureCoreWebView2Async(await environment);
        }
        catch (Exception ex)
        {
            Log.Error("Assistente: iniciar a página", ex);
            Host.Children.Remove(web);
            _web = null;
            throw;
        }

        var core = web.CoreWebView2;
        var settings = core.Settings;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
#if !DEBUG
        settings.AreDevToolsEnabled = false;
#endif
        core.SetVirtualHostNameToFolderMapping(VirtualHost, Path.Combine(AppContext.BaseDirectory, "wwwroot"), CoreWebView2HostResourceAccessKind.Deny);
        core.NavigationStarting += (_, e) =>
        {
            // A página nunca navega para fora; links das respostas abrem no navegador padrão.
            if (!e.Uri.StartsWith($"https://{VirtualHost}/", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                OpenExternal(e.Uri);
            }
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        };
        core.WebMessageReceived += (_, e) =>
        {
            try
            {
                using var document = JsonDocument.Parse(e.WebMessageAsJson);
                var message = document.RootElement;
                if (message.ValueKind == JsonValueKind.Object && message.TryGetProperty("type", out var type) && type.GetString() == "ready")
                {
                    _ready = true;
                    if (_theme is not null)
                    {
                        core.PostWebMessageAsJson(_theme);
                    }
                }

                service.OnMessage(message);
            }
            catch (JsonException)
            {
                // mensagem que não é nossa
            }
        };
        service.Post = json =>
        {
            if (_ready)
            {
                core.PostWebMessageAsJson(json);
            }
        };
        core.Navigate($"https://{VirtualHost}/assistant.html");
    }

    /// <summary>Cores do tema do aplicativo (mesma mensagem da página dos terminais).</summary>
    public void SetTheme(object message)
    {
        _theme = JsonSerializer.Serialize(message);
        if (_ready)
        {
            _web?.CoreWebView2.PostWebMessageAsJson(_theme);
        }
    }

    public void FocusInput()
    {
        if (_web is null)
        {
            return;
        }

        _web.Focus();
        if (_ready)
        {
            _web.CoreWebView2.PostWebMessageAsJson("{\"type\":\"focus\"}");
        }
    }

    private static void OpenExternal(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("Assistente: abrir link", ex);
        }
    }
}
