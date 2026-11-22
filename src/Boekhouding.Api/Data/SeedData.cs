namespace Boekhouding.Api.Data;

/// <summary>
/// Startgegevens voor een nieuwe server (bv. Azure, waar je niet zomaar bestanden kunt neerzetten):
/// de map SeedData naast de exe (tiktok-tokens.json, uploads/…) wordt naar de datamap gekopieerd.
/// Bestanden die daar al staan worden nooit overschreven.
/// </summary>
public static class SeedData
{
    public static void CopyMissing(string seedDir, string dataDir)
    {
        if (!Directory.Exists(seedDir)) return;
        var copied = 0;
        foreach (var source in Directory.EnumerateFiles(seedDir, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dataDir, Path.GetRelativePath(seedDir, source));
            if (File.Exists(target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
            copied++;
        }
        if (copied > 0) Console.WriteLine($"Startgegevens: {copied} bestand(en) naar {dataDir} gekopieerd.");
    }
}
