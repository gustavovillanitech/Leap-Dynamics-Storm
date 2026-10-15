# Tareas Storm: sesión con Ray 2026-10-13

Fuentes cruzadas: video `2026-10-13 10-00-59_NewTasksFromStorms.mp4` (transcript completo en `TranscriptMeeting-NewTasksStorms_20261013.txt`), correo recap de Ray (13-10), resumen Zoom de la misma reunión, correo de Christine "Storm Dashboard Info, Guidelines & Questions" (07-10), hilo "Couple Questions for Today" (05-10 → 07-10) y `WNBA - End of Season 2026 TPRR Submission Template_BLANK - Working Copy.xlsm`.

> Regla vigente (§31.1): presupuesto ~50 h/mes. Antes de construir, mandar estimado a Ray (correo listo al final).

---

## A. Tus tareas, en orden de prioridad

Ray en 10:41-11:11: "let's start with the other items... work on those first, and then we'll start on the dashboards and reports". Las P1 son las que discutieron al inicio.

### P1. Cambios chicos de configuración (hacer primero)

| # | Tarea | Dónde | Min. video | Est. |
|---|---|---|---|---|
| 1 | **Max Activation Spend = 8 % del Total / Net Total, siempre** | `new_DealConfiguration.new_MaxActivationSpendPercent` + deals con `new_maxactivationspendpercent` congelado | 05:21-06:02 | 1 h |
| 2 | **Campo `Trade Value`** (currency) en la sección *Financial & Contract* del form de Deals | Tabla `new_deals`, form principal, `SectionFinancialContract` | 06:09-07:25 | 0,5 h |
| 3 | **Vista Active Deal Lines**: quitar columna Inventory, poner Notes | Vista de `new_deallines` usada por el subgrid del form de Deals | 03:27-05:12 | 0,25 h |
| 4 | **Appointment Type** (choice, obligatorio) con 4 valores | Tabla `appointment`, form principal + quick create | 21:47-22:02 | 0,5 h |
| 5 | **Timeline de Account = mismos tipos de actividad que el de Opportunity** | Form principal de Account → control Timeline → "Activity types" (y/o tablas de actividad incluidas en la app) | 23:17-23:53 | 0,5 h |
| 6 | **Merge `Alexa` → `Amazon Alexa`** (Amazon Alexa queda como master) | PROD, merge de cuentas (mismo patrón que Kia, §26) | Solo en el recap | 0,5 h |
| 7 | **Ítem compartido entre dos paquetes se agrega dos veces** | Canvas Deal Line Builder, `btnAddPackage` / armado del carrito | 02:13-03:15 | 1-1,5 h |

**Detalle y riesgos**

1. **8 %.** Hoy el cálculo usa primero el % congelado del deal (plugin `FreezeMaxActivationSpendPercent` al pasar a Closed Won, DS-1008) y si no hay, el % global. "Always 8 % across the board" choca con ese congelado.
   - Verificar el valor actual del % global y cuántos deals tienen un % congelado ≠ 8.
   - ❓ **Decisión para Ray:** ¿los Closed Won con otro % congelado también pasan a 8 %? Si sí: bulk update de `new_maxactivationspendpercent` = 8 y recalcular `new_maxactivationspend`. Si "siempre 8" es definitivo, el freeze sobra; no lo quites sin que Ray lo confirme.
   - Plugin (`InventoryManagement.CalculateMaxActivationSpend`) y JS (`DealForm.calculateMaxActivationSpend`) no se tocan si basta con los datos.
2. **Trade Value.** Solo mostrar el campo: no resta del Total (Ray 07:00-07:15, "this still stays the same amount"). Tipo currency.
   - Conexión: la plantilla TPRR separa `CASH REVENUE` y `TRADE REVENUE`. Ray metió el Total del deal en cash y Christine resta el trade a mano (13:36-13:56). Con este campo, el próximo TPRR sale directo: Cash = Total − Trade Value.
