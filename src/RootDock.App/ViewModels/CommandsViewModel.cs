using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using RootDock.Core.Commands;
using RootDock.Core.Settings;
using Wpf.Ui.Controls;

namespace RootDock.App.ViewModels;

public enum CommandSendMode
{
    /// <summary>Digita e aperta Enter.</summary>
    Run,

    /// <summary>Só coloca no prompt, para revisar antes.</summary>
    Paste,
}

public sealed partial class CommandParamViewModel : ObservableObject
{
    public CommandParamViewModel(CommandParameter parameter, Action changed)
    {
        Parameter = parameter;
        _value = parameter.Default ?? "";
        _changed = changed;
    }

    private readonly Action _changed;

    public CommandParameter Parameter { get; }

    public string Label => Parameter.Label;

    public string Placeholder => Parameter.Placeholder ?? "";

    [ObservableProperty]
    private string _value;

    partial void OnValueChanged(string value) => _changed();
}

/// <summary>Um comando da biblioteca (ou do usuário) pronto para a tela.</summary>
public sealed partial class CommandItemViewModel : ObservableObject
{
    public CommandItemViewModel(CommandDefinition definition, string categoryName, bool isCustom)
    {
        Definition = definition;
        CategoryName = categoryName;
        IsCustom = isCustom;
        Params = new ObservableCollection<CommandParamViewModel>(definition.Params.Select(p => new CommandParamViewModel(p, UpdatePreview)));
        _preview = definition.DisplayCommand;
        SearchText = Normalize(string.Join(' ', new[] { definition.Title, definition.Cmd, definition.Description, categoryName }.Concat(definition.Tags)));
        // Com valores padrão preenchidos, a prévia já mostra o comando final.
        UpdatePreview();
    }

    public CommandDefinition Definition { get; }

    public string Id => Definition.Id;

    public string Title => Definition.Title;

    public string DisplayCommand => Definition.DisplayCommand;

    public string Description => Definition.Description;

    public string? Tip => Definition.Tip;

    public bool HasTip => !string.IsNullOrWhiteSpace(Definition.Tip);

    public string CategoryName { get; }

    public bool IsCustom { get; }

    public bool IsCaution => Definition.IsCaution;

    public bool IsSudo => Definition.Sudo;

    public bool IsKeys => Definition.Keys;

    public bool HasParams => Params.Count > 0;

    public ObservableCollection<CommandParamViewModel> Params { get; }

    internal string SearchText { get; }

    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private bool _isFormOpen;
    [ObservableProperty] private string _preview;
    [ObservableProperty] private string _error = "";

    /// <summary>Comando final com os campos preenchidos e escapados; null se faltar algo (ver <see cref="Error"/>).</summary>
    public string? Render()
    {
        if (IsKeys)
        {
            return Definition.Cmd;
        }

        try
        {
            var text = CommandTemplate.Render(Definition, Params.ToDictionary(p => p.Parameter.Name, p => p.Value));
            Error = "";
            return text;
        }
        catch (CommandRenderException ex)
        {
            Error = ex.Message;
            return null;
        }
    }

    private void UpdatePreview()
    {
        try
        {
            Preview = CommandTemplate.Render(Definition, Params.ToDictionary(p => p.Parameter.Name, p => p.Value));
            Error = "";
        }
        catch (CommandRenderException)
        {
            Preview = DisplayCommand;
        }
    }

    internal static string Normalize(string text)
    {
        // Busca sem acentos e sem maiúsculas: "memoria" encontra "Memória".
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}

public sealed partial class CommandCategoryViewModel : ObservableObject
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required SymbolRegular Icon { get; init; }

    public string? Description { get; init; }

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public bool IsSpecial { get; init; }

    public ObservableCollection<CommandItemViewModel> Items { get; } = [];

    public string CountText => Items.Count.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty] private bool _isExpanded;
}

/// <summary>Biblioteca de comandos: temas, busca, favoritos, recentes e comandos do usuário.</summary>
public sealed partial class CommandsViewModel : ObservableObject
{
    private const int MaxRecent = 8;

