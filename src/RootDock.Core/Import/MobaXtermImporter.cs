using System.Text;
using RootDock.Core.Sessions;

namespace RootDock.Core.Import;

public sealed record ImportResult(
    IReadOnlyList<SessionEntry> Sessions,
    IReadOnlyList<string> Folders,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Importa sessões do arquivo MobaXterm.ini (seções [Bookmarks], [Bookmarks_1], ...).
/// Formato de uma sessão SSH: <c>Nome=#109#0%host%porta%usuario%...</c>, com campos separados por "%".
/// Só são lidos os campos confirmados em arquivos reais: host (1), porta (2), usuário (3) e chave privada (14).
/// </summary>
public static class MobaXtermImporter
{
    private const string SshTypeCode = "109";
    private const int HostField = 1;
    private const int PortField = 2;
    private const int UserField = 3;
    private const int PrivateKeyField = 14;

    public const string SourceName = "MobaXterm";

    /// <summary>Procura o MobaXterm.ini nos locais padrão da versão instalada e da portátil.</summary>
    public static string? FindDefaultIniPath()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MobaXterm", "MobaXterm.ini"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MobaXterm", "MobaXterm.ini"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    public static ImportResult ImportFile(string iniPath)
    {
        var bytes = File.ReadAllBytes(iniPath);
        var profileDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Parse(DecodeIni(bytes), profileDir);
    }

    /// <summary>Versões recentes gravam UTF-8; versões antigas gravam ANSI (Windows-1252).</summary>
    internal static string DecodeIni(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes)
                .TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }

    public static ImportResult Parse(string iniText, string profileDirectory)
    {
        var sessions = new List<SessionEntry>();
        var folders = new List<string>();
        var warnings = new List<string>();

        string? section = null;
        var folder = "";
        var sectionSessions = new List<(string Name, string Value)>();

        void FlushSection()
        {
            foreach (var (name, value) in sectionSessions)
            {
                var entry = ParseSession(name, value, folder, profileDirectory, warnings);
                if (entry is not null)
                {
                    sessions.Add(entry);
                }
            }

            sectionSessions.Clear();
        }

        foreach (var rawLine in iniText.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                FlushSection();
                section = line[1..^1];
                folder = "";
                continue;
            }

            if (section is null || !IsBookmarksSection(section))
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq];
            var value = line[(eq + 1)..];
            if (key == "SubRep")
            {
                folder = FolderPaths.Normalize(value);
                if (folder.Length > 0)
                {
                    folders.Add(folder);
                }
            }
            else if (key != "ImgNum" && value.StartsWith('#'))
            {
                sectionSessions.Add((key, value));
            }
        }

        FlushSection();
        return new ImportResult(sessions, folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), warnings);
    }

    private static bool IsBookmarksSection(string section) =>
        section == "Bookmarks" || section.StartsWith("Bookmarks_", StringComparison.Ordinal);

    private static SessionEntry? ParseSession(string name, string value, string folder, string profileDirectory, List<string> warnings)
    {
        // value = "#<tipo>#<subtipo>%campo1%campo2..." seguido de outros blocos separados por "#".
        var blocks = value.Split('#');
        if (blocks.Length < 3)
        {
            warnings.Add($"Sessão \"{name}\": formato não reconhecido, ignorada.");
            return null;
        }

        var typeCode = blocks[1];
        if (typeCode != SshTypeCode)
        {
            warnings.Add($"Sessão \"{name}\": tipo {typeCode} ainda não é suportado (só SSH), ignorada.");
            return null;
        }

        var fields = blocks[2].Split('%');
        var host = Field(fields, HostField);
        if (string.IsNullOrWhiteSpace(host))
        {
            warnings.Add($"Sessão \"{name}\": sem host, ignorada.");
            return null;
        }

        var port = int.TryParse(Field(fields, PortField), out var p) && p is > 0 and < 65536 ? p : 22;
        var user = Field(fields, UserField);
        var key = ExpandPlaceholders(Field(fields, PrivateKeyField), profileDirectory);

        if (key is not null && !File.Exists(key))
        {
            warnings.Add($"Sessão \"{name}\": chave privada '{key}' não encontrada; a sessão foi importada mesmo assim.");
        }

        return new SessionEntry
        {
            Name = name.Trim(),
            FolderPath = folder,
            Kind = SessionKind.Ssh,
            Host = host.Trim(),
            Port = port,
            Username = string.IsNullOrWhiteSpace(user) ? null : user.Trim(),
            PrivateKeyPath = key,
            ImportedFrom = SourceName,
        };
    }

    private static string? Field(string[] fields, int index) =>
        index < fields.Length && fields[index].Length > 0 ? fields[index] : null;

    private static string? ExpandPlaceholders(string? path, string profileDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var drive = Path.GetPathRoot(profileDirectory)?.TrimEnd('\\') ?? "C:";
        return path
            .Replace("_ProfileDir_", profileDirectory, StringComparison.Ordinal)
            .Replace("_CurrentDrive_", drive, StringComparison.Ordinal)
            .Trim();
    }
}

public static class SessionImportMerger
{
    /// <summary>
    /// Adiciona as sessões importadas ao documento, ignorando as que já existem
    /// (mesma pasta, nome, host e porta). Retorna quantas foram adicionadas.
    /// </summary>
    public static int Merge(SessionDocument document, ImportResult result)
    {
        var existing = new HashSet<string>(document.Sessions.Select(Key), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var session in result.Sessions)
        {
            if (existing.Add(Key(session)))
            {
                document.Sessions.Add(session);
                added++;
            }
        }

        foreach (var folder in result.Folders)
        {
            if (!document.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                document.Folders.Add(folder);
            }
        }

        return added;
    }

    private static string Key(SessionEntry s) => $"{FolderPaths.Normalize(s.FolderPath)}|{s.Name}|{s.Host}|{s.Port}";
}
