# Informe técnico final — Radar de Ofertas Multinúcleo

**Curso:** IC-3101 Arquitectura de Computadoras  
**Institución:** TEC, Campus Local San Carlos  
**Periodo:** II Semestre 2026  
**Equipo:** Santiago Alonzo Vargas Céspedes, Kenneth Esteban Castro Alvarado y Yamil Campos Miranda  
**Fecha de los artefactos de medición:** 15 de noviembre de 2026, hora local (UTC−6)

## 1. Resumen

Se evaluó el pipeline completo con cinco repeticiones por configuración, en modo replay, variando los trabajadores de E/S y de CPU. La mediana secuencial fue **16.5739 s**. La mejor mediana registrada en el barrido de E/S fue **12.4547 s** con un trabajador; aumentar `workers_io` no mejoró ese resultado de forma consistente. En el barrido de CPU, dos trabajadores mejoraron la mediana de un trabajador en 1.75 %, mientras que las configuraciones de 4 o más trabajadores fueron más lentas que esa referencia.

El experimento de contención mostró una carrera real: la variante sin sincronización contó 364,128 de las 500,000 operaciones esperadas. La acumulación local por hilo (`ThreadLocal<T>`) fue la variante sincronizada más rápida, con 2.5527 ms. La validación guardada del emparejamiento reporta 61 pares, precisión y exhaustividad de 100.0 %.

Estos resultados son mediciones del **pipeline completo**, no de cada etapa por separado. El replay usado tenía latencia fija y jitter en cero. Por tanto, las mediciones no permiten identificar el cuello de botella de una etapa, evaluar la respuesta a otras latencias ni comparar replay con una ejecución en vivo.

## 2. Ficha del equipo de medición

La información de sistema se obtuvo localmente mediante `Win32_Processor` y `Win32_ComputerSystem`. El SO, la arquitectura del proceso, el runtime y los 12 procesadores lógicos coinciden con los metadatos de `web/data/benchmarks.json`.

| Propiedad | Valor |
|---|---|
| Procesador | 12th Gen Intel(R) Core(TM) i5-12400F |
| Núcleos físicos | 6 |
| Procesadores lógicos | 12 |
| SMT | Activo (12 procesadores lógicos para 6 núcleos físicos) |
| Memoria RAM instalada | 25,615,966,208 bytes (25.62 GB decimales; aproximadamente 23.86 GiB) |
| Sistema operativo | Microsoft Windows 10.0.26200 |
| Arquitectura del proceso | X64 |
| Lenguaje / runtime ejecutado | C# / .NET 10.0.12 |
| Target del proyecto | `net8.0`, con `RollForward=Major` |

El archivo de benchmark indica que fue generado el **15-11-2026 a las 19:53:24** hora local. El experimento de contención se capturó el mismo día a las **18:33:35** hora local.

## 3. Método y definición de métricas

El runner ejecuta cinco repeticiones de cada configuración en replay. Cada repetición usa una base SQLite temporal nueva; el tiempo incluye el pipeline completo, incluida la persistencia. El barrido de E/S usa `workers_io = [1, 2, 4, 8, 16, 32, 64]`. El de CPU usa `workers_cpu = [1, 2, 4, 8, 12, 24]`, con la configuración de E/S base del runner.

Se reportan las definiciones aplicadas por el runner:

$$S_{abs}(p) = \frac{T_{sec}}{T_{par}(p)}$$

$$S_{rel}(p) = \frac{T_{par}(1)}{T_{par}(p)}$$

$$E(p) = \frac{S_{rel}(p)}{p}$$

$$S(p) = \frac{1}{f + (1-f)/p}$$

$$e = \frac{1/S_{rel}(p) - 1/p}{1 - 1/p}$$

Los tiempos de las tablas están en segundos, convertidos de los milisegundos del JSON/CSV. Se muestran cuatro decimales. Los valores de speedup, eficiencia y Karp–Flatt se toman de los artefactos generados por el runner. Para la fila secuencial, `S_abs=1.00` por convención de presentación; para cada barrido, `p=1` tiene `S_rel=1.00`, `E=1.00` y no se define `e`.

