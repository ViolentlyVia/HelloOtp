using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using HelloOtp.Core;
using Xunit;

namespace HelloOtp.Tests;

/// <summary>
/// Fails if HelloOtp's own code references any networking type. It covers this project's assemblies
/// only. Framework and library code is covered by the firewall rule the installer adds.
/// </summary>
public class NoNetworkTests
{
    private static readonly string[] Forbidden =
    {
        "System.Net",
        "Windows.Networking",
        "Windows.Web",
        "Microsoft.AspNetCore",
    };

    private static List<string> NetworkReferences(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();

        var found = new List<string>();
        foreach (var handle in metadata.AssemblyReferences)
        {
            var name = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
            if (IsForbidden(name))
                found.Add("assembly " + name);
        }
        foreach (var handle in metadata.TypeReferences)
        {
            var type = metadata.GetTypeReference(handle);
            var ns = metadata.GetString(type.Namespace);
            if (IsForbidden(ns))
                found.Add($"type {ns}.{metadata.GetString(type.Name)}");
        }
        return found;
    }

    private static bool IsForbidden(string name) =>
        Forbidden.Any(prefix => name == prefix || name.StartsWith(prefix + ".", StringComparison.Ordinal));

    [Fact]
    public void ScannerDetectsNetworkCode() =>
        Assert.NotEmpty(NetworkReferences(typeof(System.Net.Http.HttpClient).Assembly.Location));

    [Fact]
    public void CoreHasNoNetworkReferences() =>
        Assert.Empty(NetworkReferences(typeof(Totp).Assembly.Location));

    [Fact]
    public void AppHasNoNetworkReferences() =>
        Assert.Empty(NetworkReferences(typeof(HelloOtp.App).Assembly.Location));
}
