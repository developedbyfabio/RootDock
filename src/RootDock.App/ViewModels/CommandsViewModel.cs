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
    public CommandItemViewModel(CommandDefinition definition, string categoryName, bool isCustom, bool isMacro = false, string? family = null)
    {
        Definition = definition;
        CategoryName = categoryName;
        IsCustom = isCustom;
        IsMacro = isMacro;
        Family = family;
        Params = new ObservableCollection<CommandParamViewModel>(definition.Params.Select(p => new CommandParamViewModel(p, UpdatePreview)));
        _preview = definition.DisplayCommand;
        SearchText = Normalize(string.Join(' ', new[] { definition.Title, definition.Cmd, definition.Description, categoryName }.Concat(definition.Tags)));
        _titleText = Normalize(definition.Title);
        _tagText = Normalize(string.Join(' ', definition.Tags));
        _categoryText = Normalize(categoryName);
        _commandText = Normalize(definition.Cmd);
        _descriptionText = Normalize(definition.Description);
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

    /// <summary>Macro (Ferramentas → Macros): o clique executa os passos em sequência.</summary>
    public bool IsMacro { get; }

    /// <summary>Família de sistema do comando (null = do usuário, vale em qualquer terminal).</summary>
    public string? Family { get; }

    public bool IsWindows => Family == OsFamily.Windows;

    /// <summary>Atalho Win+R: abre o programa no computador em vez de digitar no terminal.</summary>
    public bool IsRun => Definition.Run;

    /// <summary>Comando do Windows que só existe no PowerShell (selo na tela).</summary>
    public bool IsPowerShell => IsWindows && Definition.PowerShell;

    /// <summary>"Colocar no terminal sem executar" não vale para macro (são vários passos) nem para Win+R.</summary>
    public bool CanPaste => !IsMacro && !IsRun;

    /// <summary>O que "Copiar" leva (macro: só os comandos, um por linha); null = o comando.</summary>
    public string? CopyText { get; init; }

    public bool IsCaution => Definition.IsCaution;

    public bool IsSudo => Definition.Sudo;

    public bool IsKeys => Definition.Keys;

    public bool HasParams => Params.Count > 0;

    public ObservableCollection<CommandParamViewModel> Params { get; }

    internal string SearchText { get; }

    private readonly string _titleText;
    private readonly string _tagText;
    private readonly string _categoryText;
    private readonly string _commandText;
    private readonly string _descriptionText;

    /// <summary>Palavras da busca sem acentos e sem maiúsculas (como o texto pesquisado).</summary>
    public static IReadOnlyList<string> QueryWords(string query) =>
        Normalize(query.Trim()).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Todas as palavras aparecem em algum lugar do comando (título, comando, descrição, tema ou etiquetas).</summary>
    public bool Matches(IReadOnlyList<string> words) => words.All(w => SearchText.Contains(w, StringComparison.Ordinal));

    /// <summary>
    /// Quanto o comando tem a ver com a busca: palavra no título vale mais que nas etiquetas, no tema
    /// e no próprio comando; só citada na descrição, quase nada. Assim "mysql" traz primeiro os
    /// comandos do MySQL, não um que menciona o mysql como exemplo (o Enter usa o primeiro).
    /// </summary>
    public int Relevance(IReadOnlyList<string> words)
    {
        var score = 0;
        foreach (var w in words)
        {
            var strong = (StartsWord(_titleText, w) ? 10 : _titleText.Contains(w, StringComparison.Ordinal) ? 6 : 0)
                         + (_tagText.Contains(w, StringComparison.Ordinal) ? 4 : 0)
                         + (_categoryText.Contains(w, StringComparison.Ordinal) ? 4 : 0)
                         + (_commandText.Contains(w, StringComparison.Ordinal) ? 3 : 0);
            // A descrição só desempata quem não tem a palavra em nenhum outro lugar.
            score += strong > 0 ? strong : _descriptionText.Contains(w, StringComparison.Ordinal) ? 1 : 0;
        }

        return score;
    }

    // "php" começa uma palavra em "versao do php" e em "php.ini", mas não em "ophp".
    private static bool StartsWord(string text, string word)
    {
        for (var i = text.IndexOf(word, StringComparison.Ordinal); i >= 0; i = text.IndexOf(word, i + 1, StringComparison.Ordinal))
        {
            if (i == 0 || !char.IsLetterOrDigit(text[i - 1]))
            {
                return true;
            }
        }

        return false;
    }

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
            var text = CommandTemplate.Render(Definition, Params.ToDictionary(p => p.Parameter.Name, p => p.Value), IsWindows);
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
            Preview = CommandTemplate.Render(Definition, Params.ToDictionary(p => p.Parameter.Name, p => p.Value), IsWindows);
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

/// <summary>Opção do seletor "Sistema" do painel de comandos.</summary>
public sealed partial class OsChoice(string id, string label) : ObservableObject
{
    public string Id { get; } = id;

    [ObservableProperty] private string _label = label;

    /// <summary>Nome lido pelo leitor de tela e pela automação (o ComboBox usa ToString nos itens).</summary>
    public override string ToString() => Label;
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

    private const string Auto = "auto";

    private readonly AppSettings _settings;
    private readonly Action _save;
    private readonly CommandLibrary _library;
    private readonly Dictionary<string, List<CommandCategoryViewModel>> _byFamily = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CommandItemViewModel> _byId = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly OsChoice _autoChoice = new(Auto, "Automático");
    private List<CommandCategoryViewModel> _builtIn = [];
    private string _family = "";
    private string? _detectedFamily;
    private string? _detectedLabel;

    public CommandsViewModel(CommandLibrary library, AppSettings settings, Action save)
    {
        _library = library;
        _settings = settings;
        _save = save;
        OsChoices = [_autoChoice, .. OsFamily.All.Select(f => new OsChoice(f, OsFamily.Label(f)))];
        _selectedOsChoice = OsChoices.FirstOrDefault(c => c.Id == settings.CommandsOs) ?? _autoChoice;

        // Tudo começa recolhido: a pessoa vê os temas e abre o que quiser.
        ApplyFamily(force: true);
    }

    /// <summary>Seletor "Sistema": Automático (segue o terminal em foco) ou uma família fixa.</summary>
    public ObservableCollection<OsChoice> OsChoices { get; }

    [ObservableProperty] private OsChoice _selectedOsChoice;

    partial void OnSelectedOsChoiceChanged(OsChoice value)
    {
        _settings.CommandsOs = value.Id;
        _save();
        ApplyFamily();
    }

    /// <summary>Família dos comandos mostrados agora (debian, rhel, suse ou windows).</summary>
    public string Family => _family;

    /// <summary>A família mudou (escolha no seletor ou outro terminal em foco no modo automático).</summary>
    public event Action? FamilyChanged;

    /// <summary>
    /// Sistema do terminal em foco (null = não dá para saber, ex.: Telnet: fica o que estava).
    /// No modo automático, a lista passa a ser a dessa família.
    /// </summary>
    public void SetDetected(string? family, string? label)
    {
        if (family is null || (family == _detectedFamily && label == _detectedLabel))
        {
            return;
        }

        _detectedFamily = family;
        _detectedLabel = label;
        _autoChoice.Label = $"Automático · {label ?? OsFamily.Label(family)}";
        ApplyFamily();
    }

    private void ApplyFamily(bool force = false)
    {
        var family = SelectedOsChoice.Id == Auto ? _detectedFamily ?? OsFamily.Debian : SelectedOsChoice.Id;
        if (!force && family == _family)
        {
            return;
        }

        _family = family;
        if (!_byFamily.TryGetValue(family, out var categories))
        {
            categories = BuildFamily(family);
            _byFamily[family] = categories;
        }

        _builtIn = categories;
        _byId.Clear();
        foreach (var item in _builtIn.SelectMany(c => c.Items))
        {
            item.IsFavorite = _settings.FavoriteCommands.Contains(item.Id);
            _byId[item.Id] = item;
        }

        Rebuild();
        FamilyChanged?.Invoke();
    }

    private List<CommandCategoryViewModel> BuildFamily(string family) => _library.Categories
        .Select(c => (Category: c, Commands: c.For(family)))
        .Where(x => x.Commands.Count > 0)
        .Select(x =>
        {
            var category = new CommandCategoryViewModel
            {
                Id = x.Category.Id,
                Name = x.Category.Name,
                Icon = Enum.TryParse<SymbolRegular>(x.Category.Icon, out var icon) ? icon : SymbolRegular.Document20,
                Description = x.Category.Description,
            };
            foreach (var command in x.Commands)
            {
                category.Items.Add(new CommandItemViewModel(command, x.Category.Name, isCustom: false, family: family));
            }

            return category;
        })
        .ToList();

    public ObservableCollection<CommandCategoryViewModel> Sections { get; } = [];

    [ObservableProperty] private string _search = "";

    /// <summary>Há texto na busca: a tela mostra <see cref="Results"/> no lugar dos temas.</summary>
    [ObservableProperty] private bool _isSearching;

    /// <summary>
    /// Resultados da busca, trocados de uma vez (não item a item). A tela usa uma lista virtualizada, que só cria
    /// os cartões visíveis: com uma letra só quase tudo casa, e desenhar centenas de cartões travava a digitação.
    /// </summary>
    [ObservableProperty] private IReadOnlyList<CommandItemViewModel> _results = [];

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

    /// <summary>As macros mudaram (janela de macros ou gravação): redesenha a seção e abre ela.</summary>
    public void MacrosChanged(bool expand = false)
    {
        var ids = _settings.Macros.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        _settings.FavoriteCommands.RemoveAll(id => id.StartsWith("macro.", StringComparison.Ordinal) && !ids.Contains(id));
        _settings.RecentCommands.RemoveAll(id => id.StartsWith("macro.", StringComparison.Ordinal) && !ids.Contains(id));
        if (expand)
        {
            _expanded.Add("macros");
        }

        Rebuild();
    }

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
        var macroItems = _settings.Macros.Select(ToItem).ToList();
        foreach (var item in customItems.Concat(macroItems))
        {
            _byId[item.Id] = item;
        }

        var words = CommandItemViewModel.QueryWords(Search);
        if (words.Count > 0)
        {
            // Os temas ficam como estão (escondidos) enquanto se busca: nada de desmontar e remontar a cada letra.
            // Mais relevantes primeiro; no empate, a ordem de sempre (macros, meus comandos, biblioteca).
            var matches = macroItems.Concat(customItems).Concat(_builtIn.SelectMany(c => c.Items))
                .Where(i => i.Matches(words))
                .OrderByDescending(i => i.Relevance(words))
                .ToList();
            if (!IsSearching || !matches.SequenceEqual(Results))
            {
                Results = matches; // mesma lista (ex.: favoritou um resultado): não volta a rolagem ao topo
            }

            ResultSummary = matches.Count == 0 ? "Nenhum comando encontrado. Tente outra palavra (ex.: disco, log, porta)." : $"{matches.Count} comandos";
            IsSearching = true;
            return;
        }

        IsSearching = false;
        Results = [];
        ResultSummary = "";
        Sections.Clear();
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

        var macros = new CommandCategoryViewModel
        {
            Id = "macros",
            Name = "Minhas macros",
            Icon = SymbolRegular.Flash20,
            Description = macroItems.Count == 0
                ? "Sequências de comandos executadas de uma vez, esperando cada uma terminar. Grave o que você digita ou monte a sua em Ferramentas → Macros."
                : null,
            IsSpecial = true,
            IsExpanded = _expanded.Contains("macros"),
        };
        foreach (var item in macroItems)
        {
            macros.Items.Add(item);
        }

        Sections.Add(macros);

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

    private CommandItemViewModel ToItem(Core.Macros.CommandMacro macro)
    {
        var steps = Core.Macros.MacroRunner.Runnable(macro).Count;
        var description = string.IsNullOrWhiteSpace(macro.Description)
            ? steps == 1 ? "1 passo" : $"{steps} passos"
            : macro.Description;
        var preview = macro.Preview;
        if (_byId.TryGetValue(macro.Id, out var existing) && existing.IsMacro && existing.Definition.Cmd == preview &&
            existing.Title == macro.Name && existing.Description == description && existing.IsCaution == macro.LooksDestructive)
        {
            return existing;
        }

        var definition = new CommandDefinition
        {
            Id = macro.Id,
            Title = string.IsNullOrWhiteSpace(macro.Name) ? "Macro sem nome" : macro.Name,
            Cmd = preview,
            Description = description,
            Risk = macro.LooksDestructive ? "caution" : "safe",
            Tags = ["macro"],
        };
        return new CommandItemViewModel(definition, "Minhas macros", isCustom: false, isMacro: true)
        {
            IsFavorite = _settings.FavoriteCommands.Contains(macro.Id),
            CopyText = string.Join("\n", macro.Commands),
        };
    }
}
