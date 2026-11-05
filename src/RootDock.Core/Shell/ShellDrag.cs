using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace RootDock.Core.Shell;

/// <summary>Imagem que acompanha o cursor (32 bits, alfa pré-multiplicado, linhas de baixo para cima).</summary>
/// <param name="Bitmap">HBITMAP de uma DIB; passa a ser do Windows se a imagem for aceita.</param>
public sealed record DragImage(IntPtr Bitmap, int Width, int Height, int OffsetX, int OffsetY);

/// <summary>
/// Arrastar arquivos virtuais para fora do RootDock (Explorer, área de trabalho). Precisa ser
/// chamado na thread da interface (STA), dentro do movimento do mouse com o botão apertado.
/// </summary>
public static class ShellDrag
{
    public const int DropEffectNone = 0;
    public const int DropEffectCopy = 1;

    private const int DRAGDROP_S_DROP = 0x00040100;
    private const int DRAGDROP_S_CANCEL = 0x00040101;
    private const int DRAGDROP_S_USEDEFAULTCURSORS = 0x00040102;
    private const int MK_LBUTTON = 0x0001;
    private const int DSH_ALLOWDROPDESCRIPTIONTEXT = 0x0001;

    /// <summary>Arrasta e retorna se foi solto num destino que aceitou (a cópia pode continuar depois).</summary>
    public static bool DoDragDrop(VirtualFileDataObject data, DragImage? image)
    {
        var proxy = CreateMtaProxy(data);
        var dropped = false;
        try
        {
            data.SetAsyncMode(1);
            if (image is not null)
            {
                SetImage(proxy, image);
            }

            var source = Marshal.GetComInterfaceForObject(new DropSource(data), typeof(IDropSource));
            try
            {
                var hr = Native.DoDragDrop(proxy, source, DropEffectCopy, out var effect);
                dropped = hr == DRAGDROP_S_DROP && effect != DropEffectNone;
            }
            finally
            {
                Marshal.Release(source);
            }
        }
        finally
        {
            Marshal.Release(proxy);
            data.DragEnded(dropped);
        }

        return dropped;
    }

    /// <summary>
    /// Publica o objeto a partir do MTA e devolve um proxy para a thread atual. Quem receber esse
    /// proxy (o Explorer, em outro processo) fala direto com o MTA.
    /// </summary>
    public static IntPtr CreateMtaProxy(VirtualFileDataObject data)
    {
        var iid = typeof(ComDataObject).GUID;
        var stream = IntPtr.Zero;
        Mta.Run(() =>
        {
            var unknown = Marshal.GetComInterfaceForObject(data, typeof(ComDataObject));
            try
            {
                Marshal.ThrowExceptionForHR(Native.CoMarshalInterThreadInterfaceInStream(ref iid, unknown, out stream));
            }
            finally
            {
                Marshal.Release(unknown);
            }
        });
        Marshal.ThrowExceptionForHR(Native.CoGetInterfaceAndReleaseStream(stream, ref iid, out var proxy));
        return proxy;
    }