3. **Vista deal lines.** Contexto: el nombre de la deal line ya lleva el paquete, así que Inventory repite lo mismo. Notes queda en la grilla para que no editen el nombre a mano (04:08-04:30). El lookup a Inventory se queda dentro del registro (05:00).
4. **Appointment Type.** Primero revisar si ya existe un campo de tipo en `appointment` (Ray cree que no). Valores, copiar tal cual:
   - `In-person – BECU Storm Center`
   - `In-person – Client/Prospect office`
   - `In-person – other`
   - `Virtual Teams/Zoom Call`

   Business Required solo se valida en el form. Las citas que vengan del sync de Outlook entran vacías, y eso no es un bug.
5. **Timeline.** Ray: "at the app level removing the activity types in the timeline". ❓ No se ve qué tipos sobran. Hace falta captura del **min 23:30-23:45** (timeline de Account vs Opportunity).
   - No requiere build: notas largas se suben como **Note + adjunto** (22:10-23:11). Upload de archivos ya mostrado por Ray.
6. **Merge Alexa.** Antes del merge:
   - Listar deals, oportunidades y contactos de `Alexa`.
   - Ojo con `Alexa - 2026 - Storm` (`cb0e3e38…`), que `PROJECT_STATE` marca como posible deal real con líneas borradas.
   - Después del merge, confirmar que `Amazon Alexa - 2026` ($300k, Closed Won) no quede duplicado con ese deal.
   - Amazon Alexa está en el TPRR como partner, así que el nombre del master es el correcto.
7. **Duplicado por paquete.** En tu correo del 07-10 dijiste que el builder agrega el ítem una sola vez, pero en 03:04 le respondiste a Ray que no hay lógica que lo impida. Las dos cosas no pueden ser ciertas: hay un dedupe en el carrito (probablemente un `Distinct`/`LookUp` por inventario al juntar componentes). Encuéntralo.
   - Verificar también que dos líneas con el mismo inventario en un deal no rompan `SetName` ni el conteo de inventario.
   - ⚠ No está en el recap por correo de Ray, pero sí en el video y en el Zoom. Inclúyelo en el estimado para que quede aprobado.

### P2. Documentación

| # | Tarea | Para quién | Min. | Est. |
|---|---|---|---|---|
| 8 | **Write-up: handover de ventas a servicio** con una oportunidad de servicio por año | Ray (nota corta, EN) | 24:40-31:29, 36:19-38:16 | 1 h |

Lo acordado:
- Una oportunidad por año = un negocio por año (37:04-38:16). Ray confirma que es lo correcto: cada año tiene su ciclo (renovación, recap, playoff option).
- En el deal, **Originating Opportunity** = la venta original y **Opportunity** = la oportunidad de servicio de ese año (30:28-31:01).
- Proceso: crear la service opportunity del año y asociarle el deal de ese año.
- ❓ Para que la nota sea correcta, falta revisar qué asigna `CloneMultiYearDeals` en `Opportunity` y `Originating Opportunity` a los deals 2027+. Si los clones apuntan a la oportunidad original, Christine tiene que re-apuntarlos a mano cada año. Si es así, la nota tiene que decirlo, o proponer automatizarlo y estimarlo aparte.

### P3. Cuando Ray lo destrabe

| # | Tarea | Estado |
|---|---|---|
| 9 | **Plugin: renombrar deal lines cuando se renombra el inventario** | "Not super urgent". Antes estaba en espera (3 h). |
| 10 | **Views/dashboards por cada bullet de reporting de Christine**, con un link por bullet | Lo lidera Ray (10:41: "let me work through this"). Entras después de P1/P2. Sin Power BI (licencias). |

- ⚠ **#9 y el package lookup.** El recap de Ray pide el package lookup como pendiente, pero **ya está en PROD desde el 07-10** (tu correo, punto 5). Díselo.
- Lo que sí falta para el plugin es un **backfill del lookup `Package` en las deal lines creadas antes del 07-10**. Sin eso, el rename no puede preservar el nombre del paquete en las líneas viejas. Súmalo al estimado de #9.

