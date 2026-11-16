# ADR 006: Algoritmo de emparejamiento de títulos

## Título y fecha

**Normalización y similitud combinada para títulos de videojuegos**  
**Fecha:** 2026-11-15

## Contexto

Los nombres de catálogo y los candidatos de las fuentes pueden diferir por puntuación, diacríticos, plataforma, edición o numeración. Se requiere un algoritmo propio, un umbral configurable y una señal para enviar coincidencias ambiguas a revisión.

## Alternativas consideradas

- Igualdad exacta, simple pero frágil ante cambios de formato.
- Levenshtein solamente, que mide edición de caracteres sin ponderar tokens.
- Similitud por tokens solamente, que tolera variaciones pero puede ignorar el orden.
- Combinar similitud de caracteres y tokens, con reglas explícitas para sufijos y secuelas.

## Decisión

Normalizar a minúsculas, descomponer Unicode y retirar diacríticos, quitar apóstrofos y puntuación, colapsar espacios y retirar sufijos reconocidos de plataforma/edición. Los números romanos finales de II a X se convierten a su equivalente arábigo; se conserva el número para distinguir secuelas.

Calcular la similitud así:

`similitud = 0.7 × similitud_Levenshtein + 0.3 × Sørensen–Dice(tokens)`

Levenshtein se normaliza por la longitud mayor. La similitud Sørensen–Dice se calcula sobre conjuntos de tokens. Si los títulos tienen numeraciones finales distintas, la puntuación se limita a 0.74. El umbral es configurable en `matching_threshold`, actualmente 0.85; se acepta una coincidencia si `similitud >= umbral`.

Marcar como dudosa una coincidencia aceptada si queda a menos de 0.05 del umbral, si la segunda candidata queda a menos de 0.03 de la mejor, o si el candidato tiene etiquetas de edición, DLC, bundle u otro caso difícil. El validador mantiene 61 pares etiquetados y genera precisión y exhaustividad.

## Consecuencias

- La comparación es determinista y puede distribuirse por `Parallel.ForEach`; `workers_cpu` limita el grado de paralelismo.
- El umbral y las ponderaciones requieren revisión con el conjunto etiquetado al agregar fuentes o nuevos casos.
- La supresión de sufijos ayuda con ediciones, pero puede juntar productos distintos; la señal de duda y la validación son necesarias.
- El resultado actual de validación puede consultarse en `web/data/matching-validation.json`; no representa métricas de rendimiento.

## Estado

**Aceptado e implementado.**
