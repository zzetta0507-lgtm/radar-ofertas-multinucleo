using System.Reflection;
using Microsoft.Data.Sqlite;
using RadarOfertasMultinucleo.Configuration;
using RadarOfertasMultinucleo.Domain;

namespace RadarOfertasMultinucleo.Infrastructure.Database;

public sealed class SqliteCatalogRepository
{
    private readonly string _connectionString;

    public SqliteCatalogRepository(PipelineOptions options)
    {
        var path = Path.GetFullPath(options.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            ForeignKeys = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var schemaResource = Assembly.GetExecutingAssembly()
            .GetManifestResourceNames()
            .Single(name => name.EndsWith(
                "Infrastructure.Database.001_esquema_inicial.sql",
                StringComparison.Ordinal));
        await using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(schemaResource)
            ?? throw new InvalidOperationException($"No se encontró el recurso SQL {schemaResource}."))
        using (var reader = new StreamReader(stream))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = await reader.ReadToEndAsync(cancellationToken);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var sourceCommand = connection.CreateCommand();
        sourceCommand.CommandText = """
            INSERT OR IGNORE INTO sources (id, name, source_type, base_url)
            VALUES
                ('opencritic', 'OpenCritic', 'calificacion', 'https://opencritic.com'),
                ('steam', 'Steam', 'tienda', 'https://store.steampowered.com');
            """;
        await sourceCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveGameAsync(GameRecord game, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO games (id, canonical_title, release_date, msrp, currency, type)
            VALUES ($id, $title, $releaseDate, $msrp, $currency, $type)
            ON CONFLICT(id) DO UPDATE SET
                canonical_title = excluded.canonical_title,
                release_date = excluded.release_date,
                msrp = excluded.msrp,
                currency = excluded.currency,
                type = excluded.type;
            """,
            cancellationToken,
            ("$id", game.Id.ToString("D")),
            ("$title", game.CanonicalTitle),
            ("$releaseDate", game.ReleaseDate?.ToString("yyyy-MM-dd")),
            ("$msrp", game.Msrp),
            ("$currency", game.Currency),
            ("$type", ToDatabaseType(game.Type)));

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO game_sources (game_id, source_id, external_id, source_url, cover_image_url)
            VALUES ($gameId, $sourceId, $externalId, $sourceUrl, $cover)
            ON CONFLICT(game_id, source_id) DO UPDATE SET
                external_id = excluded.external_id,
                source_url = excluded.source_url,
                cover_image_url = excluded.cover_image_url;
            """,
            cancellationToken,
            ("$gameId", game.Id.ToString("D")),
            ("$sourceId", SourceId(game.ExternalId)),
            ("$externalId", game.ExternalId),
            ("$sourceUrl", game.SourceUrl),
            ("$cover", game.CoverImageUrl));

        foreach (var platform in game.Platforms.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await ExecuteAsync(
                connection,
                transaction,
                "INSERT OR IGNORE INTO platforms (name) VALUES ($name);",
                cancellationToken,
                ("$name", platform));

            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT OR IGNORE INTO game_platforms (game_id, platform_id)
                SELECT $gameId, id FROM platforms WHERE name = $name;
                """,
                cancellationToken,
                ("$gameId", game.Id.ToString("D")),
                ("$name", platform));
        }

        foreach (var title in game.AlternativeTitles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await ExecuteAsync(
                connection,
                transaction,
                "INSERT OR IGNORE INTO alternative_titles (game_id, title) VALUES ($gameId, $title);",
                cancellationToken,
                ("$gameId", game.Id.ToString("D")),
                ("$title", title));
        }

        foreach (var tag in game.DifficultCaseTags.Distinct(StringComparer.Ordinal))
        {
            await ExecuteAsync(
                connection,
                transaction,
                "INSERT OR IGNORE INTO discovery_case_tags (game_id, tag) VALUES ($gameId, $tag);",
                cancellationToken,
                ("$gameId", game.Id.ToString("D")),
                ("$tag", tag));
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<long> SaveRatingAsync(
        Guid gameId,
        string sourceId,
        decimal score,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO rating_observations
                (game_id, source_id, captured_at, score, score_scale)
            VALUES ($gameId, $sourceId, $capturedAt, $score, 100);
            """;
        command.Parameters.AddWithValue("$gameId", gameId.ToString("D"));
        command.Parameters.AddWithValue("$sourceId", sourceId);
        command.Parameters.AddWithValue("$capturedAt", capturedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$score", score);
        await command.ExecuteNonQueryAsync(cancellationToken);

        command.Parameters.Clear();
        command.CommandText = "SELECT last_insert_rowid();";
        return (long)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("No se obtuvo el identificador de calificación."));
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string SourceId(string externalId) =>
        externalId.StartsWith("steam:", StringComparison.Ordinal) ? "steam" : "opencritic";

    private static string ToDatabaseType(GameType type) =>
        type switch
        {
            GameType.Game => "juego",
            GameType.GameWithDlc => "juego + DLC",
            GameType.Dlc => "DLC",
            GameType.Other => "otro",
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Tipo de juego desconocido.")
        };
}
