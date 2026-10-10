using System.Security.Cryptography;
using System.Text;
using SqlVitals.Engine.Service;

namespace SqlVitals.Collector.Tests;

/// <summary>The real DPAPI, LocalMachine scope (#172): what the service, the admins and the app all decrypt.</summary>
public sealed class MachineSecretProtectorTests
{
    [Fact]
    public void ASecret_ComesBack_AndIsntInTheProtectedText()
    {
        var protector = new MachineSecretProtector();

        var text = protector.Protect("https://hooks.slack.com/services/T0001/B0002/abcdEFGH");

        Assert.DoesNotContain("abcdEFGH", text);
        Assert.Equal("https://hooks.slack.com/services/T0001/B0002/abcdEFGH", protector.Unprotect(text));
    }

    [Fact]
    public void AnotherProgramsMachineSecret_DoesntDecryptAsOurs()
    {
        var theirs = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes("theirs"), null, DataProtectionScope.LocalMachine));

        Assert.ThrowsAny<CryptographicException>(() => new MachineSecretProtector().Unprotect(theirs));
    }
}
