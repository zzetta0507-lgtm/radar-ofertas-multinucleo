# Radar de Ofertas Multinúcleo

Proyecto académico para IC-3101 Arquitectura de Computadoras (TEC San Carlos).
La aplicación se implementa en C# sobre .NET 8; el catálogo e historial se
almacenan en SQLite y el sitio público consume JSON estático desde `web/data/`.

## Estructura inicial

- `src/RadarOfertasMultinucleo/`: aplicación .NET y contratos de las seis etapas.
- `src/RadarOfertasMultinucleo/config.json`: modo, trabajadores, colas y límites.
- `src/RadarOfertasMultinucleo/Infrastructure/Database/001_esquema_inicial.sql`:
  esquema relacional inicial. El archivo debe aplicarse al crear `data/radar.db`.
- `web/`: sitio estático publicado por GitHub Pages.

La implementación es incremental. En este hito se definen contratos, modelos,
configuración y esquema; aún no se conectan fuentes externas ni se ejecutan las
etapas de negocio.

## Ejecutar la base de configuración

Desde la raíz del repositorio:

```powershell
dotnet run --project src/RadarOfertasMultinucleo
dotnet run --project src/RadarOfertasMultinucleo -- --mode concurrent
```

El ejecutable valida la configuración y el modo solicitado. Las etapas se
incorporarán en hitos posteriores.