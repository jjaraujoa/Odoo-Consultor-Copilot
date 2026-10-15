---
name: odoo-replicar-personalizacion
description: Captura, documenta, valida, compara y reconstruye selectivamente personalizaciones de Odoo entre bases, incluyendo campos Studio, vistas, acciones, automatizaciones, reportes QWeb, aprobaciones y seguridad. Usar cuando se solicite guardar una personalización existente, preparar una réplica portable, migrar solo una funcionalidad entre entornos Odoo, comprobar diferencias contra una base destino o actualizar un paquete de réplica sin exportar todas las personalizaciones de Studio.
---

# Réplica selectiva de personalizaciones Odoo

Construir un paquete técnico local y portable para una funcionalidad concreta; no hacer una exportación integral de Studio ni crear un módulo salvo petición expresa.

## Límites de seguridad

- Usar exclusivamente herramientas cerradas de `OdooConsultor` para acceder a Odoo. Descubrir capacidades, modelos, campos y permisos antes de inspeccionar.
- Preferir metadatos técnicos; no leer registros comerciales, binarios, documentos de clientes, datos personales, secretos ni API keys.
- Conservar evidencia solo anonimizada y técnica. Fijar `contains_business_records` en `false`.
- Tratar producción como solo lectura. No escribir durante captura, comparación ni generación local.
- En staging, preparar cambios con paquetes controlados y aplicar solo tras la confirmación exacta `APLICAR <id>`.
- Las acciones de servidor Fase 1, los campos calculados Fase 2 y los efectos
  ejecutables Fase 3 pueden usar código en staging únicamente mediante
  `replica_preparar_paquete`: cada archivo debe estar contenido en la réplica y
  coincidir por SHA-256 con el manifiesto inmediatamente antes de escribir.
- Las automatizaciones Fase 3 con correo o webhooks se instalan inactivas. Una
  prueba externa requiere `PROBAR-EFECTO <id>`; activarlas requiere
  `ACTIVAR-EFECTOS <id>`, y un webhook entrante exige además
  `PRUEBA-MANUAL <id>`.
- La Fase 4 puede ejecutar en staging QWeb/reportes, reglas de aprobación
  Enterprise descubiertas en el destino, ACL y reglas de registro. Los grupos,
  usuarios, formatos y acciones se resuelven mediante referencias tipadas; las
  pruebas negativas usan un perfil funcional separado. Producción solo genera
  runbook.
- Las vistas ejecutables Fase 3 se crean en staging desde XML firmado. Cada
  botón debe usar `type="action"`, el marcador `name="{{ACTION_ID}}"` y una
  `action_key` que identifique una acción de la misma réplica. No usar IDs
  numéricos fijos ni convertir el cambio en runbook por contener el botón.
- Para una automatización externa que deba quedar activa tras la autorización,
  declarar `install_active: false`, `activation_mode: gated` y
  `desired_active_after_authorization: true`. Si falta esa declaración, no
  afirmar que fue activada ni solicitar reintentos: hay que regenerar el paquete.
- No usar una llamada genérica a métodos de Odoo ni tratar IDs numéricos de origen como portables.

## Flujo

### 1. Delimitar la funcionalidad

Pedir el comportamiento que debe preservarse y el entorno/perfil de origen. Clasificar cada hallazgo como núcleo funcional, auxiliar, futuro o ajeno. No asumir que toda personalización Studio de un modelo pertenece al alcance.

Si una dependencia es ambigua, mostrar únicamente la evidencia técnica y pedir confirmación antes de incluirla. Registrar inclusiones y exclusiones explícitas.

### 2. Descubrir e inventariar el origen

1. Consultar `conexion_estado`, confirmar que el perfil corresponde al origen y descubrir las capacidades disponibles.
2. Usar las herramientas cerradas de inspección e inventario para comprobar modelos, versión de Odoo, campos, vistas y permisos de metadatos.
3. Capturar solo lo necesario: modelos, campos, selecciones, relaciones, opciones de UI, vistas heredadas, botones, acciones, automatizaciones y dependencias.
4. Guardar nombres técnicos, configuración y hashes de metadatos. Conservar IDs numéricos solo como evidencia de origen, en un bloque `source_evidence`; reemplazar referencias de destino por `{{PLACEHOLDER}}`.

Para acciones con código, copiar el texto verificado sin corregirlo ni refactorizarlo, normalizar solo finales de línea, calcular SHA-256 y documentar recomendaciones por separado. No extraer binarios ni adjuntos.

### 3. Generar el paquete