    private readonly AppSettings _settings;
    private readonly Action _save;
    private readonly List<CommandCategoryViewModel> _builtIn;
    private readonly Dictionary<string, CommandItemViewModel> _byId = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);

    public CommandsViewModel(CommandLibrary library, AppSettings settings, Action save)
    {
        _settings = settings;
        _save = save;
        _builtIn = library.Categories.Select(c =>
        {
            var category = new CommandCategoryViewModel
            {
                Id = c.Id,
                Name = c.Name,
                Icon = Enum.TryParse<SymbolRegular>(c.Icon, out var icon) ? icon : SymbolRegular.Document20,
                Description = c.Description,
            };
            foreach (var command in c.Commands)
            {
                var item = new CommandItemViewModel(command, c.Name, isCustom: false);
                _byId[item.Id] = item;
                category.Items.Add(item);
            }

            return category;
        }).ToList();

        foreach (var id in settings.FavoriteCommands)
        {
            if (_byId.TryGetValue(id, out var item))
            {
                item.IsFavorite = true;
            }
        }

        // Tudo começa recolhido: a pessoa vê os temas e abre o que quiser.
        Rebuild();
    }

    public ObservableCollection<CommandCategoryViewModel> Sections { get; } = [];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _targetText = "Nenhum terminal ativo";
    [ObservableProperty] private bool _canRun;
    [ObservableProperty] private string _targetWarning = "";
    [ObservableProperty] private string _resultSummary = "";

    /// <summary>Pedido para enviar o comando ao terminal ativo (tratado pela janela).</summary>
    public event Action<CommandItemViewModel, CommandSendMode>? RunRequested;

    public int TotalCommands => _builtIn.Sum(c => c.Items.Count);

    partial void OnSearchChanged(string value) => Rebuild();

    /// <summary>Clique no comando: abre o formulário se tiver campos; senão executa.</summary>
    public void Activate(CommandItemViewModel item)
    {
        if (item.HasParams && !item.IsFormOpen)
        {
            item.IsFormOpen = true;
            return;
        }

        RunRequested?.Invoke(item, CommandSendMode.Run);
    }

    public void Send(CommandItemViewModel item, CommandSendMode mode) => RunRequested?.Invoke(item, mode);

    public void ToggleFavorite(CommandItemViewModel item)
    {
        item.IsFavorite = !item.IsFavorite;
        _settings.FavoriteCommands.Remove(item.Id);
        if (item.IsFavorite)
        {
            _settings.FavoriteCommands.Insert(0, item.Id);
        }

        _save();
        Rebuild();
    }

    public void RecordUse(CommandItemViewModel item)
    {
        _settings.RecentCommands.Remove(item.Id);
        _settings.RecentCommands.Insert(0, item.Id);
        if (_settings.RecentCommands.Count > MaxRecent)
        {
            _settings.RecentCommands.RemoveRange(MaxRecent, _settings.RecentCommands.Count - MaxRecent);
        }

        _save();
        if (Search.Length == 0)
        {
            Rebuild();
        }
    }

    public void AddCustom(CustomCommand command)
    {
        _settings.CustomCommands.Add(command);
        _save();
        _expanded.Add("meus");
        Rebuild();
    }

    public void UpdateCustom(CustomCommand command)
    {
        var index = _settings.CustomCommands.FindIndex(c => c.Id == command.Id);
        if (index >= 0)
        {
            _settings.CustomCommands[index] = command;
            _save();
            Rebuild();
        }
    }

    public void RemoveCustom(string id)
    {
        _settings.CustomCommands.RemoveAll(c => c.Id == id);
        _settings.FavoriteCommands.Remove(id);
        _settings.RecentCommands.Remove(id);
        _save();
        Rebuild();
    }

    public CustomCommand? FindCustom(string id) => _settings.CustomCommands.FirstOrDefault(c => c.Id == id);

    /// <summary>Volta ao estado inicial ao abrir o painel: busca vazia e todos os temas recolhidos.</summary>
    public void CollapseAll()
    {
        _expanded.Clear();
        foreach (var item in _byId.Values)
        {
            item.IsFormOpen = false;
        }

        if (Search.Length > 0)
        {
            Search = ""; // a mudança da busca já reconstrói a lista
        }
        else
        {
            Rebuild();
        }
    }

    public void SetExpanded(CommandCategoryViewModel category, bool expanded)
    {
        category.IsExpanded = expanded;
        if (Search.Length > 0)
        {
            return;
        }

        if (expanded)
        {
            _expanded.Add(category.Id);
        }
        else
        {
            _expanded.Remove(category.Id);
        }
    }

    /// <summary>Atualiza o destino exibido ("Enviando para: ...").</summary>
    public void SetTarget(string? name, string? warning)
    {
        CanRun = name is not null && string.IsNullOrEmpty(warning);
        TargetText = name ?? "Nenhum terminal ativo";
        TargetWarning = warning ?? (name is null ? "Abra um terminal para executar os comandos." : "");
    }

    private void Rebuild()
    {
        // Os itens são reaproveitados: formulários preenchidos e estrelas não se perdem ao buscar.
        var customItems = _settings.CustomCommands.Select(ToItem).ToList();
        foreach (var item in customItems)
        {
            _byId[item.Id] = item;
        }

        Sections.Clear();
        var query = CommandItemViewModel.Normalize(Search.Trim());
        if (query.Length > 0)
        {
            var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var matches = customItems.Concat(_builtIn.SelectMany(c => c.Items))
                .Where(i => words.All(w => i.SearchText.Contains(w, StringComparison.Ordinal)))
                .ToList();
            var results = new CommandCategoryViewModel
            {
                Id = "busca",
                Name = "Resultados",
                Icon = SymbolRegular.Search20,
                IsSpecial = true,
                IsExpanded = true,
            };
            foreach (var match in matches)
            {
                results.Items.Add(match);
            }

            ResultSummary = matches.Count == 0 ? "Nenhum comando encontrado. Tente outra palavra (ex.: disco, log, porta)." : $"{matches.Count} comandos";
            Sections.Add(results);
            return;
        }

        ResultSummary = "";
        AddSpecial("favoritos", "Favoritos", SymbolRegular.Star20, _settings.FavoriteCommands);
        AddSpecial("recentes", "Usados recentemente", SymbolRegular.History20, _settings.RecentCommands);

        var mine = new CommandCategoryViewModel
        {
            Id = "meus",
            Name = "Meus comandos",
            Icon = SymbolRegular.Wrench20,
            Description = customItems.Count == 0
                ? "Cadastre seus próprios comandos com o botão +. Use {{nome}} para campos que você preenche na hora."
                : null,
            IsSpecial = true,
            IsExpanded = _expanded.Contains("meus"),
        };
        foreach (var item in customItems)
        {
            mine.Items.Add(item);
        }

        Sections.Add(mine);

        foreach (var category in _builtIn)
        {
            category.IsExpanded = _expanded.Contains(category.Id);
            Sections.Add(category);
        }
    }

    private void AddSpecial(string id, string name, SymbolRegular icon, IEnumerable<string> ids)
    {
        var items = ids.Select(i => _byId.GetValueOrDefault(i)).OfType<CommandItemViewModel>().ToList();
        if (items.Count == 0)
        {
            return;
        }

        var section = new CommandCategoryViewModel { Id = id, Name = name, Icon = icon, IsSpecial = true, IsExpanded = _expanded.Contains(id) };
        foreach (var item in items)
        {
            section.Items.Add(item);
        }

        Sections.Add(section);
    }

    private CommandItemViewModel ToItem(CustomCommand custom)
    {
        if (_byId.TryGetValue(custom.Id, out var existing) && existing.Definition.Cmd == custom.Cmd && existing.Title == custom.Title &&
            existing.Description == (custom.Description ?? "") && existing.IsCaution == custom.Caution)
        {
            return existing;
        }

        var definition = new CommandDefinition
        {
            Id = custom.Id,
            Title = custom.Title,
            Cmd = custom.Cmd,
            Description = custom.Description ?? "",
            Risk = custom.Caution ? "caution" : "safe",
            Params = CommandTemplate.FieldNames(custom.Cmd).Select(n => new CommandParameter { Name = n, Label = n }).ToList(),
        };
        return new CommandItemViewModel(definition, "Meus comandos", isCustom: true)
        {
            IsFavorite = _settings.FavoriteCommands.Contains(custom.Id),
        };
    }
}
