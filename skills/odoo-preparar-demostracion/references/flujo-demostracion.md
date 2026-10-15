# Flujo de demostración

## Descubrimiento

Recoger objetivo, audiencia, duración, historia que se quiere contar, datos iniciales, resultado esperado y límites. Convertir cada requerimiento en una afirmación comprobable.

## Matriz

Para cada requerimiento indicar:

- clasificación;
- evidencia y nivel de fuente;
- módulo y configuración necesarios;
- pasos de demostración;
- datos ficticios requeridos;
- limitación, integración o riesgo de efecto externo;
- estado de validación en la base.

## Escenario

### Prerrequisitos de módulos

Cuando el inventario clasifique una función como `nativo_no_instalado`, localizar
el nombre técnico con `modulos_buscar`. Después de crear `source-lock.json`,
preparar un plan asociado al `demo_slug`. La vista previa debe mostrar módulos
raíz, dependencias, auto-instalaciones potenciales, permisos administrativos y
ausencia de reversión automática.

La instalación solo ocurre después de `APLICAR <id>`. Verificar siempre con
`modulos_verificar_instalacion` y repetir el inventario antes de preparar datos.
Una respuesta ambigua se verifica; nunca se repite automáticamente. Los módulos
personalizados, producción y la desinstalación permanecen fuera de este flujo.

Los escenarios esquemas 1, 2 y 3 siguen siendo legibles. Marketing usa esquema
4 y las demostraciones operativas nuevas usan esquema 5; ambos solo admiten
adaptadores registrados por el motor y declaran
`delivery_mode: database_records`. Marketing cubre UTM, leads
CRM, landing no publicada con formulario cerrado, encuesta y preguntas
inactivas, mailings A/B en borrador y campañas o actividades de automatización
inactivas cuando los modelos existen.

Para una demostración general, `demo_preparar_registros_operativos` compila una
historia conectada de hasta 100 registros mediante tipos lógicos cerrados. Los
componentes se declaran en orden de dependencia y pueden relacionar terceros,
planes y cuentas analíticas, productos, proyectos, oportunidades, documentos de
venta y compra, facturas, asientos, transferencias y mantenimiento. Diarios,
cuentas contables, tipos de operación y ubicaciones existentes se entregan como
referencias tipadas en memoria y se validan contra su modelo al aplicar. El
escenario crea borradores; confirmar, contabilizar, reservar o validar no forma
parte de `CREAR-DEMO`.

El catálogo ampliado también cubre modelos de distribución analítica, servicios
que generan proyectos, tarifas de proveedor, listas y reglas de precios,
acuerdos de compra, ubicaciones, lotes con vencimiento, métodos de entrega,
vehículos, actividades CRM y diarios. Cada elemento conserva campos y relaciones
fijados por el motor.

La capa transaccional añade conteos de existencia, notas crédito, pagos en
borrador, movimientos de extracto y costos en destino. Schema 5 añade además
fiscalidad, almacenes/rutas, comisiones, presupuestos, activos y préstamos.
Aplicar inventario, contabilizar pagos y calcular o validar costos usa planes
separados; no conecta bancos ni importa archivos.

En Odoo 19, capturar primero `demo_inventariar_cobertura_total` y preparar cada
bloque con `demo_preparar_flujo`. El plan solo puede referirse a componentes
protegidos y transiciones registradas por el motor. Vence en 12 horas y se
ejecuta tras `EJECUTAR-DEMO <id>` con `demo_ejecutar_flujo`; después se verifica
por lectura. La conciliación requiere un componente principal y al menos un
`componentes_relacionados`, todos del escenario. Si Odoo responde de forma
ambigua, no repetir la acción. Los resultados derivados se capturan por
relaciones verificadas. La recuperación se prepara con
`demo_preparar_compensacion` y requiere `COMPENSAR-DEMO <id>`; nunca elimina.

El contenido entra como texto simple y enums. El motor genera layouts, HTML y
dominios cerrados. No aceptar arquitectura Website, QWeb, JavaScript, métodos,
modelos libres, expresiones ni dominios Odoo enviados por el modelo. La vista
previa enumera registros, modelos, cantidades, relaciones y vacíos sin adaptador.
La documentación y el guion no constituyen por sí solos una demo lista cuando
el motor puede crear registros.

En Odoo 19 los movimientos de inventario se describen con
`description_picking`. Para una entrega parcial, el escenario relaciona una
cantidad ejecutada cerrada con su transferencia, movimiento, producto,
ubicaciones y lote opcional. La validación posterior debe declarar si crea
backorder o cancela el saldo; nunca ambas opciones.

Los nombres UTM se declaran exclusivamente mediante `configuracion.utm`. En
Odoo 18 el motor omite la sección visual de Survey y asocia directamente las
preguntas a la encuesta; Odoo 19 conserva el adaptador de sección. Una aplicación
incompleta conserva `failed_partial` después de la verificación y puede continuar
sin repetir los componentes protegidos.

La confirmación de creación vence en 12 horas. Si vence durante una aplicación
`failed_partial` y quedan componentes `planned`, el consultor puede volver a
escribir exactamente `CREAR-DEMO <id>`. El motor revalida perfil, fuentes,
versión, módulos y capacidades antes de abrir una ventana nueva de 12 horas y
continuar sobre los registros protegidos; el agente nunca reutiliza por sí solo
la confirmación anterior. Los escenarios ya aplicados o verificados no se
reactivan mediante este mecanismo. Los identificadores se mantienen cifrados
con DPAPI y no deben aparecer en respuestas ni auditoría. Los registros usan
nombres naturales. Una etiqueta corta `DEMO <n>` identifica el escenario solo
ante el consultor; la propiedad técnica usa DPAPI y, si hay permiso, XML IDs
bajo `odoo_ai_demo`. Nunca se adopta un registro ajeno por coincidencia de nombre.

## Ensayo

Recorrer el guion como consultor, cronometrar la secuencia y verificar que etiquetas y menús coincidan con la interfaz real. La navegación del agente es de solo lectura. El consultor puede enviar el formulario únicamente con datos ficticios y correo `example.invalid`; cuentas sociales, WhatsApp, publicidad, analítica externa y cualquier efecto real se muestran como integración o runbook.

Para Email Marketing registrar dos resultados: `database_verified` para estado,
relaciones y existencia, y `editor_ready` para `.o_layout`, `#design-element`,
`.oe_structure`, snippets válidos, imagen interna y personalización única. El
estado final no puede ser `verified` si falta `editor_ready`. Una prueba visual
opcional abre el editor en solo lectura y no guarda, prueba, programa ni envía.

Cuando se remedie una demo aplicada, preparar primero un reemplazo. El original
permanece intacto durante preparación, creación y verificación. Solo
`ARCHIVAR-REEMPLAZO <id>` puede desactivarlo, y
`RESTAURAR-REEMPLAZO <id>` restaura su snapshot protegido.

## Cierre

Ejecutar verificaciones de existencia y relaciones, documentar resultados y limpiar. Si un modelo no admite archivado o desactivación segura, dejarlo marcado para limpieza manual o reconstrucción posterior de staging; nunca ejecutar `unlink`.
