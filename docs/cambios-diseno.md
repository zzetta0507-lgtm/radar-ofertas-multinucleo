# Cambios respecto al diseño inicial

Este documento registra cambios de diseño y distingue decisiones implementadas de funciones todavía pendientes. No contiene predicciones teóricas de rendimiento.

## Runtime de .NET

- El target permanece en `net8.0`.
- Se habilitó `RollForward=Major` para permitir ejecutar el binario .NET 8 en un runtime mayor instalado, como .NET 10. Esto no cambia el target de compilación ni sustituye la validación CI sobre .NET 8.
- Las mediciones deben registrar el runtime que realmente ejecutó el proceso.

## SQLite y vida útil de conexiones

- La inicialización configura `PRAGMA journal_mode=WAL`, claves foráneas y `PRAGMA busy_timeout=5000`.
- Las conexiones usan `Pooling=False` y `DefaultTimeout=5`; los métodos del repositorio las cierran con `await using`.
- Las escrituras del repositorio se serializan con `SemaphoreSlim` y las tablas padre se aseguran antes de insertar observaciones dependientes.
- El runner de benchmarks usa una base temporal nueva por repetición y llama `SqliteConnection.ClearAllPools()` antes de iniciarla y después del pipeline, antes de eliminar el directorio temporal. El cambio evita handles retenidos sobre `radar.db` en Windows.

## Pipeline y experimentación

- La etapa de emparejamiento usa un solo consumidor de canal y paralelismo interno mediante `Parallel.ForEach` en modo `parallel`, limitado por `workers_cpu`; así se evita multiplicar trabajadores de canal y CPU.
- El benchmark requiere replay y exporta mediciones observadas. No se agregan predicciones numéricas teóricas al Hito 1.
- Las decisiones de sincronización, backpressure, persistencia y emparejamiento están detalladas en [los ADR](./adr/).

## Funcionalidad pendiente respecto al diseño de producto

- El catálogo actual descubre metadatos de OpenCritic y Steam; los conectores completos de precios, minoristas, Metacritic y How Long To Beat aún deben integrarse.
- `deal_rule` está definido en configuración, pero el cálculo de `IsDeal`, descuentos y mínimos históricos queda pendiente de la etapa de precios. Ver [ADR 004](./adr/004-definicion-oferta.md).
- La interfaz tolera la ausencia de archivos de métricas y muestra estados vacíos; no se deben interpretar esos estados como mediciones iguales a cero.

## Hito 1: análisis teórico de rendimiento

**Pendiente de redacción manual por el equipo.**  
Esta sección se deja intencionalmente sin fórmulas evaluadas, cifras ni predicciones generadas con IA. El equipo incorporará aquí su análisis teórico y sus cálculos manuales.
