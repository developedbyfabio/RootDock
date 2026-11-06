using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RootDock.App.Services;
using RootDock.App.ViewModels;
using RootDock.Core.Macros;
using RootDock.Core.Settings;
using Wpf.Ui.Controls;

namespace RootDock.App.Dialogs;

/// <summary>
/// Ferramentas → Macros: lista, editor de passos (comando, esperar texto, pausa), gravar e executar.
/// Tudo é salvo sozinho (ao trocar de macro, executar, gravar ou fechar).
/// </summary>
public partial class MacrosWindow : FluentWindow
{
    private static MacrosWindow? _open;

    private readonly AppSettings _settings;
    private readonly Action _save;
    private readonly Action _changed;
    private readonly Func<CommandMacro, Task<bool>> _run;
    private readonly Func<bool> _record;
    private readonly Func<string?> _target;
    private readonly ObservableCollection<MacroEditorViewModel> _items = [];
    private readonly DispatcherTimer _autosave;
    private bool _reloading;

    private MacrosWindow(AppSettings settings, Action save, Action changed, Func<CommandMacro, Task<bool>> run, Func<bool> record, Func<string?> target)
    {
        InitializeComponent();
        _settings = settings;
        _save = save;
        _changed = changed;
        _run = run;
        _record = record;
        _target = target;
        // Edição contínua (digitando): salva um pouco depois de parar, para não perder nada se o programa fechar.
        _autosave = new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, (_, _) => Commit(), Dispatcher);
        _autosave.Stop();
        List.ItemsSource = _items;
        Reload(null);
        Activated += (_, _) => UpdateTarget();
        Deactivated += (_, _) => Commit();
        Closing += OnClosingWindow;
        Closed += (_, _) => _open = null;
    }

    private MacroEditorViewModel? Current => List.SelectedItem as MacroEditorViewModel;

    /// <summary>Abre (ou traz para a frente) a janela, opcionalmente já numa macro.</summary>
    public static void ShowFor(Window owner, AppSettings settings, Action save, Action changed, Func<CommandMacro, Task<bool>> run, Func<bool> record,
        Func<string?> target, string? select = null, bool rename = false)
    {
        if (_open is null)
        {
            _open = new MacrosWindow(settings, save, changed, run, record, target) { Owner = owner };
            _open.Show();
        }
        else
        {
            _open.Commit();
            _open.Activate();
        }

        _open.Reload(select ?? _open.Current?.Id);
        if (rename)
        {
            _open.Dispatcher.BeginInvoke(() =>
            {
                _open?.NameBox.Focus();
                _open?.NameBox.SelectAll();
            }, DispatcherPriority.Input);
        }
    }

    /// <summary>As macros mudaram fora daqui (excluída pelo painel de comandos): mostra a lista atual.</summary>
    public static void RefreshIfOpen()
    {
        if (_open is { } window)
        {
            window._autosave.Stop();
            window.Reload(window.Current?.Id);
        }
    }

    private void Reload(string? select)
    {
        // Esvaziar a lista dispara a troca de seleção, que salva: sem esta trava, salvaria a lista
        // vazia por cima das macros.
        _reloading = true;
        try
        {
            Fill(select);
        }
        finally
        {
            _reloading = false;
        }

        ShowEditor();
    }

    private void Fill(string? select)
    {
        foreach (var item in _items)
        {
            item.PropertyChanged -= OnItemChanged;
            item.Steps.CollectionChanged -= OnStepsChanged;
        }

        _items.Clear();
        foreach (var macro in _settings.Macros)
        {
            var item = MacroEditorViewModel.From(macro);
            item.PropertyChanged += OnItemChanged;
            item.Steps.CollectionChanged += OnStepsChanged;
            foreach (var step in item.Steps)
            {
                step.PropertyChanged += OnStepChanged;
            }

            _items.Add(item);
        }

        List.SelectedItem = _items.FirstOrDefault(i => i.Id == select) ?? _items.FirstOrDefault();
        ListEmpty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowEditor()
    {
        var current = Current;
        Editor.DataContext = current;
        Editor.Visibility = current is null ? Visibility.Collapsed : Visibility.Visible;
        EditorEmpty.Visibility = current is null ? Visibility.Visible : Visibility.Collapsed;
        UpdateTarget();
    }

    private void UpdateTarget()
    {
        var target = _target();
        TargetText.Text = target is null ? "Nenhum terminal ativo: abra ou selecione um terminal" : $"Vai executar em {target}";
        TargetDot.Fill = (System.Windows.Media.Brush)FindResource(target is null ? "RdTextDim" : "RdDev");
        RunButton.IsEnabled = target is not null && Current is { HasSteps: true };
    }

    /// <summary>
    /// Grava as edições da tela. Só atualiza as macros que existem (pelo Id): criar, duplicar e
    /// excluir mexem direto nas configurações, e uma macro gravada com a janela aberta não se perde.
    /// </summary>
    private void Commit()
    {
        _autosave.Stop();
        if (_reloading)
        {
            return; // a lista está sendo montada a partir das configurações
        }

        var changed = false;
        foreach (var item in _items)
        {
            var model = item.ToModel();
            var index = _settings.Macros.FindIndex(m => m.Id == model.Id);
            if (index >= 0 && !Same(model, _settings.Macros[index]))
            {
                _settings.Macros[index] = model;
                changed = true;
            }
        }

        if (changed)
        {
            _save();
            _changed();
        }
    }

    private static bool Same(CommandMacro a, CommandMacro b) =>
        a.Id == b.Id && a.Name == b.Name && a.Description == b.Description && a.WaitForPrompt == b.WaitForPrompt &&
        a.Steps.Count == b.Steps.Count &&
        a.Steps.Zip(b.Steps).All(s => s.First.Kind == s.Second.Kind && s.First.Text == s.Second.Text && s.First.Seconds.Equals(s.Second.Seconds));

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        Touch();
        if (e.PropertyName == nameof(MacroEditorViewModel.HasSteps))
        {
            UpdateTarget();
        }
    }

    private void OnStepsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        Touch();
        if (e.NewItems is { } added)
        {
            foreach (MacroStepViewModel step in added)
            {
                step.PropertyChanged -= OnStepChanged; // Move reinsere o mesmo passo
                step.PropertyChanged += OnStepChanged;
            }
        }
    }

    private void OnStepChanged(object? sender, PropertyChangedEventArgs e) => Touch();

    private void Touch()
    {
        _autosave.Stop();
        _autosave.Start();
    }

    // ------------------------------------------------------------------ lista

    private void OnSelect(object sender, SelectionChangedEventArgs e)
    {
        if (_reloading)
        {
            return;
        }

        Commit();
        ShowEditor();
    }

    private void OnNew(object sender, RoutedEventArgs e)
    {
        Commit();
        var macro = new CommandMacro { Name = "", Steps = [MacroStep.Command("")] };
        _settings.Macros.Add(macro);
        _save();
        _changed();
        Reload(macro.Id);
        NameBox.Focus();
    }

    private void OnRecord(object sender, RoutedEventArgs e)
    {
        Commit();
        if (_record())
        {
            Close(); // a gravação acontece no terminal; ao parar, esta janela abre com a macro nova
        }
    }

    private void OnDuplicate(object sender, RoutedEventArgs e)
    {
        if (Current is not { } current)
        {
            return;
        }

        Commit();
        var copy = current.ToModel().Clone();
        copy.Id = new CommandMacro().Id;
        copy.Name = (string.IsNullOrWhiteSpace(copy.Name) ? "Macro" : copy.Name) + " (cópia)";
        _settings.Macros.Insert(_settings.Macros.FindIndex(m => m.Id == current.Id) + 1, copy);
        _save();
        _changed();
        Reload(copy.Id);
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Current is not { } current)
        {
            return;
        }

        if (!await Prompts.ConfirmAsync("Excluir macro", $"Excluir a macro \"{current.DisplayName}\"? Não dá para desfazer.", "Excluir", danger: true))
        {
            return;
        }

        var index = _items.IndexOf(current);
        _settings.Macros.RemoveAll(m => m.Id == current.Id);
        _save();
        _changed();
        var next = _settings.Macros.ElementAtOrDefault(Math.Min(index, _settings.Macros.Count - 1));
        Reload(next?.Id);
    }

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (Current is not { } current)
        {
            return;
        }

        Commit();
        var macro = current.ToModel();
        if (MacroRunner.Runnable(macro).Count == 0)
        {
            await Prompts.InfoAsync("Macro vazia", "Adicione pelo menos um comando antes de executar.");
            return;
        }

        if (await _run(macro))
        {
            Close(); // deixa o terminal à vista; o andamento aparece no topo da janela principal
        }
    }

    // ------------------------------------------------------------------ passos

    private void OnAddCommand(object sender, RoutedEventArgs e) => AddStep(MacroStep.Command(""));

    private void OnAddWaitFor(object sender, RoutedEventArgs e) => AddStep(MacroStep.Expect(""));

    private void OnAddWait(object sender, RoutedEventArgs e) => AddStep(MacroStep.Pause(2));

    private void AddStep(MacroStep step)
    {
        if (Current is not { } current)
        {
            return;
        }

        current.Add(step);
        UpdateTarget();
        // Rola até o passo novo e põe o cursor nele.
        Dispatcher.BeginInvoke(() =>
        {
            StepsScroll.ScrollToEnd();
            if (StepsList.ItemContainerGenerator.ContainerFromIndex(current.Steps.Count - 1) is FrameworkElement row &&
                FindChild<Wpf.Ui.Controls.TextBox>(row) is { } box)
            {
                box.Focus();
            }
        }, DispatcherPriority.Loaded);
    }

    private void OnStepUp(object sender, RoutedEventArgs e) => MoveStep(sender, -1);

    private void OnStepDown(object sender, RoutedEventArgs e) => MoveStep(sender, 1);

    private void MoveStep(object sender, int delta)
    {
        if (Current is { } current && (sender as FrameworkElement)?.DataContext is MacroStepViewModel step)
        {
            current.Move(step, delta);
        }
    }

    private void OnStepRemove(object sender, RoutedEventArgs e)
    {
        if (Current is { } current && (sender as FrameworkElement)?.DataContext is MacroStepViewModel step)
        {
            current.Steps.Remove(step);
            UpdateTarget();
        }
    }

    private void OnClosingWindow(object? sender, CancelEventArgs e) => Commit();

    private static T? FindChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T { } match && (match as UIElement)?.IsVisible != false)
            {
                return match;
            }

            if (FindChild<T>(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
