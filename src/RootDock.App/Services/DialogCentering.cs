using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RootDock.App.Services;

/// <summary>
/// Janelas com SizeToContent crescem só para baixo quando o conteúdo muda (ex.: aba "Avançado").
/// Isto mantém o centro vertical onde estava e não deixa a janela sair da área útil do monitor.
/// </summary>
public static class DialogCentering
{
    public static void KeepCentered(Window window)
    {
        // O centro fica guardado (e só muda quando a pessoa arrasta a janela): recalcular a partir
        // da posição acumularia o arredondamento de pixels a cada troca de tamanho.
        double? center = null;
        var moving = false;
        window.LocationChanged += (_, _) =>
        {
            if (!moving && window.IsLoaded)
            {
                center = window.Top + window.ActualHeight / 2;
            }
        };
        window.SizeChanged += (_, e) =>
        {
            if (!window.IsLoaded || !e.HeightChanged || e.PreviousSize.Height <= 0 || window.WindowState != WindowState.Normal)
            {
                return;
            }

            center ??= window.Top + e.PreviousSize.Height / 2;
            var area = WorkArea(window);
            var top = Math.Max(area.Top, Math.Min(center.Value - e.NewSize.Height / 2, area.Bottom - e.NewSize.Height));
            moving = true;
            try
            {
                window.Top = top;
            }
            finally
            {
                moving = false;
            }
        };
    }

    /// <summary>
    /// A janela nunca fica maior que a área útil do monitor (sem a barra de tarefas): limita largura e altura,
    /// puxa a janela para dentro da tela ao abrir e refaz o limite se ela for arrastada para outro monitor.
    /// O conteúdo da janela precisa rolar (ScrollViewer) para caber quando a tela é menor que ele.
    /// </summary>
    public static void FitToWorkArea(Window window, double margin = 12)
    {
        Rect? limited = null;
        void Limit(Rect area)
        {
            if (limited == area)
            {
                return;
            }

            limited = area;
            window.MaxWidth = Math.Max(window.MinWidth, area.Width - margin * 2);
            window.MaxHeight = Math.Max(window.MinHeight, area.Height - margin * 2);
        }

        // Antes do primeiro desenho: pela tela da janela dona, que é onde o CenterOwner põe o diálogo.
        window.SourceInitialized += (_, _) => Limit(window.Owner is { IsLoaded: true } owner ? WorkArea(owner) : SystemParameters.WorkArea);
        window.Loaded += (_, _) =>
        {
            var area = WorkArea(window);
            Limit(area);
            window.Left = Math.Max(area.Left, Math.Min(window.Left, area.Right - window.ActualWidth));
            window.Top = Math.Max(area.Top, Math.Min(window.Top, area.Bottom - window.ActualHeight));
        };
        window.LocationChanged += (_, _) =>
        {
            if (window.IsLoaded && window.WindowState == WindowState.Normal)
            {
                Limit(WorkArea(window));
            }
        };
    }

    /// <summary>Área útil (sem a barra de tarefas) do monitor onde a janela está, em unidades WPF.</summary>
    private static Rect WorkArea(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info) || PresentationSource.FromVisual(window) is not { CompositionTarget: { } target })
        {
            return SystemParameters.WorkArea;
        }

        var fromDevice = target.TransformFromDevice;
        var topLeft = fromDevice.Transform(new Point(info.Work.Left, info.Work.Top));
        var bottomRight = fromDevice.Transform(new Point(info.Work.Right, info.Work.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private const int MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public int Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
