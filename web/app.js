(() => {
  "use strict";

  const PAGE_SIZE = 12;
  const FALLBACK_CRC_RATE = 510;
  const PRICE_STALE_AFTER_MS = 24 * 60 * 60 * 1000;
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
    epicgames: "Epic Games Store",
    gog: "GOG",
    eneba: "Eneba",
    humble: "Humble Store",
    humblestore: "Humble Store",
    gmg: "Green Man Gaming",
    greenmangaming: "Green Man Gaming",
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
    detailChart: null,
    catalogPayload: null,
    historyPayload: null,
    refreshing: false,
    cheapSharkStores: null,
    dialogGameId: null,
    exchangeRate: FALLBACK_CRC_RATE,
    exchangeRateUpdatedAt: 0,
    displayCurrency: "CRC",
    showDealsOnly: false,
    lastRefreshAt: null
  };

  const elements = {
    updated: document.querySelector("#last-updated"),
    refresh: document.querySelector("#refresh-button"),
    currencyCrc: document.querySelector("#currency-crc"),
    currencyUsd: document.querySelector("#currency-usd"),
    dealsFilter: document.querySelector("#deals-filter"),
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

  function convertedAmount(value, currency) {
    const amount = numberOrNull(value);
    if (amount === null) return null;
    const sourceCurrency = String(currency || "USD").toUpperCase();
    if (sourceCurrency === state.displayCurrency) return amount;
    if (sourceCurrency === "USD" && state.displayCurrency === "CRC") {
      return amount * state.exchangeRate;
    }
    if (sourceCurrency === "CRC" && state.displayCurrency === "USD") {
      return amount / state.exchangeRate;
    }
    return amount;
  }

  function formatMoney(value, currency) {
    const amount = convertedAmount(value, currency);
    if (amount === null) return "Sin precio";
    const code = state.displayCurrency;
    if (!currencyFormatters.has(code)) {
      try {
        currencyFormatters.set(code, new Intl.NumberFormat("es-CR", {
          style: "currency",
          currency: code,
          maximumFractionDigits: code === "CRC" ? 0 : 2
        }));
      } catch {
        currencyFormatters.set(code, new Intl.NumberFormat("es-CR", {
          maximumFractionDigits: 2
        }));
      }
    }
    return currencyFormatters.get(code).format(amount);
  }

  function formatMoneyWithReference(value, currency) {
    const primary = formatMoney(value, currency);
    if (numberOrNull(value) === null || state.displayCurrency !== "CRC" ||
        String(currency || "USD").toUpperCase() !== "USD") return primary;
    return `${primary} · ${new Intl.NumberFormat("en-US", {
      style: "currency",
      currency: "USD",
      maximumFractionDigits: 2
    }).format(Number(value))}`;
  }

  async function loadExchangeRate() {
    const controller = new AbortController();
    const timeout = window.setTimeout(() => controller.abort(), 8000);
    try {
      const response = await fetch("https://open.er-api.com/v6/latest/USD", {
        cache: "no-store",
        signal: controller.signal
      });
      if (!response.ok) throw new Error(`Tipo de cambio HTTP ${response.status}`);
      const payload = await response.json();
      const rate = numberOrNull(payload?.rates?.CRC);
      if (payload?.result !== "success" || rate === null || rate <= 0) {
        throw new Error("La respuesta no contiene el tipo de cambio CRC.");
      }
      state.exchangeRate = rate;
    } catch {
      state.exchangeRate = FALLBACK_CRC_RATE;
    } finally {
      window.clearTimeout(timeout);
      state.exchangeRateUpdatedAt = Date.now();
    }
  }

  function hasActiveDiscount(offer, game) {
    if (offer.currentPrice === null) return false;
    const regular = numberOrNull(offer.regularPrice) ??
      (String(offer.currency || "USD").toUpperCase() === String(game.currency || "USD").toUpperCase()
        ? numberOrNull(game.msrp)
        : null);
    return regular !== null && regular > 0 && offer.currentPrice < regular;
  }

  function discountPercent(offer, game) {
    if (!hasActiveDiscount(offer, game)) return null;
    const regular = numberOrNull(offer.regularPrice) ??
      (String(offer.currency || "USD").toUpperCase() === String(game.currency || "USD").toUpperCase()
        ? numberOrNull(game.msrp)
        : null);
    return Math.round((1 - offer.currentPrice / regular) * 100);
  }

  function priceFreshness(offer, game) {
    const capturedAt = offer.staleSince || offer.capturedAt;
    const captured = capturedAt ? new Date(capturedAt) : null;
    const validDate = captured !== null && !Number.isNaN(captured.getTime());
    const stale = validDate &&
      Date.now() - captured.getTime() > PRICE_STALE_AFTER_MS;
    if (stale || game.liveFailed) {
      return `<span class="stale-label">Dato desactualizado desde ${escapeHtml(formatDate(capturedAt))}</span>`;
    }
    const now = new Date();
    if (validDate &&
        captured.getFullYear() === now.getFullYear() &&
        captured.getMonth() === now.getMonth() &&
        captured.getDate() === now.getDate()) {
      return `<span class="fresh-label">Actualizado hoy</span>`;
    }
    return `<span class="fresh-label">${validDate
      ? `Actualizado ${escapeHtml(formatDate(capturedAt))}`
      : "Fecha no disponible"}</span>`;
  }

  function normalizeDiscount(current, regular) {
    if (current === null || regular === null || regular <= 0 || current >= regular) return null;
    const percent = Math.round((1 - current / regular) * 100);
    return percent > 0 ? percent : null;
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
    const reportedDiscount = numberOrNull(raw.discountPercent ?? raw.discount);
    const discountPercent = normalizeDiscount(currentPrice, regularPrice) ??
      (reportedDiscount > 0 && currentPrice !== null && regularPrice === null ? reportedDiscount : null);
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
          if (offer.currentPrice !== null && offer.capturedAt) {
            game.history.push({
              store: offer.store,
              price: offer.currentPrice,
              currency: offer.currency,
              capturedAt: offer.capturedAt
            });
          }
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
      if (state.showDealsOnly && !game.offers.some(offer => hasActiveDiscount(offer, game))) return false;
      return true;
    });

    const priceOf = game => {
      const prices = game.offers
        .filter(offer => offer.currentPrice !== null)
        .map(offer => convertedAmount(offer.currentPrice, offer.currency));
      return prices.length ? Math.min(...prices) : Number.POSITIVE_INFINITY;
    };
    const discountOf = game => Math.max(0, ...game.offers.map(offer => discountPercent(offer, game) || 0));
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
      .sort((a, b) => convertedAmount(a.currentPrice, a.currency) - convertedAmount(b.currentPrice, b.currency));
    const best = pricedOffers[0];
    const topDiscount = best ? discountPercent(best, game) : null;
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
          ${topDiscount ? `<span class="discount-badge">−${topDiscount}%</span>` : ""}
          ${historicalLow ? `<span class="historic-badge">MÍNIMO HISTÓRICO</span>` : ""}
        </div>
        <div class="game-card-body">
          <h3 class="game-title">${alt}</h3>
          <div class="card-platforms">${platformTags || `<span class="platform-tag">Plataforma no indicada</span>`}</div>
          <div class="card-bottom">
            <div class="price-block">
              <strong class="price-current ${best ? "" : "no-price"}">${best ? escapeHtml(formatMoneyWithReference(best.currentPrice, best.currency)) : "Sin ofertas"}</strong>
              <span class="price-regular ${topDiscount ? "discount-reference" : ""}">${topDiscount
                ? `<s>${escapeHtml(formatMoneyWithReference(best.regularPrice ?? game.msrp, best.currency))}</s>`
                : best ? `<span class="price-regular-label">Precio regular</span>` : ""}</span>
            </div>
            <div class="rating-block">
              ${score !== null
                ? `<strong class="rating-score">★ ${escapeHtml(score)}</strong><span class="rating-source">${scoreLabel}</span>`
                : `<span class="rating-source">SIN CALIFICACIÓN</span>`}
            </div>
          </div>
          ${sourceLine ? `<p class="card-store">Mejor precio en ${sourceLine} · ${priceFreshness(best, game)}</p>` : ""}
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
      hasActiveDiscount(offer, game))).length;
    const stores = new Set(state.games.flatMap(game => game.offers.map(offer => offer.store)));
    const platforms = new Set(state.games.flatMap(game => game.platforms));
    document.querySelector("#stat-games").textContent = state.games.length.toLocaleString("es-CR");
    document.querySelector("#stat-deals").textContent = deals.toLocaleString("es-CR");
    document.querySelector("#stat-stores").textContent = stores.size.toLocaleString("es-CR");
    document.querySelector("#stat-platforms").textContent = platforms.size.toLocaleString("es-CR");
  }

  function updateTimestamp(catalogPayload, metricsPayload, benchmarksPayload) {
    const candidates = [
      state.lastRefreshAt,
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

  function renderDetailPriceRow(offer, game) {
    const link = safeHttpUrl(offer.url);
    const activeDiscount = hasActiveDiscount(offer, game);
    const discount = discountPercent(offer, game);
    const regular = numberOrNull(offer.regularPrice) ??
      (String(offer.currency || "USD").toUpperCase() === String(game.currency || "USD").toUpperCase()
        ? numberOrNull(game.msrp)
        : null);
    const regularLabel = activeDiscount && regular !== null
      ? `<s>${escapeHtml(formatMoneyWithReference(regular, offer.currency))}</s> <span class="price-discount">−${discount}%</span>`
      : `<span class="regular-price-label">Precio regular</span>`;
    const updateLabel = offer.isLive
      ? `<span class="fresh-label">Oferta en vivo</span>`
      : priceFreshness(offer, game);
    return `<div class="detail-price-row">
      <strong>${escapeHtml(offer.store)}</strong>
      <span>${escapeHtml(formatMoneyWithReference(offer.currentPrice, offer.currency))} ${regularLabel}</span>
      <small>${updateLabel}${offer.cheapestPriceEver !== null && offer.cheapestPriceEver !== undefined
        ? ` · Mínimo histórico: ${escapeHtml(formatMoneyWithReference(offer.cheapestPriceEver, offer.currency))}`
        : ""}</small>
      ${link ? `<a href="${escapeHtml(link)}" target="_blank" rel="noopener noreferrer">Abrir tienda ↗</a>` : `<small>Enlace no disponible</small>`}
    </div>`;
  }

  function renderLiveDeals(game) {
    const container = document.querySelector("#live-deals");
    if (!container) return;
    const offers = game.liveOffers || [];
    if (game.liveLoading) {
      container.innerHTML = `<span class="detail-empty">Consultando CheapShark; los precios guardados permanecen visibles.</span>`;
    } else if (game.liveFailed) {
      container.innerHTML = `<div class="detail-empty">No se pudo consultar CheapShark por un error de red/CORS. Se muestran las observaciones guardadas.</div>`;
    } else if (offers.length) {
      container.innerHTML = `<span class="fresh-label">${offers.length} tiendas actualizadas en vivo</span>`;
    } else {
      container.innerHTML = `<span class="detail-empty">Sin ofertas en vivo; se conservan las observaciones guardadas.</span>`;
    }
  }

  function openGameDetails(gameId, refreshLive = true) {
    const game = state.games.find(item => item.id === gameId);
    if (!game) return;
    state.dialogChart?.destroy();
    state.dialogChart = null;
    state.dialogGameId = game.id;
    const cover = safeHttpUrl(game.cover);
    const offersByStore = new Map(game.offers
      .filter(offer => offer.currentPrice !== null)
      .map(offer => [offer.store, offer]));
    (game.liveOffers || []).forEach(offer => offersByStore.set(offer.store, offer));
    const prices = [...offersByStore.values()]
      .sort((a, b) =>
        convertedAmount(a.currentPrice, a.currency) - convertedAmount(b.currentPrice, b.currency));
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
      ? prices.map(offer => renderDetailPriceRow(offer, game)).join("")
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
            <div class="detail-stat"><span>MSRP</span><strong>${game.msrp === null ? "No disponible" : escapeHtml(formatMoneyWithReference(game.msrp, game.currency))}</strong></div>
            ${ratings.length ? ratings.map(([name, value]) => `<div class="detail-stat"><span>${escapeHtml(name)}</span><strong>${escapeHtml(value)}</strong></div>`).join("") : ""}
          </div>
        </div>
      </div>
      <section class="detail-section">
        <h3>Precios disponibles</h3>
        <div class="detail-prices">${detailPriceRows}</div>
        <p id="live-deals" class="live-deal-status"></p>
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

    renderLiveDeals(game);
    if (!elements.dialog.open) elements.dialog.showModal();
    renderDetailChart(game);
    if (refreshLive) loadLiveDeals(game);
  }

  async function loadLiveDeals(game) {
    if (game.liveLoading) return;
    game.liveLoading = true;
    game.liveFailed = false;
    renderLiveDeals(game);
    try {
      state.cheapSharkStores ||= fetch("https://www.cheapshark.com/api/1.0/stores")
        .then(response => {
          if (!response.ok) throw new Error(`Tiendas HTTP ${response.status}`);
          return response.json();
        })
        .catch(error => {
          state.cheapSharkStores = null;
          throw error;
        });
      const [searchResponse, stores] = await Promise.all([
        fetch(`https://www.cheapshark.com/api/1.0/games?title=${encodeURIComponent(game.title)}&limit=5`, { cache: "no-store" }),
        state.cheapSharkStores
      ]);
      if (!searchResponse.ok) throw new Error(`Búsqueda CheapShark HTTP ${searchResponse.status}`);
      const searchResults = await searchResponse.json();
      const bestMatch = (Array.isArray(searchResults) ? searchResults : [])
        .map(row => ({
          row,
          score: titleSimilarity(game.title, row.external || row.title || row.internalName || "")
        }))
        .sort((a, b) => b.score - a.score)[0];
      if (!bestMatch || bestMatch.score < 0.8 || !bestMatch.row.gameID) {
        game.liveOffers = [];
        game.liveLoading = false;
        if (state.dialogGameId === game.id && elements.dialog.open) {
          openGameDetails(game.id, false);
        }
        return;
      }

      const [gameResponse] = await Promise.all([
        fetch(`https://www.cheapshark.com/api/1.0/games?id=${encodeURIComponent(bestMatch.row.gameID)}`, { cache: "no-store" })
      ]);
      if (!gameResponse.ok) throw new Error(`Detalle CheapShark HTTP ${gameResponse.status}`);
      const [gameData, storeRows] = await Promise.all([gameResponse.json(), stores]);
      if (state.dialogGameId !== game.id || !elements.dialog.open) {
        game.liveLoading = false;
        return;
      }
      const info = gameData?.info || {};
      const deals = Array.isArray(gameData?.deals) ? gameData.deals : [];
      const storeNames = new Map((Array.isArray(storeRows) ? storeRows : [])
        .filter(store => store && String(store.isActive) !== "0")
        .map(store => [String(store.storeID), String(store.storeName || `Tienda ${store.storeID}`)]));
      const cheapestPriceEver = numberOrNull(gameData?.cheapestPriceEver?.price);
      const capturedAt = new Date().toISOString();
      game.liveOffers = deals
        .filter(deal => numberOrNull(deal.price) !== null)
        .map(deal => ({
          store: normalizeStore(storeNames.get(String(deal.storeID)) || `Tienda ${deal.storeID}`),
          sourceId: String(deal.storeID),
          currentPrice: numberOrNull(deal.price),
          regularPrice: numberOrNull(deal.retailPrice),
          currency: "USD",
          discountPercent: normalizeDiscount(
            numberOrNull(deal.price),
            numberOrNull(deal.retailPrice)),
          url: cheapSharkDealUrl(deal.dealID),
          capturedAt,
          isLive: true,
          cheapestPriceEver
        }))
        .sort((a, b) => a.currentPrice - b.currentPrice);
      game.liveOffers.forEach(offer => {
        game.history = game.history.filter(point => !(point.isLive && point.store === offer.store));
        game.history.push({
          store: offer.store,
          price: offer.currentPrice,
          currency: offer.currency,
          capturedAt,
          isLive: true
        });
      });
      game.liveFailed = false;
      game.liveLoading = false;
      if (info.thumb && !game.cover) game.cover = safeHttpUrl(info.thumb);
      renderCatalog();
      openGameDetails(game.id, false);
    } catch (error) {
      game.liveFailed = true;
      game.liveLoading = false;
      console.warn("No se pudieron actualizar las ofertas en vivo de CheapShark.", error);
      if (state.dialogGameId === game.id && elements.dialog.open) {
        renderCatalog();
        openGameDetails(game.id, false);
      }
    }
  }

  function cheapSharkDealUrl(dealId) {
    if (!dealId) return "";
    let normalized = String(dealId);
    try { normalized = decodeURIComponent(normalized); } catch { /* Keep the encoded id. */ }
    return safeHttpUrl(`https://www.cheapshark.com/redirect?dealID=${encodeURIComponent(normalized)}`);
  }

  function titleSimilarity(left, right) {
    const a = normalizeTitle(left);
    const b = normalizeTitle(right);
    if (!a || !b) return 0;
    if (a === b) return 1;
    const aTokens = new Set(a.split(" "));
    const bTokens = new Set(b.split(" "));
    const dice = 2 * [...aTokens].filter(token => bTokens.has(token)).length /
      Math.max(1, aTokens.size + bTokens.size);
    if ((a.startsWith(`${b} `) || b.startsWith(`${a} `)) &&
        Math.abs(aTokens.size - bTokens.size) <= 2) return Math.max(.82, dice);
    return dice;
  }

  function normalizeTitle(value) {
    return String(value || "").normalize("NFD").replace(/[\u0300-\u036f]/g, "")
      .toLocaleLowerCase("en").replace(/[^a-z0-9]+/g, " ").trim();
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
    const labels = dateKeys.map(date => {
      const hasLivePoint = history.some(point => point.isLive &&
        new Date(point.capturedAt).toISOString() === date);
      return formatDate(date, hasLivePoint
        ? { dateStyle: "short", timeStyle: "short" }
        : { dateStyle: "short" });
    });
    const palette = ["#c8f169", "#76cde0", "#ff9a66", "#c39af7", "#ff777f", "#f4d36a"];
    const datasets = [...grouped.entries()].map(([store, points], index) => {
      const values = Array(dateKeys.length).fill(null);
      const originalValues = Array(dateKeys.length).fill(null);
      points.forEach(point => {
        const key = new Date(point.capturedAt).toISOString();
        const position = dateIndex.get(key);
        if (position !== undefined) {
          values[position] = convertedAmount(point.price, point.currency);
          originalValues[position] = { price: point.price, currency: point.currency, isLive: point.isLive };
        }
      });
      return {
        label: store,
        data: values,
        originalValues,
        borderColor: palette[index % palette.length],
        backgroundColor: palette[index % palette.length],
        pointRadius: originalValues.map(point => point?.isLive ? 5 : 2),
        pointBackgroundColor: originalValues.map(point =>
          point?.isLive ? "#c8f169" : palette[index % palette.length]),
        borderWidth: 2,
        tension: .25
      };
    });
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
          tooltip: { callbacks: { label: item => {
            const original = item.dataset.originalValues?.[item.dataIndex];
            return `${item.dataset.label}: ${original
              ? formatMoneyWithReference(original.price, original.currency)
              : formatMoney(item.parsed.y, state.displayCurrency)}`;
          } } }
        },
        scales: {
          x: { grid: { color: "rgba(255,255,255,.06)" }, ticks: { color: "#858a97", maxTicksLimit: 6 } },
          y: { grid: { color: "rgba(255,255,255,.07)" }, ticks: { color: "#858a97", callback: value => formatMoney(value, state.displayCurrency) } }
        }
      }
    });
  }

  async function initialize() {
    const exchangeRatePromise = loadExchangeRate();
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

    state.catalogPayload = values.catalog;
    state.historyPayload = values.history;
    state.games = normalizeCatalog(state.catalogPayload);
    mergeHistory(state.historyPayload);
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
    await exchangeRatePromise;
    renderCatalog();
    if (state.dialogGameId && elements.dialog.open) openGameDetails(state.dialogGameId, false);
  }

  function preserveLiveState(nextGames) {
    const previous = new Map(state.games.map(game => [game.id, game]));
    for (const game of nextGames) {
      const old = previous.get(game.id);
      if (!old) continue;
      game.liveOffers = old.liveOffers;
      game.liveFailed = old.liveFailed;
      game.liveLoading = old.liveLoading;
      if (old.liveOffers?.length) {
        game.history = game.history.filter(point => !point.isLive);
        game.history.push(...old.history.filter(point => point.isLive));
      }
    }
    return nextGames;
  }

  async function refreshLiveData() {
    if (state.refreshing) return;
    state.refreshing = true;
    elements.refresh.disabled = true;
    elements.refresh.textContent = "Actualizando…";
    try {
      const timestamp = Date.now();
      const results = await Promise.allSettled([
        loadJson(`${DATA_FILES.catalog}?t=${timestamp}`),
        loadJson(`${DATA_FILES.metrics}?t=${timestamp}`),
        Date.now() - state.exchangeRateUpdatedAt > 60 * 60 * 1000
          ? loadExchangeRate()
          : Promise.resolve()
      ]);
      const errors = [];
      if (results[0].status === "fulfilled" && results[0].value) {
        state.catalogPayload = results[0].value;
        state.games = preserveLiveState(normalizeCatalog(state.catalogPayload));
        mergeHistory(state.historyPayload);
        renderFilters();
        renderCatalog();
      } else if (results[0].status === "rejected") {
        errors.push(results[0].reason?.message || "No se pudo actualizar el catálogo.");
      }
      if (results[1].status === "fulfilled" && results[1].value) {
        state.metrics = results[1].value;
        renderMetrics(state.metrics, state.benchmarks);
      } else if (results[1].status === "rejected") {
        errors.push(results[1].reason?.message || "No se pudieron actualizar las métricas.");
      }
      if (results[2].status === "fulfilled") {
        renderCatalog();
      }
      state.lastRefreshAt = new Date();
      updateTimestamp(state.catalogPayload, state.metrics, state.benchmarks);
      if (state.dialogGameId && elements.dialog.open) {
        openGameDetails(state.dialogGameId);
      }
      elements.error.hidden = errors.length === 0;
      elements.error.textContent = errors.join(" ");
    } catch (error) {
      elements.error.hidden = false;
      elements.error.textContent = error.message || "No se pudieron actualizar los datos.";
    } finally {
      state.refreshing = false;
      elements.refresh.disabled = false;
      elements.refresh.textContent = "Actualizar ahora";
    }
  }

  function resetFilters() {
    elements.search.value = "";
    elements.type.value = "";
    elements.platform.value = "";
    elements.format.value = "";
    elements.store.value = "";
    elements.sort.value = "name";
    state.showDealsOnly = false;
    elements.dealsFilter.setAttribute("aria-pressed", "false");
    state.page = 1;
    renderCatalog();
  }

  [elements.search, elements.type, elements.platform, elements.format, elements.store, elements.sort]
    .forEach(control => control.addEventListener(control === elements.search ? "input" : "change", () => {
      state.page = 1;
      renderCatalog();
    }));
  document.querySelector("#reset-filters").addEventListener("click", resetFilters);
  elements.dealsFilter.addEventListener("click", () => {
    state.showDealsOnly = !state.showDealsOnly;
    elements.dealsFilter.setAttribute("aria-pressed", String(state.showDealsOnly));
    state.page = 1;
    renderCatalog();
  });
  const updateCurrency = currency => {
    state.displayCurrency = currency;
    elements.currencyCrc.setAttribute("aria-pressed", String(currency === "CRC"));
    elements.currencyUsd.setAttribute("aria-pressed", String(currency === "USD"));
    renderCatalog();
    if (state.dialogGameId && elements.dialog.open) openGameDetails(state.dialogGameId, false);
  };
  elements.currencyCrc.addEventListener("click", () => updateCurrency("CRC"));
  elements.currencyUsd.addEventListener("click", () => updateCurrency("USD"));
  elements.grid.addEventListener("click", event => {
    const button = event.target.closest("[data-game-id]");
    if (button) openGameDetails(button.dataset.gameId);
  });
  document.querySelector("#dialog-close").addEventListener("click", () => elements.dialog.close());
  elements.refresh.addEventListener("click", refreshLiveData);
  elements.dialog.addEventListener("click", event => {
    if (event.target === elements.dialog) elements.dialog.close();
  });
  elements.dialog.addEventListener("close", () => {
    state.dialogGameId = null;
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
  window.setInterval(refreshLiveData, 30_000);
})();
