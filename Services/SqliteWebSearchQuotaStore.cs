using Microsoft.Data.Sqlite;

namespace Project_no._1.Services;

public interface IWebSearchQuotaStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<WebSearchQuotaReservation> TryReserveAsync(CancellationToken cancellationToken);
    Task<int> GetMonthlyRemainingAsync(CancellationToken cancellationToken);
}

public sealed class SqliteWebSearchQuotaStore(IConfiguration configuration, IWebHostEnvironment environment)
    : IWebSearchQuotaStore
{
    private const int DefaultMonthlyLimit = 900;
    private const int DefaultDailyLimit = 30;
    private const int DefaultHourlyLimit = 5;

    private readonly string _connectionString = BuildConnectionString(configuration, environment);
    private readonly int _monthlyLimit = ReadLimit(configuration, "TAVILY_MONTHLY_SEARCH_LIMIT", DefaultMonthlyLimit);
    private readonly int _dailyLimit = ReadLimit(configuration, "TAVILY_DAILY_SEARCH_LIMIT", DefaultDailyLimit);
    private readonly int _hourlyLimit = ReadLimit(configuration, "TAVILY_HOURLY_SEARCH_LIMIT", DefaultHourlyLimit);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS tavily_search_usage (
                period TEXT PRIMARY KEY,
                searches_used INTEGER NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<WebSearchQuotaReservation> TryReserveAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var periods = new[]
        {
            (Key: $"month:{now:yyyy-MM}", Limit: _monthlyLimit, Name: "monthly"),
            (Key: $"day:{now:yyyy-MM-dd}", Limit: _dailyLimit, Name: "daily"),
            (Key: $"hour:{now:yyyy-MM-dd-HH}", Limit: _hourlyLimit, Name: "hourly")
        };

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);

        var monthlyUsage = 0;
        foreach (var period in periods)
        {
            await using var checkCommand = connection.CreateCommand();
            checkCommand.Transaction = transaction;
            checkCommand.CommandText = "SELECT searches_used FROM tavily_search_usage WHERE period = $period;";
            checkCommand.Parameters.AddWithValue("$period", period.Key);
            var value = await checkCommand.ExecuteScalarAsync(cancellationToken);
            var used = value is null or DBNull ? 0 : Convert.ToInt32(value);
            if (period.Name == "monthly")
            {
                monthlyUsage = used;
            }

            if (used >= period.Limit)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new WebSearchQuotaReservation(false, period.Name, period.Limit, Math.Max(0, _monthlyLimit - monthlyUsage));
            }
        }

        foreach (var period in periods)
        {
            await using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText = """
                INSERT INTO tavily_search_usage(period, searches_used)
                VALUES($period, 1)
                ON CONFLICT(period) DO UPDATE SET searches_used = searches_used + 1;
                """;
            updateCommand.Parameters.AddWithValue("$period", period.Key);
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new WebSearchQuotaReservation(true, null, 0, Math.Max(0, _monthlyLimit - monthlyUsage - 1));
    }

    public async Task<int> GetMonthlyRemainingAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken);
        var used = await GetMonthlyUsageAsync(connection, null, now, cancellationToken);
        return Math.Max(0, _monthlyLimit - used);
    }

    private static async Task<int> GetMonthlyUsageAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT searches_used FROM tavily_search_usage WHERE period = $period;";
        command.Parameters.AddWithValue("$period", $"month:{now:yyyy-MM}");
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static int ReadLimit(IConfiguration configuration, string key, int defaultValue)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!int.TryParse(value, out var limit) || limit < 1)
        {
            throw new InvalidOperationException($"{key} must be a positive whole number.");
        }

        return limit;
    }

    private static string BuildConnectionString(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var databasePath = configuration["TITANAI_DB_PATH"];
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            var userDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            databasePath = Path.Combine(
                string.IsNullOrWhiteSpace(userDataPath) ? environment.ContentRootPath : userDataPath,
                "TitanAI",
                "missions.db");
        }

        return new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }
}

public sealed record WebSearchQuotaReservation(bool Allowed, string? LimitedPeriod, int Limit, int MonthlyRemaining);
