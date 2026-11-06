using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using RootDock.Core.Macros;
using Wpf.Ui.Controls;

namespace RootDock.App.ViewModels;

/// <summary>Um passo no editor de macros.</summary>
public sealed partial class MacroStepViewModel : ObservableObject
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public MacroStepViewModel(MacroStep step)
    {
        _kind = step.Kind;
        _text = step.Text;
        _seconds = step.Seconds > 0 ? step.Seconds : step.Kind == MacroStepKind.WaitFor ? MacroRunner.DefaultExpectSeconds : 2;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCommand), nameof(IsWait), nameof(IsWaitFor), nameof(KindLabel), nameof(Icon), nameof(Placeholder))]
    private MacroStepKind _kind;

    [ObservableProperty] private string _text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SecondsText))]
    private double _seconds;

    /// <summary>Posição na lista (1, 2, 3...).</summary>
    [ObservableProperty] private int _number;

    public bool IsCommand => Kind == MacroStepKind.Command;

    public bool IsWait => Kind == MacroStepKind.Wait;

    public bool IsWaitFor => Kind == MacroStepKind.WaitFor;

    public string KindLabel => Kind switch
    {
        MacroStepKind.Wait => "Pausa",
        MacroStepKind.WaitFor => "Esperar texto",
        _ => "Comando",
    };

    public SymbolRegular Icon => Kind switch
    {
        MacroStepKind.Wait => SymbolRegular.Timer20,
        MacroStepKind.WaitFor => SymbolRegular.Hourglass20,
        _ => SymbolRegular.WindowConsole20,
    };

    public string Placeholder => Kind == MacroStepKind.WaitFor ? "texto que aparece (ex.: password, [Y/n])" : "comando (ex.: sudo systemctl restart nginx)";

    /// <summary>Segundos editados como texto ("2,5" ou "2.5").</summary>
    public string SecondsText
    {
        get => Seconds.ToString("0.#", PtBr);
        set
        {
            var text = (value ?? "").Trim().Replace('.', ',');
            if (double.TryParse(text, NumberStyles.Float, PtBr, out var seconds) && seconds is > 0 and <= 86_400)
            {
                Seconds = Math.Round(seconds, 1);
            }

            OnPropertyChanged();
        }
    }

    /// <summary>Comando fica como foi digitado (espaço no fim pode importar, ex.: set /p X=Nome? ); o texto esperado sem espaços nas pontas.</summary>
    public MacroStep ToModel() => new()
    {
        Kind = Kind,
        Text = Kind switch { MacroStepKind.Wait => "", MacroStepKind.WaitFor => Text.Trim(), _ => Text },
        Seconds = IsCommand ? 0 : Seconds,
    };
}

/// <summary>Uma macro na janela de macros (lista e editor).</summary>
public sealed partial class MacroEditorViewModel : ObservableObject
{
    private MacroEditorViewModel(CommandMacro macro)
    {
        Id = macro.Id;
        _name = macro.Name;
        _description = macro.Description ?? "";
        _waitForPrompt = macro.WaitForPrompt;
        foreach (var step in macro.Steps)
        {
            Steps.Add(new MacroStepViewModel(step));
        }

        Renumber();
        Steps.CollectionChanged += OnStepsChanged;
    }

    public string Id { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string _name;

    [ObservableProperty] private string _description;

    [ObservableProperty] private bool _waitForPrompt;

    public ObservableCollection<MacroStepViewModel> Steps { get; } = [];

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Macro sem nome" : Name;

    public string Summary => Steps.Count switch
    {
        0 => "sem passos",
        1 => "1 passo",
        _ => $"{Steps.Count} passos",
    };

    public bool HasSteps => Steps.Count > 0;

    public static MacroEditorViewModel From(CommandMacro macro) => new(macro);

    public void Add(MacroStep step, int? index = null)
    {
        var item = new MacroStepViewModel(step);
        if (index is { } i && i >= 0 && i <= Steps.Count)
        {
            Steps.Insert(i, item);
        }
        else
        {
            Steps.Add(item);
        }
    }

    public void Move(MacroStepViewModel step, int delta)
    {
        var from = Steps.IndexOf(step);
        var to = from + delta;
        if (from >= 0 && to >= 0 && to < Steps.Count)
        {
            Steps.Move(from, to);
        }
    }

    /// <summary>
    /// Para salvar: comando colado com várias linhas vira um passo por linha e passos vazios saem.
    /// </summary>
    public CommandMacro ToModel()
    {
        var steps = new List<MacroStep>();
        foreach (var step in Steps.Select(s => s.ToModel()))
        {
            if (step.Kind == MacroStepKind.Command)
            {
                steps.AddRange(step.Text.Replace("\r", "").Split('\n')
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Select(MacroStep.Command));
            }
            else if (step.Kind == MacroStepKind.Wait || step.Text.Length > 0)
            {
                steps.Add(step);
            }
        }

        return new CommandMacro
        {
            Id = Id,
            Name = Name.Trim(),
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            WaitForPrompt = WaitForPrompt,
            Steps = steps,
        };
    }

    private void OnStepsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Renumber();
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasSteps));
    }

    private void Renumber()
    {
        for (var i = 0; i < Steps.Count; i++)
        {
            Steps[i].Number = i + 1;
        }
    }
}
