using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using RootDock.App.Services;
using RootDock.App.Terminal;
using RootDock.Core.Recording;
using Wpf.Ui.Controls;

namespace RootDock.App.Dialogs;

/// <summary>Lista das gravações de terminal, com reprodução numa aba do RootDock.</summary>
public partial class RecordingsWindow : FluentWindow
{
    /// <summary>Gravações maiores que isso não são abertas (a reprodução carrega tudo na tela).</summary>
    private const long MaxPlayableBytes = 60L * 1024 * 1024;

    private readonly string _directory;
    private readonly TerminalWorkspace _workspace;

    private sealed record Item(string Path, string Title, DateTime Started, TimeSpan Length, long Bytes)
    {
        public string FileName => System.IO.Path.GetFileName(Path);

        public string When => Started.ToString("dd/MM/yyyy HH:mm");

        public string Duration => Length.TotalHours >= 1 ? Length.ToString(@"h\:mm\:ss") : Length.ToString(@"mm\:ss");

        public string Size => Format.Bytes(Bytes);

        public override string ToString() => Title;
    }

    private RecordingsWindow(string directory, TerminalWorkspace workspace)
    {
        InitializeComponent();
        _directory = directory;
        _workspace = workspace;
        Load();
    }

    public static void ShowFor(Window owner, string directory, TerminalWorkspace workspace) =>
        new RecordingsWindow(directory, workspace) { Owner = owner }.Show();

    private void Load()
    {
        var items = new List<Item>();
        if (Directory.Exists(_directory))
        {
            foreach (var file in new DirectoryInfo(_directory).EnumerateFiles("*.cast").OrderByDescending(f => f.LastWriteTime))
            {
                try
                {
                    var cast = Asciicast.Read(file.FullName, headerOnly: true);
                    items.Add(new Item(file.FullName, cast.Title, cast.Started?.LocalDateTime ?? file.CreationTime, cast.Duration, file.Length));
                }
                catch (Exception)
                {
                    // Qualquer problema no arquivo (cortado, editado à mão): aparece como inválido.
                    items.Add(new Item(file.FullName, file.Name + " (arquivo inválido)", file.CreationTime, TimeSpan.Zero, file.Length));
                }
            }
        }

        List.ItemsSource = items;
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (items.Count > 0)
        {
            List.SelectedIndex = 0;
        }
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not Item item)
        {
            return;
        }

        if (item.Bytes > MaxPlayableBytes)
        {
            _ = Prompts.InfoAsync("Gravação grande demais", $"Esta gravação tem {Format.Bytes(item.Bytes)}; a reprodução abre até {Format.Bytes(MaxPlayableBytes)}. Use o asciinema para arquivos maiores.");
            return;
        }

        try
        {
            var cast = Asciicast.Read(item.Path);
            _workspace.Send(new
            {
                type = "openReplay",
                id = Guid.NewGuid().ToString("N"),
                title = $"▶ {cast.Title}",
                width = cast.Width,
                height = cast.Height,
                duration = cast.Duration.TotalSeconds,
                events = cast.Events.Select(ev => new object[] { Math.Round(ev.Time, 4), ev.Kind, ev.Data }),
            });
            Owner?.Activate();
            _workspace.FocusTerminal();
        }
        catch (Exception ex)
        {
            Log.Error($"Abrir gravação {item.Path}", ex);
            _ = Prompts.InfoAsync("Não foi possível abrir", "A gravação está corrompida ou não é do formato asciinema v2: " + ex.Message);
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_directory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_directory}\"") { UseShellExecute = true });
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not Item item)
        {
            return;
        }

        if (!await Prompts.ConfirmAsync("Excluir gravação", $"Excluir \"{item.FileName}\"? Isso não pode ser desfeito.", "Excluir", danger: true))
        {
            return;
        }

        try
        {
            File.Delete(item.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Prompts.InfoAsync("Não foi possível excluir", ex.Message);
        }

        Load();
    }
}