### 3.1 Pipeline completo — línea base secuencial

| Workers (p) | Mediana (s) | Mín – máx (s) | S_abs(p) | S_rel(p) | E(p) | e (Karp–Flatt) |
|---|---:|---:|---:|---:|---:|---:|
| Secuencial | 16.5739 | 15.3234 – 17.1096 | 1.00 | — | — | — |

### 3.2 Pipeline completo — barrido de E/S (`workers_io`)

| Workers (p) | Mediana (s) | Mín – máx (s) | S_abs(p) | S_rel(p) | E(p) | e (Karp–Flatt) |
|---|---:|---:|---:|---:|---:|---:|
| E/S 1 | 12.4547 | 12.2206 – 14.1838 | 1.3307 | 1.0000 | 1.0000 | — |
| E/S 2 | 13.2988 | 12.1793 – 14.0230 | 1.2463 | 0.9365 | 0.4683 | 1.1356 |
| E/S 4 | 12.7766 | 12.2115 – 13.8385 | 1.2972 | 0.9748 | 0.2437 | 1.0345 |
| E/S 8 | 12.6797 | 12.1175 – 13.9720 | 1.3071 | 0.9823 | 0.1228 | 1.0206 |
| E/S 16 | 13.0386 | 12.1378 – 14.1682 | 1.2711 | 0.9552 | 0.0597 | 1.0500 |
| E/S 32 | 13.2344 | 12.0882 – 13.9798 | 1.2523 | 0.9411 | 0.0294 | 1.0646 |
| E/S 64 | 12.8974 | 12.1144 – 14.1199 | 1.2851 | 0.9657 | 0.0151 | 1.0361 |

### 3.3 Pipeline completo — barrido de CPU (`workers_cpu`)

| Workers (p) | Mediana (s) | Mín – máx (s) | S_abs(p) | S_rel(p) | E(p) | e (Karp–Flatt) |
|---|---:|---:|---:|---:|---:|---:|
| CPU 1 | 12.7529 | 12.1020 – 13.6932 | 1.2996 | 1.0000 | 1.0000 | — |
| CPU 2 | 12.5297 | 12.2299 – 14.0608 | 1.3228 | 1.0178 | 0.5089 | 0.9650 |
| CPU 4 | 14.4669 | 14.3954 – 16.5261 | 1.1456 | 0.8815 | 0.2204 | 1.1792 |
| CPU 8 | 15.0970 | 14.2465 – 16.5997 | 1.0978 | 0.8447 | 0.1056 | 1.2101 |
| CPU 12 | 15.1243 | 14.4720 – 16.7177 | 1.0958 | 0.8432 | 0.0703 | 1.2029 |
| CPU 24 | 15.0496 | 14.7424 – 16.6568 | 1.1013 | 0.8474 | 0.0353 | 1.1879 |

### 3.4 Tiempos por etapa

La exportación vigente de `benchmarks.json` contiene la línea base secuencial y los arreglos `ioSweep` y `cpuSweep`; `benchmarks.csv` contiene las mismas configuraciones. Ninguno incluye duración por etapa. En consecuencia, **no hay mediciones ni tablas Anexo 2 por etapa en los artefactos recién exportados** y no se asignan tiempos estimados a Descubrir, Descargar, Parsear, Emparejar, Persistir o Publicar. Las seis etapas ejecutan código real, pero sus duraciones individuales no fueron instrumentadas en esta exportación.

La siguiente tabla deja constancia de la ausencia de datos por etapa; los guiones indican que no se calcularon métricas, no que la duración sea cero.

