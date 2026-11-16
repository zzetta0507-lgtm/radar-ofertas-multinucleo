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
