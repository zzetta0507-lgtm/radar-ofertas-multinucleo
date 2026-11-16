# Bitácora de IA

## Hito 2 - descubrimiento, replay y concurrencia

- Se eligieron el sitemap público de juegos de OpenCritic y el endpoint JSON paginado de búsqueda de Steam para no incorporar una lista estática de videojuegos. El descubrimiento exige verificar los mínimos configurados antes de considerarse exitoso.
- Se consulta `robots.txt` antes de cada host, se usa un `User-Agent` identificable, se limita la cadencia por dominio y no se siguen redirecciones automáticamente. No se implementan proxies ni evasión de controles.
- `--record-replay` conserva cuerpos HTTP y su URL en `data/replay/`. `--source replay` solo lee esas respuestas y falla explícitamente si falta alguna; no puede recurrir a la red. El retardo fijo y el jitter determinista por URL/semilla permiten repetir la misma simulación.
- `sequential` usa las mismas etapas y transfiere un elemento a la vez por las colas acotadas. `concurrent` usa varios trabajadores de descarga y deja el emparejamiento con uno; `parallel` habilita varios trabajadores de emparejamiento. `SemaphoreSlim` limita concurrencia y cadencia por host.
- El benchmark de contención conserva una demostración controlada de actualización perdida con sincronización de barrera para hacer visible la carrera, además de medir las cuatro estrategias de agregación solicitadas. Los tiempos reportados son observados por ejecución; no se agregaron predicciones teóricas.
- Este hito implementa el flujo de descubrimiento y metadatos de OpenCritic/Steam. No sustituye aún conectores de precio, minoristas, Metacritic ni How Long To Beat.
