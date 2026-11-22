namespace Boekhouding.Api.Data;

/// <summary>Mappen van de server (database en geüploade/opgehaalde factuurbestanden).</summary>
public record AppPaths(string DataDirectory, string UploadDirectory);
