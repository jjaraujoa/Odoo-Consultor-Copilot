# Demostraciones operativas en staging

OdooConsultor 0.13 amplía las demostraciones más allá de Marketing. El agente
puede preparar una historia conectada de hasta 100 registros sintéticos usando
`demo_preparar_registros_operativos`.

## Cobertura inicial

- empresas cliente y proveedor, y sus contactos;
- planes, subplanes y cuentas analíticas;
- productos de servicio o almacenables;
- proyectos y tareas;
- oportunidades;
- cotizaciones y líneas de venta;
- solicitudes de cotización y líneas de compra;
- facturas de cliente/proveedor y líneas, siempre en borrador;
- asientos y apuntes, siempre en borrador;
- transferencias y movimientos de inventario, siempre en borrador;
- equipos y solicitudes de mantenimiento.

El agente no proporciona un modelo ni un método de Odoo. El tipo lógico decide
el modelo, los campos permitidos, las relaciones, el estado inicial y la
estrategia de limpieza.

## Flujo

1. Inventariar módulos, campos y permisos en la base real.
2. Fijar `source-lock.json`.
3. Instalar primero módulos oficiales faltantes mediante un plan confirmado.
4. Preparar los registros operativos ordenados por dependencia.
5. Revisar modelos, cantidades, relaciones, referencias existentes y estados.
6. Confirmar exactamente con `CREAR-DEMO <id>`.
7. Verificar por lectura cada registro y sus relaciones internas.

Diarios, cuentas contables, tipos de operación y ubicaciones existentes se
entregan como referencias tipadas en memoria. Sus IDs no se guardan en el
escenario ni en la auditoría.

## Transiciones funcionales separadas

`CREAR-DEMO` crea documentos en borrador o registros desactivables. Después,
`demo_preparar_acciones` puede preparar exclusivamente: confirmar ventas y
compras, ganar o perder oportunidades, reservar o validar transferencias y
contabilizar facturas, asientos o pagos, aplicar inventario y calcular o validar
costos en destino. La ejecución exige `EJECUTAR-DEMO <id>` y la
verificación se hace por lectura. Una respuesta ambigua o un asistente de Odoo
detiene la automatización sin repetir la acción. La conciliación y los efectos
bancarios externos siguen fuera del catálogo.

Cada componente admite una sola transición por plan. Reservar y luego validar
una transferencia, por ejemplo, se prepara en dos planes consecutivos para que
el segundo parta del estado ya verificado por el primero.

Los documentos transaccionales no se eliminan durante la limpieza. Cuando Odoo
no permite archivarlos de forma segura, se conservan y la recuperación final es
reconstruir o restaurar el staging.
