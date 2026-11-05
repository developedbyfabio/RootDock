using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using RootDock.Core.Sessions;
using RootDock.Core.Settings;

namespace RootDock.Core.Serialization;

/// <summary>
/// Metadados de JSON gerados na compilação para os arquivos lidos na abertura (sessões e
/// preferências). Sem isso, o System.Text.Json monta tudo por reflexão no primeiro uso, o que
/// custava de 300 a 500 ms na inicialização. O formato dos arquivos não muda.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionDocument))]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class RootDockJsonContext : JsonSerializerContext;

internal static class RootDockJson
{
    /// <summary>
    /// Mesmas opções de sempre (camelCase, indentado, nulos omitidos). Tipos fora do contexto gerado
    /// continuam funcionando por reflexão.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = JsonTypeInfoResolver.Combine(RootDockJsonContext.Default, new DefaultJsonTypeInfoResolver()),
    };
}
