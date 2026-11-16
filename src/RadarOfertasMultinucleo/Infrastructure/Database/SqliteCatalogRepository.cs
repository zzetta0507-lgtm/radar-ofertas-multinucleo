using System.Reflection;
using Microsoft.Data.Sqlite;
using RadarOfertasMultinucleo.Configuration;
using RadarOfertasMultinucleo.Domain;

namespace RadarOfertasMultinucleo.Infrastructure.Database;

public sealed class SqliteCatalogRepository
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SqliteCatalogRepository(PipelineOptions options)
    {
        var path = Path.GetFullPath(options.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
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
                INSERT INTO sources (id, name, source_type, base_url)
                VALUES
                    ('opencritic', 'OpenCritic', 'calificacion', 'https://opencritic.com'),
                    ('steam', 'Steam', 'tienda', 'https://store.steampowered.com')
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    source_type = excluded.source_type,
                    base_url = excluded.base_url;
                """;
            await sourceCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task SaveGameAsync(GameRecord game, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            var sourceId = SourceId(game.ExternalId);

            await EnsureSourceAsync(connection, transaction, sourceId, cancellationToken);
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
                ("$sourceId", sourceId),
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
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<long> SaveRatingAsync(
        Guid gameId,
        string canonicalTitle,
        string sourceId,
        decimal score,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();

            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT OR IGNORE INTO games (id, canonical_title, type)
                VALUES ($id, $title, 'juego');
                """,
                cancellationToken,
                ("$id", gameId.ToString("D")),
                ("$title", canonicalTitle));
            await EnsureSourceAsync(connection, transaction, sourceId, cancellationToken);

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO rating_observations
                    (game_id, source_id, captured_at, score, score_scale)
                VALUES ($gameId, $sourceId, $capturedAt, $score, 100)
                ON CONFLICT(game_id, source_id, captured_at) DO UPDATE SET
                    score = excluded.score,
                    score_scale = excluded.score_scale;
                """;
            command.Parameters.AddWithValue("$gameId", gameId.ToString("D"));
            command.Parameters.AddWithValue("$sourceId", sourceId);
            command.Parameters.AddWithValue("$capturedAt", capturedAt.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$score", score);
            await command.ExecuteNonQueryAsync(cancellationToken);

            command.Parameters.Clear();
            command.CommandText = """
                SELECT id FROM rating_observations
                WHERE game_id = $gameId AND source_id = $sourceId AND captured_at = $capturedAt;
                """;
            command.Parameters.AddWithValue("$gameId", gameId.ToString("D"));
            command.Parameters.AddWithValue("$sourceId", sourceId);
            command.Parameters.AddWithValue("$capturedAt", capturedAt.ToUniversalTime().ToString("O"));
            var observationId = (long)(await command.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("No se obtuvo el identificador de calificación."));
            await transaction.CommitAsync(cancellationToken);
            return observationId;
        }
        finally
        {
            _writeLock.Release();
        }
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

    private static Task EnsureSourceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceId,
        CancellationToken cancellationToken)
    {
        var source = sourceId.ToLowerInvariant() switch
        {
            "opencritic" => ("OpenCritic", "calificacion", "https://opencritic.com"),
            "steam" => ("Steam", "tienda", "https://store.steampowered.com"),
            "metacritic" => ("Metacritic", "calificacion", "https://www.metacritic.com"),
            "howlongtobeat" => ("How Long To Beat", "duracion", "https://howlongtobeat.com"),
            _ => throw new InvalidOperationException(
                $"No hay metadatos de fuente configurados para '{sourceId}'.")
        };

        return ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO sources (id, name, source_type, base_url)
            VALUES ($id, $name, $type, $url)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                source_type = excluded.source_type,
                base_url = excluded.base_url;
            """,
            cancellationToken,
            ("$id", sourceId),
            ("$name", source.Item1),
            ("$type", source.Item2),
            ("$url", source.Item3));
    }

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
