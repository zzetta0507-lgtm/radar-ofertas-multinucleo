(() => {
  "use strict";

  const PAGE_SIZE = 12;
  const DATA_FILES = {
    catalog: "./data/catalog.json",
    history: "./data/history.json",
    metrics: "./data/metrics.json",
    benchmarks: "./data/benchmarks.json"
  };
  const TYPE_NAMES = {
    0: "juego",
    1: "juego + dlc",
    2: "dlc",
    3: "otro",
    game: "juego",
    gamewithdlc: "juego + dlc",
    dlc: "dlc",
    other: "otro"
  };
  const STORE_NAMES = {
    steam: "Steam",
    playstation: "PlayStation Store",
    playstationstore: "PlayStation Store",
    nintendo: "Nintendo eShop",
    nintendoeshop: "Nintendo eShop",
    xbox: "Xbox Store",
    epic: "Epic Games Store",
    epicgamesstore: "Epic Games Store",
    gog: "GOG",
    amazon: "Amazon",
    walmart: "Walmart",
    bestbuy: "Best Buy",
    target: "Target",
    opencritic: "OpenCritic",
    metacritic: "Metacritic",
    gamersgate: "GamersGate"
  };

  const state = {
    games: [],
    metrics: null,
    benchmarks: null,
    page: 1,
    charts: [],
    detailChart: null
  };

  const elements = {
    updated: document.querySelector("#last-updated"),
    grid: document.querySelector("#catalog-grid"),
    empty: document.querySelector("#catalog-empty"),
    emptyTitle: document.querySelector("#empty-title"),
    emptyDescription: document.querySelector("#empty-description"),
    resultCount: document.querySelector("#result-count"),
    pagination: document.querySelector("#pagination"),
    error: document.querySelector("#catalog-error"),
    search: document.querySelector("#search-input"),
    type: document.querySelector("#type-filter"),
    platform: document.querySelector("#platform-filter"),
    format: document.querySelector("#format-filter"),
    store: document.querySelector("#store-filter"),
    sort: document.querySelector("#sort-filter"),
    dialog: document.querySelector("#game-dialog"),
    dialogContent: document.querySelector("#dialog-content"),
    metricsEmpty: document.querySelector("#metrics-empty"),
    metricsContent: document.querySelector("#metrics-content")
  };

  const currencyFormatters = new Map();

  function numberOrNull(value) {
    if (value === null || value === undefined || value === "") return null;
    const number = Number(value);
    return Number.isFinite(number) ? number : null;
  }

  function normalizeType(value) {
    if (value === null || value === undefined) return "juego";
    const key = String(value).trim().toLowerCase().replace(/[\s_-]+/g, "");
    return TYPE_NAMES[key] || String(value).trim().toLowerCase();
  }

  function normalizeFormat(value) {
    if (typeof value === "number") {
      return ({ 0: "fisico", 1: "digital", 2: "desconocido" })[value] || "desconocido";
    }
    const key = String(value || "").trim().toLowerCase();
    if (key === "physical" || key === "fisico" || key === "físico") return "fisico";
    if (key === "digital") return "digital";
    return "desconocido";
  }

  function normalizeStore(value) {
    const raw = String(value || "Tienda").trim();
    const key = raw.toLowerCase().replace(/[^a-z0-9]/g, "");
    return STORE_NAMES[key] || raw;
  }

  function normalizeList(value) {
    if (Array.isArray(value)) return value.filter(item => item !== null && item !== undefined);
    return value ? [value] : [];
  }

  function escapeHtml(value) {
    return String(value ?? "").replace(/[&<>"']/g, character => ({
      "&": "&amp;",
      "<": "&lt;",
      ">": "&gt;",
      "\"": "&quot;",
      "'": "&#39;"
    })[character]);
  }

  function safeHttpUrl(value) {
    if (!value) return "";
    try {
      const url = new URL(value, window.location.href);
      return url.protocol === "https:" || url.protocol === "http:" ? url.href : "";
    } catch {
      return "";
    }
  }

  function formatMoney(value, currency) {
    const amount = numberOrNull(value);
    if (amount === null) return "Sin precio";
    const code = String(currency || "CRC").toUpperCase();
    if (!currencyFormatters.has(code)) {
      try {
        currencyFormatters.set(code, new Intl.NumberFormat("es-CR", {
          style: "currency",
          currency: code,
          maximumFractionDigits: 2
        }));
      } catch {
        currencyFormatters.set(code, new Intl.NumberFormat("es-CR", {
          maximumFractionDigits: 2
        }));
      }
    }
    return currencyFormatters.get(code).format(amount);
  }

  function formatDuration(value, key = "") {
    const amount = numberOrNull(value);
    if (amount === null) return "No disponible";
    if (key.toLowerCase().includes("minute") || amount >= 100) {
      const minutes = Math.round(amount);
      const hours = Math.floor(minutes / 60);
      const remainder = minutes % 60;
      return hours ? `${hours} h${remainder ? ` ${remainder} min` : ""}` : `${minutes} min`;
    }
    return `${amount} h`;
  }

  function formatDate(value, options = {}) {
    if (!value) return "No disponible";
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return "No disponible";
    return new Intl.DateTimeFormat("es-CR", {
      dateStyle: options.dateStyle || "medium",
      ...(options.timeStyle ? { timeStyle: options.timeStyle } : {})
    }).format(date);
  }

  function getArray(payload, keys = []) {
    if (Array.isArray(payload)) return payload;
    if (!payload || typeof payload !== "object") return [];
    for (const key of keys) {
      if (Array.isArray(payload[key])) return payload[key];
    }
    return [];
  }

  async function loadJson(path) {
    const response = await fetch(path, { cache: "no-store" });
    if (response.status === 404) return null;
    if (!response.ok) throw new Error(`No se pudo cargar ${path} (HTTP ${response.status}).`);
    return response.json();
  }

  function normalizeOffer(raw, fallbackStore = "") {
    const currentPrice = numberOrNull(raw.currentPrice ?? raw.price ?? raw.bestPrice);
    const regularPrice = numberOrNull(raw.regularPrice ?? raw.listPrice ?? raw.msrpAtStore);
    let discountPercent = numberOrNull(raw.discountPercent ?? raw.discount);
    if (discountPercent === null && currentPrice !== null && regularPrice > 0) {
      discountPercent = Math.max(0, Math.round((1 - currentPrice / regularPrice) * 100));
    }
    return {
      store: normalizeStore(raw.store ?? raw.storeName ?? raw.sourceName ?? raw.sourceId ?? fallbackStore),
      sourceId: String(raw.sourceId ?? raw.storeId ?? fallbackStore ?? ""),
      currentPrice,
      regularPrice,
      currency: raw.currency || "CRC",
      format: normalizeFormat(raw.format),
      discountPercent,
      url: safeHttpUrl(raw.productUrl ?? raw.url ?? raw.link),
      capturedAt: raw.capturedAt ?? raw.timestamp ?? raw.retrievedAt ?? null,
      historicalLow: Boolean(raw.isHistoricalLow ?? raw.historicalLow ?? raw.isAllTimeLow),
      isStale: Boolean(raw.isStale ?? raw.stale),
      staleSince: raw.staleSince ?? null,
      rating: numberOrNull(raw.rating ?? raw.score),
      ratingSource: raw.ratingSource || raw.ratingProvider || raw.sourceId || null
    };
  }

  function catalogRows(payload) {
    return getArray(payload, ["games", "catalog", "items", "offers"]);
  }

  function normalizeCatalog(payload) {
    const groups = new Map();
    for (const raw of catalogRows(payload)) {
      if (!raw || typeof raw !== "object") continue;
      const title = raw.canonicalTitle ?? raw.title ?? raw.name;
      if (!title) continue;
      const id = String(raw.gameId ?? raw.id ?? title.toLowerCase());
      const playtime = raw.hltb ?? raw.howLongToBeat ?? raw.playtime ?? {};
      let game = groups.get(id);
      if (!game) {
        game = {
          id,
          title: String(title),
          type: normalizeType(raw.type ?? raw.gameType),
          platforms: new Set(),
          offers: [],
          history: [],
          cover: safeHttpUrl(raw.coverImageUrl ?? raw.coverUrl ?? raw.imageUrl ?? raw.image),
          msrp: numberOrNull(raw.msrp),
          currency: raw.currency || "CRC",
          metacritic: numberOrNull(raw.metacriticScore ?? raw.metacritic),
          openCritic: numberOrNull(raw.openCriticScore),
          rating: null,
          ratingSource: null,
          mainStory: raw.mainStoryMinutes ?? raw.hltbMainStory ?? raw.mainStory ?? playtime.mainStoryMinutes ?? playtime.mainStory ?? null,
          storyExtras: raw.storyAndExtrasMinutes ?? raw.hltbStoryAndExtras ?? raw.storyAndExtras ?? playtime.storyAndExtrasMinutes ?? playtime.storyAndExtras ?? null,
          completionist: raw.completionistMinutes ?? raw.hltbCompletionist ?? raw.completionist ?? playtime.completionistMinutes ?? playtime.completionist ?? null,
          historicalLow: Boolean(raw.isHistoricalLow ?? raw.historicalLow ?? raw.isAllTimeLow),
          updatedAt: raw.capturedAt ?? raw.updatedAt ?? raw.timestamp ?? null
        };
        groups.set(id, game);
      }

      for (const platform of normalizeList(raw.platforms ?? raw.platform)) {
        if (typeof platform === "string") game.platforms.add(platform);
        else if (platform?.name) game.platforms.add(String(platform.name));
      }
      if (!game.cover) {
        game.cover = safeHttpUrl(raw.coverImageUrl ?? raw.coverUrl ?? raw.imageUrl ?? raw.image);
      }
      game.msrp ??= numberOrNull(raw.msrp);
      game.metacritic ??= numberOrNull(raw.metacriticScore ?? raw.metacritic);
      game.openCritic ??= numberOrNull(raw.openCriticScore);
      game.updatedAt ||= raw.capturedAt ?? raw.updatedAt ?? raw.timestamp ?? null;
      game.historicalLow ||= Boolean(raw.isHistoricalLow ?? raw.historicalLow ?? raw.isAllTimeLow);
      normalizeList(raw.history).forEach(point => {
        const price = numberOrNull(point?.currentPrice ?? point?.price);
        const capturedAt = point?.capturedAt ?? point?.timestamp ?? point?.date;
        if (price !== null && capturedAt && !Number.isNaN(new Date(capturedAt).getTime())) {
          game.history.push({
            store: normalizeStore(point.store ?? point.storeName ?? point.sourceId),
            price,
            currency: point.currency || game.currency,
            capturedAt
          });
        }
      });

      const rating = numberOrNull(raw.rating ?? raw.score);
      const ratingSource = String(raw.ratingSource ?? raw.sourceId ?? "").toLowerCase();
      if (rating !== null) {
        if (ratingSource.includes("metacritic") || raw.metacriticScore !== undefined) {
          game.metacritic ??= rating;
        } else if (ratingSource.includes("opencritic") || raw.openCriticScore !== undefined) {
          game.openCritic ??= rating;
        }
      }

      const nestedOffers = normalizeList(raw.offers ?? raw.prices);
      const offers = nestedOffers.length
        ? nestedOffers.map(offer => normalizeOffer(offer, raw.sourceId))
        : [normalizeOffer(raw, raw.sourceId)];
      for (const offer of offers) {
        if (offer.currentPrice !== null || offer.regularPrice !== null) {
          game.offers.push(offer);
        }
      }
    }
    return [...groups.values()].map(game => ({
      ...game,
      platforms: [...game.platforms].sort((a, b) => a.localeCompare(b, "es")),
      offers: uniqueOffers(game.offers)
    }));
  }

  function uniqueOffers(offers) {
    const seen = new Set();
    return offers.filter(offer => {
      const key = `${offer.store}|${offer.currentPrice}|${offer.url}|${offer.capturedAt}`;
      if (seen.has(key)) return false;
      seen.add(key);
      return true;
    });
  }

  function historyRows(payload) {
    return getArray(payload, ["history", "observations", "prices", "items"]);
  }

  function mergeHistory(payload) {
    const byId = new Map(state.games.map(game => [game.id, game]));
    for (const row of historyRows(payload)) {
      if (!row || typeof row !== "object") continue;
      const game = byId.get(String(row.gameId ?? row.id ?? ""));
      if (!game) continue;
      const price = numberOrNull(row.currentPrice ?? row.price);
      const capturedAt = row.capturedAt ?? row.timestamp ?? row.date ?? null;
      if (price === null || !capturedAt || Number.isNaN(new Date(capturedAt).getTime())) continue;
      const store = normalizeStore(row.store ?? row.storeName ?? row.sourceId);
      game.history.push({
        store,
        price,
        currency: row.currency || game.currency,
        capturedAt
      });
    }
    for (const game of state.games) {
      const prices = game.history.map(item => item.price);
      if (prices.length) {
        const minimum = Math.min(...prices);
        game.historicalLow ||= game.offers.some(offer => offer.currentPrice === minimum);
        game.history = game.history.sort((a, b) => new Date(a.capturedAt || 0) - new Date(b.capturedAt || 0));
      }
    }
  }

  function filteredGames() {
    const query = elements.search.value.trim().toLocaleLowerCase("es");
    const selectedType = elements.type.value;
    const selectedPlatform = elements.platform.value;
    const selectedFormat = elements.format.value;
    const selectedStore = elements.store.value;
    const games = state.games.filter(game => {
      if (query && !game.title.toLocaleLowerCase("es").includes(query)) return false;
      if (selectedType && game.type !== selectedType) return false;
      if (selectedPlatform && !game.platforms.includes(selectedPlatform)) return false;
      if (selectedFormat && !game.offers.some(offer => offer.format === selectedFormat)) return false;
      if (selectedStore && !game.offers.some(offer => offer.store === selectedStore)) return false;
      return true;
    });

    const priceOf = game => {
      const prices = game.offers.map(offer => offer.currentPrice).filter(value => value !== null);
      return prices.length ? Math.min(...prices) : Number.POSITIVE_INFINITY;
    };
    const discountOf = game => Math.max(0, ...game.offers.map(offer => offer.discountPercent || 0));
    const sorters = {
      name: (a, b) => a.title.localeCompare(b.title, "es"),
      price: (a, b) => priceOf(a) - priceOf(b) || a.title.localeCompare(b.title, "es"),
      discount: (a, b) => discountOf(b) - discountOf(a) || a.title.localeCompare(b.title, "es"),
      metacritic: (a, b) => (b.metacritic ?? -1) - (a.metacritic ?? -1) || a.title.localeCompare(b.title, "es")
    };
    return games.sort(sorters[elements.sort.value] || sorters.name);
  }

  function renderFilters() {
    const platforms = new Set();
    const stores = new Set();
    state.games.forEach(game => {
      game.platforms.forEach(platform => platforms.add(platform));
      game.offers.forEach(offer => stores.add(offer.store));
    });
    populateSelect(elements.platform, [...platforms].sort((a, b) => a.localeCompare(b, "es")), "Todas");
    populateSelect(elements.store, [...stores].sort((a, b) => a.localeCompare(b, "es")), "Todas las tiendas");
  }

  function populateSelect(select, values, placeholder) {
    const selected = select.value;
    select.replaceChildren(new Option(placeholder, ""));
    values.forEach(value => select.add(new Option(value, value)));
    if (values.includes(selected)) select.value = selected;
  }

  function renderCatalog() {
    const games = filteredGames();
    const pageCount = Math.ceil(games.length / PAGE_SIZE);
    state.page = Math.min(Math.max(1, state.page), Math.max(1, pageCount));
    const pageGames = games.slice((state.page - 1) * PAGE_SIZE, state.page * PAGE_SIZE);

    elements.resultCount.innerHTML = `<strong>${games.length.toLocaleString("es-CR")}</strong> ${games.length === 1 ? "juego" : "juegos"} encontrados`;
    elements.grid.hidden = games.length === 0;
    elements.empty.hidden = games.length !== 0;
    elements.pagination.hidden = pageCount <= 1;
    if (!games.length) {
      elements.emptyTitle.textContent = state.games.length ? "No encontramos coincidencias" : "Catálogo todavía vacío";
      elements.emptyDescription.textContent = state.games.length
        ? "Prueba con otros filtros o limpia la búsqueda."
        : "Cuando el pipeline publique el catálogo, los juegos aparecerán aquí.";
    }

    elements.grid.innerHTML = pageGames.map((game, index) => renderCard(game, index)).join("");
    elements.grid.querySelectorAll("img[data-fallback]").forEach(image => {
      image.addEventListener("error", () => {
        const placeholder = document.createElement("div");
        placeholder.className = "card-image-placeholder";
        placeholder.textContent = image.dataset.fallback;
        image.replaceWith(placeholder);
      }, { once: true });
    });
    renderPagination(pageCount);
    updateSummary();
  }

  function renderCard(game, index) {
    const pricedOffers = game.offers
      .filter(offer => offer.currentPrice !== null)
      .sort((a, b) => a.currentPrice - b.currentPrice);
    const best = pricedOffers[0];
    const topDiscount = best?.discountPercent ?? Math.max(0, ...game.offers.map(offer => offer.discountPercent || 0));
    const historicalLow = game.historicalLow || Boolean(best?.historicalLow);
    const score = game.metacritic ?? game.openCritic;
    const scoreLabel = game.metacritic !== null ? "METACRITIC" : game.openCritic !== null ? "OPENCRITIC" : "";
    const cover = safeHttpUrl(game.cover);
    const alt = escapeHtml(game.title);
    const platformTags = game.platforms.slice(0, 3)
      .map(platform => `<span class="platform-tag">${escapeHtml(platform)}</span>`).join("");
    const sourceLine = best ? escapeHtml(best.store) : "";

    return `
      <article class="game-card">
        <div class="card-image-wrap">
          ${cover
            ? `<img class="card-image" src="${escapeHtml(cover)}" alt="Portada de ${alt}" loading="lazy" referrerpolicy="no-referrer" data-fallback="${escapeHtml(game.title.slice(0, 1).toUpperCase())}">`
            : `<div class="card-image-placeholder">${escapeHtml(game.title.slice(0, 1).toUpperCase())}</div>`}
          ${topDiscount > 0 ? `<span class="discount-badge">−${topDiscount}%</span>` : ""}
          ${historicalLow ? `<span class="historic-badge">MÍNIMO HISTÓRICO</span>` : ""}
        </div>
        <div class="game-card-body">
          <h3 class="game-title">${alt}</h3>
          <div class="card-platforms">${platformTags || `<span class="platform-tag">Plataforma no indicada</span>`}</div>
          <div class="card-bottom">
            <div class="price-block">
              <strong class="price-current ${best ? "" : "no-price"}">${best ? formatMoney(best.currentPrice, best.currency) : "Sin ofertas"}</strong>
              <span class="price-regular">${best?.regularPrice !== null && best?.regularPrice !== undefined ? formatMoney(best.regularPrice, best.currency) : " "}</span>
            </div>
            <div class="rating-block">
              ${score !== null
                ? `<strong class="rating-score">★ ${escapeHtml(score)}</strong><span class="rating-source">${scoreLabel}</span>`
                : `<span class="rating-source">SIN CALIFICACIÓN</span>`}
            </div>
          </div>
          ${sourceLine ? `<p class="card-store">Mejor precio en ${sourceLine}${best.isStale ? ` · <span class="stale-label">dato desactualizado desde ${escapeHtml(formatDate(best.staleSince || best.capturedAt))}</span>` : ""}</p>` : ""}
          <button class="card-open" type="button" data-game-id="${escapeHtml(game.id)}">Ver detalle <span aria-hidden="true">→</span></button>
        </div>
      </article>`;
  }

  function renderPagination(pageCount) {
    elements.pagination.replaceChildren();
    if (pageCount <= 1) return;
    const previous = createPageButton("‹", state.page - 1, state.page === 1, "Página anterior");
    elements.pagination.append(previous);
    const start = Math.max(1, state.page - 2);
    const end = Math.min(pageCount, start + 4);
    for (let page = start; page <= end; page++) {
      const button = createPageButton(String(page), page, false, `Página ${page}`);
      if (page === state.page) {
        button.classList.add("active");
        button.setAttribute("aria-current", "page");
      }
      elements.pagination.append(button);
    }
    elements.pagination.append(createPageButton("›", state.page + 1, state.page === pageCount, "Página siguiente"));
  }

  function createPageButton(label, page, disabled, ariaLabel) {
    const button = document.createElement("button");
    button.className = "page-button";
    button.type = "button";
    button.textContent = label;
    button.disabled = disabled;
    button.setAttribute("aria-label", ariaLabel);
    button.addEventListener("click", () => {
      state.page = page;
      renderCatalog();
      document.querySelector("#catalogo").scrollIntoView({ behavior: "smooth", block: "start" });
    });
    return button;
  }

  function updateSummary() {
    const deals = state.games.filter(game => game.offers.some(offer =>
      offer.currentPrice !== null && (offer.discountPercent || 0) > 0)).length;
    const stores = new Set(state.games.flatMap(game => game.offers.map(offer => offer.store)));
    const platforms = new Set(state.games.flatMap(game => game.platforms));
    document.querySelector("#stat-games").textContent = state.games.length.toLocaleString("es-CR");
    document.querySelector("#stat-deals").textContent = deals.toLocaleString("es-CR");
    document.querySelector("#stat-stores").textContent = stores.size.toLocaleString("es-CR");
    document.querySelector("#stat-platforms").textContent = platforms.size.toLocaleString("es-CR");
  }

  function updateTimestamp(catalogPayload, metricsPayload, benchmarksPayload) {
    const candidates = [
      metricsPayload?.lastRunAt,
      metricsPayload?.lastRun,
      metricsPayload?.generatedAt,
      benchmarksPayload?.generatedAt,
      catalogPayload?.lastUpdated,
      catalogPayload?.generatedAt,
      ...state.games.map(game => game.updatedAt)
    ].filter(Boolean);
    const latest = candidates
      .map(value => new Date(value))
      .filter(date => !Number.isNaN(date.getTime()))
      .sort((a, b) => b - a)[0];
    elements.updated.textContent = latest ? formatDate(latest, { dateStyle: "medium", timeStyle: "short" }) : "Sin datos publicados";
  }

  function valueFrom(source, keys) {
    for (const key of keys) {
      if (source?.[key] !== undefined && source[key] !== null) return source[key];
    }
    return null;
  }

  function formatMilliseconds(value) {
    const amount = numberOrNull(value);
    if (amount === null) return "No disponible";
    if (amount >= 1000) return `${(amount / 1000).toFixed(2)} s`;
    return `${amount.toFixed(amount % 1 ? 1 : 0)} ms`;
  }

  function renderMetrics(metricsPayload, benchmarksPayload) {
    const hasMetrics = metricsPayload !== null && metricsPayload !== undefined;
    const hasBenchmarkData = benchmarkRows(null, benchmarksPayload).length > 0;
    const hasPanelData = hasMetrics || hasBenchmarkData;
    elements.metricsEmpty.hidden = hasPanelData;
    elements.metricsContent.hidden = !hasPanelData;
    if (!hasPanelData) {
      destroyCharts();
      return;
    }

    const duration = hasMetrics
      ? valueFrom(metricsPayload, ["durationMs", "totalDurationMs", "elapsedMilliseconds"])
      : null;
    const durationMs = numberOrNull(duration);
    const mode = hasMetrics
      ? valueFrom(metricsPayload, ["mode", "executionMode"])
      : null;
    document.querySelector("#metric-run-date").textContent = formatDate(
      hasMetrics ? valueFrom(metricsPayload, ["lastRunAt", "lastRun", "generatedAt"]) : null,
      { dateStyle: "medium", timeStyle: "short" });
    document.querySelector("#metric-run-duration").textContent =
      durationMs === null
        ? `Duración no disponible${mode ? ` · ${mode}` : ""}`
        : `Duración: ${(durationMs / 1000).toFixed(2)} s${mode ? ` · ${mode}` : ""}`;
    document.querySelector("#metric-unmatched").textContent =
      numberOrNull(hasMetrics ? valueFrom(metricsPayload, ["unmatchedGames", "unmatched", "gamesWithoutMatch"]) : null)?.toLocaleString("es-CR") ?? "—";
    document.querySelector("#metric-uncertain").textContent =
      numberOrNull(hasMetrics ? valueFrom(metricsPayload, ["uncertainMatches", "doubtfulMatches", "uncertain"]) : null)?.toLocaleString("es-CR") ?? "—";

    renderStageBreakdown(hasMetrics ? valueFrom(metricsPayload, ["stageDurations", "stages", "stageTimes"]) : null);
    renderRequestBreakdown(hasMetrics ? valueFrom(metricsPayload, ["requestsBySite", "requestsBySource", "sources"]) : null);
    renderBenchmarkCharts(metricsPayload, benchmarksPayload);
  }

  function stageEntries(value) {
    if (Array.isArray(value)) {
      return value.map((stage, index) => ({
        name: stage.name ?? stage.stageName ?? `Etapa ${index}`,
        time: numberOrNull(stage.durationMs ?? stage.elapsedMs ?? stage.timeMs ?? stage.milliseconds)
      })).filter(stage => stage.time !== null);
    }
    if (value && typeof value === "object") {
      return Object.entries(value).map(([name, data]) => ({
        name,
        time: numberOrNull(typeof data === "object"
          ? data.durationMs ?? data.elapsedMs ?? data.timeMs
          : data)
      })).filter(stage => stage.time !== null);
    }
    return [];
  }

  function renderStageBreakdown(value) {
    const container = document.querySelector("#stage-breakdown");
    const stages = stageEntries(value);
    if (!stages.length) {
      container.innerHTML = `<div class="mini-empty">No hay desglose de etapas en el archivo de métricas.</div>`;
      return;
    }
    const max = Math.max(1, ...stages.map(stage => stage.time));
    container.innerHTML = stages.map(stage => `
      <div class="stage-row">
        <span class="stage-name">${escapeHtml(stage.name)}</span>
        <div class="stage-track" aria-hidden="true"><div class="stage-fill" style="width:${Math.max(2, stage.time / max * 100)}%"></div></div>
        <span class="stage-time">${formatMilliseconds(stage.time)}</span>
      </div>`).join("");
  }

  function requestEntries(value) {
    if (Array.isArray(value)) return value;
    if (value && typeof value === "object") {
      return Object.entries(value).map(([site, counts]) => ({ site, ...(counts || {}) }));
    }
    return [];
  }

  function renderRequestBreakdown(value) {
    const container = document.querySelector("#request-breakdown");
    const requests = requestEntries(value);
    if (!requests.length) {
      container.innerHTML = `<div class="mini-empty">No hay conteos de solicitudes por fuente.</div>`;
      return;
    }
    container.innerHTML = requests.map(row => {
      const site = normalizeStore(row.site ?? row.name ?? row.source);
      const success = numberOrNull(row.successful ?? row.success ?? row.successfulRequests) ?? 0;
      const retried = numberOrNull(row.retried ?? row.retries ?? row.retriedRequests) ?? 0;
      const failed = numberOrNull(row.failed ?? row.failures ?? row.failedRequests) ?? 0;
      const total = numberOrNull(row.total) ?? success + retried + failed;
      return `<div class="request-row">
        <span class="request-name">${escapeHtml(site)}</span>
        <span class="request-counts">
          <span class="success" title="Exitosas">✓ ${success}</span>
          <span class="retry" title="Reintentadas">↻ ${retried}</span>
          <span class="failure" title="Fallidas">× ${failed}</span>
        </span>
        <span class="stage-time">${total}</span>
      </div>`;
    }).join("");
  }

  function benchmarkRows(metricsPayload, benchmarksPayload) {
    const source = benchmarksPayload || metricsPayload?.benchmarks || metricsPayload;
    if (!source) return [];
    return getArray(source, ["cpuSweep", "results", "runs", "benchmarkResults"])
      .map(row => row && typeof row === "object"
        ? { ...row, workersCpu: row.workersCpu ?? row.workers_cpu ?? row.parallelism ?? row.p ?? row.workers }
        : null)
      .filter(row => row && typeof row === "object");
  }

  function renderBenchmarkCharts(metricsPayload, benchmarksPayload) {
    destroyCharts();
    const rows = benchmarkRows(metricsPayload, benchmarksPayload);
    const cpuRows = rows.filter(row => numberOrNull(row.workersCpu ?? row.workers_cpu ?? row.parallelism ?? row.p) !== null);
    const labels = cpuRows.map(row => String(row.workersCpu ?? row.workers_cpu ?? row.parallelism ?? row.p));
    const speedupValues = cpuRows.map(row =>
      numberOrNull(row.speedupRelative ?? row.relativeSpeedup ?? row.sRel ?? row.speedup) ?? null);
    const efficiencyValues = cpuRows.map(row =>
      numberOrNull(row.efficiency ?? row.efficiencyPercent ?? row.e) ?? null);
    const hasData = labels.length > 0 && (speedupValues.some(value => value !== null) || efficiencyValues.some(value => value !== null));

    document.querySelector("#speedup-chart").hidden = !hasData;
    document.querySelector("#efficiency-chart").hidden = !hasData;
    document.querySelector("#speedup-empty").hidden = hasData;
    document.querySelector("#efficiency-empty").hidden = hasData;
    if (!hasData || typeof Chart === "undefined") return;

    const commonOptions = {
      responsive: true,
      maintainAspectRatio: false,
      interaction: { intersect: false, mode: "index" },
      plugins: {
        legend: { display: false },
        tooltip: { backgroundColor: "#252832", titleColor: "#f3f4f6", bodyColor: "#c5c8d0", borderColor: "#3a3e49", borderWidth: 1 }
      },
      scales: {
        x: { grid: { display: false }, ticks: { color: "#858a97", font: { size: 10 } }, title: { display: true, text: "Trabajadores CPU", color: "#858a97" } },
        y: { beginAtZero: true, grid: { color: "rgba(255,255,255,.07)" }, ticks: { color: "#858a97", font: { size: 10 } } }
      }
    };
    const speedupContext = document.querySelector("#speedup-chart").getContext("2d");
    const efficiencyContext = document.querySelector("#efficiency-chart").getContext("2d");
    state.charts.push(new Chart(speedupContext, {
      type: "line",
      data: { labels, datasets: [{ data: speedupValues, borderColor: "#c8f169", backgroundColor: "rgba(200,241,105,.12)", pointBackgroundColor: "#c8f169", pointRadius: 3, borderWidth: 2, tension: .28, spanGaps: true, fill: true }] },
      options: commonOptions
    }));
    state.charts.push(new Chart(efficiencyContext, {
      type: "line",
      data: { labels, datasets: [{ data: efficiencyValues, borderColor: "#76cde0", backgroundColor: "rgba(118,205,224,.1)", pointBackgroundColor: "#76cde0", pointRadius: 3, borderWidth: 2, tension: .28, spanGaps: true, fill: true }] },
      options: commonOptions
    }));
  }

  function destroyCharts() {
    state.charts.forEach(chart => chart.destroy());
    state.charts = [];
  }

  function openGameDetails(gameId) {
    const game = state.games.find(item => item.id === gameId);
    if (!game) return;
    state.dialogChart?.destroy();
    state.dialogChart = null;
    const cover = safeHttpUrl(game.cover);
    const prices = game.offers
      .filter(offer => offer.currentPrice !== null)
      .sort((a, b) => a.currentPrice - b.currentPrice);
    const ratings = [
      ["Metacritic", game.metacritic],
      ["OpenCritic", game.openCritic]
    ].filter(([, value]) => value !== null);
    const hltb = [
      ["Historia principal", game.mainStory, "mainStoryMinutes"],
      ["Historia + extras", game.storyExtras, "storyAndExtrasMinutes"],
      ["Completista", game.completionist, "completionistMinutes"]
    ];
    const detailPriceRows = prices.length
      ? prices.map(offer => {
        const link = safeHttpUrl(offer.url);
        return `<div class="detail-price-row">
          <strong>${escapeHtml(offer.store)}</strong>
          <span>${formatMoney(offer.currentPrice, offer.currency)}${offer.regularPrice !== null ? ` <small><s>${formatMoney(offer.regularPrice, offer.currency)}</s></small>` : ""}</span>
          <small>${formatDate(offer.capturedAt)}</small>
          ${offer.isStale ? `<small class="stale-label">Dato desactualizado desde ${escapeHtml(formatDate(offer.staleSince || offer.capturedAt))}</small>` : ""}
          ${link ? `<a href="${escapeHtml(link)}" target="_blank" rel="noopener noreferrer">Abrir tienda ↗</a>` : `<small>Enlace no disponible</small>`}
        </div>`;
      }).join("")
      : `<div class="detail-empty">No hay observaciones de precio disponibles para este juego.</div>`;

    const scores = ratings.length
      ? ratings.map(([name, value]) => `<div class="detail-stat"><span>${escapeHtml(name)}</span><strong>${escapeHtml(value)}</strong></div>`).join("")
      : `<div class="detail-empty">No hay calificaciones publicadas.</div>`;
    const times = hltb.map(([name, value, key]) =>
      `<div class="detail-stat"><span>${escapeHtml(name)}</span><strong>${formatDuration(value, key)}</strong></div>`).join("");
    const platforms = game.platforms.length
      ? game.platforms.map(platform => `<span class="platform-tag">${escapeHtml(platform)}</span>`).join("")
      : `<span class="platform-tag">No indicadas</span>`;

    elements.dialogContent.innerHTML = `
      <div class="detail-header">
        ${cover ? `<img class="detail-cover" src="${escapeHtml(cover)}" alt="Portada de ${escapeHtml(game.title)}" referrerpolicy="no-referrer">` : `<div class="detail-cover card-image-placeholder">${escapeHtml(game.title.slice(0, 1).toUpperCase())}</div>`}
        <div>
          <p class="eyebrow">${escapeHtml(game.type.toLocaleUpperCase("es"))}</p>
          <h2 class="detail-title" id="dialog-title">${escapeHtml(game.title)}</h2>
          <div class="detail-meta">${platforms}</div>
          <div class="detail-stats">
            <div class="detail-stat"><span>MSRP</span><strong>${game.msrp === null ? "No disponible" : formatMoney(game.msrp, game.currency)}</strong></div>
            ${ratings.length ? ratings.map(([name, value]) => `<div class="detail-stat"><span>${escapeHtml(name)}</span><strong>${escapeHtml(value)}</strong></div>`).join("") : ""}
          </div>
        </div>
      </div>
      <section class="detail-section">
        <h3>Precios disponibles</h3>
        <div class="detail-prices">${detailPriceRows}</div>
      </section>
      <section class="detail-section">
        <h3>How Long To Beat</h3>
        <div class="detail-stats">${times}</div>
      </section>
      <section class="detail-section">
        <h3>Historial de precios por tienda</h3>
        <div class="detail-chart-wrap"><canvas id="detail-price-chart" aria-label="Historial de precios"></canvas></div>
        <p id="detail-history-empty" class="detail-empty" hidden>No hay historial de precios publicado.</p>
      </section>`;

    elements.dialog.showModal();
    renderDetailChart(game);
  }

  function renderDetailChart(game) {
    const history = game.history || [];
    const canvas = document.querySelector("#detail-price-chart");
    const empty = document.querySelector("#detail-history-empty");
    if (!history.length || typeof Chart === "undefined") {
      canvas.hidden = true;
      empty.hidden = false;
      if (history.length && typeof Chart === "undefined") {
        empty.textContent = "No se pudo cargar Chart.js para dibujar el historial.";
      }
      return;
    }

    const grouped = new Map();
    history.forEach(point => {
      if (!point.capturedAt) return;
      if (!grouped.has(point.store)) grouped.set(point.store, []);
      grouped.get(point.store).push(point);
    });
    const dateKeys = [...new Set(history
      .filter(point => point.capturedAt && !Number.isNaN(new Date(point.capturedAt).getTime()))
      .map(point => new Date(point.capturedAt).toISOString()))]
      .sort();
    const dateIndex = new Map(dateKeys.map((date, index) => [date, index]));
    const labels = dateKeys.map(date => formatDate(date, { dateStyle: "short" }));
    const palette = ["#c8f169", "#76cde0", "#ff9a66", "#c39af7", "#ff777f", "#f4d36a"];
    const datasets = [...grouped.entries()].map(([store, points], index) => ({
      label: store,
      data: points.reduce((values, point) => {
        const key = new Date(point.capturedAt).toISOString();
        const position = dateIndex.get(key);
        if (position !== undefined) values[position] = point.price;
        return values;
      }, Array(dateKeys.length).fill(null)),
      borderColor: palette[index % palette.length],
      backgroundColor: palette[index % palette.length],
      pointRadius: 2,
      borderWidth: 2,
      tension: .25
    }));
    if (!datasets.length) {
      canvas.hidden = true;
      empty.hidden = false;
      return;
    }
    empty.hidden = true;
    state.detailChart = new Chart(canvas.getContext("2d"), {
      type: "line",
      data: { labels, datasets },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        interaction: { intersect: false, mode: "nearest" },
        plugins: {
          legend: { labels: { color: "#b6bac4", boxWidth: 9, usePointStyle: true, font: { size: 10 } } },
          tooltip: { callbacks: { label: item => `${item.dataset.label}: ${formatMoney(item.parsed.y, game.currency)}` } }
        },
        scales: {
          x: { grid: { color: "rgba(255,255,255,.06)" }, ticks: { color: "#858a97", maxTicksLimit: 6 } },
          y: { grid: { color: "rgba(255,255,255,.07)" }, ticks: { color: "#858a97", callback: value => formatMoney(value, game.currency) } }
        }
      }
    });
  }

  async function initialize() {
    const paths = Object.values(DATA_FILES);
    const loaded = await Promise.allSettled(paths.map(loadJson));
    const values = {};
    const errors = [];
    Object.keys(DATA_FILES).forEach((key, index) => {
      const result = loaded[index];
      if (result.status === "fulfilled") values[key] = result.value;
      else {
        values[key] = null;
        errors.push(result.reason?.message || `Error al cargar ${DATA_FILES[key]}.`);
      }
    });

    state.games = normalizeCatalog(values.catalog);
    mergeHistory(values.history);
    state.metrics = values.metrics;
    state.benchmarks = values.benchmarks;
    renderFilters();
    renderCatalog();
    renderMetrics(state.metrics, state.benchmarks);
    updateTimestamp(values.catalog, values.metrics, values.benchmarks);

    if (errors.length) {
      elements.error.hidden = false;
      elements.error.textContent = errors.join(" ");
    }
  }

  function resetFilters() {
    elements.search.value = "";
    elements.type.value = "";
    elements.platform.value = "";
    elements.format.value = "";
    elements.store.value = "";
    elements.sort.value = "name";
    state.page = 1;
    renderCatalog();
  }

  [elements.search, elements.type, elements.platform, elements.format, elements.store, elements.sort]
    .forEach(control => control.addEventListener(control === elements.search ? "input" : "change", () => {
      state.page = 1;
      renderCatalog();
    }));
  document.querySelector("#reset-filters").addEventListener("click", resetFilters);
  elements.grid.addEventListener("click", event => {
    const button = event.target.closest("[data-game-id]");
    if (button) openGameDetails(button.dataset.gameId);
  });
  document.querySelector("#dialog-close").addEventListener("click", () => elements.dialog.close());
  elements.dialog.addEventListener("click", event => {
    if (event.target === elements.dialog) elements.dialog.close();
  });
  elements.dialog.addEventListener("close", () => {
    state.detailChart?.destroy();
    state.detailChart = null;
  });
  document.addEventListener("keydown", event => {
    if (event.key === "/" && !event.ctrlKey && !event.metaKey && !event.altKey &&
        !["INPUT", "SELECT", "TEXTAREA"].includes(document.activeElement?.tagName)) {
      event.preventDefault();
      elements.search.focus();
    }
  });

  initialize();
})();
