# ADR 002: Persistencia relacional local

## Título y fecha

**SQLite en `data/radar.db`, con WAL e historial de precios append-only**  
**Fecha:** 2026-11-15

## Contexto

El pipeline necesita almacenamiento local y reproducible para catálogo, plataformas, fuentes, observaciones y relaciones. El historial de precios debe conservar capturas anteriores, incluso cuando el precio actual cambie.

## Alternativas consideradas

- **SQLite:** archivo local, transacciones ACID, claves foráneas y ejecución sin servicio externo.
- **Motores cliente-servidor** (por ejemplo PostgreSQL o MySQL): mayor concurrencia multiusuario y operación centralizada, pero requieren servicio, credenciales, despliegue y configuración que no aportan al experimento local inicial.

## Decisión

Usar SQLite en `data/radar.db`, con esquema relacional normalizado, claves foráneas activas e historial de precios append-only. Configurar `PRAGMA journal_mode=WAL`, `PRAGMA busy_timeout=5000`, `Pooling=False` y `DefaultTimeout=5`. El repositorio serializa sus escrituras compartidas con `SemaphoreSlim`; las conexiones se cierran con `await using`. En benchmarks se limpian explícitamente los pools antes de crear o eliminar bases temporales.

Las observaciones de precio no se actualizan ni borran: cada captura es una fila nueva con su timestamp. Las consultas de mínimo histórico se derivan de esas observaciones.

## Consecuencias

- SQLite simplifica las ejecuciones locales y de replay, y WAL permite lectores concurrentes mientras se escribe, sujeto a las reglas de SQLite.
- `busy_timeout` permite esperar brevemente por bloqueos; no reemplaza la serialización de escritores del repositorio.
- El pool deshabilitado y `ClearAllPools()` evitan handles retenidos en Windows durante el ciclo de vida de bases temporales.
- SQLite sigue teniendo un escritor efectivo a la vez; una carga multiusuario futura podría requerir revisar el motor.

## Estado

**Aceptado e implementado.**
