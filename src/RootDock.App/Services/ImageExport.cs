using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RootDock.Core.Export;

namespace RootDock.App.Services;

/// <summary>Resultado de copiar/salvar a imagem do print (para a mensagem no diálogo).</summary>
public sealed record ImageExportResult(bool Ok, string Message, string? Path = null, bool Cancelled = false);

/// <summary>Copiar como imagem, salvar PNG e exportar PDF (o PNG vem pronto da tela do print).</summary>
public static class ImageExport
{
    private static string? _lastFolder;

    public static ImageExportResult Copy(byte[] png)
    {
        var image = Decode(png);
        var data = new DataObject();
        data.SetData("PNG", new MemoryStream(png), false); // Teams, WhatsApp, navegadores, Office: mantém a transparência
        data.SetImage(OnWhite(image));                     // programas antigos (bitmap sem transparência)
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                break;
            }
            catch (COMException) when (attempt < 5)
            {
                Thread.Sleep(60); // área de transferência ocupada por outro programa
            }
        }

        return new ImageExportResult(true, $"Imagem copiada ({image.PixelWidth} × {image.PixelHeight}). Cole com Ctrl+V no Teams, WhatsApp, e-mail, Word...");
    }

    public static ImageExportResult SavePng(byte[] png, string title)
    {
        if (Ask(title, "png", "Imagem PNG (*.png)|*.png") is not { } path)
        {
            return new ImageExportResult(false, "", Cancelled: true);
        }

        File.WriteAllBytes(path, png);
        return new ImageExportResult(true, $"Salvo: {Path.GetFileName(path)}", path);
    }

    /// <param name="scale">Escala usada ao gerar o PNG (2 = 2x): o PDF fica no tamanho real da tela.</param>
    public static ImageExportResult SavePdf(byte[] png, string title, double scale)
    {
        if (Ask(title, "pdf", "Documento PDF (*.pdf)|*.pdf") is not { } path)
        {
            return new ImageExportResult(false, "", Cancelled: true);
        }

        var image = new FormatConvertedBitmap(Decode(png), PixelFormats.Bgra32, null, 0);
        var width = image.PixelWidth;
        var height = image.PixelHeight;
        var pixels = new byte[width * height * 4];
        image.CopyPixels(pixels, width * 4, 0);
        var rgb = new byte[width * height * 3];
        var alpha = new byte[width * height];
        var opaque = true;
        for (int i = 0, p = 0; i < alpha.Length; i++, p += 4)
        {
            rgb[i * 3] = pixels[p + 2];
            rgb[(i * 3) + 1] = pixels[p + 1];
            rgb[(i * 3) + 2] = pixels[p];
            alpha[i] = pixels[p + 3];
            opaque &= alpha[i] == 255;
        }

        // Pixels da tela (96 por polegada) para pontos do PDF (72 por polegada).
        var points = 72.0 / 96.0 / Math.Max(0.5, scale);
        var temp = path + ".rootdock-part";
        using (var file = File.Create(temp))
        {
            PdfImageWriter.Write(file, width, height, rgb, opaque ? null : alpha, width * points, height * points, title);
        }

        File.Move(temp, path, overwrite: true);
        return new ImageExportResult(true, $"PDF salvo: {Path.GetFileName(path)}", path);
    }

    private static string? Ask(string title, string extension, string filter)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = extension == "pdf" ? "Exportar o print como PDF" : "Salvar o print como imagem",
            FileName = $"{SafeName(title)} {DateTime.Now:yyyy-MM-dd HH-mm}.{extension}",
            Filter = filter,
            DefaultExt = "." + extension,
            AddExtension = true,
            InitialDirectory = _lastFolder is { } folder && Directory.Exists(folder)
                ? folder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        _lastFolder = Path.GetDirectoryName(dialog.FileName);
        return dialog.FileName;
    }

    private static string SafeName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = string.Concat(title.Select(c => invalid.Contains(c) ? '_' : c)).Trim().Trim('.');
        if (clean.Length > 60)
        {
            clean = clean[..60].Trim();
        }

        return clean.Length > 0 ? $"Print - {clean}" : "Print";
    }

    private static BitmapSource Decode(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        frame.Freeze();
        return frame;
    }

    /// <summary>Versão sem transparência (sobre branco) para quem só entende bitmap comum.</summary>
    private static BitmapSource OnWhite(BitmapSource image)
    {
        var source = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        for (var p = 0; p < pixels.Length; p += 4)
        {
            var a = pixels[p + 3];
            if (a == 255)
            {
                continue;
            }

            for (var c = 0; c < 3; c++)
            {
                pixels[p + c] = (byte)((pixels[p + c] * a + (255 * (255 - a))) / 255);
            }

            pixels[p + 3] = 255;
        }

        var result = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }
}
