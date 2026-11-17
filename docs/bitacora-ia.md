# Bitácora de IA

## Hito 2 - descubrimiento, replay y concurrencia

- Se eligieron el sitemap público de juegos de OpenCritic y el endpoint JSON paginado de búsqueda de Steam para no incorporar una lista estática de videojuegos. El descubrimiento exige verificar los mínimos configurados antes de considerarse exitoso.
- Se consulta `robots.txt` antes de cada host, se usa un `User-Agent` identificable, se limita la cadencia por dominio y no se siguen redirecciones automáticamente. No se implementan proxies ni evasión de controles.
- `--record-replay` conserva cuerpos HTTP y su URL en `data/replay/`. `--source replay` solo lee esas respuestas y falla explícitamente si falta alguna; no puede recurrir a la red. El retardo fijo y el jitter determinista por URL/semilla permiten repetir la misma simulación.
- `sequential` usa las mismas etapas y transfiere un elemento a la vez por las colas acotadas. `concurrent` usa varios trabajadores de descarga y deja el emparejamiento con uno; `parallel` habilita varios trabajadores de emparejamiento. `SemaphoreSlim` limita concurrencia y cadencia por host.
- El benchmark de contención conserva una demostración controlada de actualización perdida con sincronización de barrera para hacer visible la carrera, además de medir las cuatro estrategias de agregación solicitadas. Los tiempos reportados son observados por ejecución; no se agregaron predicciones teóricas.
- Este hito implementa el flujo de descubrimiento y metadatos de OpenCritic/Steam. No sustituye aún conectores de precio, minoristas, Metacritic ni How Long To Beat.

## Hito 3 - interfaz pública, emparejamiento y benchmarks

- La UI consume `catalog.json`, `history.json`, `metrics.json` y `benchmarks.json`; los documentos ausentes se tratan como estados vacíos. El historial usa un eje categórico de fechas para no depender de un adaptador adicional de Chart.js.
- El emparejamiento normaliza diacríticos, puntuación, sufijos de plataforma y ediciones, conserva los números de secuela y combina Levenshtein con similitud por tokens. Las coincidencias cercanas, empatadas o marcadas como edición/DLC/bundle se publican con `isUncertainMatch`.
- `Parallel.ForEach` trabaja sobre candidatos del catálogo y `workers_cpu` limita el grado de paralelismo. La etapa consume un solo trabajador en el modo paralelo para evitar paralelismo anidado; en `sequential` y `concurrent` el grado interno queda en uno.
- `--validate-matching` ejecuta 61 pares etiquetados (incluye ediciones, sufijos de plataforma, caracteres especiales y secuelas), y registra precisión, exhaustividad y pares mal clasificados en `web/data/matching-validation.json`.
- `--run-benchmarks` solo acepta `--source replay`. Ejecuta cinco repeticiones de la línea base, los barridos configurados de E/S y las potencias de dos hasta el número de procesadores lógicos, más el doble; exporta medianas, mínimos, máximos, speedups, eficiencia y Karp–Flatt observados a JSON/CSV. Cada repetición crea un cliente replay nuevo y una base SQLite temporal aislada para no reutilizar caché en memoria ni acumular historial entre medidas; los tiempos incluyen persistencia.
- GitHub Actions configura .NET 8, ejecuta el modo paralelo, hace commit de los cambios en `web/data/` cuando existen y publica la carpeta `web/` en Pages.

## Actualización automática y tiendas adicionales

- Se agregaron fuentes de precios de Steam, Epic Games Store, GOG, Eneba y CheapShark; los IDs de tiendas de CheapShark se traducen a Humble Store y Green Man Gaming. Los datos se normalizan en observaciones SQLite y se publican junto con enlaces e historial.
- Las llamadas usan el cliente HTTP existente, que verifica `robots.txt`, identifica el `User-Agent` y regula solicitudes concurrentes por dominio. La métrica de solicitudes distingue las fuentes directas y las consultas de CheapShark atribuidas a Humble y GMG.
- Ante una falla o ausencia temporal de precio, la publicación conserva la observación previa y señala la fecha en `isStale`/`staleSince`.
- El workflow programa ejecuciones cada 15 minutos y rebasea los cambios de datos antes de hacer push. La web consulta catálogo y métricas cada 30 segundos; el detalle consulta CheapShark en segundo plano y mantiene los precios publicados como respaldo.

## Caso real - integridad referencial en persistencia concurrente

- Durante una ejecución de prueba con `--mode concurrent --record-replay`, la Etapa 4 falló al insertar una calificación con `SQLite Error 19: FOREIGN KEY constraint failed`. El emparejador podía elegir un candidato obtenido del sitemap cuyo registro aún no había sido insertado por la Etapa 0; además, las escrituras de descubrimiento y persistencia podían competir por SQLite.
- `SaveRatingAsync` ahora garantiza dentro de la misma transacción que existan el juego (inserción segura si aún no fue descubierto) y la fuente, y luego inserta/actualiza idempotentemente la observación. `SaveGameAsync` también asegura la fuente antes de escribir filas dependientes.
- Las escrituras del repositorio se serializan con `SemaphoreSlim`; la base opera en WAL y cada conexión tiene `busy_timeout` de 5 segundos (`DefaultTimeout` y `PRAGMA busy_timeout`). El esquema conserva las claves foráneas habilitadas.
- Verificación realizada: `dotnet build src\RadarOfertasMultinucleo --no-restore` compiló sin errores ni advertencias; una prueba SQL aislada confirmó WAL, UPSERT idempotente y ausencia de violaciones FK. No se repitió el replay completo; el fallo original se registró a partir de la excepción observada durante la ejecución de prueba.
- En las repeticiones del benchmark se deshabilitó el pooling de SQLite y se limpian los pools tras cerrar el pipeline, antes de borrar la base temporal. Así se liberan los handles de Windows y se evita dejar `radar.db` bloqueada entre iteraciones.
