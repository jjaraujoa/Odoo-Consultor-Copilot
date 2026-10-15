# Esquema de paquete de réplica

## Estructura mínima

```text
replicas/<slug>/
├── README.md
├── manifest.json
└── acciones/                 # solo si hay código
    └── <nombre>.py
```

No añadir datos de negocio, adjuntos, binarios, secretos ni identificadores de usuarios.

## `manifest.json`

Usar JSON UTF-8 con esta estructura mínima:

```json
{
  "schema_version": 2,
  "name": "slug_tecnico",
  "description": "Qué comportamiento preserva.",
  "source": {
    "environment": "staging",
    "odoo_version": "19",
    "captured_on": "YYYY-MM-DD",
    "contains_business_records": false,
    "metadata_hashes": {}
  },
  "dependencies": {"models": [], "features": []},
  "fields": [],
  "views": [],
  "actions": [],
  "deployment_order": [],
  "excluded_artifacts": ["all_business_records"]
}
```

El motor conserva lectura compatible del esquema 1. Un manifiesto de esquema 2
sin `phase` mantiene el contrato de Fase 1. Una app nueva de Fase 2 debe usar
expresamente `schema_version: 2`, `phase: 2` y los grupos `models`, `fields`,
`views`, `window_actions` y `menus`. Una réplica de automatizaciones debe usar
`schema_version: 2`, `phase: 3` y los grupos `computed_fields`,
`mail_templates`, `server_actions`, `views` y `automations`.
Una réplica de gobierno debe usar `schema_version: 2`, `phase: 4` y los grupos
`qweb_views`, `reports`, `approvals`, `access_controls`, `record_rules` y
`negative_access_tests`.

El esquema 2 usa claves lógicas estables, `mode` (`create`, `update` o
`deactivate`), dependencias, referencias XML ID o marcadores declarados,
archivos y hashes. `server_action` sigue aceptado por compatibilidad para la
acción cerrada de Fase 1. Automatizaciones y efectos ejecutables pertenecen a
la Fase 3; reportes, aprobaciones y seguridad pertenecen a la Fase 4.

### App Fase 2

Estructura mínima:

```json
{
  "schema_version": 2,
  "phase": 2,
  "name": "slug_tecnico",
  "description": "Estructura de la app.",
  "source": {
    "environment": "staging",
    "odoo_version": "19",
    "captured_on": "YYYY-MM-DD",
    "contains_business_records": false
  },
  "dependencies": {"models": [], "features": ["studio"]},
  "models": [],
  "fields": [],
  "views": [],
  "window_actions": [],
  "menus": [],
  "deployment_order": ["models", "fields", "views", "window_actions", "menus"],
  "excluded_artifacts": ["all_business_records"]
}
```

Cada componente declara una `key` técnica estable. Modelos, campos, acciones y
menús usan `mode`; las vistas usan `operation` porque `mode` tiene significado
propio en `ir.ui.view`. `update` y `deactivate` requieren un `previous_hash`
SHA-256 calculado sobre el snapshot normalizado que devolvió el análisis del
destino.

Los modelos deben comenzar por `x_`. Los tipos de campo admitidos son `char`,
`text`, `html`, `integer`, `float`, `monetary`, `date`, `datetime`, `boolean`,
`binary`, `selection`, `many2one`, `one2many` y `many2many`. Incluir
`relation_field` en One2many, moneda en `currency_field` y tabla/columnas en
Many2many. Un cálculo nuevo usa `compute_file`, `compute_sha256` y `depends`.

Una vista base solo puede dirigirse a un modelo `x_`. Para un modelo estándar,
declarar `inherit_view_xml_id` o `inherit_view_name`. El XML puede estar en
`arch_template` o en `arch_file` con `arch_sha256`; no admite botones
ejecutables, scripts ni QWeb.

Las acciones de ventana declaran `key`, `name`, `res_model` y `view_mode`. Los
menús pueden usar `parent_key`, `parent_xml_id` o `parent_reference`, y enlazar
una acción declarada mediante `action_key`. El padre lógico debe aparecer antes
que el hijo.

