# Radar de Ofertas Multinúcleo

Proyecto académico para IC-3101 Arquitectura de Computadoras (TEC San Carlos).
La aplicación se implementa en C# sobre .NET 8; el catálogo e historial se
almacenan en SQLite y el sitio público consume JSON estático desde `web/data/`.

## Estructura inicial

- `src/RadarOfertasMultinucleo/`: aplicación .NET y contratos de las seis etapas.
- `src/RadarOfertasMultinucleo/config.json`: modo, trabajadores, colas y límites.
- `src/RadarOfertasMultinucleo/Infrastructure/Database/001_esquema_inicial.sql`:
  esquema relacional inicial. El archivo debe aplicarse al crear `data/radar.db`.
- `web/`: sitio estático publicado por GitHub Pages.

El catálogo se descubre dinámicamente desde OpenCritic y Steam. El flujo de este
hito captura metadatos, ratings y plataformas; todavía no integra fuentes de
precios, Metacritic ni How Long To Beat.

## Ejecutar el pipeline

Desde la raíz del repositorio:

```powershell
dotnet run --project src/RadarOfertasMultinucleo
dotnet run --project src/RadarOfertasMultinucleo -- --mode concurrent
dotnet run --project src/RadarOfertasMultinucleo -- --mode parallel --record-replay
dotnet run --project src/RadarOfertasMultinucleo -- --mode parallel --source replay
dotnet run --project src/RadarOfertasMultinucleo -- --benchmark-contention
dotnet run --project src/RadarOfertasMultinucleo -- --validate-matching
dotnet run --project src/RadarOfertasMultinucleo -- --source replay --run-benchmarks
```

`--record-replay` guarda respuestas HTTP en `data/replay/`. El modo replay no
usa la red y requiere que todas las respuestas estén grabadas. Sus retardos se
ajustan con `replay_latency_fixed_ms` y `replay_latency_jitter_ms`.

El emparejamiento compara cada título descargado con el conjunto de candidatos
del catálogo usando similitud propia (Levenshtein y similitud por tokens), y en
modo `parallel` reparte esas comparaciones con `Parallel.ForEach`. El comando
`--validate-matching` evalúa 61 pares etiquetados y genera
`web/data/matching-validation.json`.

`--run-benchmarks` requiere `--source replay`; mide cinco repeticiones por
configuración, barre trabajadores de E/S y CPU y exporta las observaciones a
`web/data/benchmarks.json` y `web/data/benchmarks.csv`. No genera predicciones
teóricas. Los benchmarks miden el pipeline completo, incluida la persistencia,
con una base SQLite temporal nueva para cada repetición.

El descubrimiento exige los mínimos indicados por `catalog_min_games` y
`catalog_min_difficult_cases`; si las fuentes no los alcanzan, informa error
sin presentar el catálogo incompleto como éxito. El benchmark escribe
`data/benchmark-contention.json`.

Consulta [la Bitácora de IA](docs/bitacora-ia.md) para las decisiones y límites
del Hito 2.