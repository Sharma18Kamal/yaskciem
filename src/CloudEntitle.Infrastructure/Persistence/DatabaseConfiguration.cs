namespace CloudEntitle.Infrastructure.Persistence;

public interface IDatabaseConfiguration
{
    string DatabaseDirectory { get; }
    string DatabasePath { get; }
    string ConnectionString { get; }
}

public sealed class DatabaseConfiguration : IDatabaseConfiguration
{
    public DatabaseConfiguration(string? databaseDirectory = null)
    {
        DatabaseDirectory = databaseDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudEntitle", "Data");
        DatabasePath = Path.Combine(DatabaseDirectory, "cloudentitle.db");
        ConnectionString = $"Data Source={DatabasePath}";
    }

    public string DatabaseDirectory { get; }
    public string DatabasePath { get; }
    public string ConnectionString { get; }
}
