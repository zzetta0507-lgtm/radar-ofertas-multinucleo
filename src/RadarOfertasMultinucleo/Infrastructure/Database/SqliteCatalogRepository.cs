using System.Reflection;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RadarOfertasMultinucleo.Configuration;
using RadarOfertasMultinucleo.Domain;

namespace RadarOfertasMultinucleo.Infrastructure.Database;

public sealed class SqliteCatalogRepository
{
    private readonly string _connectionString;
    private readonly CatalogSeedCatalog _seedCatalog;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SqliteCatalogRepository(PipelineOptions options, CatalogSeedCatalog seedCatalog)
    {
        _seedCatalog = seedCatalog;
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

            await EnsureEstimatedHistoryColumnAsync(connection, cancellationToken);
            await EnsureCatalogEnrichmentColumnsAsync(connection, cancellationToken);
            await EnsureEstimatedPlaytimeColumnAsync(connection, cancellationToken);

            await using var sourceCommand = connection.CreateCommand();
            sourceCommand.CommandText = """
                INSERT INTO sources (id, name, source_type, base_url)
                VALUES
                    ('opencritic', 'OpenCritic', 'calificacion', 'https://opencritic.com'),
                    ('steam', 'Steam', 'tienda', 'https://store.steampowered.com'),
                    ('epicgames', 'Epic Games Store', 'tienda', 'https://store.epicgames.com'),
                    ('gog', 'GOG', 'tienda', 'https://www.gog.com'),
                    ('eneba', 'Eneba', 'tienda', 'https://www.eneba.com'),
                    ('humble', 'Humble Store', 'tienda', 'https://www.humblebundle.com/store'),
                    ('gmg', 'Green Man Gaming', 'tienda', 'https://www.greenmangaming.com'),
                    ('metacritic', 'Metacritic', 'calificacion', 'https://www.metacritic.com'),
                    ('gamersgate', 'GamersGate', 'tienda', 'https://www.gamersgate.com')
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

    private static async Task EnsureEstimatedHistoryColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var columnsCommand = connection.CreateCommand();
        columnsCommand.CommandText = "PRAGMA table_info(price_observations);";
        var hasEstimatedColumn = false;
        await using (var reader = await columnsCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), "is_estimated", StringComparison.OrdinalIgnoreCase))
                {
                    hasEstimatedColumn = true;
                    break;
                }
            }
        }

        if (!hasEstimatedColumn)
        {
            await using var alterCommand = connection.CreateCommand();
            alterCommand.CommandText = """
                ALTER TABLE price_observations
                ADD COLUMN is_estimated INTEGER NOT NULL DEFAULT 0
                    CHECK (is_estimated IN (0, 1));
                """;
            await alterCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task EnsureCatalogEnrichmentColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(games);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }

        var additions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["short_description"] = "TEXT",
            ["genres_json"] = "TEXT",
            ["developer"] = "TEXT",
            ["metadata_checked_at"] = "TEXT",
            ["playtime_checked_at"] = "TEXT"
        };
        foreach (var (column, type) in additions)
        {
            if (columns.Contains(column)) continue;
            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER TABLE games ADD COLUMN {column} {type};";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task EnsureEstimatedPlaytimeColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var columnsCommand = connection.CreateCommand();
        columnsCommand.CommandText = "PRAGMA table_info(playtime_observations);";
        var hasEstimatedColumn = false;
        await using (var reader = await columnsCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), "is_estimated", StringComparison.OrdinalIgnoreCase))
                {
                    hasEstimatedColumn = true;
                    break;
                }
            }
        }

        if (!hasEstimatedColumn)
        {
            await using var alterCommand = connection.CreateCommand();
            alterCommand.CommandText = """
                ALTER TABLE playtime_observations
                ADD COLUMN is_estimated INTEGER NOT NULL DEFAULT 0
                    CHECK (is_estimated IN (0, 1));
                """;
            await alterCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<string>> ReclassifyExistingGamesAsync(
        CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            var existingGames = new List<(string Id, string Title)>();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT id, canonical_title FROM games;";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    existingGames.Add((reader.GetString(0), reader.GetString(1)));
                }
            }

            foreach (var (id, title) in existingGames)
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    "UPDATE games SET type = $type WHERE id = $id AND type <> $type;",
                    cancellationToken,
                    ("$id", id),
                    ("$type", ToDatabaseType(
                        GameTypeClassifier.Classify(title, _seedCatalog.KnownDlcTitles))));
            }

            await transaction.CommitAsync(cancellationToken);
            return existingGames.Select(game => game.Title).ToArray();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<CatalogGameMetadata>> ReadCatalogMetadataAsync(
        CancellationToken cancellationToken)
    {
        var games = new List<CatalogGameMetadata>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                g.id,
                g.canonical_title,
                g.type,
                g.release_date,
                g.msrp,
                g.currency,
                COALESCE((
                    SELECT group_concat(platform_name, char(31))
                    FROM (
                        SELECT p.name AS platform_name
                        FROM game_platforms gp
                        JOIN platforms p ON p.id = gp.platform_id
                        WHERE gp.game_id = g.id
                        ORDER BY p.name
                    )
                ), ''),
                (
                    SELECT gs.cover_image_url
                    FROM game_sources gs
                    WHERE gs.game_id = g.id AND gs.cover_image_url IS NOT NULL
                    ORDER BY CASE gs.source_id WHEN 'steam' THEN 0 ELSE 1 END
                    LIMIT 1
                ),
                (
                    SELECT ro.score
                    FROM rating_observations ro
                    WHERE ro.game_id = g.id AND ro.source_id = 'opencritic'
                    ORDER BY ro.captured_at DESC
                    LIMIT 1
                ),
                (
                    SELECT ro.score
                    FROM rating_observations ro
                    WHERE ro.game_id = g.id AND ro.source_id = 'metacritic'
                    ORDER BY ro.captured_at DESC
                    LIMIT 1
                ),
                (
                    SELECT po.main_story_minutes
                    FROM playtime_observations po
                    WHERE po.game_id = g.id AND po.source_id = 'howlongtobeat'
                        AND po.main_story_minutes IS NOT NULL
                    ORDER BY po.is_estimated, po.captured_at DESC
                    LIMIT 1
                ),
                (
                    SELECT po.story_and_extras_minutes
                    FROM playtime_observations po
                    WHERE po.game_id = g.id AND po.source_id = 'howlongtobeat'
                        AND po.story_and_extras_minutes IS NOT NULL
                    ORDER BY po.is_estimated, po.captured_at DESC
                    LIMIT 1
                ),
                (
                    SELECT po.completionist_minutes
                    FROM playtime_observations po
                    WHERE po.game_id = g.id AND po.source_id = 'howlongtobeat'
                        AND po.completionist_minutes IS NOT NULL
                    ORDER BY po.is_estimated, po.captured_at DESC
                    LIMIT 1
                ),
                (
                    SELECT po.captured_at
                    FROM playtime_observations po
                    WHERE po.game_id = g.id AND po.source_id = 'howlongtobeat'
                        AND (po.main_story_minutes IS NOT NULL OR
                             po.story_and_extras_minutes IS NOT NULL OR
                             po.completionist_minutes IS NOT NULL)
                    ORDER BY po.is_estimated, po.captured_at DESC
                    LIMIT 1
                ),
                g.short_description,
                g.genres_json,
                g.developer,
                COALESCE((
                    SELECT po.is_estimated
                    FROM playtime_observations po
                    WHERE po.game_id = g.id AND po.source_id = 'howlongtobeat'
                        AND po.main_story_minutes IS NOT NULL
                    ORDER BY po.is_estimated, po.captured_at DESC
                    LIMIT 1
                ), 0),
                COALESCE((
                    SELECT po.is_estimated
                    FROM playtime_observations po
                    WHERE po.game_id = g.id AND po.source_id = 'howlongtobeat'
                        AND po.story_and_extras_minutes IS NOT NULL
                    ORDER BY po.is_estimated, po.captured_at DESC
                    LIMIT 1
                ), 0),
                COALESCE((
                    SELECT po.is_estimated
                    FROM playtime_observations po
                    WHERE po.game_id = g.id AND po.source_id = 'howlongtobeat'
                        AND po.completionist_minutes IS NOT NULL
                    ORDER BY po.is_estimated, po.captured_at DESC
                    LIMIT 1
                ), 0)
            FROM games g
            ORDER BY g.canonical_title COLLATE NOCASE, g.id;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var platforms = reader.GetString(6)
                .Split('\u001f', StringSplitOptions.RemoveEmptyEntries);
            games.Add(new CatalogGameMetadata(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4)
                    ? null
                    : Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                platforms,
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8)
                    ? null
                    : Convert.ToDecimal(reader.GetValue(8), CultureInfo.InvariantCulture),
                reader.IsDBNull(9)
                    ? null
                    : Convert.ToDecimal(reader.GetValue(9), CultureInfo.InvariantCulture),
                reader.IsDBNull(10) ? null : reader.GetInt32(10),
                reader.IsDBNull(11) ? null : reader.GetInt32(11),
                reader.IsDBNull(12) ? null : reader.GetInt32(12),
                reader.IsDBNull(13)
                    ? null
                    : DateTimeOffset.Parse(
                        reader.GetString(13),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.IsDBNull(15)
                    ? Array.Empty<string>()
                    : JsonSerializer.Deserialize<string[]>(reader.GetString(15)) ?? Array.Empty<string>(),
                reader.IsDBNull(16) ? null : reader.GetString(16),
                reader.GetInt32(17) != 0,
                reader.GetInt32(18) != 0,
                reader.GetInt32(19) != 0));
        }

        return games;
    }

    public async Task<IReadOnlyList<GameMetadataEnrichmentTarget>> ReadMetadataEnrichmentTargetsAsync(
        CancellationToken cancellationToken)
    {
        var targets = new List<GameMetadataEnrichmentTarget>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                g.id,
                g.canonical_title,
                g.type,
                COALESCE(
                    (
                        SELECT gs.external_id
                        FROM game_sources gs
                        WHERE gs.game_id = g.id AND gs.source_id = 'steam'
                        LIMIT 1
                    ),
                    (
                        SELECT po.product_url
                        FROM price_observations po
                        WHERE po.game_id = g.id
                            AND po.source_id = 'steam'
                            AND po.product_url IS NOT NULL
                        ORDER BY po.captured_at DESC
                        LIMIT 1
                    )
                ),
                g.metadata_checked_at,
                g.playtime_checked_at
            FROM games g
            ORDER BY g.canonical_title COLLATE NOCASE, g.id;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            targets.Add(new GameMetadataEnrichmentTarget(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4)
                    ? null
                    : DateTimeOffset.Parse(
                        reader.GetString(4),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind),
                reader.IsDBNull(5)
                    ? null
                    : DateTimeOffset.Parse(
                        reader.GetString(5),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind)));
        }

        return targets;
    }

    public async Task SaveGameNarrativeMetadataAsync(
        Guid gameId,
        string? shortDescription,
        IReadOnlyList<string> genres,
        string? developer,
        string? releaseDate,
        DateTimeOffset checkedAt,
        CancellationToken cancellationToken)
    {
        var genresJson = genres.Count == 0 ? null : JsonSerializer.Serialize(genres);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE games
                SET short_description = COALESCE($description, short_description),
                    genres_json = COALESCE($genres, genres_json),
                    developer = COALESCE($developer, developer),
                    release_date = COALESCE($releaseDate, release_date),
                    metadata_checked_at = $checkedAt
                WHERE id = $gameId;
                """;
            command.Parameters.AddWithValue("$description", (object?)shortDescription ?? DBNull.Value);
            command.Parameters.AddWithValue("$genres", (object?)genresJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$developer", (object?)developer ?? DBNull.Value);
            command.Parameters.AddWithValue("$releaseDate", (object?)releaseDate ?? DBNull.Value);
            command.Parameters.AddWithValue("$checkedAt", checkedAt.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$gameId", gameId.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task MarkPlaytimeLookupCheckedAsync(
        Guid gameId,
        DateTimeOffset checkedAt,
        CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE games
                SET playtime_checked_at = $checkedAt
                WHERE id = $gameId;
                """;
            command.Parameters.AddWithValue("$checkedAt", checkedAt.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$gameId", gameId.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<PriceHistoryEntry>> ReadPriceHistoryAsync(
        CancellationToken cancellationToken)
    {
        var history = new List<PriceHistoryEntry>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, game_id, source_id, current_price, currency, format, captured_at,
                   product_url, regular_price, is_deal, discount_percent, is_estimated
            FROM price_observations
            WHERE current_price IS NOT NULL
            ORDER BY captured_at, id;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            history.Add(new PriceHistoryEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                ParseOfferFormat(reader.GetString(5)),
                DateTimeOffset.Parse(
                    reader.GetString(6),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8)
                    ? null
                    : Convert.ToDecimal(reader.GetValue(8), CultureInfo.InvariantCulture),
                reader.GetInt32(9) != 0,
                reader.IsDBNull(10)
                    ? null
                    : Convert.ToDecimal(reader.GetValue(10), CultureInfo.InvariantCulture),
                reader.GetInt32(11) != 0));
        }

        return history;
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
                    release_date = COALESCE(excluded.release_date, games.release_date),
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
                ("$type", ToDatabaseType(
                    GameTypeClassifier.Classify(game.CanonicalTitle, _seedCatalog.KnownDlcTitles))));

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

    public async Task<long> SavePriceObservationAsync(
            Guid gameId,
            string canonicalTitle,
            string sourceId,
            string? productUrl,
            decimal? currentPrice,
            decimal? regularPrice,
            string? currency,
            OfferFormat format,
            bool isDeal,
            decimal? discountPercent,
            DateTimeOffset capturedAt,
            CancellationToken cancellationToken,
            bool isEstimated = false)
        {
            if (currentPrice is null)
            {
                throw new ArgumentException("Una observación de precio requiere precio actual.", nameof(currentPrice));
            }

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
                    INSERT INTO price_observations (
                        game_id, source_id, captured_at, product_url, current_price,
                        regular_price, currency, format, is_deal, discount_percent, is_estimated)
                    VALUES (
                        $gameId, $sourceId, $capturedAt, $productUrl, $currentPrice,
                        $regularPrice, $currency, $format, $isDeal, $discountPercent, $isEstimated)
                    RETURNING id;
                    """;
                command.Parameters.AddWithValue("$gameId", gameId.ToString("D"));
                command.Parameters.AddWithValue("$sourceId", sourceId);
                command.Parameters.AddWithValue("$capturedAt", capturedAt.ToUniversalTime().ToString("O"));
                command.Parameters.AddWithValue("$productUrl", (object?)productUrl ?? DBNull.Value);
                command.Parameters.AddWithValue("$currentPrice", currentPrice.Value);
                command.Parameters.AddWithValue("$regularPrice", (object?)regularPrice ?? DBNull.Value);
                command.Parameters.AddWithValue("$currency", (object?)currency ?? DBNull.Value);
                command.Parameters.AddWithValue("$format", ToDatabaseFormat(format));
                command.Parameters.AddWithValue("$isDeal", isDeal ? 1 : 0);
                command.Parameters.AddWithValue("$discountPercent", (object?)discountPercent ?? DBNull.Value);
                command.Parameters.AddWithValue("$isEstimated", isEstimated ? 1 : 0);
                var observationId = (long)(await command.ExecuteScalarAsync(cancellationToken)
                    ?? throw new InvalidOperationException("No se obtuvo el identificador de precio."));
                await transaction.CommitAsync(cancellationToken);
                return observationId;
            }
            finally
            {
                _writeLock.Release();
            }
        }

    public async Task SavePlaytimeObservationAsync(
            Guid gameId,
            string canonicalTitle,
            string sourceId,
            int? mainStoryMinutes,
            int? storyAndExtrasMinutes,
            int? completionistMinutes,
            DateTimeOffset capturedAt,
            CancellationToken cancellationToken,
            bool isEstimated = false)
        {
            if (mainStoryMinutes is null &&
                storyAndExtrasMinutes is null &&
                completionistMinutes is null)
            {
                throw new ArgumentException("La observación de duración no contiene tiempos.");
            }

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
                    INSERT INTO playtime_observations (
                        game_id, source_id, captured_at, main_story_minutes,
                        story_and_extras_minutes, completionist_minutes, is_estimated)
                    VALUES (
                        $gameId, $sourceId, $capturedAt, $mainStory,
                        $storyAndExtras, $completionist, $isEstimated)
                    ON CONFLICT(game_id, source_id, captured_at) DO UPDATE SET
                        main_story_minutes = excluded.main_story_minutes,
                        story_and_extras_minutes = excluded.story_and_extras_minutes,
                        completionist_minutes = excluded.completionist_minutes,
                        is_estimated = excluded.is_estimated;
                    """;
                command.Parameters.AddWithValue("$gameId", gameId.ToString("D"));
                command.Parameters.AddWithValue("$sourceId", sourceId);
                command.Parameters.AddWithValue("$capturedAt", capturedAt.ToUniversalTime().ToString("O"));
                command.Parameters.AddWithValue("$mainStory", (object?)mainStoryMinutes ?? DBNull.Value);
                command.Parameters.AddWithValue("$storyAndExtras", (object?)storyAndExtrasMinutes ?? DBNull.Value);
                command.Parameters.AddWithValue("$completionist", (object?)completionistMinutes ?? DBNull.Value);
                command.Parameters.AddWithValue("$isEstimated", isEstimated ? 1 : 0);
                await command.ExecuteNonQueryAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
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
        externalId.StartsWith("seed:", StringComparison.Ordinal)
            ? "catalog-seed"
            : externalId.StartsWith("steam:", StringComparison.Ordinal)
                ? "steam"
                : "opencritic";

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
            "epicgames" => ("Epic Games Store", "tienda", "https://store.epicgames.com"),
            "gog" => ("GOG", "tienda", "https://www.gog.com"),
            "eneba" => ("Eneba", "tienda", "https://www.eneba.com"),
            "humble" => ("Humble Store", "tienda", "https://www.humblebundle.com/store"),
            "gmg" => ("Green Man Gaming", "tienda", "https://www.greenmangaming.com"),
            "metacritic" => ("Metacritic", "calificacion", "https://www.metacritic.com"),
            "howlongtobeat" => ("How Long To Beat", "duracion", "https://howlongtobeat.com"),
            "gamersgate" => ("GamersGate", "tienda", "https://www.gamersgate.com"),
            "catalog-seed" => ("Catálogo de semillas", "metadatos", "https://store.steampowered.com"),
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

    private static string ToDatabaseFormat(OfferFormat format) =>
        format switch
        {
            OfferFormat.Physical => "fisico",
            OfferFormat.Digital => "digital",
            OfferFormat.Unknown => "desconocido",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Formato de oferta desconocido.")
        };

    private static OfferFormat ParseOfferFormat(string format) =>
        format switch
        {
            "fisico" => OfferFormat.Physical,
            "digital" => OfferFormat.Digital,
            _ => OfferFormat.Unknown
        };
}
