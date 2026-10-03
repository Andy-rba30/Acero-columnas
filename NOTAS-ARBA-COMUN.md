# Notas para ARBA-comun (desde Acero-columnas)

Incidencias encontradas al integrar `external/ARBA-comun` v1.0.0 que se resuelven en el repo común, no aquí
(INTEGRACION.md, paso 0). Mientras tanto el add-in las sortea en su propio `.csproj`.

1. **`Arba.Comun.props` + glob por defecto del SDK → CS2002.** Un proyecto SDK-style compila `**/*.cs` bajo su carpeta,
   así que `external/ARBA-comun/src/*.cs` entraba dos veces (una por el glob y otra por el `<Compile Include>` del
   `.props`): 13 avisos `CS2002: Source file specified multiple times`. Además el glob arrastraba
   `external/ARBA-comun/build/CheckUsage.cs` y `external/ARBA-comun/tests/Program.cs` al ensamblado del add-in.
   Solución aplicada en `ColumnRebar.csproj`: `<DefaultItemExcludes>$(DefaultItemExcludes);external/**</DefaultItemExcludes>`.
   Propuesta para el común: que el `.props` lo haga él (`<DefaultItemExcludes>$(DefaultItemExcludes);$(ArbaComunDir)**</DefaultItemExcludes>`
   con la ruta relativa al proyecto) o que INTEGRACION.md §2 lo indique como paso obligatorio.
