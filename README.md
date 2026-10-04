# Armado automático de columnas — add-in Revit 2027

Genera la armadura de **columnas estructurales de sección rectangular, en L, en T,
en U, en cruz o cualquier otra sección poligonal rectilínea** (bordes paralelos a dos
ejes) a partir de la **geometría real del elemento**, sin depender de los nombres de
parámetros de tus familias: barras longitudinales, estribos cerrados y grapas.

Es el hermano del add-in de muros de contención
([Acero-automatico](https://github.com/Andy-rba30/Acero-automatico)): misma base
(lectura del sólido con rebanadas, ventana previa con esquema, red de seguridad que
deshace el elemento entero si una barra queda fuera del hormigón, `config.json`).

Antes de crear nada abre una **ventana** en la que se ve qué se ha detectado en cada
columna seleccionada y se elige el armado: tipos de barra, separación de
longitudinales, prolongaciones, distribución de estribos (global y por columna),
ganchos, grapas y recubrimiento, con un esquema de la sección y del alzado.

Comparte con el resto de add-ins ARBA el código común
[ARBA-comun](https://github.com/Andy-rba30/ARBA-comun) (submódulo `external/ARBA-comun`,
contrato **1.0.0**): cinta `ARBA`, partición `COLUMNAS - COL-{marca}`, parámetros
compartidos `ARBA - Origen` / `ARBA - Código` / `Metrado - Elemento`, borrar y
rearmar, y migración de modelos anteriores. Ver [Contrato ARBA-comun](#contrato-arba-comun).

## Cómo deduce la sección

1. Toma el sólido del elemento. Si la columna está **unida** a vigas o losas que le
   quitan hormigón, usa la geometría completa de la familia (`GetOriginalGeometry`),
   porque la armadura de la columna sigue de largo por el nudo.
2. Corta el sólido con una rebanada horizontal fina a media altura y lee el contorno
   de la cara superior de la rebanada. Tiene que ser **un único contorno cerrado sin
   huecos y con bordes rectos**.
3. Toma el borde más largo como eje `u`; `v` es el perpendicular. Comprueba que todos
   los bordes son paralelos a `u` o a `v` (sección **rectilínea**).
4. Repite el corte en estaciones a lo largo de toda la altura (`prismCheckStepMm`) y
   comprueba que la sección es la misma en todas: **prisma vertical**. Columnas
   inclinadas, con capitel, escalonadas o con vacíos se rechazan.
5. Descompone el contorno en sus **rectángulos máximos** (los rectángulos formados por
   la retícula de coordenadas de los vértices que están enteros dentro de la sección y
   no caben en otro mayor). Cada uno lleva un **estribo cerrado**:

   | Sección | Rectángulos = estribos |
   |---------|------------------------|
   | rectangular | 1 |
   | en L | 2 (solapados en la esquina) |
   | en T | 2 (ala + alma) |
   | en cruz | 2 |
   | en U | 3 (base + dos brazos) |

Cualquier otra forma (circular, con esquinas redondeadas, hueca, inclinada, de
sección variable) se **rechaza** con un mensaje claro y sin crear ninguna barra.

## Reglas de armado de la sección (`ColumnPlan`)

- Cada estribo va a `coverMm` de las caras de la columna (medido al exterior del
  estribo).
- Hay **barra obligada** en cada esquina de cada estribo y en cada punto en que un
  lado de un estribo **cruza** un lado de otro (la barra queda apoyada en los dos,
  como en la esquina interior de una L). Llevan el tipo de barra **de esquina**.
- Las **intermedias** llevan su propio tipo de barra (puede ser otro diámetro, por
  ejemplo esquinas Ø3/4" e intermedias Ø5/8") y se cuentan **por líneas**, que el
  plugin detecta en la sección: **filas** (los lados horizontales de los estribos,
  de arriba abajo: F1, F2...) y **verticales** (los lados verticales, de izquierda a
  derecha: V1, V2...). Una sección rectangular tiene 2 filas y 2 verticales; una L,
  3 y 3; una T, 3 filas y 4 verticales. Cada línea lleva un **total de barras**;
  el mínimo son sus obligadas (el armado que cierra los estribos: F1 2, F2 3, F3 3
  en una L típica) y en el cuadro de la columna seleccionada, con una entrada por
  línea, solo se puede **subir**; lo elegido vale también para las demás columnas
  seleccionadas con la **misma sección** (mismas medidas y rectángulos), así que al armar
  varias iguales a la vez todas se arman igual. Las que faltan se añaden en los huecos entre obligadas
  (sin cruzar nunca el vacío de una U), nunca a menos de 1.5 diámetros libres (si no
  caben, la casilla se pone en rojo y se avisa). Cuando otro estribo parte la línea
  (la esquina interior de una L), el **reparto** dice dónde van: al hueco más grande,
  hacia la izquierda, hacia la derecha o simétrico (por pares izquierda-derecha; en
  las verticales, hacia abajo o hacia arriba). También se cambia línea a línea.
- **Estribos interiores** (opcionales, por columna; las columnas de la misma sección los
  comparten): además del estribo de cada
  rectángulo se pueden añadir estribos cerrados que abrazan un grupo de barras, como el
  estribo central que ata las tres barras del medio de las caras largas. Se eligen por
  **posiciones de barra**: en horizontal de izquierda a derecha y en vertical de arriba
  abajo, contadas sobre todas las barras de la sección (el esquema las numera en cuanto la
  columna tiene alguno). En una sección de 830×450 con 7 barras por cara larga y 4 por
  cara corta, *horizontal de la 3 a la 5, vertical de la 1 a la 4* da ese estribo central
  (E2), de todo el alto. El estribo se ajusta por fuera a las barras elegidas, lleva la
  misma distribución, tipo y gancho que los demás y no cambia ni añade barras. Tiene que
  haber barra en sus cuatro esquinas, caber en el hormigón con recubrimiento (no puede
  cruzar el vacío de una U) y no coincidir con otro estribo; si no, la casilla se pone en
  rojo y la columna no se arma.
- **Grapas** (opcionales): una por cada par de intermedias enfrentadas de un mismo
  estribo, salvo donde ya pasa el lado de otro estribo (también de un estribo interior).
  Se colocan en cada cota de estribo, con sus ganchos.

Con recubrimiento 40 mm, estribo Ø3/8" y longitudinal Ø5/8", las secciones del plano
de referencia salen como están dibujadas subiendo solo la fila que toca: C-2
(150×250) con F1 = F2 = 3 da 6 barras; C-3 (L 250×450) con F1 = 4 da 10 barras;
C-1 y C-4 igual.

Las barras del mismo diámetro alineadas y equiespaciadas a lo largo de `u` se crean
como un solo conjunto de Revit (array), igual que si se modelaran a mano.

## Distribución de estribos en altura (`StirrupLayout`)

Se escribe como en los planos: **`1@50, 5@100, R@250`** = el primer estribo a 50 mm
de la base, cinco más cada 100 mm y el resto cada 250 mm como máximo (repartidos por
igual en el tramo central). Los valores menores de 5 se leen en metros (`1@.05`).
Con **Repetir desde la coronación** (por defecto) los grupos fijos se colocan también
desde arriba en espejo: zona de confinamiento arriba y abajo. Sin esa casilla el
resto sigue hasta arriba y el último estribo queda un hueco por debajo de la
coronación.

Cada columna puede tener **su propia distribución y su propia separación de
longitudinales** desde la lista de la ventana (por ejemplo, la columna del primer
piso con más estribos que la del último). Si la columna es demasiado corta para toda
la distribución, los grupos se recortan por la mitad y se avisa.

Los desfases de base y coronación descuentan altura (por ejemplo el canto de la losa
si el elemento la incluye).

## Longitudinales

- Van de la base a la coronación de la columna. Con **prolongación inferior** y
  **superior** sobresalen esa longitud (anclaje en la cimentación, empalme con el piso
  siguiente). Esas prolongaciones son las únicas partes de barra que pueden estar
  fuera del hormigón de la columna: el resto se comprueba.
- Con **patilla inferior** llevan una pata horizontal a 90° hacia fuera de la
  sección (lo normal en el arranque sobre la zapata) o hacia el centro, a elegir.
  Necesita prolongación inferior mayor que 0: la patilla queda por debajo de la
  base, dentro de la cimentación (si no, quedaría dentro del hormigón de la columna
  a ras de la base y se rechazaría). El alzado la dibuja a trazos.

## Ganchos

Los estribos y las grapas se crean con estilo *Estribo/atadura* (`StirrupTie`) y el
tipo de gancho (`RebarHookType`) elegido en la ventana en los dos extremos
(normalmente 135°). La ventana ofrece solo los ganchos **de estilo Estribo/atadura**
del proyecto (los de estilo *Estándar* no: Revit no los admite en estribos) y, si al
proyecto le falta algún ángulo, el **catálogo** del plugin lo ofrece igualmente
(`Estribo - 90`, prolongación 6 diámetros; `Estribo - 135`, 6; `Estribo sismico - 135`,
8; `Estribo - 180`, 4): el tipo se crea en el proyecto al armar y se avisa en el
informe. El plugin no sabe de antemano hacia qué lado va a poner Revit el
gancho: crea el primer estribo, lee su geometría real y, si el gancho asoma fuera del
hormigón, lo borra, **invierte la orientación** y lo vuelve a crear (y lo avisa en
el informe). El resto de estribos usan la orientación buena.

## Comprobaciones de seguridad

Igual que en el add-in de muros: **o se arma la columna entera y bien, o no se arma**.

1. **Antes de crear cada barra** se comprueba que su eje, y seis fibras desplazadas
   medio diámetro (cuatro en el plano de la sección para las longitudinales), quedan
   dentro del sólido del elemento (`Solid.IntersectWithCurve`), en todas las
   posiciones del array.
2. **Después de crear y regenerar** se lee la geometría real de cada barra de cada
   conjunto (radios de doblado y ganchos incluidos) y se vuelve a comprobar. Las
   longitudinales se recortan a la altura de la columna para esa comprobación.
3. Cualquier fallo deshace la subtransacción de ese elemento: no queda ni una barra.

El informe final dice, columna a columna, qué se ha creado y por qué se ha rechazado
lo que no.

## Interfaz gráfica

- **Columnas seleccionadas**: forma detectada (rectangular, en L, en T...), tamaño,
  número de estribos, altura y, en rojo, el motivo del rechazo. Cada fila armable
  tiene su **distribución de estribos** y su **separación de longitudinales** propias
  (vacío = valor general). Clic en una fila para verla en los esquemas.
- **Barras longitudinales**: tipo de las de esquina y de las intermedias, reparto
  general, cuadro por línea de la columna seleccionada y de las demás con la misma
  sección (F1, F2..., V1, V2..., cada una con su total y su reparto; botón "mínimo"
  para volver a las obligadas),
  prolongaciones y patilla.
- **Estribos**: tipo, gancho, giro del gancho, distribución, simetría y desfases, y el
  cuadro de **estribos interiores** de la columna seleccionada y de sus iguales, con su
  propio scroll ("Anadir estribo interior" propone el centrado de todo el alto; cada fila
  tiene las cuatro posiciones de barra y "quitar").
- **Grapas**: activar, tipo, gancho y dirección.
- **Recubrimiento y partición**: recubrimiento al estribo y plantilla del parámetro
  Partición (`{categoria}`, `{prefijo}`, `{marca}`, `{id}`, `{codigo}` / `{estribo}`,
  `{tipo}`, `{familia}`, `{conjunto}`), con el ejemplo de la columna seleccionada. Si
  la plantilla no empieza por `{categoria} - {prefijo}-` la ventana avisa de que
  incumple el contrato. El pie muestra la versión del contrato ARBA-comun.
- **Sección**: hormigón, cada estribo con su color y sus ganchos dibujados con el
  ángulo del tipo elegido (90°, 135° o 180°; esquema), las etiquetas F1... y V1...
  de las líneas,
  grapas con sus ganchos y cada barra a su diámetro (rojo oscuro las obligadas,
  naranja las intermedias). Rueda: zoom; arrastrar: mover; doble clic: encajar. Al
  pasar el ratón por una barra o estribo se ve su posición y diámetro.
- **Alzado**: la columna con las longitudinales (prolongaciones a trazos) y cada
  estribo, con la etiqueta de cada tramo (`inf 1@50`, `resto R@250 (=238)`).
- **Guardar como valores por defecto** escribe `config.json`; **Armar** crea las
  barras; **Cancelar** no toca nada.

Sin tipo de barra elegido el esquema se dibuja con diámetros orientativos y el botón
Armar avisa de qué falta.

## config.json

```jsonc
{
  "coverMm": 40,
  "longitudinal": { "barTypeName": "", "intermediateBarTypeName": "",
                    "fillMode": "auto",
                    "bottomExtensionMm": 0, "topExtensionMm": 0, "bottomLegMm": 0, "legDirection": "out" },
  "stirrups":     { "barTypeName": "", "hookTypeName": "135", "hookOrientation": "left",
                    "distribution": "1@50, 5@100, R@250", "symmetric": true, "bottomOffsetMm": 0, "topOffsetMm": 0 },
  "crossties":    { "enabled": false, "barTypeName": "", "hookTypeName": "135", "hookOrientation": "left", "directions": "both" },
  "partitionTemplate": "{categoria} - {prefijo}-{marca}",
  "probeSliceMm": 10, "prismCheckStepMm": 300, "prismCheckToleranceMm": 2, "rectilinearAngleDeg": 0.5
}
```

Los nombres de tipo de barra y de gancho pueden ser exactos o un fragmento
(`"135"`, `"3/8"`; el gancho puede ser también uno del catálogo); sin coincidencia no se
arma, nunca se sustituye por otro tipo
(`NameMatch.First` del común).

## Contrato ARBA-comun

El add-in sigue el contrato 1.0.0 de [ARBA-comun](https://github.com/Andy-rba30/ARBA-comun)
(`external/ARBA-comun/CONTRATO.md`), igual que Zapatas, Cimientos, Bloques, Vigas,
Losas, Muros y el plugin de metrados:

- **Partición** de cada conjunto: `COLUMNAS - COL-{marca}` (categoría del anfitrión,
  prefijo `COL` del add-in, Marca de la columna o su Id si está vacía). Se escribe en el
  parámetro predefinido Partición con respaldo por nombre en inglés y español: antes se
  buscaba solo `"Partition"` y en Revit en español no se escribía nada. Con la plantilla
  `{categoria} - {prefijo}-{marca}-{estribo}` cada estribo tendría su propia partición
  (`COLUMNAS - COL-C3-1`); por defecto el detalle va solo en `ARBA - Código`.
- **Parámetros compartidos** (GUID fijos, grupo Datos, de ejemplar; el comando los
  vincula a las armaduras al empezar, conservando los valores de un parámetro homónimo
  anterior): `ARBA - Origen` = `COLUMNAS`, `ARBA - Código` = `longitudinal`,
  `estribo N` o `grapa`, `Metrado - Elemento` = `COLUMNAS` (es el filtro de la tabla
  "Metrado acero - Columnas" del plugin de metrados).
- **Borrar y rearmar**: si alguna columna elegida ya tiene conjuntos con
  `ARBA - Origen = COLUMNAS`, al pulsar Armar se pregunta una vez: *Borrar la armadura
  del add-in y rearmar* (sin duplicados; si el nuevo armado falla, la columna se deshace
  entera y conserva la anterior) o *Conservar y armar encima*. La armadura manual o de
  otros add-ins no se toca.
- **Migración** de modelos anteriores: si una columna tiene barras `COL-C1` sin
  `ARBA - Origen` (versión anterior del add-in), se ofrece *Migrar sin rearmar*
  (reescribe la partición a `COLUMNAS - COL-C1` y rellena origen, código y
  `Metrado - Elemento` sin crear ni borrar barras), *Migrar, borrar y rearmar* o
  *Conservar y armar encima*. El botón "Migrar particiones y origen" de todo el modelo
  lo aporta el plugin de metrados.
- **Cinta**: pestaña `ARBA`, panel `Acero`, desplegable `Acero`, botón `Columnas`
  (nombre interno `ARBA_Acero_Columnas`) con icono propio; el orden de paneles y el
  desplegable los gestiona `ArbaRibbon` del común, compartido con los demás add-ins.

## Compilar e instalar

Requiere el SDK de .NET 10 y Revit 2027 (los paquetes `Nice3point.Revit.Api.*`
traen las DLL de la API; para Revit 2025/2026 cambia el `TargetFramework` a
`net8.0-windows`, la versión del paquete y `<RevitVersion>`). El código común viene
como **submódulo git**, así que hay que clonarlo con él:

```
git clone --recurse-submodules https://github.com/Andy-rba30/Acero-columnas
# o, en un clon ya hecho:
git submodule update --init
dotnet build -c Release
```

`ColumnRebar.csproj` importa `external/ARBA-comun/Arba.Comun.props`, que compila
`external/ARBA-comun/src/**/*.cs` dentro de `ColumnRebar.dll` (nunca como DLL aparte:
Revit carga todos los add-ins a la vez). Para subir de versión del común:
`git -C external/ARBA-comun checkout v1.x.0` y commit del puntero. No se modifica nada
dentro de `external/ARBA-comun`; lo que le falte se anota en `NOTAS-ARBA-COMUN.md`.

En Debug y en Windows la compilación copia `ColumnRebar.dll`, `config.json` y
`ColumnRebar.addin` a `%AppData%\Autodesk\Revit\Addins\2027\` (fuera de Windows el
paso se omite, pero el proyecto compila igual gracias a `EnableWindowsTargeting`). Al
abrir Revit aparece la pestaña **ARBA** con el panel **Acero** y su desplegable
**Acero**, donde está el botón **Columnas** junto a los de los demás add-ins ARBA
instalados; el comando queda también en Complementos > Herramientas externas.

## Estructura del código

| Archivo | Qué hace |
|---------|----------|
| `Rectilinear.cs` | Geometría pura de polígonos rectilíneos: limpieza, comprobación, rectángulos máximos, nombre de la forma. |
| `ColumnPlan.cs` | Armado de la sección (estribos, barras obligadas e intermedias, grapas, filas para arrays). Pura, compartida por ventana y generador. |
| `StirrupLayout.cs` | Lectura de `1@50, 5@100, R@250` y cotas de los estribos. Pura. |
| `ColumnSection.cs` | Lectura del sólido de Revit: rebanadas, ejes locales, prisma vertical, geometría completa si está unida. |
| `HostAnalysis.cs` | Resultado por elemento (sección o motivo de rechazo) y elecciones por columna. |
| `RebarGenerator.cs` | Crea los `Rebar` con las dos redes de seguridad y la inversión automática de ganchos. |
| `RebarOptionsWindow.cs`, `SectionPreview.cs`, `ElevationPreview.cs` | Ventana y esquemas (WPF en código, sin XAML). |
| `ArmarColumnaCommand.cs` | Comando externo: análisis, ventana, parámetros del contrato, borrar y rearmar, migración, informe. |
| `RibbonApp.cs` | Botón `Columnas` (icono propio) en la cinta `ARBA` compartida. |
| `AppConfig.cs` | Configuración (`config.json`), plantilla de Partición por defecto del contrato. |
| `external/ARBA-comun/src/` | Código común ARBA (submódulo, `namespace Arba.Comun`): `ArbaContract`, `ArbaPartition`, `PartitionName`, `ArbaOrigin`, `ArbaSharedParams`, `ArbaMigration`, `ArbaRibbon`, `RevitTheme`, `NameMatch`. |

Las clases puras (`Rectilinear`, `ColumnPlan`, `StirrupLayout`) no dependen de Revit
y se pueden probar en un programa de consola.
