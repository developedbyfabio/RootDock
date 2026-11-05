using System.Text;
using RootDock.Core.Import;
using RootDock.Core.Sessions;

namespace RootDock.Core.Tests;

public class MobaXtermImporterTests
{
    // Estrutura igual à gravada pelo MobaXterm 26.x (valores fictícios).
    private const string SampleIni = """
        [Misc]
        SomeSetting=1
        [Bookmarks]
        SubRep=
        ImgNum=42
        10.0.0.1=#109#0%10.0.0.1%22%%%-1%-1%%%22%%0%0%0%%%1080%%0%0%1#MobaFont%10%0%0%-1%15%236,236,236%30,30,30%180,180,192%0%-1%0%%xterm%-1%-1%_Std_Colors_0_%80%24%0%1%-1%<none>%%0%0%-1#0# #-1
        Servidor RDP=#91#4%10.0.0.9%3389%admin%0%0%0#MobaFont%10#0# #-1
        [Bookmarks_1]
        SubRep=Empresa\Producao
        ImgNum=41
        App - 11.202=#109#0%192.168.11.202%2222%deploy%%-1%-1%%%22%%0%0%0%_ProfileDir_\.ssh\id_ed25519%%-1%0%0%0%%1080%%0%0%1#MobaFont%10#0# #-1
        """;

    [Fact]
    public void Parse_reads_ssh_sessions_folders_and_fields()
    {
        var result = MobaXtermImporter.Parse(SampleIni, @"C:\Users\teste");

        Assert.Equal(2, result.Sessions.Count);

        var root = result.Sessions[0];
        Assert.Equal("10.0.0.1", root.Name);
        Assert.Equal("", root.FolderPath);
        Assert.Equal("10.0.0.1", root.Host);
        Assert.Equal(22, root.Port);
        Assert.Null(root.Username);
        Assert.Null(root.PrivateKeyPath);
        Assert.Equal(SessionKind.Ssh, root.Kind);
        Assert.Equal(MobaXtermImporter.SourceName, root.ImportedFrom);

        var nested = result.Sessions[1];
        Assert.Equal("App - 11.202", nested.Name);
        Assert.Equal("Empresa/Producao", nested.FolderPath);
        Assert.Equal("192.168.11.202", nested.Host);
        Assert.Equal(2222, nested.Port);
        Assert.Equal("deploy", nested.Username);
        Assert.Equal(@"C:\Users\teste\.ssh\id_ed25519", nested.PrivateKeyPath);

        Assert.Equal(["Empresa/Producao"], result.Folders);
    }

    [Fact]
    public void Parse_warns_about_unsupported_session_types()
    {
        var result = MobaXtermImporter.Parse(SampleIni, @"C:\Users\teste");

        Assert.Contains(result.Warnings, w => w.Contains("Servidor RDP") && w.Contains("91"));
    }

    [Fact]
    public void DecodeIni_falls_back_to_windows_1252_for_legacy_files()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(1252).GetBytes("[Bookmarks]\r\nSubRep=Produção\r\n");

        var text = MobaXtermImporter.DecodeIni(bytes);

        Assert.Contains("Produção", text);
    }

    [Fact]
    public void Merge_skips_sessions_that_were_already_imported()
    {
        var document = new SessionDocument();
        var result = MobaXtermImporter.Parse(SampleIni, @"C:\Users\teste");

        Assert.Equal(2, SessionImportMerger.Merge(document, result));
        Assert.Equal(0, SessionImportMerger.Merge(document, MobaXtermImporter.Parse(SampleIni, @"C:\Users\teste")));
        Assert.Equal(2, document.Sessions.Count);
        Assert.Equal(["Empresa", "Empresa/Producao"], document.AllFolders());
    }
}