La Fase 2 no crea ACL, reglas, registros de negocio, reportes ni automatizaciones.
Un cambio de tipo, relación o tabla solo puede aplicarse si el campo no contiene
valores y queda marcado con reversión manual; con datos requiere una migración.

### Automatizaciones y efectos Fase 3

Estructura mínima:

```json
{
  "schema_version": 2,
  "phase": 3,
  "name": "slug_tecnico",
  "description": "Comportamiento automatizado sin datos de negocio.",
  "source": {
    "environment": "staging",
    "odoo_version": "19",
    "captured_on": "YYYY-MM-DD",
    "contains_business_records": false
  },
  "dependencies": {"models": [], "features": ["base_automation"]},
  "references": {},
  "computed_fields": [],
  "mail_templates": [],
  "server_actions": [],
  "views": [],
  "automations": [],
  "deployment_order": [
    "computed_fields",
    "mail_templates",
    "server_actions",
    "action_chains",
    "views",
    "automations",
    "automation_bindings",
    "activation_gates"
  ],
  "excluded_artifacts": ["all_business_records"]
}
```

Las referencias usan claves `MAYUSCULAS_TECNICAS` y tipos cerrados `email`,
`https_url` o `xml_id`. Solo `xml_id` declara `model`. El manifiesto guarda el
marcador `{{CLAVE}}`, nunca el correo, URL o ID real; el valor se entrega en
memoria al aplicar y no se conserva en el paquete ni en la auditoría.

`computed_fields` solo actualiza campos Studio `x_` ya existentes. Requiere
`mode: update`, `previous_hash`, `compute_file`, `compute_sha256` y `depends`.
La creación de un campo calculado corresponde a Fase 2.

Cada plantilla declara `key`, `name`, `model` y `mode`. Excepto al desactivar,
debe incluir `subject_file`/`subject_sha256` y
`body_html_file`/`body_html_sha256`. `email_to` y `email_cc` solo aceptan una
referencia tipada de correo; no incluir destinatarios literales.

Los estados de `server_actions` admitidos son `code`, `multi`,
`next_activity`, `mail_post` y `webhook`. El código, las notas y cualquier otro
contenido ejecutable se guardan en archivos firmados. Las acciones `multi`
usan `child_action_keys`; correo usa `template_key`; actividades resuelven sus
tipos y usuarios mediante referencias XML ID; webhooks salientes usan una
  referencia HTTPS y una lista cerrada de campos sin secretos.

Cada elemento de `views` declara `key`, `name`, `model`, `mode`, un único padre
mediante `inherit_view_name` o `inherit_view_xml_id`, `arch_file`,
`arch_sha256` y `action_key`. El XML firmado debe contener uno o más botones
`type="action"` con `name="{{ACTION_ID}}"`. El motor sustituye ese marcador
solo por la acción declarada en la misma réplica. `type="object"`, IDs fijos y
acciones externas al paquete están bloqueados.

Los disparadores admitidos son los declarados por Odoo 19 y validados por el
motor, incluidos altas/cambios, tiempo, mensajes y `on_webhook`. Dominios y
`record_getter` se guardan como archivos firmados. Cada automatización declara
acciones del mismo modelo. `on_change` solo admite acciones `code`; `on_unlink`
no admite correo ni actividades. No crear ciclos ni compartir una acción entre
dos automatizaciones.

La aplicación deja inactivas las automatizaciones con correo o webhooks. Si se
pretende activarlas después de la autorización, declarar `install_active:false`,
`activation_mode:"gated"` y `desired_active_after_authorization:true`. La
prueba saliente usa `PROBAR-EFECTO <id>` y la activación usa
`ACTIVAR-EFECTOS <id>`. Un webhook entrante exige además
`PRUEBA-MANUAL <id>`. Producción siempre genera runbook.

### Reportes, aprobaciones y seguridad Fase 4

La Fase 4 exige Odoo 19 y usa este orden cerrado:

