namespace Northpass.Engine.Zapret2;

public static class Zapret2Catalog
{
    public static Stream OpenTrustedManifest() => typeof(Zapret2Catalog).Assembly.GetManifestResourceStream("Northpass.Zapret2.TrustedManifest")
        ?? throw new InvalidOperationException("This Northpass build is missing its trusted engine catalog.");
    public const string ReviewedSourceCommit = "6b6c63e3385fa73f8af3be4a69171e947f5a319d";
    public static IEnumerable<Stream> OpenPreviousTrustedManifests() => typeof(Zapret2Catalog).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith("Northpass.Zapret2.Previous.", StringComparison.Ordinal))
        .Select(n => typeof(Zapret2Catalog).Assembly.GetManifestResourceStream(n)!);
}
