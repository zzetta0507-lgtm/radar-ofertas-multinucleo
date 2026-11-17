PRAGMA journal_mode = WAL;
PRAGMA busy_timeout = 5000;
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS games (
    id TEXT PRIMARY KEY NOT NULL,
    canonical_title TEXT NOT NULL,
    release_date TEXT,
    msrp NUMERIC CHECK (msrp IS NULL OR msrp >= 0),
    currency TEXT CHECK (currency IS NULL OR length(currency) = 3),
    type TEXT NOT NULL CHECK (type IN ('juego', 'juego + DLC', 'DLC', 'otro')),
    short_description TEXT,
    genres_json TEXT,
    developer TEXT,
    metadata_checked_at TEXT,
    playtime_checked_at TEXT
);

CREATE TABLE IF NOT EXISTS platforms (
    id INTEGER PRIMARY KEY,
    name TEXT NOT NULL UNIQUE
);

CREATE TABLE IF NOT EXISTS game_platforms (
    game_id TEXT NOT NULL REFERENCES games(id) ON DELETE CASCADE,
    platform_id INTEGER NOT NULL REFERENCES platforms(id),
    PRIMARY KEY (game_id, platform_id)
);

CREATE TABLE IF NOT EXISTS alternative_titles (
    game_id TEXT NOT NULL REFERENCES games(id) ON DELETE CASCADE,
    title TEXT NOT NULL,
    PRIMARY KEY (game_id, title)
);

CREATE TABLE IF NOT EXISTS sources (
    id TEXT PRIMARY KEY NOT NULL,
    name TEXT NOT NULL,
    source_type TEXT NOT NULL CHECK (source_type IN ('tienda', 'minorista', 'calificacion', 'duracion', 'metadatos')),
    base_url TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS game_sources (
    game_id TEXT NOT NULL REFERENCES games(id) ON DELETE CASCADE,
    source_id TEXT NOT NULL REFERENCES sources(id),
    external_id TEXT NOT NULL,
    source_url TEXT NOT NULL,
    cover_image_url TEXT,
    PRIMARY KEY (game_id, source_id),
    UNIQUE (source_id, external_id)
);

CREATE TABLE IF NOT EXISTS discovery_case_tags (
    game_id TEXT NOT NULL REFERENCES games(id) ON DELETE CASCADE,
    tag TEXT NOT NULL,
    PRIMARY KEY (game_id, tag)
);

CREATE TABLE IF NOT EXISTS price_observations (
    id INTEGER PRIMARY KEY,
    game_id TEXT NOT NULL REFERENCES games(id),
    source_id TEXT NOT NULL REFERENCES sources(id),
    captured_at TEXT NOT NULL,
    product_url TEXT,
    source_product_id TEXT,
    current_price NUMERIC CHECK (current_price IS NULL OR current_price >= 0),
    regular_price NUMERIC CHECK (regular_price IS NULL OR regular_price >= 0),
    currency TEXT CHECK (currency IS NULL OR length(currency) = 3),
    format TEXT NOT NULL CHECK (format IN ('fisico', 'digital', 'desconocido')),
    is_deal INTEGER NOT NULL CHECK (is_deal IN (0, 1)),
    discount_percent NUMERIC CHECK (
        discount_percent IS NULL OR (discount_percent >= 0 AND discount_percent <= 100)
    ),
    is_estimated INTEGER NOT NULL DEFAULT 0 CHECK (is_estimated IN (0, 1))
);

CREATE INDEX IF NOT EXISTS ix_price_observations_game_source_time
    ON price_observations(game_id, source_id, captured_at);

CREATE TABLE IF NOT EXISTS rating_observations (
    id INTEGER PRIMARY KEY,
    game_id TEXT NOT NULL REFERENCES games(id),
    source_id TEXT NOT NULL REFERENCES sources(id),
    captured_at TEXT NOT NULL,
    score NUMERIC NOT NULL,
    score_scale NUMERIC NOT NULL CHECK (score_scale > 0),
    UNIQUE (game_id, source_id, captured_at)
);

CREATE TABLE IF NOT EXISTS playtime_observations (
    id INTEGER PRIMARY KEY,
    game_id TEXT NOT NULL REFERENCES games(id),
    source_id TEXT NOT NULL REFERENCES sources(id),
    captured_at TEXT NOT NULL,
    main_story_minutes INTEGER CHECK (main_story_minutes IS NULL OR main_story_minutes >= 0),
    story_and_extras_minutes INTEGER CHECK (
        story_and_extras_minutes IS NULL OR story_and_extras_minutes >= 0
    ),
    completionist_minutes INTEGER CHECK (
        completionist_minutes IS NULL OR completionist_minutes >= 0
    ),
    is_estimated INTEGER NOT NULL DEFAULT 0 CHECK (is_estimated IN (0, 1)),
    UNIQUE (game_id, source_id, captured_at)
);

CREATE TRIGGER IF NOT EXISTS price_observations_no_update
BEFORE UPDATE ON price_observations
BEGIN
    SELECT RAISE(ABORT, 'price_observations es append-only');
END;

CREATE TRIGGER IF NOT EXISTS price_observations_no_delete
BEFORE DELETE ON price_observations
BEGIN
    SELECT RAISE(ABORT, 'price_observations es append-only');
END;
