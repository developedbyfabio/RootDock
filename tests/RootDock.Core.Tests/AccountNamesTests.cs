using RootDock.Core.Sftp;

namespace RootDock.Core.Tests;

/// <summary>Dono e grupo pelo nome no painel de arquivos (/etc/passwd e /etc/group).</summary>
public class AccountNamesTests
{
    [Fact]
    public void Reads_ids_and_names_from_passwd_and_group()
    {
        const string passwd = """
            root:x:0:0:root:/root:/bin/bash
            # comentário
            www-data:x:33:33:www-data:/var/www:/usr/sbin/nologin
            ndbadm:x:1001:79:SAP HANA:/usr/sap/NDB/home:/bin/sh
            +::::::
            quebrada
            toor:x:0:0:outro nome para o 0:/root:/bin/sh
            """;
        const string group = "root:x:0:\nsapsys:x:79:ndbadm\r\nwww-data:x:33:\n";

        var users = AccountNames.Parse(passwd);
        var groups = AccountNames.Parse(group);

        Assert.Equal("root", users[0]); // o primeiro nome de cada id vale
        Assert.Equal("www-data", users[33]);
        Assert.Equal("ndbadm", users[1001]);
        Assert.Equal(3, users.Count);
        Assert.Equal("sapsys", groups[79]);
        Assert.Equal(3, groups.Count);
    }
}
