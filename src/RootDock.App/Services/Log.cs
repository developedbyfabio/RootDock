using System.IO;
using RootDock.Core;

namespace RootDock.App.Services;

/// <summary>Registro simples de erros em %APPDATA%\RootDock\logs. Nunca grava senhas.</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static void Error(string context, Exception ex) => Write("ERRO", $"{context}: {ex}");

    public static void Info(string message) => Write("INFO", message);

    private static void Write(string level, string message)
    {
        try
        {
            var dir = AppPaths.Default.LogsDirectory;
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"rootdock-{DateTime.Now:yyyyMMdd}.log");
            lock (Gate)
            {
                File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // Sem log não é motivo para derrubar o aplicativo.
        }
    }
}
