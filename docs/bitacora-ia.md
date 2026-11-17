# Seguimiento semanal

Registro de reuniones del equipo del proyecto Radar de Ofertas Multinúcleo.

Participantes del equipo:

- Santiago Alonzo Vargas Céspedes — Arquitectura y sincronización
- Kenneth Esteban Castro Alvarado — Rendimiento y experimentación
- Yamil Campos Miranda — Datos y producto

## Semana del 2 al 8 de noviembre de 2026 (Hito 1 · Diseño)

### Reunión 1

- **Fecha y hora:** 2 de noviembre de 2026, 12:00 m.
- **Modalidad/lugar:** Presencial, casa de Kenneth.
- **Asistentes:** Todos (Santiago Vargas, Kenneth Castro, Yamil Campos).
- **Decisiones técnicas:**
  - Se elaboró el mapa mental de la arquitectura: pipeline de 6 etapas conectadas por colas acotadas.
  - Se asignaron oficialmente los 3 roles del equipo: arquitectura y sincronización, rendimiento y experimentación, datos y producto.
  - Se seleccionó C# sobre .NET 8 como lenguaje y runtime, y SQLite como base de datos.
  - Se organizaron el tablero y el acta del equipo.
  - Las predicciones teóricas del Hito 1 se redactarán manualmente, sin cifras generadas con IA.
- **Bloqueos y riesgos:**
  - Definir el tamaño de las colas acotadas sin mediciones previas.
  - SQLite admite un único escritor activo simultáneo, lo que exige planificar desde el diseño la serialización de escrituras.
- **Acuerdos, responsable y fecha objetivo:**
  - [x] Acuerdo: Documentar el diseño del pipeline de 6 etapas, las colas acotadas y los límites de concurrencia/cadencia en ADRs · Responsable: Santiago Vargas · Fecha objetivo: 6 de noviembre de 2026
  - [x] Acuerdo: Diseñar el modo replay determinista y el plan del banco de pruebas para medir los modos de ejecución · Responsable: Kenneth Castro · Fecha objetivo: 6 de noviembre de 2026
  - [x] Acuerdo: Definir el esquema inicial de SQLite y las fuentes de descubrimiento (sitemap de OpenCritic y API paginada de Steam) · Responsable: Yamil Campos · Fecha objetivo: 6 de noviembre de 2026
- **Seguimiento de acuerdos anteriores:** Sin acuerdos previos; esta es la reunión de arranque del proyecto.

### Reunión 2

- **Fecha y hora:** 6 de noviembre de 2026, 7:00 p.m.
- **Modalidad/lugar:** Virtual.
- **Asistentes:** Todos (Santiago Vargas, Kenneth Castro, Yamil Campos).
- **Decisiones técnicas:**
  - Se inició el desarrollo base con descubrimiento desde el sitemap de OpenCritic y la API paginada de Steam.
  - El cliente HTTP verifica `robots.txt`, usa un `User-Agent` identificable y controla cadencia y concurrencia por host con `SemaphoreSlim`.
  - Se implementó el modo `--record-replay` / `--source replay`.
  - Se creó el esquema inicial `001_esquema_inicial.sql` en SQLite.
  - Se estructuraron los 3 modos de ejecución: `sequential`, `concurrent` y `parallel`.
- **Bloqueos y riesgos:**
  - Respetar los límites de cada host sin degradar el rendimiento de la descarga.
  - Garantizar que el replay sea determinista con latencia fija y jitter por URL/semilla.
- **Acuerdos, responsable y fecha objetivo:**
  - [x] Acuerdo: Integrar los modos de ejecución con colas acotadas y control de cadencia/concurrencia por host · Responsable: Santiago Vargas · Fecha objetivo: 9 de noviembre de 2026
  - [x] Acuerdo: Validar la grabación y reproducción de respuestas HTTP en `data/replay/` · Responsable: Kenneth Castro · Fecha objetivo: 9 de noviembre de 2026
  - [x] Acuerdo: Cargar el esquema SQLite y probar el descubrimiento sobre OpenCritic y Steam · Responsable: Yamil Campos · Fecha objetivo: 9 de noviembre de 2026
- **Seguimiento de acuerdos anteriores:** Se cumplieron los acuerdos de la Reunión 1: ADRs del pipeline, plan de replay y esquema de datos.

## Semana del 9 al 15 de noviembre de 2026 (Hito 2 · Avance funcional)

### Reunión 3

- **Fecha y hora:** 9 de noviembre de 2026, 12:00 m.
- **Modalidad/lugar:** Presencial, casa de Kenneth.
- **Asistentes:** Todos (Santiago Vargas, Kenneth Castro, Yamil Campos).
- **Decisiones técnicas:**
  - Santiago propuso usar GitHub (GitHub Pages + GitHub Actions) como plataforma gratuita para alojar y publicar el sitio estático `web/`; el equipo lo aprobó por unanimidad y descartó opciones externas.
  - Se definió el flujo de despliegue continuo con `deploy.yml`.
  - El historial de precios inicia el 9 de noviembre de 2026.
- **Bloqueos y riesgos:**
  - Adaptar la salida del pipeline a archivos JSON estáticos en lugar de un servidor propio.
  - Evitar conflictos de commits cuando el workflow publique datos de forma periódica.
- **Acuerdos, responsable y fecha objetivo:**
  - [x] Acuerdo: Configurar GitHub Pages y `deploy.yml` para el despliegue automático de `web/` · Responsable: Santiago Vargas · Fecha objetivo: 14 de noviembre de 2026
  - [x] Acuerdo: Preparar la exportación de métricas y resultados de benchmarks hacia `metrics.json` y `benchmarks.json` · Responsable: Kenneth Castro · Fecha objetivo: 14 de noviembre de 2026
  - [x] Acuerdo: Exportar `catalog.json` e `history.json` y adaptar la interfaz web a esa estructura · Responsable: Yamil Campos · Fecha objetivo: 14 de noviembre de 2026
