using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RootDock.App.Services;

/// <summary>
/// MessageBox do WPF-UI que abre centralizada sobre a janela do RootDock, no monitor em que ela
/// está. A original se centraliza no monitor principal quando a janela dona está maximizada (ou
/// não tem dona): com dois monitores, a confirmação aparecia na outra tela.
/// </summary>
public class AppMessageBox : Wpf.Ui.Controls.MessageBox
{
    public AppMessageBox()
    {
        // Estilo implícito vale só para o tipo exato: sem isto a subclasse ficava sem o modelo do
        // WPF-UI (título, margens, botões) e a caixa saía pequena e vazia.
        SetResourceReference(StyleProperty, typeof(Wpf.Ui.Controls.MessageBox));
    }

    /// <summary>
    /// Chamado pelo WPF-UI no Loaded, já com o tamanho final. O original centraliza no monitor
    /// principal (SystemParameters.PrimaryScreenWidth); aqui vai para o centro da janela ativa.
    /// </summary>
    protected override void CenterWindowOnScreen()
    {
        if (!DialogPlacement.CenterOver(this, Owner ?? OwnerCandidate()))
        {
            base.CenterWindowOnScreen();
        }
    }

    /// <summary>Janela ativa do RootDock (diálogo aberto por outro diálogo fica sobre ele), senão a principal.</summary>
    private static Window? OwnerCandidate()
    {
        var windows = Application.Current?.Windows.OfType<Window>().Where(w => w.IsVisible && w is not Wpf.Ui.Controls.MessageBox).ToList() ?? [];
        return windows.FirstOrDefault(w => w.IsActive) ?? (Application.Current?.MainWindow is { IsVisible: true } main ? main : windows.FirstOrDefault());
    }
}

/// <summary>Posiciona uma janela no centro de outra, em pixels físicos (vale para monitores com escalas diferentes).</summary>
internal static class DialogPlacement
{
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    public static bool CenterOver(Window window, Window? owner)
    {
        if (owner is null)
        {
            return false;
        }

        var ownerHwnd = new WindowInteropHelper(owner).Handle;
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (ownerHwnd == IntPtr.Zero || !GetWindowRect(ownerHwnd, out var o))
        {
            return false;
        }

        // Minimizada: o retângulo não serve; usa o monitor dela.
        var monitor = MonitorFromWindow(ownerHwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        var work = info.rcWork;
        if (owner.WindowState == WindowState.Minimized)
        {
            o = work;
        }

        // Tamanho na escala do monitor de destino: indo para um monitor com outra escala (ex.: 125% →
        // 100%), o WPF mantém o tamanho em DIPs e o tamanho em pixels muda junto.
        var scale = GetDpiForWindow(ownerHwnd) / 96.0;
        var width = (int)Math.Round((double.IsNaN(window.Width) ? window.ActualWidth : window.Width) * scale);
        var height = (int)Math.Round((double.IsNaN(window.Height) ? window.ActualHeight : window.Height) * scale);
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var x = (o.Left + o.Right) / 2 - width / 2;
        var y = (o.Top + o.Bottom) / 2 - height / 2;
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));
        return SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
