using Npgsql;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.AddRedisClient("cache");
builder.AddNpgsqlDataSource("visits");

var app = builder.Build();

app.MapGet("/", async (IConnectionMultiplexer redis, NpgsqlDataSource visits, IConfiguration configuration) =>
{
    var counted = await redis.GetDatabase().StringIncrementAsync("visits");
    await using var command = visits.CreateCommand("""
        CREATE TABLE IF NOT EXISTS visits (at timestamptz NOT NULL);
        INSERT INTO visits VALUES (now());
        SELECT count(*) FROM visits;
        """);
    var stored = await command.ExecuteScalarAsync();
    return $"{configuration["GREETING"]}! Redis counted {counted} visits and PostgreSQL stored {stored}.";
});

app.MapGet("/health", () => "Healthy");

app.Run();
