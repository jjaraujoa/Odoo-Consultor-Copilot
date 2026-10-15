# Demostraciones comerciales esquema 2

> Documento histórico de compatibilidad. OdooConsultor 0.10 prepara nuevos
> escenarios schema 4 con registros reales de staging, nombres visibles limpios
> e identidad DPAPI/XML ID invisible. Los prefijos descritos aquí solo aplican a
> escenarios heredados y no deben usarse en demos nuevas.

## Resultado

OdooConsultor 0.8.7 amplía el ciclo de demostraciones sin ampliar el lenguaje
de llamadas a Odoo. Codex describe contenido comercial mediante un contrato
cerrado; el motor comprueba módulos, metadatos, permisos y selecciones y lo
compila en operaciones concretas. No existe una herramienta RPC/JSON-2 genérica.

```text
contenido simple + enums
        ↓
adaptador cerrado y allowlists
        ↓
vista previa + hashes + source lock
        ↓  CREAR-DEMO <id>, máximo 12 horas
create/search_read/read/write cerrados en staging
        ↓
verificación por campos esperados
        ↓  LIMPIAR-DEMO <id>
despublicar/desactivar; nunca unlink
```

## Adaptadores

| Adaptador | Modelo técnico | Estado inicial | Efecto permitido | Limpieza |
|---|---|---|---|---|
| `website_landing_unpublished` | `website.page` | no publicada, no indexada | crear bloque predefinido con formulario exclusivo a `crm.lead` | despublicar y desactivar |
| `survey_draft` | `survey.survey` | inactiva, acceso por invitación | crear encabezado de encuesta | desactivar |
| `survey_section` | `survey.question` | contenida por encuesta | crear sección ordenada | contención por padre y revisión manual |
| `survey_question` | `survey.question` | contenida por encuesta | tipos enumerados, orden y obligatoriedad | contención por padre y revisión manual |
| `survey_answer` | `survey.question.answer` | contenida por encuesta | opciones y columnas de selección | contención por padre y revisión manual |
| `survey_matrix_row` | `survey.question.answer` | contenida por encuesta | filas de matriz validadas | contención por padre y revisión manual |
| `mailing_draft` | `mailing.mailing` | borrador | plantilla visual, A/B, UTM y lista o modelo objetivo | desactivar |
| `marketing_campaign_inactive` | `marketing.campaign` | inactiva y borrador | modelo `crm.lead` o `mailing.contact`; DSL de audiencia cerrado | desactivar |
| `marketing_activity_inactive` | `marketing.activity` | contenida por campaña inactiva | email en borrador, espera y rama por apertura/clic | contención por padre y revisión manual |

Se conservan los adaptadores UTM, CRM, lista y contacto de mailing del escenario
versión 1. El motor acepta escenarios 1 y 2, pero solo prepara nuevos escenarios
como versión 2.

## Contrato de entrada

`demo_preparar_escenario` acepta opcionalmente `configuracion` con cinco
secciones: `utm`, `website`, `survey`, `mailing` y `automation`. Cada sección tiene
`additionalProperties: false` en MCP. Los parámetros desconocidos se rechazan.

- UTM: `source_name`, `medium_name`, `campaign_name`, `lead_name` y
  `contact_name`; el motor conserva siempre el prefijo `[DEMO:...]`.
- Website: texto simple, beneficios, texto legal y layout `hero_form` o
  `split_form`. El motor genera el QWeb/HTML fijo; no acepta arquitectura libre.
- Survey: tipos `simple_choice`, `multiple_choice`, `text_box`, `char_box`,
  `numerical_box`, `scale`, `date`, `datetime` y `matrix`. Una matriz exige al
  menos dos columnas y dos filas.
- Mailing: asunto A/B, preheader, encabezado, cuerpo, CTA, porcentaje, URL de
  imagen exclusivamente bajo `https://example.invalid/` y variable cerrada
  `recipient_name`.
- Automation: modelo `crm.lead` o `mailing.contact`, filtro
  `all_demo_records` o `has_email`, espera y rama enumerada por apertura o clic.

Los dominios resultantes son compilados internamente. Ningún dominio, método,
modelo, endpoint, XML, JavaScript o expresión llega libremente desde el modelo.

## Escenario versión 2

Además de los datos de versión 1, conserva:

- huella de credencial, serie Odoo y versión del catálogo de adaptadores;
- hash del archivo `source-lock.json`, módulos y capacidades efectivas;
- plan de referencias portables, sin IDs persistidos;
- estados iniciales, resultados esperados y campos comerciales;
- procedimientos de verificación y limpieza;
- efectos externos bloqueados y operaciones manuales.

Los IDs creados o recuperados y la marca `created_by_scenario` permanecen dentro
de un snapshot DPAPI. La limpieza omite registros reutilizados. Los componentes
sin campo de archivo seguro quedan identificados y contenidos por su encuesta o
campaña inactiva.