```json
{
  "schema_version": 2,
  "phase": 4,
  "references": {},
  "qweb_views": [],
  "reports": [],
  "approvals": [],
  "access_controls": [],
  "record_rules": [],
  "negative_access_tests": [],
  "deployment_order": [
    "qweb_views",
    "reports",
    "approvals",
    "access_controls",
    "record_rules",
    "negative_access_tests"
  ]
}
```

Las referencias usan claves mayúsculas y tipo `xml_id` o `user_login`. Los
modelos permitidos son `res.groups`, `res.users`, `report.paperformat`,
`ir.actions.actions` e `ir.actions.server`. El valor real se entrega en memoria
al aplicar y no se persiste.

Cada `qweb_view` declara `key`, `name`, `template_name` con prefijo `odooai.`,
`mode`, `arch_file` y `arch_sha256`. Se bloquean scripts, iframes, atributos de
evento, `t-js`, `t-raw`, `t-debug`, `t-log` y URLs ejecutables. Cada `report`
apunta mediante `template_key` a una plantilla de la misma réplica y puede
resolver formato, grupos, nombre de impresión, adjunto y dominio mediante
referencias o archivos firmados.

Cada `approval` declara el modelo y exactamente uno entre `method` y
`action_ref`, además de usuarios o grupo aprobador. El motor descubre
`studio.approval.rule` y sus campos en la base antes de habilitar la operación;
no asume que Enterprise tenga el mismo contrato en todas las revisiones.

Las ACL usan `access_controls` y las reglas usan `record_rules`. Cada componente
declara un grupo o `global:true`, nunca ambos. Los dominios de reglas se guardan
como listas JSON firmadas. Todo cambio de seguridad debe acompañarse de
`negative_access_tests`, con operación `read`, `create`, `write` o `unlink` y
resultado esperado. Las pruebas se ejecutan con un perfil funcional separado
registrado en Credenciales de Windows; solo se devuelve permitido/no permitido
y, opcionalmente, un conteo limitado.

`update` y `deactivate` exigen `previous_hash`. `REVERTIR <id>` restaura valores
previos protegidos o desactiva componentes creados; nunca elimina metadatos.
Producción siempre genera runbook sin contactar Odoo.

### Campos

Cada elemento de `fields` debe declarar al menos `model`, `name`, `label` y `type`. Incluir cuando corresponda `relation`, `relation_table`, `column1`, `column2`, `domain`, `required`, `readonly`, `stored`, `copied`, `indexed`, `selection`, `filename_field`, `widget` y `options`.

Cada opción de `selection` declara `sequence`, `label` y `value`. Preservar sus valores técnicos y orden.

### Vistas

Cada vista debe declarar `model`, `type`, `mode`, `inherit_view_name`, `priority`, `active` y `arch_template`. Declarar cada marcador presente en XML dentro de `placeholders`, por ejemplo:

```json
"placeholders": {
  "{{SERVER_ACTION_ID}}": "ID asignado por la base de destino"
}
```

El XML debe ser válido. Una acción de tipo `action` no puede contener un ID numérico de origen: usar un marcador declarado.

### Acciones y automatizaciones

Cada entrada en `actions` debe tener `name`, `model`, `state`, `risk_class`, `deployment_method`, `source_file` y `source_sha256`. Para una acción cerrada de Fase 1 en staging usar `deployment_method: "validated_replica"`; las demás usan `manual_runbook`. La ruta es relativa al directorio de la réplica y su SHA-256 es el del archivo en UTF-8 con finales de línea normalizados a LF.

No modificar el código capturado. Guardar recomendaciones en el README o en una propuesta separada.

### IDs y evidencia de origen

No usar IDs numéricos de origen como referencias de destino. Si se necesita conservarlos para trazabilidad, ubicarlos dentro de `source_evidence` junto con una descripción, nunca en `views`, `actions` o componentes aplicables.

### Exclusiones y pruebas

`excluded_artifacts` debe ser una lista no vacía que incluya explícitamente los datos de negocio excluidos. Documentar las pruebas de aceptación y verificaciones en el README y, si se requiere procesamiento automático, en `acceptance_tests`.
