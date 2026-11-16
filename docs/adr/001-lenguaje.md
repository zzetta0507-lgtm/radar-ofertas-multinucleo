# ADR 001: Lenguaje y runtime

## Título y fecha

**Adopción de C# sobre .NET 8 con compatibilidad de ejecución hacia versiones mayores**  
**Fecha:** 2026-11-15

## Contexto

El proyecto debe demostrar concurrencia de E/S y paralelismo de CPU en un pipeline de seis etapas, emplear colas acotadas y conservar código mantenible y medible. El proyecto apunta a `net8.0`; el archivo de proyecto configura `RollForward` como `Major`, lo que permite ejecutar en un runtime .NET mayor, como .NET 10, si el runtime objetivo no está instalado.

## Alternativas consideradas

- **C#/.NET 8:** APIs integradas `Channel<T>`, `SemaphoreSlim`, `Parallel.ForEach`, `Task` y `async/await`; tipado estático y soporte multiplataforma.
- **C#/.NET 10 como target:** acceso directo al runtime más reciente, a costa de exigirlo en todos los entornos. Se prefiere conservar `net8.0` como target estable y usar `RollForward` para hosts con runtime mayor.
- **Python 3.11+:** ecosistema útil para scraping y análisis. CPython 3.11 convencional mantiene el GIL para hilos de CPU; el paralelismo de CPU requeriría procesos u otra distribución/runtime. `Channel<T>` y `Parallel.ForEach` no son APIs de Python, por lo que su equivalente exigiría mecanismos distintos y menos uniformes para el objetivo didáctico.

## Decisión

Usar C# con target .NET 8 (`net8.0`) y `RollForward=Major`. El código usa `Channel<T>` para backpressure, `SemaphoreSlim` para límites de recursos y `Parallel.ForEach` para la fase de emparejamiento en el modo paralelo. CI instala .NET 8 y valida el target del proyecto.

## Consecuencias

- Las mediciones deben registrar versión real del runtime, hardware y sistema operativo, ya que el roll-forward puede ejecutar el mismo binario sobre un runtime mayor.
- Desarrollo, CI y documentación mantienen .NET 8 como referencia de compatibilidad; no se debe cambiar el target a .NET 10 sin decisión explícita.
- En Python se podría alcanzar paralelismo con procesos o una variante sin GIL, pero se perdería la alineación directa con los mecanismos que se estudian.

## Estado

**Aceptado e implementado.**
