using System.Runtime.InteropServices;
using System.Windows;

namespace RootDock.App.Services;

/// <summary>
/// Área de transferência sem derrubar a interface: ela fica ocupada por outro programa com
/// frequência (o redirecionamento de área de transferência do RDP é o caso clássico) e o WPF lança
/// COMException. Tenta algumas vezes e desiste em silêncio.
/// </summary>
public static class SafeClipboard
{
    public static bool SetText(string text)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(30);
            }
        }

        Log.Info("Área de transferência ocupada: o texto não foi copiado.");
        return false;
    }

    public static IReadOnlyList<string> GetFileDropList()
    {
        try
        {
            return Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList().Cast<string>().ToList() : [];
        }
        catch (COMException)
        {
            return [];
        }
    }
}
