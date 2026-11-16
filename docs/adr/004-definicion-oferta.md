# ADR 004: Definición de oferta y mínimo histórico

## Título y fecha

**Regla parametrizable de oferta y mínimo por tienda**  
**Fecha:** 2026-11-15

## Contexto

El sistema debe distinguir una rebaja de un precio simplemente observado. El MSRP del catálogo y el precio regular informado por una tienda pueden diferir. El mínimo histórico debe comparar observaciones comparables y no mezclar monedas ni tiendas.

## Alternativas consideradas

- Clasificar solo contra el MSRP del repositorio.
- Clasificar solo contra el precio regular informado por la tienda.
- Usar una regla combinada parametrizable y conservar el precio de referencia que justificó la clasificación.
- Calcular un mínimo global por juego, mezclando tiendas o monedas, o calcularlo por tienda/formato/moneda.

## Decisión

El contrato de configuración es `deal_rule` con `RepositoryMsrp`, `StoreRegularPrice` o `Combined`; el valor actual es `combined`.

La regla funcional acordada para conectar con los datos de precio es:

1. No clasificar como oferta si falta el precio actual o la referencia requerida, si la referencia es cero, o si el precio actual no es estrictamente menor.
2. `RepositoryMsrp`: oferta cuando `precio_actual < MSRP`.
3. `StoreRegularPrice`: oferta cuando `precio_actual < precio_regular_tienda`.
4. `Combined`: oferta cuando se cumple cualquiera de las dos comparaciones disponibles. Para el descuento se usa primero el precio regular de tienda si este es superior al precio actual; si no, se usa el MSRP que cumpla la condición.
5. `descuento_pct = 100 × (precio_referencia − precio_actual) / precio_referencia`, limitado al intervalo 0–100 y redondeado a dos decimales.
6. El mínimo histórico se calcula para la misma combinación de juego, tienda/fuente, formato y moneda, usando el mínimo de `current_price` no nulo en `price_observations`. Una observación actual es mínimo histórico si iguala ese mínimo. No se comparan directamente precios de monedas distintas.

## Consecuencias

- La regla de negocio es reproducible y se puede cambiar mediante configuración.
- La comparación por tienda, formato y moneda evita declarar un mínimo por una conversión implícita inexistente.
- El esquema ya conserva observaciones de precio append-only, pero la etapa actual todavía no integra conectores de precio ni aplica el cálculo; hasta entonces no se deben publicar ofertas inventadas.

## Estado

**Aceptado como contrato; implementación pendiente de la etapa de precios.** El enum y la opción de configuración existen, pero `IsDeal` aún no se calcula con esta regla.
