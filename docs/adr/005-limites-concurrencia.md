# ADR 005: Límites de concurrencia y backpressure

## Título y fecha

**Colas acotadas, trabajadores configurables y límite por dominio**  
**Fecha:** 2026-11-15

## Contexto

El pipeline conecta seis etapas y debe evitar crecimiento ilimitado de memoria, ráfagas irresponsables de solicitudes y paralelismo de CPU no controlado. Las configuraciones deben permitir experimentación repetible sin fijar conclusiones teóricas.

## Alternativas consideradas

- Colas ilimitadas, que simplifican la producción pero permiten acumular datos sin límite.
- Colas acotadas con `FullMode.Wait`, que propagan backpressure al productor.
- Una cantidad fija de trabajadores no configurable.
- Límites configurables para trabajadores de E/S, cálculo y solicitudes por host.

## Decisión

Usar cinco canales acotados (`Channel<T>`) entre las seis etapas. Sus capacidades corresponden a `queue_size_stage1..5`; `FullMode=Wait` aplica backpressure. El archivo actual configura capacidad 64 en cada frontera. Los valores son parámetros iniciales, no predicciones ni resultados de rendimiento.

La configuración inicial usa `workers_io=4`, `workers_cpu=4`, `max_concurrent_requests_per_domain=2` y una cadencia mínima de 1000 ms por dominio. El limitador de dominio combina un `SemaphoreSlim` de concurrencia con otro para serializar la programación de solicitudes.

En `sequential` se procesa un elemento a la vez. En `concurrent`, varios trabajadores atienden descargas y el emparejamiento queda con grado interno uno. En `parallel`, la etapa de emparejamiento usa un único consumidor de canal y `Parallel.ForEach` limitado por `workers_cpu`, evitando paralelismo anidado. Los valores se ajustan mediante `config.json` durante los barridos.

## Consecuencias

- La memoria queda limitada por las capacidades configuradas y el productor se detiene cuando una cola está llena.
- Los límites por dominio favorecen una carga responsable; pueden reducir el throughput y se deben conservar en las mediciones de E/S.
- Barridos de trabajadores y tamaños de cola deben publicar mediciones observadas, hardware y runtime; no se asume escalamiento ideal.
- El flujo actual consulta un conjunto limitado de fuentes; ampliar fuentes requerirá reevaluar los límites por host.

## Estado

**Aceptado e implementado.** Los valores concretos actuales están en `src/RadarOfertasMultinucleo/config.json`.