---

## B. Tareas de Ray (no tuyas, pero te pueden caer)

- **TPRR** (entrega a la liga el **jueves 22-10**). Ray lo trabaja en el *Working Copy*:
  - Hojas `Closed Won 2026` (37 deals) y `Deal Lines` (827 líneas, export SQL).
  - **Christine** llena: nombres completos de partner, WNBA Category faltante, fechas de contrato, retention status y la columna `WNBA Collection` de `Deal Lines` (hoy vacía, 0/827).
  - Después Ray arma las fórmulas SUMIF por deal y por categoría y copia los valores a la plantilla limpia. El working copy no se puede entregar con las pestañas extra.
  - ⚠ El resumen Zoom dice que *tú* llenas la WNBA collection por línea. **Es incorrecto**: en 15:01-15:32 y 16:59-17:11 Ray dice que se lo pidió a Christine.
  - ⚠ Para Ray: 9 líneas tienen `new_collectionname = NULL` y no van a caer en ningún bucket.
- **Campo WNBA Collection en `new_product`** (17:25-18:08) para no traducir dos veces al año. Está a nombre de Ray en el Zoom, pero si se crea probablemente la carga la hagas tú. Recordar §memoria: los campos espejo viven en `new_product`, no en `new_inventory`.
- **Clonar deal a otra cuenta** (20:41-21:44): Ray lo despriorizó ("not spend development time on this"). Solo pensarlo.
- **Plantilla Excel de pricing grid** desde Dynamics (19:11-20:14): baja prioridad, no ahora.
- **Trak**: Ray quiere hablarlo más adelante esta semana (40:42).
- **Otros clientes**: Charlotte Hornets (2 meses de limpieza: uso de campos y merges de contactos con XrmToolBox) y JS para Dream. Rebecca vuelve el jueves. Ray te puede pedir ayuda.

## C. Cerrados sin acción

- **13 Coins** (00:43-01:06): Ray pregunta "can you follow up with them", es decir, con Storm, si la línea va al ítem combo. ❓ ¿Le escribes tú a Nate/Christine o se lo redactas a Ray? Normalmente Ray es el interlocutor.
- **Cantidad del LED combo** (01:32-02:07): "she knows that one, don't worry".
- **Ítems sin rate**: siguen del lado de Storm.
- **Notas largas de reuniones**: Note + adjunto, no se construye nada.

---

## D. Lo que no encontré en el audio

- **"Deal line escalator functionality"**: no aparece en ningún momento del audio. Ray estuvo en espera de 31:31 a 35:59 (silencio total), sin conversación. Si lo mostró en pantalla o lo resaltó en un correo sin hablarlo, pásame la captura con el minuto.
- **Merge de Alexa**: solo está en el recap por correo, no en el audio.

---

## E. Correo de estimado para Ray (copiar y pegar)

```
Hi Ray,

Thanks for the recap. Estimates before I start, including testing:

- Max Activation Spend at 8% of Total: 1h
- Trade Value field on the Deal form: 0.5h
- Notes column instead of Inventory on Active Deal Lines: 0.25h
- Appointment Type field (required): 0.5h
- Account timeline activity types to match the opportunity: 0.5h
- Merge Alexa into Amazon Alexa: 0.5h
- Add a shared item once per package in the Deal Line Builder: 1-1.5h
- Sales-to-service handover write-up: 1h

Total: 5.25-5.75h

Two notes:
- The package lookup on the deal line is already live in production (Oct 7). For the deal line rename plugin we would also need to backfill the package on lines created before Oct 7. I can estimate that when we pick it back up.
- Max Activation Spend: closed won deals keep the percentage that was active when they closed. Should those also move to 8%, or only open deals?

Thanks,
Gustavo
```
