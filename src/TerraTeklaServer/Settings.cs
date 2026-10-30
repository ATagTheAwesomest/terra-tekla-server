namespace TerraTeklaServer;

/// <summary>
/// Startup settings, from local.settings.json next to the exe (Windows service) or environment
/// variables. Every key is described in docs/SETUP.md.
/// </summary>
public sealed record Settings(
    string AccessKey,
    string ConversionUrl,
    string ConversionKey,
    string ClientRoot,
    string DataDir,
    string[] CorsOrigins,
    PhotoSettings Photos)
{
    public string DatabasePath => Path.Combine(DataDir, "terra.db");

    public static Settings From(IConfiguration config)
    {
        var dataDir = (config["DATA_DIR"] ?? "").Trim();
        if (dataDir == "")
            dataDir = "data";
        if (!Path.IsPathRooted(dataDir))
            dataDir = Path.Combine(AppContext.BaseDirectory, dataDir);

        var clientRoot = (config["CLIENT_ROOT"] ?? "").Trim();
        if (clientRoot != "" && !Path.IsPathRooted(clientRoot))
            clientRoot = Path.Combine(AppContext.BaseDirectory, clientRoot);

        return new Settings(
            (config["ACCESS_KEY"] ?? "").Trim(),
            (config["CONVERSION_URL"] ?? "").Trim().TrimEnd('/'),
            (config["CONVERSION_KEY"] ?? "").Trim(),
            clientRoot,
            dataDir,
            (config["CORS_ORIGINS"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            PhotoSettings.From(config));
    }
}
