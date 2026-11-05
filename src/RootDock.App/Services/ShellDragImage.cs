using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RootDock.Core.Shell;

namespace RootDock.App.Services;

/// <summary>
/// Imagem que acompanha o cursor ao arrastar arquivos para o Explorer: ícone e nome (ou "N itens")
/// numa etiqueta com as cores do tema, como a do próprio Windows.
/// </summary>
internal static class ShellDragImage
{
    public static DragImage? Create(ImageSource? icon, string text, Visual reference)
    {
        try
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            if (icon is not null)
            {
                label.Children.Add(new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            }

            label.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 13,
                MaxWidth = 300,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Brush("RdText", Colors.White),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var card = new Border
            {
                Background = Brush("RdSurface", Color.FromRgb(0x17, 0x1B, 0x23)),
                BorderBrush = Brush("RdAccent", Color.FromRgb(0x7A, 0xA2, 0xF7)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(9, 5, 11, 5),
                Opacity = 0.92,
                Child = label,
            };
            TextOptions.SetTextFormattingMode(card, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(card, TextRenderingMode.Grayscale); // sem ClearType sobre fundo com alfa

            card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            card.Arrange(new Rect(card.DesiredSize));
            card.UpdateLayout();

            var dpi = VisualTreeHelper.GetDpi(reference);
            var width = Math.Max(1, (int)Math.Ceiling(card.ActualWidth * dpi.DpiScaleX));
            var height = Math.Max(1, (int)Math.Ceiling(card.ActualHeight * dpi.DpiScaleY));
            var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(card);
            var stride = width * 4;
            var pixels = new byte[stride * height];
            bitmap.CopyPixels(pixels, stride, 0);

            var hbitmap = Dib(width, height, pixels, stride);
            if (hbitmap == IntPtr.Zero)
            {
                return null;
            }

            // Cursor sobre o ícone: a etiqueta vai à direita dele e a dica "Copiar para..." logo abaixo.
            return new DragImage(hbitmap, width, height, (int)(14 * dpi.DpiScaleX), height / 2);
        }
        catch (Exception ex)
        {
            Log.Info($"Imagem do arrastar: {ex.Message}");
            return null; // arrasta sem imagem
        }
    }

    private static Brush Brush(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    /// <summary>DIB de 32 bits de baixo para cima (o formato que o shell espera), alfa pré-multiplicado.</summary>
    private static IntPtr Dib(int width, int height, byte[] pixels, int stride)
    {
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = height,
            biPlanes = 1,
            biBitCount = 32,
        };
        var hbitmap = CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
        if (hbitmap == IntPtr.Zero || bits == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        for (var y = 0; y < height; y++)
        {
            Marshal.Copy(pixels, (height - 1 - y) * stride, bits + y * stride, stride);
        }

        return hbitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage, out IntPtr bits, IntPtr hSection, uint offset);
}
