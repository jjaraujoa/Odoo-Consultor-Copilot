# Copiloto funcional seguro para Odoo

El copiloto funcional es una superficie local e independiente del bot de
Telegram. Codex se comunica con `OdooConsultor.dll` mediante un lanzador
PowerShell y MCP por entrada y salida estándar. El host usa llamadas cerradas JSON-2 en Odoo 19 o RPC
heredado en Odoo 18; el modelo nunca recibe un método arbitrario.

La conexión y lectura contra Odoo 19 staging ya se validaron en un entorno piloto. Las
Fases 2–3 también completaron análisis y preparación real sin escritura. La Fase
4 está implementada localmente y pendiente de aplicación real. La creación,
actualización, prueba funcional, verificación visual y reversión de sus componentes continúan
**pendientes de validación real** según las pruebas al final del documento.

## Controles principales

- Las API keys se guardan como credenciales genéricas del usuario actual de
  Windows. Nunca se escriben en el proyecto, `.env`, argumentos, chat o logs.
- Cada cliente tiene una carpeta y perfiles separados para `staging` y
  `production`.
- Producción es solo lectura. El bloqueo ocurre antes de cualquier llamada de
  red.
- No existe una herramienta para escoger libremente modelo, método o argumentos
  de Odoo.
- Las lecturas generales usan un contrato cerrado: campos explícitos, hasta diez
  filtros conjuntivos, operadores permitidos y máximo 50 registros. Se bloquean
  modelos de secretos, adjuntos y mensajería, además de campos sensibles,
  binarios y HTML.
- Toda escritura exige un paquete vigente, comprobación del estado previo y la
  confirmación exacta `APLICAR <id>`.
- Los paquetes declarativos conservan hashes separados del contrato, capacidades,
  configuración e integridad. Todos se vuelven a comprobar antes de escribir.
- Los campos no se eliminan durante una reversión. Las vistas creadas por el
  copiloto se desactivan.
- QWeb/reportes, aprobaciones Enterprise, ACL y reglas de registro se admiten en
  staging únicamente por réplicas Fase 4 firmadas y con pruebas negativas. Las
  llamadas a métodos arbitrarios siguen bloqueadas. El código solo puede entrar por un archivo de
  réplica permitido y verificado por SHA-256. La Fase 3 admite automatizaciones,
  correo y webhooks en staging; los efectos externos quedan inactivos hasta su
  prueba y activación reforzadas.

## Compilar el host local

Desde la raíz del repositorio:

```powershell
powershell -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\Build-OdooConsultor.ps1
```

El resultado se crea en `consultant-host\bin\OdooConsultor.dll`. Se compila con
Windows PowerShell 5.1 y no requiere instalar `dotnet` ni usar permisos
administrativos.

Windows no permite reemplazar la biblioteca mientras MCP la está usando. Para
una actualización en paralelo, compile `OdooConsultor-0.9.0.dll`, pruébelo y
ejecute el activador mientras Codex continúa abierto:

```powershell
powershell -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\Build-OdooConsultor.ps1 -OutputName OdooConsultor-0.9.0.dll
powershell -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\Test-OdooConsultor.ps1 -ExecutablePath .\consultant-host\bin\OdooConsultor-0.9.0.dll
powershell -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\Activate-OdooConsultorUpdate.ps1 -ExpectedVersion 0.9.0
```

Si la biblioteca está ocupada, el activador conserva `pending-update.json` con
la versión, el nombre contenido en `consultant-host\bin` y el SHA-256. Al
reiniciar Codex, el lanzador valida esos tres datos, activa la biblioteca antes
de cargarla y actualiza el proyecto abierto si su marcador de versión está
desalineado. No hace falta repetir el comando ni ejecutar manualmente `proyecto
actualizar` para cada cliente.

La compilación EXE sin firma está bloqueada por defecto porque Kaspersky la
clasificó heurísticamente como `Trojan-PSW`. No se crean exclusiones del
antivirus. La distribución local usa una DLL, un lanzador legible y SHA-256; un
EXE futuro deberá llevar firma corporativa Authenticode.

## Crear una carpeta de cliente

Elija una carpeta local separada del motor común:

