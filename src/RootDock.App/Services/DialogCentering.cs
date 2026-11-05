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
