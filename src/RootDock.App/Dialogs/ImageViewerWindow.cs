using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RootDock.App.Services;

namespace RootDock.App.Dialogs;

/// <summary>
/// Imagem do chat em tamanho grande, centralizada sobre a janela do RootDock com o fundo
/// escurecido. Fecha com Esc, com o X ou clicando fora da imagem; o botão direito copia ou salva.
/// </summary>
public sealed class ImageViewerWindow : Window
{
    private readonly BitmapSource _image;
    private readonly byte[] _bytes;

    private ImageViewerWindow(Window owner, BitmapSource image, byte[] bytes)
    {
        _image = image;
        _bytes = bytes;
        Owner = owner;
        Title = "Imagem";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromArgb(0x73, 0, 0, 0)); // translúcido: o RootDock continua visível atrás
        WindowStartupLocation = WindowStartupLocation.Manual;

        // Cobre exatamente a janela principal (vale também maximizada e em outro monitor).
        var origin = owner.PointToScreen(new Point(0, 0));
        if (PresentationSource.FromVisual(owner)?.CompositionTarget is { } target)
        {
            origin = target.TransformFromDevice.Transform(origin);
        }

        Left = origin.X;
        Top = origin.Y;
        Width = owner.ActualWidth;
        Height = owner.ActualHeight;

        var picture = new Image
        {
            Source = image,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly, // imagem pequena não é ampliada (ficaria borrada)
            SnapsToDevicePixels = true,
            ContextMenu = Menu(image, bytes, owner),
        };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
        picture.MouseLeftButtonDown += (_, e) => e.Handled = true; // clique na imagem não fecha

        var frame = new Border
        {
            Child = picture,
            Margin = new Thickness(48),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Black,
        };

        var close = new Button
        {
            Content = "✕",
            Width = 34,
            Height = 34,
            FontSize = 15,
            Margin = new Thickness(0, 10, 12, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0x90, 0x20, 0x20, 0x20)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)),
            Cursor = Cursors.Hand,
            ToolTip = "Fechar (Esc)",
        };
        close.Click += (_, _) => Close();

        var hint = new TextBlock
        {
            Text = "Esc ou clique fora para fechar · botão direito para copiar ou salvar",
            Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 16),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
        };

        // Fundo declarado (transparente): sem ele, a regra geral do App pinta o painel raiz com a cor
        // do tema, e o modal fica opaco.
        Content = new Grid { Background = Brushes.Transparent, Children = { frame, hint, close } };
        MouseLeftButtonDown += (_, _) => Close();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                Copy(image);
            }
        };
    }

    /// <summary>Abre a imagem sobre a janela dada. false = os dados não são uma imagem que dê para mostrar.</summary>
    public static bool Show(Window owner, byte[] bytes)
    {
        if (Decode(bytes) is not { } image)
        {
            return false;
        }

        new ImageViewerWindow(owner, image, bytes).ShowDialog();
        return true;
    }

    /// <summary>Só o menu (copiar, salvar, abrir), na posição do mouse: botão direito na miniatura do chat.</summary>
    public static bool ShowMenu(Window owner, byte[] bytes)
    {
        if (Decode(bytes) is not { } image)
        {
            return false;
        }

        var menu = Menu(image, bytes, owner);
        var open = new MenuItem { Header = "Abrir em tamanho grande" };
        open.Click += (_, _) => new ImageViewerWindow(owner, image, bytes).ShowDialog();
        menu.Items.Insert(0, open);
        menu.Items.Insert(1, new Separator());
        menu.PlacementTarget = owner;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
        return true;
    }

    private static ContextMenu Menu(BitmapSource image, byte[] bytes, Window owner)
    {
        var copy = new MenuItem { Header = "Copiar imagem", InputGestureText = "Ctrl+C" };
        copy.Click += (_, _) => Copy(image);
        var save = new MenuItem { Header = "Salvar como..." };
        save.Click += (_, _) => Save(bytes, owner);
        return new ContextMenu { Items = { copy, save } };
    }

    private static BitmapSource? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or ArgumentException or COMException)
        {
            return null;
        }
    }

    private static void Copy(BitmapSource image)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Clipboard.SetImage(image);
                return;
            }
            catch (COMException)
            {
                Thread.Sleep(30); // outro programa está com a área de transferência
            }
        }

        Log.Info("Área de transferência ocupada: a imagem não foi copiada.");
    }

    private static void Save(byte[] bytes, Window owner)
    {
        var png = bytes.Length > 3 && bytes[0] == 0x89 && bytes[1] == 0x50;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "imagem-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + (png ? ".png" : ".jpg"),
            Filter = png ? "Imagem PNG|*.png" : "Imagem JPEG|*.jpg",
        };
        if (dialog.ShowDialog(owner) != true)
        {
            return;
        }

        try
        {
            File.WriteAllBytes(dialog.FileName, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(owner, "Não foi possível salvar a imagem: " + ex.Message, "RootDock", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