Crear `replicas/<slug-personalizacion>/` desde `assets/replica-template/` para
Fase 1, `assets/replica-phase2-template/` para modelos/apps de Fase 2,
`assets/replica-phase3-template/` para automatizaciones y efectos de Fase 3 o
`assets/replica-phase4-template/` para reportes, aprobaciones y seguridad.
Leer `references/manifest-schema.md` antes de editar `manifest.json`.

- Completar `README.md` con objetivo, alcance, exclusiones, dependencias, orden de reconstrucción, marcadores no portables, política de aplicación, comportamiento esperado, pruebas, verificación, advertencias y confirmación de ausencia de datos de negocio.
- Completar `manifest.json` con los componentes capturados, hashes, dependencias, orden de despliegue, exclusiones y pruebas.
- Crear `acciones/<nombre>.py` solo para cada acción o automatización con código. Referenciar su ruta y SHA-256 en el manifiesto.
- Mantener el paquete independiente de la base de origen: nombres técnicos sí; IDs numéricos portables no.

Validar localmente con:

```powershell
python scripts/validate_replica.py <ruta-de-replica>
```

Usar el ejecutable Python disponible en el entorno si `python` no está en `PATH`.

### 4. Comparar con un destino

Distinguir siempre captura, validación local, comparación, preparación, aplicación y verificación. Para comparar, confirmar la conexión destino, descubrir sus capacidades y dependencias, y clasificar cada componente como ausente, compatible o incompatible.

No sobrescribir un componente incompatible. Detectar las colisiones por nombre técnico y resolver placeholders en el destino únicamente después de obtener allí las referencias correctas.

### 5. Preparar, aplicar y verificar

1. Verificar versión, módulos y permisos del destino.
2. Preparar campos y vistas aditivas en orden de dependencia mediante los paquetes del MCP.
3. Para la Fase 1, compilar campos `char`, `binary`, `selection` y `many2many`,
   opciones técnicas, la acción firmada y las vistas mediante
   `replica_preparar_paquete`. `button type="action"` solo puede apuntar a la
   acción del mismo paquete; `type="object"` permanece bloqueado.
4. Para una app Fase 2, usar expresamente `schema_version: 2` y `phase: 2`.
   Declarar modelos, campos, vistas, acciones de ventana y menús con una `key`
   lógica. Usar `mode` o `operation` con `create`, `update` o `deactivate`; los
   dos últimos requieren el `previous_hash` devuelto por el análisis del destino.
5. En Fase 2, extender modelos estándar solo con vistas heredadas. Solo proponer
   cambios de tipo, relación o tabla si el campo no tiene valores y advertir que
   su reversión será manual; con datos, preparar una migración. No crear ACL ni
   reglas ni presentar una app como utilizable por usuarios no administradores
   hasta completar su seguridad.
6. Para Fase 3 usar `schema_version: 2` y `phase: 3`. Mantener código, dominios,
   plantillas, notas y selectores de webhook en archivos firmados. Declarar
   destinatarios, URLs, usuarios, tipos de actividad y calendarios como
   referencias tipadas; proporcionar sus valores solo en memoria al aplicar.
   Las vistas ejecutables van en el grupo `views`, después de `action_chains` y
   antes de `automations`, con `arch_file`, `arch_sha256`, padre heredado y
   `action_key`. Esto es ejecutable en staging mediante la réplica y no requiere
   un runbook manual.
7. Para Fase 4 usar `schema_version: 2` y `phase: 4`. Mantener QWeb y dominios
   en archivos firmados; declarar reportes, aprobaciones, ACL y reglas con
   claves lógicas. Resolver grupos y usuarios por XML ID o login solo en memoria.
   Cada cambio de seguridad debe declarar pruebas negativas y ejecutarlas con
   `replica_verificar_acceso_negativo` usando un perfil de verificación separado.
8. Solicitar la confirmación exacta `APLICAR <id>` para cada paquete de staging
   (en producción se utiliza el flujo declarativo de promoción `promocion_aplicar_paquete`
   con su token reforzado). La aplicación en staging no autoriza efectos externos.
9. Probar correo o webhook saliente solo contra un destino de prueba autorizado
   mediante `replica_probar_efecto_externo` y `PROBAR-EFECTO <id>`. Activar las
   automatizaciones externas por separado con `replica_activar_efectos_externos`
   y `ACTIVAR-EFECTOS <id>`; para webhooks entrantes incluir también
   `PRUEBA-MANUAL <id>` después de una prueba humana del endpoint.
10. Reinventariar y comparar contra el manifiesto. Ejecutar pruebas únicamente
   sobre datos de prueba expresamente autorizados y sin migrar datos de negocio.
   Una UAT funcional de Fase 1 se reintenta con `PROBAR <id>` sin reinstalar componentes.