| Workers (p) | Mediana (s) | Mín – máx (s) | S_abs(p) | S_rel(p) | E(p) | e (Karp–Flatt) |
|---|---:|---:|---:|---:|---:|---:|
| Etapa 0 · Descubrir — sin medición | — | — | — | — | — | — |
| Etapa 1 · Descargar — sin medición | — | — | — | — | — | — |
| Etapa 2 · Parsear — sin medición | — | — | — | — | — | — |
| Etapa 3 · Emparejar — sin medición | — | — | — | — | — | — |
| Etapa 4 · Persistir — sin medición | — | — | — | — | — | — |
| Etapa 5 · Publicar — sin medición | — | — | — | — | — | — |

### 3.5 Gráficos del panel

Los gráficos interactivos de Speedup y Eficiencia del panel de métricas están disponibles en el [sitio público Radar de Ofertas Multinúcleo](https://zzetta0507-lgtm.github.io/radar-ofertas-multinucleo/). Los valores tabulares de esta sección provienen de los archivos JSON/CSV de la ejecución registrada.

## 4. Experimento de contención

El artefacto registra **500,000 operaciones** distribuidas entre **4 trabajadores** y cuatro plataformas. El conteo correcto por plataforma es 125,000. La variante sin sincronización terminó con 364,128 ofertas en total; faltaron `500,000 − 364,128 = 135,872` actualizaciones, equivalentes a **27.17 %**. El archivo también registra una demostración puntual en PC con 4 actualizaciones esperadas y solo 1 observada.

| Variante | Tiempo (ms) | Ofertas observadas | Resultado |
|---|---:|---:|---|
| `sin_sincronizacion` | 10.4903 | 364,128 / 500,000 | Incorrecto; se perdieron 135,872 actualizaciones (27.17 %) |
| `lock_global` | 34.7148 | 500,000 | Correcto; exclusión mutua global |
| `thread_local` | 2.5527 | 500,000 | Correcto; acumulación local por trabajador y consolidación |
| `concurrent_dictionary` | 49.9466 | 500,000 | Correcto; actualizaciones concurrentes seguras |

`ThreadLocal<T>` evita que los trabajadores actualicen el mismo contador compartido en cada operación y consolida los resultados al final; en esta medición fue más rápido que el `lock` global y `ConcurrentDictionary`. El experimento no recolecta contadores de hardware de coherencia de caché, por lo que la ausencia de invalidaciones no es una medición directa: lo observado es la reducción de escrituras concurrentes sobre contadores compartidos. El tiempo menor de la variante sin sincronización no es una mejora válida, porque el resultado pierde datos.

## 5. Validación del emparejamiento

El validador almacenado en `web/data/matching-validation.json` registra **61 pares revisados manualmente**, 30 verdaderos positivos, cero falsos positivos y cero falsos negativos; no registra pares mal clasificados. Por ello, la precisión y la exhaustividad (recall) observadas son ambas **100.0 %** sobre ese conjunto etiquetado.

La implementación normaliza los títulos a minúsculas, descompone Unicode y retira diacríticos, quita apóstrofos y puntuación, colapsa espacios y remueve sufijos reconocidos de plataforma y edición. Convierte numeraciones romanas finales de II a X para compararlas, pero conserva la distinción entre secuelas. La similitud propia combina Levenshtein normalizada (70 %) con Sørensen–Dice de conjuntos de tokens (30 %). Si los números de secuela difieren, limita la puntuación a 0.74. El umbral actual configurable es **0.85**.

Una coincidencia aceptada se marca como dudosa si está a menos de 0.05 del umbral, si la segunda candidata queda a menos de 0.03 de la mejor, o si se reconoce una etiqueta de edición, DLC, bundle u otro caso difícil. El resultado perfecto describe el conjunto de 61 pares disponible; no garantiza la misma precisión para cualquier catálogo o fuente futura.

## 6. Respuestas a las preguntas de la Sección 12

### 1. ¿Por qué la etapa de descarga sigue mejorando con más workers que núcleos, y la de emparejamiento no? ¿Qué significa que la eficiencia por núcleo de la descarga supere 1?

La expectativa general para E/S es que varios trabajos asincrónicos puedan progresar mientras otros esperan la red, sin reservar un hilo de CPU durante cada espera. Por eso el grado de concurrencia de E/S puede ser mayor que el número de núcleos. En cambio, `Parallel.ForEach` para similitud de títulos ejecuta trabajo CPU-bound: no crea capacidad de cálculo ilimitada al superar los procesadores lógicos y puede añadir coordinación y contención.

**Los resultados de esta ejecución no demuestran que la descarga mejore al crecer `workers_io`**: la mediana más baja fue 12.4547 s con `workers_io=1`; con 32 y 64 fue 13.2344 y 12.8974 s, respectivamente. Las dispersiones mín–máx se solapan ampliamente. Además, `config.json` fija la latencia replay en 0 ms y jitter en 0 ms. El adaptador replay lee las respuestas grabadas y no adquiere el `SemaphoreSlim` por dominio que limita las solicitudes HTTP en vivo. En modo live, la configuración sí limita a dos solicitudes simultáneas por dominio y fija 1000 ms de intervalo mínimo entre inicios. Por tanto, ni el solapamiento real de esperas de red ni la saturación del limitador por dominio se midieron aquí.

Con la definición del runner, `E(p)=S_rel(p)/p`; `S_rel` usa como referencia el resultado de un trabajador dentro del mismo barrido. Así, `E(1)=1.00` y la eficiencia de `workers_io>1` es menor que 1 en los datos (por ejemplo, 0.0294 para 32). El `S_abs` de 1.3307 para E/S con un trabajador compara ese modo contra la línea base secuencial; **no significa que la eficiencia por núcleo del barrido de E/S sea mayor que 1**. La medida supera 1 solo si se cambia la referencia o la definición, y no debe llamarse `E(p)` de esta especificación.

### 2. ¿Qué etapa es el cuello de botella del pipeline completo y cómo cambia al variar la latencia simulada?

No se puede identificar de forma concluyente una etapa cuello de botella con estos artefactos: el cronómetro cubre el pipeline entero y no hay duración por etapa. El barrido de CPU empeora respecto a `workers_cpu=1`, pero eso no demuestra por sí solo que Emparejar sea el cuello de botella; Persistir, Parsear, la coordinación o la carga global también pueden afectar el tiempo.

Tampoco se ejecutó un barrido de latencia: `replay_latency_fixed_ms=0` y `replay_latency_jitter_ms=0`. El código aplica el retraso configurado a las respuestas replay; una variación distinta cambiaría el tiempo en la ruta de descarga y su solapamiento con otras etapas, pero no se midió cuánto ni se puede inferir un nuevo cuello de botella a partir de esta ejecución.

### 3. ¿Cuál es la fracción serial estimada y de dónde proviene en su código?

Para el barrido de CPU, Karp–Flatt da `e=0.9650`, `1.1792`, `1.2101`, `1.2029` y `1.1879` para `p=2, 4, 8, 12, 24`, respectivamente. En E/S, los valores para `p=2, 4, 8, 16, 32, 64` son `1.1356`, `1.0345`, `1.0206`, `1.0500`, `1.0646` y `1.0361`.

En un modelo ideal de Amdahl, `e` se interpreta como una fracción serial efectiva. CPU con `p=2` obtuvo `S_rel=1.0178` y `e=0.9650`, lo que indica una mejora pequeña y una fracción efectiva serial alta bajo ese modelo. En CPU con `p=4` o más, y en todo el barrido de E/S con `p>1`, `S_rel<1` y `e>1`; en esos casos `e` **no es una fracción serial física válida**, sino una señal de que el modelo ideal no explica la ejecución y que el costo añadido domina la ganancia paralela.

El código contiene posibles fuentes de serialización y coordinación: el productor/consumidor se conecta mediante cinco canales acotados con backpressure (`FullMode=Wait`); Parsear y Persistir tienen un solo consumidor; las escrituras SQLite se serializan con `SemaphoreSlim`; y la solicitud en vivo limita cada dominio con otro `SemaphoreSlim` y una cadencia mínima. Este último factor no explica las mediciones replay: la ruta replay no solicita permisos al limitador. Los artefactos no permiten repartir el `e` observado entre esos costos.

### 4. ¿Qué pasa al superar el número de núcleos lógicos? ¿Qué efecto tiene SMT?

La máquina tiene 6 núcleos físicos y 12 procesadores lógicos, con SMT activo. Al pasar de `workers_cpu=12` a 24, la mediana bajó de 15.1243 a 15.0496 s (0.0746 s, aproximadamente 0.49 %), pero ambos resultados son más lentos que la referencia CPU de un trabajador (12.7529 s). Los rangos (14.4720–16.7177 s y 14.7424–16.6568 s) se solapan. La eficiencia disminuyó de 0.0703 a 0.0353.

La mediana más baja del barrido CPU fue con `workers_cpu=2` (12.5297 s), 0.2232 s o 1.75 % más rápida que con uno (12.7529 s); los rangos de ambas configuraciones se solapan. La diferencia de 12 a 24 trabajadores también es pequeña frente a sus dispersiones, por lo que no prueba por sí sola una mejora. Al ofrecer más trabajo CPU que procesadores lógicos disponibles, puede crecer la planificación, la competencia por ejecución y la presión sobre caché; SMT comparte recursos físicos y no equivale a duplicar núcleos. Este benchmark de pipeline completo no aísla el efecto de SMT ni mide contadores de caché.

### 5. Solo C#: ¿qué diferencia hay entre `Parallel.ForEach` y `Task.WhenAll` en cada etapa?

`Parallel.ForEach` ejecuta iteraciones CPU-bound en paralelo con un grado máximo configurado y usa el `ThreadPool`; en este proyecto se emplea dentro del emparejamiento para comparar el candidato contra los títulos del catálogo. `workers_cpu` limita ese paralelismo interno.

`Task.WhenAll` no es un bucle paralelo ni crea por sí mismo un hilo por operación. Espera la finalización de varias tareas asincrónicas; durante una espera de E/S, `await` permite liberar el hilo para que avance otro trabajo. En `PipelineRunner`, `Task.WhenAll` coordina las tareas de las etapas y trabajadores, y los trabajadores de descarga leen de canales y esperan operaciones asincrónicas. El proyecto no usa actualmente `Parallel.ForEachAsync`; es otra opción de biblioteca para recorrer elementos con operaciones asincrónicas y un grado de concurrencia limitado, pero no sustituye al `Parallel.ForEach` síncrono del cálculo CPU-bound.

En resumen: Descubrir/Descargar usan coordinación asincrónica y consumidores configurables; Parsear, Persistir y Publicar son trabajadores de canal individuales en la implementación actual; Emparejar es el punto con `Parallel.ForEach` CPU-bound cuando se ejecuta en modo `parallel`.

### 6. ¿Cuánto cuesta el andamiaje paralelo (diferencia entre `T_par(1)` y `T_sec`) y de dónde viene?

La comparación indicada entre Secuencial (16.5739 s) y el modo concurrente con un trabajador de E/S (12.4547 s) arroja una diferencia neta de **4.1191 s** menos para la segunda ejecución, aproximadamente 24.85 % respecto a la mediana secuencial. No se observa un costo neto en ese contraste, sino una ejecución concurrente más rápida. Si `T_par(1)` se refiere en cambio al barrido CPU, su mediana fue 12.7529 s, 3.8210 s menos que la secuencial.

El modo concurrente ejecuta las etapas como productores y consumidores separados conectados por canales acotados; esto permite que una etapa progrese mientras otra procesa elementos anteriores. Sin embargo, la comparación también cambia el modo de ejecución, el calendario de tareas y el recorrido por etapas: no existe una medición de control que aísle cuánto de la diferencia procede exclusivamente de `Channel<T>`. Además, el worker de CPU con `p=1` en su propio barrido tiene mediana 12.7529 s; conviene no confundirlo con la fila de E/S de 12.4547 s.

### 7. ¿Qué tan cerca está una ejecución en vivo de lo que predice el modo replay?

No hay un benchmark en vivo en los archivos disponibles, ni una pareja de ejecuciones con los mismos datos, configuración y repeticiones que permita calcular la diferencia. `--run-benchmarks` requiere `--source replay`; el modo replay evita solicitudes de red y en esta configuración simula cero latencia. Por consiguiente, **no se puede cuantificar la cercanía entre ejecución en vivo y replay** con las mediciones registradas. La latencia real, límites de cada dominio, variabilidad de red, respuestas y fallos de fuentes serían factores de diferencia que se deben medir con una campaña en vivo separada.

### 8. ¿Qué tanto se acercaron las predicciones del Hito 1 a las mediciones, y qué supuesto falló?

> **[COMPLETAR MANUALMENTE SIN IA POR EL EQUIPO]:** Redactar aquí las predicciones teóricas realizadas en el Hito 1 antes de ejecutar los benchmarks y contrastar qué supuesto falló frente a los valores medidos.

## 7. Conclusiones y límites

1. En estos benchmarks replay, el aumento de `workers_io` por encima de uno no mejora de forma consistente el tiempo del pipeline completo.
2. `workers_cpu=2` obtuvo un speedup relativo de 1.0178 frente a un trabajador; las configuraciones de 4 o más trabajadores fueron más lentas. Los valores `e>1` en estas últimas señalan sobrecosto o incumplimiento de las hipótesis del modelo ideal, no una fracción serial literal mayor que uno.
3. La agregación local por trabajador entregó todos los resultados en el menor tiempo de las cuatro variantes de contención medidas; la variante sin sincronización fue más rápida solo porque perdió actualizaciones.
4. No se midieron duraciones por etapa, diferentes latencias replay, ejecución en vivo ni contadores hardware de caché. No se presentan estimaciones para suplir esas ausencias.
5. El conjunto de validación de 61 pares obtuvo 100.0 % de precisión y exhaustividad, pero es una muestra acotada y no garantiza el comportamiento para fuentes o títulos futuros.

## 8. Referencias de diseño y artefactos

- [ADR 001 — Lenguaje y runtime](adr/001-lenguaje.md)
- [ADR 002 — Base de datos](adr/002-base-datos.md)
- [ADR 003 — Hosting](adr/003-hosting.md)
- [ADR 004 — Definición de oferta](adr/004-definicion-oferta.md)
- [ADR 005 — Límites de concurrencia](adr/005-limites-concurrencia.md)
- [ADR 006 — Algoritmo de emparejamiento](adr/006-algoritmo-emparejamiento.md)
- [Cambios de diseño](cambios-diseno.md)
- Implementación: [PipelineBenchmarkRunner.cs](../src/RadarOfertasMultinucleo/Experiments/PipelineBenchmarkRunner.cs), [PipelineRunner.cs](../src/RadarOfertasMultinucleo/Pipeline/PipelineRunner.cs), [MetadataPipelineStages.cs](../src/RadarOfertasMultinucleo/Pipeline/MetadataPipelineStages.cs), [ReplayableHttpClient.cs](../src/RadarOfertasMultinucleo/Infrastructure/Replay/ReplayableHttpClient.cs), [config.json](../src/RadarOfertasMultinucleo/config.json) y [RadarOfertasMultinucleo.csproj](../src/RadarOfertasMultinucleo/RadarOfertasMultinucleo.csproj).
- Datos tabulares: `web/data/benchmarks.csv`
- Resultados estructurados: `web/data/benchmarks.json`
- Contención: `web/data/benchmark-contention.json`
- Validación de emparejamiento: `web/data/matching-validation.json`
