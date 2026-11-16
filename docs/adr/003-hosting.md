# ADR 003: Hosting del sitio público

## Título y fecha

**GitHub Pages con generación y publicación por GitHub Actions**  
**Fecha:** 2026-11-15

## Contexto

La interfaz es estática: HTML, CSS y JavaScript consumen JSON publicado bajo `web/data/`. El proyecto ya está versionado en GitHub y necesita una actualización automatizada del catálogo.

## Alternativas consideradas

- **GitHub Pages + GitHub Actions:** alojamiento estático integrado al repositorio, workflow declarativo y despliegue de la carpeta `web/`.
- **Vercel o Netlify:** experiencia sólida para frontends y despliegues por commit, pero añaden una plataforma externa sin requerirse un servidor para el sitio.
- **Render:** puede alojar servicios y sitios estáticos, pero sus funciones de backend exceden las necesidades actuales.

## Decisión

Usar GitHub Pages y GitHub Actions. El sitio se publica en [https://zzetta0507-lgtm.github.io/radar-ofertas-multinucleo/](https://zzetta0507-lgtm.github.io/radar-ofertas-multinucleo/). El workflow instala .NET 8, ejecuta el pipeline, agrega cambios de `web/data/` al repositorio cuando los hay y despliega `web/`.

## Consecuencias

- No se necesita un servidor web dinámico para filtrar o mostrar los datos.
- El sitio solo debe publicar datos que el pipeline haya generado; si falta un JSON, la UI presenta un estado vacío.
- Los datos publicados son visibles públicamente y deben respetar licencias, condiciones de uso y políticas de las fuentes.
- Una función que requiera backend o secretos no debe implementarse en JavaScript público; habría que revisar esta decisión si aparece tal requisito.

## Estado

**Aceptado e implementado.**