## Garantías antes de escribir

La aplicación comprueba, en este orden, staging, texto exacto de confirmación,
vigencia de 12 horas, huella del perfil, integridad del escenario, archivo de
fuentes, versión real de Odoo, estados de módulos, permisos, allowlists y hashes
de capacidades. Después de cada creación guarda inmediatamente la referencia
protegida; un reintento continúa desde el último componente confirmado. Proarca
Odoo 18 rechazó la sección visual tanto sin tipo como con
`question_type=false`; por compatibilidad, 0.8.2 omite `survey_section` en la
serie 18 y asocia las preguntas directamente a la encuesta. Una verificación
incompleta conserva `failed_partial` para no bloquear la recuperación.
Si la ventana vence en ese estado y aún hay componentes `planned`, el consultor
puede volver a escribir la frase exacta. El motor revalida perfil, fuentes,
versión, módulos y capacidades antes de renovar 12 horas y no repite registros
protegidos. La confirmación antigua nunca se reproduce automáticamente; los
escenarios aplicados o verificados vencidos continúan bloqueados.
La 0.8.3 conserva `vals_list` como lista para `survey.question` y
`survey.question.answer` en el RPC heredado, tal como exige el contrato
`model_create_multi`, y evita el `TypeError` de la creación individual.
Como Proarca también rechazó la creación completa con ese contrato, la 0.8.4
crea primero encuesta, título y secuencia y configura después tipo, obligatoriedad
y demás campos técnicos. El ID intermedio queda cifrado y protegido para reanudar.
El diagnóstico estructural real mostró que la llamada RPC directa a
`SurveyQuestion.create` pierde `vals_list`. La 0.8.5 usa el flujo nativo del
formulario: un comando `(0, 0, valores)` sobre
`survey.survey.question_and_page_ids`, seguido de resolución única por marcador
y encuesta. No se expone un método arbitrario.
La prueba real de 0.8.5 llegó hasta `marketing.activity`. En Proarca este modelo
exige `source_id` y `validity_duration_type`; 0.8.6 usa la fuente UTM sintética
del escenario y el valor técnico `days`, comprobado contra `fields_get`. Además,
el verificador normaliza los tipos numéricos para no confundir `0` con `0.0`.

Todos los nombres técnicos o visibles usados como identidad comienzan por
`[DEMO:...]`. Todo correo termina en `example.invalid`. Website permanece no
publicado, Surveys no distribuye invitaciones, Email Marketing queda en borrador
y Marketing Automation permanece inactivo. No hay rutas de envío, programación,
publicación, activación, acción de servidor, código, webhook, SMS o WhatsApp.

## Evidencia de pruebas

El motor actual supera 99 comprobaciones internas y el contrato MCP conserva 33
herramientas. El ciclo integral simulado verifica:

- creación en staging e idempotencia;
- confirmación incorrecta y vencida;
- cambio de source lock y permisos/capability hash;
- prefijos, correos, referencias y contenido escapado;
- landing no publicada, encuesta inactiva, mailings en borrador y automatización inactiva;
- fallo parcial en la tercera creación y reanudación sin repetir las anteriores;
- verificación por lectura, DPAPI, auditoría redactada y limpieza sin `unlink`;
- producción bloqueada y compatibilidad de esquema 1/2.

El 14 de agosto de 2026 se comprobó en Proarca staging Odoo 18 Enterprise la
existencia de los modelos y campos principales, sus selecciones y permisos. No
se ejecutaron escrituras en esa validación inicial. Una aplicación real posterior
creó y verificó 6 componentes antes de detenerse en `survey_section`. La 0.8.1
envió el valor oficial `question_type=false`, pero la base volvió a rechazarlo.
La 0.8.2 omite esa sección opcional en Odoo 18. La 0.8.3 mantiene las preguntas,
usa su contrato de creación múltiple y permite que el escenario parcial continúe
sin repetir los seis registros protegidos.
La aplicación final posterior quedó `verified` con 40 componentes, una omisión
esperada de `survey_section` en Odoo 18 y cero pendientes o efectos externos. La
revisión visual completa y la limpieza real siguen pendientes.

## Limitaciones

- La landing usa dos layouts predefinidos; no es un constructor visual general.
- La imagen de mailing solo admite un recurso no entregable en esta fase.
- No se envía el formulario: el consultor lo prueba manualmente con datos
  ficticios y el agente verifica después.
- Las preguntas, respuestas y actividades no tienen un archivo directo seguro
  en Odoo 18; quedan contenidas por el padre inactivo y se reportan para revisión.
- En Odoo 18 no se crea una página/sección visual dentro de Survey; las preguntas
  quedan directamente bajo la encuesta. Odoo 19 conserva el adaptador de sección.
- La compatibilidad Odoo 19 se conserva a nivel contractual, pero debe revalidarse
  contra una base 19 real porque metadatos y campos opcionales pueden variar.