- **Seguimiento de acuerdos anteriores:** Se cumplieron los acuerdos de la Reunión 2: cliente HTTP, modo replay y esquema SQLite quedaron operativos.

### Reunión 4

- **Fecha y hora:** 14 de noviembre de 2026, 3:00 p.m.
- **Modalidad/lugar:** Virtual.
- **Asistentes:** Todos (Santiago Vargas, Kenneth Castro, Yamil Campos).
- **Decisiones técnicas:**
  - Se adaptó el pipeline a la estructura de GitHub y se exportan `catalog.json`, `history.json`, `metrics.json` y `benchmarks.json`.
  - El workflow automático corre cada 15 minutos con rebase antes de push.
  - Se integraron Epic Games Store, GOG, CheapShark (Humble Store y Green Man Gaming) y Eneba.
- **Bloqueos y riesgos:**
  - Con `--mode concurrent --record-replay`, la Etapa 4 falló con `SQLite Error 19: FOREIGN KEY constraint failed` por una condición de carrera entre la Etapa 0 y la Etapa 4, que insertaba calificaciones antes de persistir el juego padre. Se resolvió asegurando juego y fuente en la misma transacción con UPSERT idempotente (`SaveRatingAsync` y `SaveGameAsync`) y serializando escrituras con `SemaphoreSlim`.
  - Se añadió `SqliteConnection.ClearAllPools()` al inicio y cierre de cada repetición del benchmark para liberar los handles de `radar.db` en Windows.
  - Emparejamiento de títulos entre tiendas con nombres distintos para un mismo juego.
- **Acuerdos, responsable y fecha objetivo:**
  - [x] Acuerdo: Validar la serialización de escrituras en SQLite y finalizar el experimento de contención · Responsable: Santiago Vargas · Fecha objetivo: 16 de noviembre de 2026
  - [x] Acuerdo: Ejecutar `--run-benchmarks` en modo replay y calcular `S_abs`, `S_rel`, `E(p)` y Karp–Flatt · Responsable: Kenneth Castro · Fecha objetivo: 16 de noviembre de 2026
  - [x] Acuerdo: Cerrar el algoritmo híbrido de emparejamiento y validarlo sobre 61 pares etiquetados · Responsable: Yamil Campos · Fecha objetivo: 16 de noviembre de 2026
- **Seguimiento de acuerdos anteriores:** Se cumplieron los acuerdos de la Reunión 3: despliegue en GitHub Pages/Actions y exportación de los cuatro JSON.

## Semana del 16 de noviembre de 2026 (Hito 3 · Cierre y preparación de defensa)

### Reunión 5

- **Fecha y hora:** 16 de noviembre de 2026, 12:00 m.
- **Modalidad/lugar:** Presencial, casa de Kenneth.
- **Asistentes:** Todos (Santiago Vargas, Kenneth Castro, Yamil Campos).
- **Decisiones técnicas:**
  - Se validaron los barridos experimentales en modo replay: E/S con 1, 2, 4, 8, 16, 32 y 64 workers, y CPU hasta núcleos lógicos y el doble.
  - Se verificó el algoritmo híbrido (70 % Levenshtein + 30 % Sørensen–Dice, umbral 0.85) sobre los 61 pares etiquetados.
  - Se completó el historial de 8 días con `--seed-history`, marcado con `isEstimated`.
  - Se comprobó la consulta en vivo a CheapShark y la conversión USD a CRC con `open.er-api.com`.
  - Se cerraron `docs/bitacora-ia.md` y `docs/cambios-diseno.md`.
- **Bloqueos y riesgos:**
  - Los resultados de los barridos pueden variar levemente entre repeticiones; se reportan medianas y rangos mín–máx.
  - La conversión en vivo depende de un servicio externo; se mantiene `510 CRC/USD` como respaldo.
- **Acuerdos, responsable y fecha objetivo:**
  - [x] Acuerdo: Repasar preguntas de defensa sobre pipeline, colas acotadas, sincronización de SQLite, contención y despliegue en GitHub · Responsable: Santiago Vargas · Fecha objetivo: 16 de noviembre de 2026
  - [x] Acuerdo: Repasar preguntas sobre replay, speedup, eficiencia, Karp–Flatt e informe técnico · Responsable: Kenneth Castro · Fecha objetivo: 16 de noviembre de 2026
  - [x] Acuerdo: Repasar preguntas sobre el emparejamiento, las fuentes de datos, la interfaz web y el panel de métricas · Responsable: Yamil Campos · Fecha objetivo: 16 de noviembre de 2026
- **Seguimiento de acuerdos anteriores:** Se cumplieron los acuerdos de la Reunión 4: contención, benchmarks y validación del emparejamiento.

## Índice de semanas registradas

| Semana | Reunión 1 | Reunión 2 | Estado |
|---|---|---|---|
| Semana del 2 al 8 de noviembre de 2026 | 2 de noviembre de 2026 | 6 de noviembre de 2026 | Completada (Hito 1) |
| Semana del 9 al 15 de noviembre de 2026 | 9 de noviembre de 2026 | 14 de noviembre de 2026 | Completada (Hito 2) |
| Semana del 16 de noviembre de 2026 | 16 de noviembre de 2026 | — (Defensa oral el 17 de noviembre) | Completada (Hito 3) |