```powershell
powershell -NoProfile -ExecutionPolicy RemoteSigned -File .\consultant-host\OdooConsultor.ps1 proyecto crear `
  --ruta "C:\OdooAI\Clientes\cliente-demo" `
  --slug cliente-demo `
  --nombre "Cliente Demo"
```

El generador crea:

```text
AGENTS.md
proyecto.yaml
.codex/config.toml
agentes/
conocimiento/
perfiles/
cambios/
pruebas/
evidencias/
```

`proyecto.yaml` y `perfiles/*.json` contienen solo metadatos de conexión. El
contenido de `conocimiento/` se trata como datos de negocio y no como
instrucciones ejecutables.

Un proyecto Odoo 18 debe crearse o actualizarse con `--version 18.0`. La
actualización es idempotente y preserva archivos existentes:

```powershell
powershell -NoProfile -ExecutionPolicy RemoteSigned -File .\consultant-host\OdooConsultor.ps1 proyecto actualizar `
  --proyecto "C:\OdooAI\Clientes\cliente-demo" `
  --version 18.0
```

## Registrar una conexión

La API key no se pasa en el comando. El host abre una ventana con entrada
oculta y valida `context_get`, UID y login antes de guardarla:

```powershell
powershell -NoProfile -ExecutionPolicy RemoteSigned -File .\consultant-host\OdooConsultor.ps1 conexion agregar `
  --proyecto "C:\OdooAI\Clientes\cliente-demo" `
  --entorno staging `
  --url "https://cliente-staging.example.com" `
  --login "consultor@example.com"
```

Si se pega una URL de la interfaz web terminada en `/odoo` (con o sin barra
final), el host elimina automáticamente ese sufijo antes de conectarse por
JSON-2 y guarda la URL normalizada.

En Odoo 18, `--base nombre_base` es obligatorio para autenticar el RPC heredado.
En Odoo 19 sigue siendo opcional y se usa cuando la instancia requiere la
cabecera `X-Odoo-Database`. Para producción se usa el mismo procedimiento con
`--entorno production`; el perfil queda marcado como solo lectura aunque el
usuario tenga permisos mayores en Odoo.

```powershell
powershell -NoProfile -ExecutionPolicy RemoteSigned -File .\consultant-host\OdooConsultor.ps1 conexion listar `
  --proyecto "C:\OdooAI\Clientes\cliente-demo"

powershell -NoProfile -ExecutionPolicy RemoteSigned -File .\consultant-host\OdooConsultor.ps1 conexion quitar `
  --proyecto "C:\OdooAI\Clientes\cliente-demo" `
  --entorno staging
```

`conexion quitar` elimina tanto la credencial de Windows como el perfil local.

## Conectar la carpeta con Codex

El generador escribe `.codex/config.toml` con el comando MCP y la ruta absoluta
del proyecto. Abra la carpeta del cliente en Codex, márquela como proyecto
confiable y reinicie Codex para cargar el servidor. Codex descubrirá 50
herramientas. La Fase 4 conserva `replica_verificar_acceso_negativo`; las
operaciones de fuentes, demostraciones y diagnóstico incluyen:

1. `fuentes_estado`
2. `fuentes_sincronizar`
3. `fuentes_crear_bloqueo`
4. `fuentes_buscar`
5. `odoo_identificar_instancia`
6. `modulos_buscar`
7. `modulos_preparar_instalacion`
8. `modulos_aplicar_instalacion`
9. `modulos_verificar_instalacion`
10. `demo_inventariar_aplicacion`
11. `demo_preparar_escenario`
12. `demo_preparar_registros_operativos`
13. `demo_aplicar_escenario`
14. `demo_verificar_paso`
15. `demo_verificar_escenario`
16. `demo_limpiar_escenario`
17. `funcional_inventariar_aplicacion`
18. `funcional_diagnosticar_objetivo`
19. `funcional_preparar_parametrizacion`
20. `demo_inventariar_cobertura_total`
21. `demo_preparar_flujo`
22. `demo_ejecutar_flujo`
23. `demo_verificar_flujo`
24. `demo_preparar_compensacion`
25. `demo_ejecutar_compensacion`

Las herramientas que escriben en Odoo quedan configuradas con aprobación
explícita de Codex. La preparación de paquetes y runbooks escribe únicamente en
la carpeta local del cliente.

## Fuentes verificables y demostraciones

La biblioteca compartida predeterminada es `C:\OdooAI\Fuentes\Odoo`. Solo se
actualiza mediante `fuentes_sincronizar`; cada investigación fija sus commits en
`source-lock.json`. La prioridad es: base real, documentación oficial, código
oficial, código privado autorizado y comunidad no verificada. El repositorio
Enterprise es opcional: `enterprise_source_processing: unavailable` no bloquea
los contratos cerrados basados en la base real, documentación y Community. El
código del cliente no se lee mientras `client_source_processing: blocked`.

La skill fuente vive en `skills/odoo-preparar-demostracion/` y se instala para
todos los proyectos con:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Install-OdooDemoSkill.ps1
```

Cada escenario se guarda en `demostraciones/<slug>/`. La creación requiere
`CREAR-DEMO <id>` y vence en 12 horas. En schema 4, el resultado principal son
registros sintéticos reales y relacionados en staging. Los nombres visibles son
naturales; `DEMO <n>` solo se muestra al consultor. Los IDs se protegen mediante
DPAPI y, cuando existe permiso, con XML IDs invisibles. Los correos usan
`example.invalid` y correo, SMS, redes y webhooks permanecen desactivados.
`LIMPIAR-DEMO <id>` solo archiva o desactiva; nunca elimina.

El primer adaptador es Marketing: UTM, leads CRM, listas/contactos de mailing,
borradores de campaña y automatizaciones inactivas cuando sus modelos existen.
Las demostraciones generales usan además registros operativos cerrados para
terceros, analítica, productos, proyectos, CRM, ventas, compras, facturas,
asientos, inventario y mantenimiento. Estos documentos se preparan en borrador;
las transiciones de estado se autorizan por separado.
Consulte [Conocimiento y demostraciones](conocimiento-y-demostraciones.md) para
el flujo completo y los límites de validación.

## Diagnóstico y parametrización funcional

Para CRM, Ventas, Compras, Inventario, Contabilidad o Marketing, primero use
`funcional_inventariar_aplicacion`. Después describa el objetivo mediante un
contrato schema 1 con requisitos de módulo, configuración y decisiones. La
herramienta `funcional_diagnosticar_objetivo` crea una matriz local bajo
`diagnosticos/<slug>/`.

Solo las brechas cubiertas por `crm_stage`, `sales_team` o `mailing_list` pueden
compilarse con `funcional_preparar_parametrizacion`. El paquete usa el mismo
`APLICAR <id>` de 12 horas y se verifica o revierte con las herramientas Studio
existentes. Las creaciones se archivan y las actualizaciones restauran snapshots
DPAPI; nunca se elimina un registro. Consulte [Diagnóstico y parametrización
funcional](diagnostico-parametrizacion-funcional.md) para el contrato y los
límites.

## Flujo generalista controlado

El copiloto ya no necesita una herramienta nueva para cada variación funcional.
Primero descubre la base y después expresa la necesidad mediante contratos
declarativos que el ejecutable compila.

### Descubrimiento y lectura

1. Use `odoo_descubrir_capacidades` sin modelo para localizar modelos y módulos.
2. Repita con un modelo concreto para obtener su hash de campos y la matriz de
   permisos `read/create/write/unlink` del usuario individual.
3. Use `odoo_consultar_registros` con `model`, `fields`, `filters`, `limit`,
   `order_by` y `order_direction`. No se acepta un nombre de método.
4. Use `odoo_comparar_configuracion` para contrastar campos y firmas de vistas
   entre staging y producción.

Ejemplo de consulta declarativa:

```json
{
  "model": "sale.order",
  "fields": ["name", "state", "partner_id"],
  "filters": [
    { "field": "state", "operator": "in", "value": ["draft", "sent"] }
  ],
  "limit": 20,
  "order_by": "name",
  "order_direction": "desc"
}
```

Odoo sigue aplicando reglas de registro, compañías y permisos. El compilador
aplica límites adicionales antes de enviar la solicitud.

### Contrato declarativo de cambio

El esquema 1 permite hasta diez operaciones sobre un mismo modelo objetivo:

```json
{
  "schema_version": 1,
  "objective": "Capturar referencia y fecha de revisión comercial",
  "target_model": "sale.order",
  "impact": "Dos campos opcionales nuevos; no altera datos existentes.",
  "operations": [
    {
      "type": "create_custom_field",
      "parameters": {
        "name": "x_review_reference",
        "label": "Referencia de revisión",
        "field_type": "char",
        "help": "Referencia aprobada por el equipo comercial"
      }
    },
    {
      "type": "create_custom_field",
      "parameters": {
        "name": "x_review_date",
        "label": "Fecha de revisión",
        "field_type": "date"
      }
    }
  ]
}
```

Tipos ejecutables: `create_custom_field` y `create_inherited_view`. Tipos de
campo permitidos: `char`, `text`, `integer`, `float`, `boolean`, `date`,
`datetime` y `many2one`. Una vista usa `name`, `parent_view_id` y
`architecture_xml`.

`cambio_validar_contrato` no escribe: valida esquema, parámetros, modelo y
permisos, clasifica el riesgo y devuelve las huellas. Después,
`cambio_preparar_paquete` vuelve a validar contra Odoo y crea un `change-set`
esquema 2. La aplicación continúa realizándose exclusivamente mediante
`studio_aplicar_paquete` y `APLICAR <id>`.

En este contrato general, modelos nuevos, instalación de módulos, reportes,
automatizaciones, webhooks, seguridad, acciones de servidor, código calculado,
modificaciones, eliminaciones, escritura de registros de negocio y promoción a
producción se enrutan a runbook. Las réplicas validadas tienen un contrato
separado y más amplio; nunca habilitan llamadas arbitrarias.

## Flujo de réplica validada — Fases 1 y 2

Las capacidades avanzadas no amplían el contrato general anterior. Solo se
habilitan para un manifiesto local dentro de `replicas/<slug>/`:

1. `replica_analizar_destino` comprueba componentes y colisiones sin escribir.
2. `replica_preparar_paquete` genera el diff y un paquete esquema 3. En
   producción genera un runbook.
3. El consultor revisa el paquete y confirma exactamente `APLICAR chg-...`.
4. `studio_aplicar_paquete` relee y verifica los archivos por SHA-256 antes de
   crear campos, selecciones, acción y vistas.
5. Si se proporciona `registro_prueba_id`, ejecuta únicamente la acción cerrada
   declarada sobre ese registro. El ID no se registra en claro.
6. Si la UAT falla, use `PROBAR chg-...` mediante `replica_ejecutar_uat`; los
   componentes no se reinstalan.

Disponible ahora: `char`, `binary` con auxiliar filename, `selection`,
`many2many`, opciones técnicas ordenadas, acción de servidor firmada y vistas
heredadas. Los botones `type="object"` siguen bloqueados.

### Manifiesto Fase 2

Una réplica nueva de app usa obligatoriamente:

```json
{
  "schema_version": 2,
  "phase": 2,
  "source": { "contains_business_records": false },
  "models": [],
  "fields": [],
  "views": [],
  "window_actions": [],
  "menus": []
}
```

Cada componente tiene una `key` lógica estable. Modelos, campos, acciones y
menús usan `mode`; las vistas usan `operation`. Los valores son `create`,
`update` o `deactivate`. Los dos últimos requieren el hash `previous_hash`
devuelto por `replica_analizar_destino`.

La Fase 2 admite:

- modelos personalizados `x_`;
- `char`, `text`, `html`, `integer`, `float`, `monetary`, `date`, `datetime`,
  `boolean`, `binary`, `selection`, `many2one`, `one2many` y `many2many`;
- campos relacionados y campos calculados nuevos cuyo archivo coincida por
  SHA-256;
- vistas base en modelos personalizados y vistas heredadas en modelos estándar;
- acciones de ventana y menús con dependencias lógicas, XML ID o referencias
  portables proporcionadas al aplicar;
- modificación de propiedades declarativas de artefactos Studio y desactivación
  segura de vistas y menús.

El ejemplo completo está en
`consultant-host/examples/replica-phase2-app/`. No se crean ACL: una app nueva es
una estructura técnica para administradores hasta completar su seguridad en la
Fase 4. Los cambios de tipo, relación o tabla solo se intentan sobre un campo sin
valores y su reversión es manual; con datos quedan bloqueados. No se actualiza
código calculado existente y no se crean registros de negocio.

### Manifiesto Fase 3

Una réplica de efectos usa `schema_version: 2` y `phase: 3`. Sus grupos son
`computed_fields`, `mail_templates`, `server_actions` y `automations`. El
código, los dominios, el asunto/cuerpo de correo y el selector de registro de un
webhook entrante viven en archivos con SHA-256; nunca se aceptan desde el chat o
desde el paquete.

La Fase 3 admite:

- reemplazar el código de un campo calculado Studio cuando coincide su
  `previous_hash`;
- crear, actualizar o desactivar plantillas, acciones y automatizaciones
  personalizadas;
- acciones `code`, `multi`, `next_activity`, `mail_post` y `webhook`;
- reglas de creación, actualización, archivo, tiempo, correo y webhook, con las
  restricciones reales de Odoo 19;
- referencias portables `xml_id`, `email` y `https_url` proporcionadas solo al
  aplicar; sus valores no se guardan en la réplica, paquete o auditoría;
- reversión con snapshots protegidos mediante DPAPI del usuario de Windows.

Los efectos externos tienen dos barreras adicionales:

1. `PROBAR-EFECTO <id>` ejecuta solamente la acción externa declarada sobre un
   registro de prueba mientras sus automatizaciones siguen inactivas.
2. `ACTIVAR-EFECTOS <id>` habilita las automatizaciones después de las pruebas.
   Un webhook entrante exige también `PRUEBA-MANUAL <id>` porque su llamada de
   prueba ocurre desde una herramienta externa.

El ejemplo completo está en
`consultant-host/examples/replica-phase3-effects/`. No debe aplicarse sin un
correo, endpoint HTTPS, XML IDs y registro de prueba expresamente autorizados.

### Manifiesto Fase 4

Una réplica de gobierno usa `schema_version: 2` y `phase: 4`. Sus grupos son
`qweb_views`, `reports`, `approvals`, `access_controls`, `record_rules` y
`negative_access_tests`.

- QWeb y dominios viven en archivos firmados; no se aceptan scripts, eventos,
  `t-js`, `t-raw`, `t-debug`, `t-log` ni URLs ejecutables.
- Cada reporte enlaza una plantilla de la misma réplica y puede resolver formato
  y grupos mediante XML ID.
- Las aprobaciones se habilitan solo si la base expone el contrato esperado de
  `studio.approval.rule` y el usuario tiene permisos efectivos.
- Cada ACL o regla declara grupos o alcance global, nunca ambos. Las reglas usan
  dominios JSON firmados.
- Los cambios de seguridad deben declarar pruebas negativas. Estas se ejecutan
  con un usuario funcional separado y solo devuelven acceso efectivo y un conteo
  limitado, no registros.

Registre el perfil funcional de prueba mediante una ventana segura:

```powershell
powershell -NoProfile -ExecutionPolicy RemoteSigned -File .\consultant-host\OdooConsultor.ps1 conexion agregar-prueba `
  --proyecto "C:\OdooAI\Clientes\cliente-demo" `
  --alias vendedor-restringido `
  --url "https://cliente-staging.example.com" `
  --login "usuario-funcional@example.com"
```

Después de aplicar, use `replica_verificar_acceso_negativo` para cada clave de
`negative_access_tests`. Producción solo genera runbook. La reversión restaura
snapshots o desactiva componentes creados sin eliminarlos.

## Flujo de cambio Studio

### Campo personalizado por contrato general

La primera versión admite los tipos `char`, `text`, `integer`, `float`,
`boolean`, `date`, `datetime` y `many2one`. El nombre debe comenzar por `x_`.
No se admiten campos requeridos sobre registros existentes ni código calculado.

1. Solicite el inventario del modelo.
2. Prepare el campo indicando nombre técnico, etiqueta, tipo y ayuda.
3. Revise el paquete guardado en `cambios/paquetes/`.
4. Confirme con `APLICAR chg-...` dentro de las 12 horas posteriores a preparar
   el paquete.
5. Ejecute la verificación técnica y revise visualmente Odoo.

### Vista heredada por contrato general

La arquitectura debe ser XML válido y aditivo. Se rechazan DTD, entidades,
scripts, botones, llamadas a acciones o métodos, plantillas ejecutables,
`position="replace"` y cambios de atributos existentes.

La reversión `REVERTIR chg-...` desactiva la vista creada. Los campos se
preservan para impedir pérdida de datos.

## Auditoría y evidencia

`evidencias/auditoria.jsonl` contiene una cadena de eventos enlazados mediante
SHA-256. Registra usuario de Windows, herramienta, entorno, paquete, resultado y
duración. No registra la API key, cuerpos completos ni registros de negocio.

La cadena permite detectar cambios accidentales, pero no sustituye un SIEM ni
un registro remoto inmutable.

## Pruebas locales

```powershell
powershell -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\Test-OdooConsultor.ps1
```

La prueba compila la biblioteca, ejecuta 162 comprobaciones internas, usa una
credencial ficticia de Windows, simula JSON-2, RPC Odoo 18, `/doc` y permisos denegados,
verifica las 50 herramientas MCP, bloquea producción, comprueba contratos
y consultas declarativas, contención de rutas, hashes, snapshots y rechazo de
paquetes manipulados, protege snapshots con DPAPI y compila réplicas Fase 2 y
Fases 3 y 4 por MCP sin contactar Odoo. También recorre inventario, diagnóstico,
preparación, confirmación, aplicación, idempotencia, verificación y reversión de
una parametrización funcional simulada sin `unlink`.
También recorre búsqueda, preparación, confirmación, instalación, verificación
e idempotencia de un módulo oficial simulado, sin desinstalar ni llamar métodos
escogidos por la IA.

## Validación pendiente con Odoo real

Con una base de staging autorizada todavía se debe comprobar:

1. disponibilidad real de modelos y métodos técnicos en `/doc`;
2. permisos del consultor sobre `ir.model`, `ir.model.fields` e `ir.ui.view`;
3. instalación confirmada de un módulo oficial en staging y posterior
   verificación tras la recarga del registro;
4. descubrimiento y consultas sobre modelos funcionales permitidos;
5. comparación de configuración entre staging y producción;
6. creación de un modelo Fase 2 con campos simples, relacionales, relacionados,
   monetarios, HTML, binarios, selección y cálculo firmado;
7. creación de vistas base, acción de ventana y menús de la app;
8. actualización explícita con `previous_hash` y reversión de propiedades;
9. idempotencia, error parcial, cambio concurrente y cambio de permisos;
10. revisión visual y acceso con perfiles no administradores después de ACL;
11. bloqueo de la misma escritura con el perfil de producción.
12. aplicación de una réplica Fase 3 interna y verificación de código, cadena,
    actividad y regla automática;
13. prueba de correo/webhook con destinos de prueba, seguida de activación
    reforzada e idempotencia;
14. reversión de artefactos Fase 3 y comprobación de snapshots protegidos.
15. creación y renderizado de un reporte QWeb Fase 4;
16. creación, actualización y reversión de una regla de aprobación Enterprise;
17. aplicación de ACL/reglas y pruebas negativas con un usuario funcional
    separado.
18. inventario CRM y comparación de un objetivo funcional en Odoo 18 y 19;
19. aplicación, repetición, verificación y reversión real de una etapa CRM o
    equipo de ventas;
20. bloqueo real por colisión, cambio de permisos y perfil de producción.

Ya se comprobó en entornos piloto, sin escribir, que el analizador Fase 2 puede leer los
metadatos y permisos requeridos: el ejemplo produjo 16 componentes sin bloqueos
y un paquete preparado de 18 operaciones. La Fase 3 produjo 11 componentes sin
bloqueos y un paquete preparado de 19 operaciones, también sin escribir. La
lista anterior sigue pendiente en lo relativo a aplicación, prueba externa,
verificación visual, actualización y reversión reales.

No se debe afirmar que la escritura Studio funciona en una base concreta hasta
completar esta validación.
