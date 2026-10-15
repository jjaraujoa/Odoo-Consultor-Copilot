# Instalación controlada de módulos en staging

OdooConsultor 0.11 puede instalar aplicaciones y módulos oficiales de Odoo 18
o 19 en una base de pruebas. Esta capacidad sirve para preparar demostraciones
o activar funciones nativas que Ajustes implementa instalando un módulo, por
ejemplo comisiones de ventas.

## Flujo

1. `modulos_buscar` localiza el nombre técnico en el catálogo real de la base.
2. `modulos_preparar_instalacion` verifica el usuario administrador, el origen
   oficial, el estado, las dependencias y las auto-instalaciones potenciales.
3. La vista previa enumera módulos ya presentes, módulos que se instalarán,
   dependencias, advertencias y la frase exacta `APLICAR <id>`.
4. `modulos_aplicar_instalacion` ejecuta únicamente
   `ir.module.module.button_immediate_install` sobre los módulos raíz del plan.
5. `modulos_verificar_instalacion` relee los estados. Si Odoo reinició o cortó
   la respuesta, esta herramienta comprueba el resultado sin volver a instalar.
6. Después se repite el inventario funcional o de la demostración antes de
   crear datos o parametrizar.

El plan vence a las 12 horas. Cuando se asocia a una demostración mediante
`demo_slug`, también fija el hash de su `source-lock.json`.

## Límites deliberados

- Solo staging; producción se bloquea antes de cualquier llamada de escritura.
- Solo módulos oficiales identificados por el checkout Community registrado o
  por metadatos oficiales de Odoo presentes en la instancia.
- El usuario de la API debe pertenecer a Ajustes / Administración y conservar
  permiso efectivo sobre `ir.module.module`.
- No admite módulos personalizados, nombres inexistentes, módulos de prueba,
  compras pendientes ni operaciones concurrentes.
- No configura cuentas, credenciales, proveedores, webhooks, correo, SMS,
  WhatsApp, redes sociales, pagos ni otros efectos externos.
- No expone el modelo o método de Odoo como parámetros libres.
- No desinstala módulos. Odoo documenta que la desinstalación elimina las
  estructuras y datos creados por el módulo; la recuperación segura es
  reconstruir el staging o restaurar una copia.

## Estados

- `prepared`: vista previa lista para confirmar.
- `applying`: la llamada de instalación comenzó.
- `verification_required`: la respuesta no fue concluyente; verificar, no
  repetir.
- `installation_pending`: Odoo aún muestra estados transitorios.
- `verified`: todos los módulos planificados están instalados.
- `failed` o `failed_partial`: la verificación confirmó que la instalación no
  terminó por completo.

La primera validación real prevista es un entorno Odoo 19 Enterprise. Hasta que
un consultor confirme y verifique allí un plan, la capacidad se considera
implementada y simulada, no validada contra esa base real.
