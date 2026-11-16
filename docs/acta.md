# Acta de constitución del proyecto

**Proyecto:** Radar de Ofertas Multinúcleo  
**Curso:** IC-3101 Arquitectura de Computadoras  
**Sede:** TEC, Campus Local San Carlos  
**Fecha de elaboración:** 2026-11-15  
**Repositorio:** [zzetta0507-lgtm/radar-ofertas-multinucleo](https://github.com/zzetta0507-lgtm/radar-ofertas-multinucleo)

## Integrantes y roles

| Integrante | Rol | Responsabilidades |
|---|---|---|
| Santiago Alonzo Vargas Céspedes | **Arquitectura y sincronización** | Diseño del pipeline de seis etapas; canales `Channel<T>` acotados; límites con `SemaphoreSlim`; elaboración y mantenimiento de ADRs técnicos; experimento de contención y revisión de integridad concurrente. |
| Kenneth Esteban Castro Alvarado | **Rendimiento y experimentación** | Modo replay reproducible; barridos de `workers_io` y `workers_cpu`; suite de mediciones; análisis de Speedup, Eficiencia y Karp–Flatt; preparación del informe técnico con resultados observados. |
| Yamil Campos Miranda | **Datos y producto** | Catálogo de al menos 500 juegos; conectores HTML/API y calidad de datos; algoritmo y validación de emparejamiento; sitio público en [GitHub Pages](https://zzetta0507-lgtm.github.io/radar-ofertas-multinucleo/); automatización de actualización y publicación. |

Los roles distribuyen la responsabilidad primaria; las decisiones que afecten contratos compartidos, datos publicados o resultados experimentales se revisan entre los tres integrantes.

## Objetivo y alcance

Construir un agregador público de ofertas de videojuegos y un experimento reproducible para observar los efectos de concurrencia de E/S y paralelismo de CPU en un pipeline multinúcleo. El proyecto contempla aplicación C#/.NET, persistencia SQLite, modo replay, benchmarks y una interfaz estática en español.

El trabajo se desarrolla incrementalmente por hitos. Las fuentes y funciones no implementadas se registran como pendientes; no se publicarán precios, calificaciones ni métricas que no provengan de datos observados.

## Acuerdos de comunicación

- Mantener dos reuniones de seguimiento por semana; registrar fecha, asistentes, decisiones, bloqueos y responsables en [seguimiento.md](./seguimiento.md).
- Usar GitHub Issues para tareas, preguntas técnicas y bloqueos; las decisiones duraderas se registran en ADRs.
- Usar Pull Requests para cambios compartidos. Cada PR describe el objetivo, el impacto sobre el pipeline/datos y las validaciones ejecutadas.
- Si una persona no puede asistir, deja su actualización y bloqueos en el Issue antes de la reunión.

## Flujo de trabajo en GitHub

1. Abrir o actualizar un Issue con alcance y criterios de aceptación.
2. Crear una rama `feature/<issue>-<resumen>` o `fix/<issue>-<resumen>` desde `main`.
3. Mantener el cambio acotado y ejecutar la validación correspondiente, incluyendo el build .NET cuando aplique.
4. Abrir Pull Request hacia `main`, solicitar revisión de al menos otro integrante y resolver comentarios antes de integrar.
5. Integrar mediante squash merge cuando CI esté verde. Evitar commits directos a `main`, salvo actualizaciones automáticas de datos ejecutadas por GitHub Actions.
6. La automatización actual ejecuta el pipeline y publica `web/` en GitHub Pages; los datos generados se revisan como parte de la salida de la acción.

## Resolución de conflictos técnicos

1. Exponer el problema, requisitos y alternativas en un Issue; separar hechos medidos de hipótesis.
2. Consultar el ADR relacionado. Si la decisión cambia arquitectura, datos o metodología, actualizar o crear un ADR antes de implementar.
3. Buscar consenso mediante discusión técnica y una prueba pequeña/reproducible cuando sea posible.
4. Si no hay consenso, cada integrante documenta su argumento y se decide por mayoría de integrantes, priorizando corrección, reproducibilidad, uso responsable de fuentes y cumplimiento del curso. Si el desacuerdo afecta evaluación o alcance académico, consultar al docente antes de cerrar la decisión.
5. Registrar la decisión, responsable de implementación y consecuencias en el Issue y en la bitácora correspondiente.

## Compromiso sobre resultados

El equipo no incluirá predicciones numéricas de rendimiento teórico para el Hito 1 generadas con IA. Esa sección queda reservada para elaboración manual del equipo. Los benchmarks posteriores informarán mediciones observadas y sus condiciones de ejecución.

**Aprobación de integrantes:** pendiente de firma/confirmación del equipo.
