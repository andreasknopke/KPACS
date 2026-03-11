using System.Text;

namespace KPACS.ShareRelay.Infrastructure;

public static class PostgresConnectionStringResolver
{
    public static string Resolve(IConfiguration configuration)
    {
        string? railwayUrl = configuration["DATABASE_URL"];
        if (!string.IsNullOrWhiteSpace(railwayUrl))
        {
            return ConvertRailwayUrl(railwayUrl);
        }

        string? host = configuration["PGHOST"];
        string? port = configuration["PGPORT"];
        string? database = configuration["PGDATABASE"];
        string? username = configuration["PGUSER"];
        string? password = configuration["PGPASSWORD"];
        if (!string.IsNullOrWhiteSpace(host)
            && !string.IsNullOrWhiteSpace(database)
            && !string.IsNullOrWhiteSpace(username)
            && !string.IsNullOrWhiteSpace(password))
        {
            return BuildConnectionString(
                host,
                int.TryParse(port, out int parsedPort) ? parsedPort : 5432,
                database,
                username,
                password);
        }

        string? direct = configuration.GetConnectionString("RelayDb");
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        throw new InvalidOperationException("No PostgreSQL connection string was configured. Set DATABASE_URL, PGHOST/PGDATABASE/PGUSER/PGPASSWORD, or ConnectionStrings:RelayDb.");
    }

    private static string ConvertRailwayUrl(string databaseUrl)
    {
        if (!Uri.TryCreate(databaseUrl, UriKind.Absolute, out Uri? uri))
        {
            throw new InvalidOperationException("DATABASE_URL is not a valid PostgreSQL URI.");
        }

        string userInfo = Uri.UnescapeDataString(uri.UserInfo);
        string[] userParts = userInfo.Split(':', 2, StringSplitOptions.None);
        string username = userParts.ElementAtOrDefault(0) ?? string.Empty;
        string password = userParts.ElementAtOrDefault(1) ?? string.Empty;
        string database = uri.AbsolutePath.Trim('/');

        return BuildConnectionString(uri.Host, uri.Port, database, username, password);
    }

    private static string BuildConnectionString(string host, int port, string database, string username, string password)
    {
        var builder = new StringBuilder();
        builder.Append("Host=").Append(host).Append(';');
        builder.Append("Port=").Append(port).Append(';');
        builder.Append("Database=").Append(database).Append(';');
        builder.Append("Username=").Append(username).Append(';');
        builder.Append("Password=").Append(password).Append(';');
        builder.Append("Ssl Mode=Require;Trust Server Certificate=true");
        return builder.ToString();
    }
}
