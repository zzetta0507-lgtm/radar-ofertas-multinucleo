# Radar de Ofertas Multinúcleo

Proyecto académico para IC-3101 Arquitectura de Computadoras (TEC San Carlos).
La aplicación se implementa en C# sobre .NET 8; el catálogo e historial se
almacenan en SQLite y el sitio público consume JSON estático desde `web/data/`.

## Estructura inicial

- `src/RadarOfertasMultinucleo/`: aplicación .NET y contratos de las seis etapas.
- `src/RadarOfertasMultinucleo/config.json`: modo, trabajadores, colas y límites.
- `data/catalog-seeds.json`: nombres conocidos y catálogo editable de expansiones/DLC.
- `src/RadarOfertasMultinucleo/Infrastructure/Database/001_esquema_inicial.sql`:
  esquema relacional inicial, aplicado automáticamente al inicializar `data/radar.db`.
- `web/`: sitio estático publicado por GitHub Pages.

## Documentación del equipo

- [Acta y roles](docs/acta.md)
- [Seguimiento de reuniones](docs/seguimiento.md)
- [Informe técnico final — Sección 12, Sección 16 y Anexo 2](docs/informe.md)
- [ADRs](docs/adr/)
- [Cambios de diseño](docs/cambios-diseno.md)
- [Bitácora de IA](docs/bitacora-ia.md)

El catálogo se descubre dinámicamente desde OpenCritic y Steam. El pipeline
consulta precios en Steam, Epic Games Store, GOG, Eneba y GamersGate; CheapShark
aporta ofertas de Humble Store y Green Man Gaming. Las observaciones e historial
se guardan en SQLite y se publican en `web/data/`; las fuentes con errores
conservan su último precio conocido y se marcan como desactualizadas.

`store_sources` en `config.json` controla las fuentes de precios. GitHub Actions
ejecuta el pipeline cada 15 minutos. La web vuelve a consultar catálogo y
métricas cada 30 segundos, y ofrece ofertas recientes de CheapShark en el detalle
de cada juego cuando hay conexión. Los precios se muestran en CRC por defecto y
se convierten con el tipo de cambio de Open ER-API (510 CRC/USD como respaldo);
el selector cambia también los gráficos a USD. La interfaz solo marca descuentos
activos cuando el precio actual es menor que el precio regular, y señala datos
como desactualizados después de 24 horas o ante un fallo de consulta en vivo.

## Ejecutar el pipeline

Desde la raíz del repositorio:

```powershell
dotnet run --project src/RadarOfertasMultinucleo
dotnet run --project src/RadarOfertasMultinucleo -- --seed-history
dotnet run --project src/RadarOfertasMultinucleo -- --mode concurrent
dotnet run --project src/RadarOfertasMultinucleo -- --mode parallel --record-replay
dotnet run --project src/RadarOfertasMultinucleo -- --mode parallel --source replay
dotnet run --project src/RadarOfertasMultinucleo -- --benchmark-contention
dotnet run --project src/RadarOfertasMultinucleo -- --validate-matching
dotnet run --project src/RadarOfertasMultinucleo -- --source replay --run-benchmarks
dotnet run --project src/RadarOfertasMultinucleo -- --source replay --enrich-catalog
dotnet run --project src/RadarOfertasMultinucleo -- --enrich-catalog --narrative-only
```

`--seed-history` completa de forma idempotente el historial diario desde el
2026-11-09 hasta la fecha local de Costa Rica. Las observaciones existentes se
conservan; los días derivados se marcan `isEstimated` y no sustituyen precios
reales.

El pipeline completa duraciones faltantes con estimaciones por género/tipo y
consulta How Long To Beat y metadatos narrativos en Steam (español), con respaldo
de GOG y del catálogo semilla, usando cadencia y caché SQLite por dominio. Los
intentos se espacian 30 días; una ejecución normal procesa hasta 60 juegos
elegibles. `--enrich-catalog` recorre todo el catálogo y vuelve a publicar
`catalog.json`; `--narrative-only` limita ese comando a Steam/GOG/semillas sin
volver a consultar HLTB. Con `--source replay` no consulta la red y solo genera
estimaciones locales. El JSON exporta tanto los nombres ingleses como los
campos `sinopsis`, `generos`, `desarrollador` y `lanzamiento`. El modal usa
Wikipedia como único respaldo de sinopsis en vivo; Steam se consulta solo desde
el pipeline C#.

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