    private static void SetImage(IntPtr dataObject, DragImage image)
    {
        object? helper = null;
        try
        {
            helper = Activator.CreateInstance(Type.GetTypeFromCLSID(Native.CLSID_DragDropHelper)!);
            if (helper is IDragSourceHelper2 two)
            {
                two.SetFlags(DSH_ALLOWDROPDESCRIPTIONTEXT); // "Copiar para Downloads" ao lado do cursor
            }

            var shdi = new SHDRAGIMAGE
            {
                sizeDragImage = new SIZE { cx = image.Width, cy = image.Height },
                ptOffset = new POINT { x = image.OffsetX, y = image.OffsetY },
                hbmpDragImage = image.Bitmap,
                crColorKey = unchecked((int)0xFFFFFFFF), // CLR_NONE: usa o alfa
            };
            if (helper is not IDragSourceHelper2 h || h.InitializeFromBitmap(ref shdi, dataObject) < 0)
            {
                Native.DeleteObject(image.Bitmap); // recusada: a imagem continua nossa
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Native.DeleteObject(image.Bitmap); // sem imagem o arrastar funciona do mesmo jeito
        }
        finally
        {
            if (helper is not null)
            {
                Marshal.ReleaseComObject(helper);
            }
        }
    }

    /// <summary>Quando soltar ou cancelar; cursores padrão (ou a seta quando o Explorer desenha a dica).</summary>
    [ComVisible(true)]
    private sealed class DropSource(VirtualFileDataObject data) : IDropSource
    {
        public int QueryContinueDrag(int fEscapePressed, int grfKeyState) =>
            fEscapePressed != 0 ? DRAGDROP_S_CANCEL
            : (grfKeyState & MK_LBUTTON) == 0 ? DRAGDROP_S_DROP
            : 0;

        public int GiveFeedback(int dwEffect)
        {
            if (data.IsShowingLayered)
            {
                Native.SetCursor(Native.LoadCursor(IntPtr.Zero, Native.IDC_ARROW));
                return 0;
            }

            return DRAGDROP_S_USEDEFAULTCURSORS;
        }
    }

    /// <summary>Thread fixa no MTA: mantém o apartamento vivo enquanto o Explorer copia.</summary>
    private static class Mta
    {
        private static readonly BlockingCollection<Action> Work = [];

        static Mta()
        {
            var thread = new Thread(() =>
            {
                Native.CoInitializeEx(IntPtr.Zero, Native.COINIT_MULTITHREADED);
                foreach (var action in Work.GetConsumingEnumerable())
                {
                    action();
                }
            })
            { IsBackground = true, Name = "RootDock COM (MTA)" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public static void Run(Action action)
        {
            using var done = new ManualResetEventSlim();
            ExceptionDispatchInfo? error = null;
            Work.Add(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    error = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    done.Set();
                }
            });
            done.Wait();
            error?.Throw();
        }
    }
}

[ComImport, Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDropSource
{
    [PreserveSig] int QueryContinueDrag(int fEscapePressed, int grfKeyState);

    [PreserveSig] int GiveFeedback(int dwEffect);
}

[ComImport, Guid("83E07D0D-0C5F-4163-BF1A-60B274051E40"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDragSourceHelper2
{
    [PreserveSig] int InitializeFromBitmap(ref SHDRAGIMAGE pshdi, IntPtr pDataObject);

    [PreserveSig] int InitializeFromWindow(IntPtr hwnd, ref POINT ppt, IntPtr pDataObject);

    [PreserveSig] int SetFlags(int dwFlags);
}

[StructLayout(LayoutKind.Sequential)]
internal struct SIZE
{
    public int cx;
    public int cy;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int x;
    public int y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SHDRAGIMAGE
{
    public SIZE sizeDragImage;
    public POINT ptOffset;
    public IntPtr hbmpDragImage;
    public int crColorKey;
}

internal static class Native
{
    public const int GMEM_MOVEABLE = 0x0002;
    public const int COINIT_MULTITHREADED = 0x0;
    public const int FD_ATTRIBUTES = 0x00000004;
    public const int FD_WRITESTIME = 0x00000020;
    public const int FD_FILESIZE = 0x00000040;
    public const int FD_PROGRESSUI = 0x00004000;
    public const int FD_UNICODE = unchecked((int)0x80000000);
    public const int FILE_ATTRIBUTE_DIRECTORY = 0x10;
    public const int FILE_ATTRIBUTE_NORMAL = 0x80;
    public static readonly IntPtr IDC_ARROW = 32512;

    public static readonly Guid IID_IMarshal = new("00000003-0000-0000-C000-000000000046");
    public static readonly Guid IID_IAgileObject = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");
    public static readonly Guid CLSID_DragDropHelper = new("4657278A-411B-11D2-839A-00C04FD918D0");

    [DllImport("ole32.dll")]
    public static extern int DoDragDrop(IntPtr pDataObj, IntPtr pDropSource, int dwOKEffects, out int pdwEffect);

    [DllImport("ole32.dll")]
    public static extern int CoMarshalInterThreadInterfaceInStream(ref Guid riid, IntPtr pUnk, out IntPtr ppStm);

    [DllImport("ole32.dll")]
    public static extern int CoGetInterfaceAndReleaseStream(IntPtr pStm, ref Guid iid, out IntPtr ppv);

    [DllImport("ole32.dll")]
    public static extern int CoInitializeEx(IntPtr pvReserved, int dwCoInit);

    [DllImport("ole32.dll")]
    public static extern void ReleaseStgMedium(ref System.Runtime.InteropServices.ComTypes.STGMEDIUM pmedium);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("user32.dll")]
    public static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll")]
    public static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalAlloc(int uFlags, nint dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint GlobalSize(IntPtr hMem);
}
