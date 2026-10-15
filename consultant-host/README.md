# OdooConsultor

Host local Windows para el copiloto funcional de Odoo 18 y 19. Implementa un
administrador de perfiles, almacenamiento en Credenciales de Windows, cliente
transporte JSON-2 o RPC heredado con primitivas controladas, lenguaje declarativo,
compilador de políticas y servidor MCP por STDIO. La versión 0.17.0 conserva las
Fases 1–4 de réplicas Studio e incorpora investigación con fuentes versionadas,
una biblioteca compartida y escenarios comerciales Marketing schema 4. Estos
usan adaptadores cerrados para Website, Surveys, Email Marketing y Marketing
Automation. La Fase 3 cubre código calculado actualizado,
plantillas, acciones de código, actividades, cadenas, correo, webhooks y reglas
automáticas y vistas heredadas con botones `type=action` enlazados a una acción
declarada en el mismo paquete. Los efectos externos usan una prueba y activación
separadas; la activación solo informa éxito tras releer `active=true`. El
modelo nunca puede escoger directamente un método de Odoo.

La revisión 0.11.0 añade planes cerrados de instalación de módulos oficiales.
Solo funcionan en staging, requieren un usuario administrador, muestran
dependencias y auto-instalaciones potenciales, vencen en 12 horas y se ejecutan
con `APLICAR <id>`. Un reinicio o respuesta ambigua se resuelve mediante una
verificación separada que nunca repite automáticamente la instalación. No hay
desinstalación automática.

La revisión 0.12.0 añade escenarios operativos generales con hasta 100
registros sintéticos relacionados para analítica, CRM, ventas, compras,
contabilidad, inventario, proyectos y mantenimiento. Los documentos se crean en
borrador y las transiciones de estado quedan fuera de `CREAR-DEMO`.

La revisión 0.13.0 añade una autorización separada para transiciones de negocio
mediante `EJECUTAR-DEMO <id>`: confirmar ventas/compras, ganar/perder
oportunidades, reservar/validar transferencias y contabilizar movimientos. Solo
usa registros protegidos y verifica el resultado antes de declarar éxito.

La revisión 0.14.0 incorpora maestros cerrados para una demo operativa completa:
distribución analítica, servicios/proyectos, tarifas, listas de precios, acuerdos
de compra, logística, caducidad, entregas, Flota, actividades CRM y diarios.

La revisión 0.16.0 incorpora conteos de inventario, notas crédito, pagos,
extractos y costos en destino sintéticos. Las transiciones contables y logísticas
siguen separadas de `CREAR-DEMO` y requieren `EJECUTAR-DEMO <id>`. También
corrige `stock.move` en Odoo 19 y permite preparar cantidades ejecutadas para
entregas parciales con backorder verificable.

La revisión 0.17.0 incorpora escenarios operativos schema 5 y planes de flujo
schema 2 para CRM/Ventas, Compras/Inventario y Contabilidad/Finanzas. El contrato
de cada staging se bloquea con módulos, modelos, campos y permisos reales; la
página `/doc` aporta evidencia cuando publica el método, pero su ausencia no
obliga a disponer del código Enterprise. Las transiciones continúan siendo un
catálogo cerrado, con confirmación separada, captura de registros derivados y
compensación sin eliminación. La conciliación requiere varios apuntes protegidos.

`proyecto.yaml` schema 3 separa el repositorio Enterprise opcional
(`unavailable`, `metadata_only` o `allowed`) del código personalizado del cliente
(`blocked`, `metadata_only` o `allowed`). En la configuración inicial ambos
contenidos permanecen sin procesar. DIAN, sincronización/importación bancaria,
envío de correo, WhatsApp, SMS y conectores de transportistas siguen bloqueados.

La línea 0.9 añade diagnóstico por aplicación y parametrización funcional
controlada. CRM, Ventas, Compras, Inventario, Contabilidad y Marketing pueden
inventariarse y compararse contra un objetivo schema 1. Los adaptadores
ejecutables iniciales son etapas CRM, equipos de ventas y listas de correo; los
resultados no cubiertos producen una decisión, runbook o bloqueo explícito.

La revisión 0.9.1 genera `body_arch` compatible con el diseñador nativo de
Email Marketing de Odoo 18, valida recursos visuales locales por SHA-256 y los
crea como adjuntos privados con URL interna. La verificación separa
`database_verified` y `editor_ready`. La remediación crea un reemplazo antes de
archivar el original y ofrece restauración exacta, sin `unlink` ni envíos.

La revisión 0.10.0 hace que el resultado principal de una demo sean registros
reales de staging. Elimina marcadores visibles de nombres, títulos y asuntos,
asigna una etiqueta corta solo para el consultor y conserva propiedad e
idempotencia mediante DPAPI más un XML ID oculto opcional. Una coincidencia de
nombre con datos ajenos bloquea la creación en vez de reutilizarlos.

La Fase 4 añade plantillas QWeb, reportes, aprobaciones Enterprise, ACL, reglas
de registro y pruebas negativas mediante una credencial funcional separada. La
ejecución sigue limitada a staging y a réplicas con archivos firmados.

Los paquetes Studio preparados permanecen confirmables con `APLICAR <id>`
durante 12 horas. Los escenarios de demostración usan la misma vigencia de 12
horas con su confirmación separada `CREAR-DEMO <id>`.

Los proyectos existentes se actualizan sin mover credenciales ni sobrescribir
conocimiento mediante `OdooConsultor.ps1 proyecto actualizar --proyecto
<ruta>`. La operación renueva el bloque administrado de instrucciones y el
arranque MCP; las carpetas y archivos propios del cliente se conservan.

```powershell
powershell -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\Build-OdooConsultor.ps1
powershell -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\Test-OdooConsultor.ps1
```

Consulte [`docs/copiloto-consultor.md`](../docs/copiloto-consultor.md) para la
operación y los límites de seguridad.
