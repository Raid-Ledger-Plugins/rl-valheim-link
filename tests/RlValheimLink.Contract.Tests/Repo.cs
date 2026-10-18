using System.Reflection;

namespace RlValheimLink.Contract.Tests;

internal static class Repo
{
    public static readonly string Root = typeof(Repo).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "RepoRoot").Value!;

    public static string Contract(string rel) => Path.Combine(Root, "contract", "v1", rel);
    public static string Fixture(string rel) => Path.Combine(Root, "fixtures", rel);
}