### 6. Promoción Declarativa y Segura a Producción (Pase a Producción v0.23.0)

La plataforma y el copiloto permiten la promoción controlada y declarativa de personalizaciones hacia **Producción** a través de un conjunto de herramientas dedicadas con estrictas compuertas de seguridad:

1. **Análisis de Impacto Previo (`promocion_analizar_impacto`):**
   - Ejecutar `promocion_analizar_impacto` especificando `replica_slug`.
   - Inspecciona en solo lectura Staging (garantiza que los componentes de la réplica estén 100% aplicados y probados) y Producción (detecta colisiones, faltantes o preexistencias).
   - Clasifica cada componente como `missing_in_production`, `present_identical` o `conflict_in_production`. Emite el dictamen `ready_for_promotion`.

2. **Preparación del Paquete de Promoción (`promocion_preparar_paquete`):**
   - Requiere haber superado el análisis de impacto sin conflictos bloqueantes.
   - Requiere obligatoriamente un identificador o referencia de respaldo previo (`backup_reference`, mínimo 5 caracteres, ej. `BKP-20260922-01` o snapshot ID).
   - Genera un paquete declarativo `prom-<id>` con una ventana de vigencia reducida de **2 horas**.
   - **Regla estricta de Cero Datos:** El compilador verifica y rechaza terminantemente cualquier registro sintético, de prueba o demo (`odoo_ai_demo`, `synthetic_record`, `demo_*`). Solo se promueven metadatos puros de personalización.
   - Las plantillas de correo y automatizaciones con efectos externos se configuran para instalarse **inactivas** (`active=False`).

3. **Aplicación con Confirmación Reforzada (`promocion_aplicar_paquete`):**
   - Requiere la confirmación explícita con token doble: `PROMOVER-PRODUCCION <id> RESPALDO-CONFIRMADO`.
   - Aplica los componentes en producción de forma atómica y ordenada.
   - **Garantía Visual y Sincronización Automática de Vistas:** Para evitar que campos promovidos queden invisibles en la interfaz de usuario (evitando reprocesos), `promocion_aplicar_paquete` inspecciona automáticamente el entorno de Staging, identifica las vistas XML heredadas que despliegan dichos campos (pestañas, bloques, ribbons o columnas), resuelve sus vistas padre en Producción y las sincroniza de forma segura aplicando entidades numéricas estándar de 7 bits (Anti-Mojibake). Verifica al 100% que todo campo promovido sea visible en pantalla antes de cerrar el pase.
   - Ejecuta inmediatamente una auditoría de lectura al 100% en Producción para verificar la consistencia de cada componente.
   - Emite el certificado criptográfico inmutable `promociones/<slug>/certificado_trazabilidad.json` firmado con SHA-256 (`signature_hash`) y registra la entrada en `auditoria.jsonl`.

4. **Aislamiento y Activación de Efectos Externos (`promocion_activar_efectos_externos`):**
   - Si la personalización incluye correos salientes o webhooks/automatizaciones, permanecen inactivos tras la aplicación.
   - Su activación en producción requiere un paso independiente mediante `promocion_activar_efectos_externos` y la confirmación explícita `ACTIVAR-EFECTOS-PRODUCCION <id>`.

5. **Verificación Independiente (`promocion_verificar_produccion`):**
   - Audita en cualquier momento el estado de los componentes en Producción contra el manifiesto.
   - Permite verificar el estado de promoción (`promoted_verified`) de manera idempotente.

6. **Entrega y Documentación Formal:**
   - Una vez obtenido el `certificado_trazabilidad.json`, se delega a la habilidad `odoo-documentar-personalizacion` para compilar el acta de entrega formal y documento técnico en `.docx` y Markdown bajo formato corporativo oficial.

## Actualizar una réplica

Reinventariar el alcance acordado, comparar hashes y componentes contra el manifiesto actual, explicar altas, bajas y cambios, y pedir confirmación ante cambios ambiguos. Mantener exclusiones explícitas. No reemplazar código ni configuraciones ya capturadas sin conservar su nuevo hash y la fecha de actualización.

## Recursos

- `references/manifest-schema.md`: estructura, convenciones y reglas detalladas del manifiesto.
- `scripts/validate_replica.py`: validación local determinista sin conexión a Odoo.
- `assets/replica-template/`: punto de partida compatible para Fase 1.
- `assets/replica-phase2-template/`: punto de partida para modelos/apps de Fase 2.
- `assets/replica-phase3-template/`: punto de partida para automatizaciones y efectos controlados de Fase 3.
- `assets/replica-phase4-template/`: punto de partida para QWeb/reportes, aprobaciones y seguridad de Fase 4.
