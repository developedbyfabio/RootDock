using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace RootDock.App.Rdp;

/// <summary>
/// O WebView2 desenha a página por composição (DirectComposition), e esse conteúdo fica por cima
/// de qualquer janela nativa irmã, mesmo que ela esteja acima na ordem das janelas. Sem isto, o
/// controle de Área de Trabalho Remota conecta e desenha, mas a página o cobre (tela "preta", que
/// na verdade é o fundo da aba). A janela do WebView2 ganha uma região com buracos exatamente onde
/// há controles nativos visíveis; sem nenhum visível, a região é removida.
/// </summary>
internal sealed class WebViewCutout
{
    private const int RgnDiff = 4;

    /// <summary>Tentativas enquanto a janela nativa ainda não chegou ao lugar (25 ms cada).</summary>
    private const int MaxRetries = 80;

    private readonly HwndHost _webView;
    private readonly UIElement _layer;
    private readonly Func<IEnumerable<HwndHost>> _holes;
    private readonly DispatcherTimer _retry;
    private string _applied = "";
    private bool _queued;
    private bool _watching;
    private int _retries;

    public WebViewCutout(HwndHost webView, UIElement layer, Func<IEnumerable<HwndHost>> holes)
    {
        _webView = webView;
        _layer = layer;
        _holes = holes;
        _retry = new DispatcherTimer(DispatcherPriority.Background, webView.Dispatcher) { Interval = TimeSpan.FromMilliseconds(25) };
        _retry.Tick += (_, _) =>
        {
            _retry.Stop();
            Invalidate();
        };
    }

    /// <summary>
    /// Liga enquanto houver abas de RDP. Toda mudança de layout (aba trocada, divisão, painel
    /// lateral, DPI ao mudar de monitor) refaz o recorte; a posição é lida das próprias janelas.
    /// </summary>
    public void Watch(bool on)
    {
        if (on != _watching)
        {
            _watching = on;
            if (on)
            {
                _layer.LayoutUpdated += OnLayoutUpdated;
            }
            else
            {
                _layer.LayoutUpdated -= OnLayoutUpdated;
                _retry.Stop();
                _retries = 0;
            }
        }

        Invalidate();
    }

    public void Invalidate()
    {
        if (_queued)
        {
            return;
        }

        _queued = true;

        // Depois do layout: o WPF já moveu e mostrou/escondeu as janelas nativas.
        _webView.Dispatcher.BeginInvoke(Apply, DispatcherPriority.Loaded);
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => Invalidate();

    private void Apply()
    {
        _queued = false;
        var target = _webView.Handle;
        if (target == IntPtr.Zero || !GetWindowRect(target, out var frame))
        {
            return;
        }

        // O WPF posiciona e mostra a janela nativa um pouco depois do layout: o buraco segue a
        // janela real e, enquanto ela não estiver onde o layout diz, tenta de novo em seguida.
        var holes = new List<Rect32>();
        var settling = false;
        foreach (var host in _holes())
        {
            if (!host.IsVisible)
            {
                continue;
            }

            var handle = host.Handle;
            if (handle == IntPtr.Zero || !IsWindowVisible(handle) || !GetWindowRect(handle, out var r) || r.Right <= r.Left || r.Bottom <= r.Top)
            {
                settling = true;
                continue;
            }

            var hole = new Rect32(r.Left - frame.Left, r.Top - frame.Top, r.Right - frame.Left, r.Bottom - frame.Top);
            settling |= !Matches(hole, Expected(host));
            holes.Add(hole);
        }

        if (settling && _retries < MaxRetries)
        {
            _retries++;
            _retry.Start();
        }
        else if (!settling)
        {
            _retries = 0;
        }

        var key = string.Join(";", holes);
        if (key == _applied)
        {
            return;
        }

        _applied = key;
        if (holes.Count == 0)
        {
            SetWindowRgn(target, IntPtr.Zero, true);
            return;
        }

        // Região maior que a janela: redimensionar o WebView2 não corta a página.
        var region = CreateRectRgn(0, 0, short.MaxValue, short.MaxValue);
        foreach (var hole in holes)
        {
            var cut = CreateRectRgn(hole.Left, hole.Top, hole.Right, hole.Bottom);
            CombineRgn(region, region, cut, RgnDiff);
            DeleteObject(cut);
        }

        if (SetWindowRgn(target, region, true) == 0)
        {
            DeleteObject(region); // em caso de sucesso, a região passa a ser do sistema
        }
    }

    /// <summary>Onde o layout do WPF põe o controle (pixels, relativo à janela do WebView2).</summary>
    private Rect32? Expected(HwndHost host)
    {
        try
        {
            var scale = PresentationSource.FromVisual(_webView)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            var origin = host.TransformToVisual(_webView).Transform(new Point(0, 0));
            return new Rect32(
                (int)Math.Round(origin.X * scale),
                (int)Math.Round(origin.Y * scale),
                (int)Math.Round((origin.X + host.ActualWidth) * scale),
                (int)Math.Round((origin.Y + host.ActualHeight) * scale));
        }
        catch (InvalidOperationException)
        {
            return null; // fora da árvore visual (sendo removido)
        }
    }

    private static bool Matches(Rect32 actual, Rect32? expected) =>
        expected is not { } e ||
        (Math.Abs(actual.Left - e.Left) <= 2 && Math.Abs(actual.Top - e.Top) <= 2 &&
         Math.Abs(actual.Right - e.Right) <= 2 && Math.Abs(actual.Bottom - e.Bottom) <= 2);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32(int left, int top, int right, int bottom)
    {
        public int Left = left;
        public int Top = top;
        public int Right = right;
        public int Bottom = bottom;

        public override readonly string ToString() => $"{Left},{Top},{Right},{Bottom}";
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect32 rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr region, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr destination, IntPtr source1, IntPtr source2, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
