# Diagnóstico y parametrización funcional

OdooConsultor 0.9 añade una ruta controlada para comparar una necesidad
funcional contra la configuración real de Odoo y convertir solo las brechas
permitidas en un paquete confirmado.

## Qué resuelve

El flujo distingue cinco resultados:

1. `compliant`: la base ya coincide con el objetivo;
2. `confirmed_package`: existe un adaptador cerrado que puede parametrizarlo en
   staging;
3. `consultant_decision`: falta una decisión de negocio que Odoo no puede tomar;
4. `runbook`: requiere instalar módulos u otra actuación manual;
5. `blocked`: faltan modelo, campos, permisos o existe una coincidencia ambigua.

Lo observado en la base se etiqueta `verificado_base`. El diagnóstico no
sustituye la documentación oficial ni demuestra por sí solo que un proceso sea
el adecuado para el cliente.

## Aplicaciones inventariables

- `crm`
- `ventas`
- `compras`
- `inventario`
- `contabilidad`
- `marketing`

El inventario devuelve estados de módulos, modelos visibles, hashes de campos,
permisos efectivos y disponibilidad de adaptadores. Los conteos se limitan a
200 registros técnicos y no incorporan registros de negocio a la auditoría.

## Adaptadores ejecutables iniciales

| Adaptador | Modelo fijado por el motor | Valores permitidos |
|---|---|---|
| `crm_stage` | `crm.stage` | `name`, `sequence`, `fold`, `active` |
| `sales_team` | `crm.team` | `name`, `active` |
| `mailing_list` | `mailing.list` | `name`, `active` |

El nombre del modelo no entra por el chat ni por MCP. Cada adaptador fija modelo,
campos, tipos, permisos y estrategia de reversión. Compras, Inventario y
Contabilidad ya pueden inventariarse y diagnosticarse, pero sus configuraciones
todavía producen decisiones o runbooks hasta incorporar adaptadores específicos.

## Contrato de objetivo

Ejemplo para CRM:

```json
{
  "schema_version": 1,
  "application": "crm",
  "objective": "Preparar el embudo comercial acordado",
  "requirements": [
    {
      "key": "crm-installed",
      "kind": "module",
      "module": "crm",
      "expected_state": "installed"
    },
    {
      "key": "qualified-stage",
      "kind": "configuration",
      "adapter": "crm_stage",
      "match_name": "Calificado",
      "desired_state": "present",
      "desired": {
        "sequence": 20,
        "fold": false
      }
    },
    {
      "key": "lost-policy",
      "kind": "decision",
      "question": "¿Cuándo debe marcarse una oportunidad como perdida?",
      "resolved": false
    }
  ]
}
```

Los únicos tipos de requisito son `module`, `configuration` y `decision`. Los
parámetros desconocidos se rechazan. No se aceptan código, dominios, métodos,
modelos libres ni valores anidados arbitrarios.

## Flujo recomendado

1. Ejecutar `odoo_identificar_instancia`.
2. Ejecutar `funcional_inventariar_aplicacion` para la aplicación elegida.
3. Redactar y revisar el objetivo funcional `schema_version: 1`.
4. Ejecutar `funcional_diagnosticar_objetivo` con un slug estable.
5. Revisar la matriz: cumplimiento, brechas, decisiones, runbooks y bloqueos.
6. Resolver decisiones antes de cambiar Odoo.
7. Ejecutar `funcional_preparar_parametrizacion` indicando el `diagnostic_id` y,
   cuando corresponda, las claves de brecha elegidas.
8. Revisar el paquete y confirmar exactamente `APLICAR <id>` dentro de 12 horas.
9. Ejecutar `studio_verificar_paquete`.
10. Si hace falta deshacerlo, confirmar `REVERTIR <id>` y volver a verificar.

En producción, el paso 7 genera un runbook. No existe una ruta de escritura.

## Precondiciones e idempotencia

Antes de la primera escritura, el motor vuelve a comprobar:

- cliente, entorno y huella de credencial;
- versión real de Odoo;
- modelos, campos y tipos del adaptador;
- permisos de lectura, creación y escritura;
- ausencia de colisiones por nombre;
- hash del registro previo para actualizaciones;
- integridad del diagnóstico y del paquete.

Un reintento de un paquete aplicado devuelve el estado local sin llamar a Odoo.
Si una creación ya existe con exactamente los valores deseados, se recupera su
ID en lugar de duplicarla. Una coincidencia incompatible bloquea el paquete.

## Reversión y evidencia

Los registros creados se archivan con `active=false`. Las actualizaciones y
desactivaciones restauran un snapshot cifrado con DPAPI del usuario de Windows.
Nunca se elimina un registro y nunca se usa `unlink`.

La auditoría conserva herramienta, paquete, resultado, duración, cantidades y
hashes. No guarda los nombres configurados, snapshots legibles, credenciales ni
datos personales. Los archivos bajo `diagnosticos/` permanecen locales y se
ignoran en Git por defecto.

## Validación pendiente

El ciclo completo pasa contra servidores simulados JSON-2 y el transporte RPC
cerrado de Odoo 18 conserva sus pruebas. Antes de declarar operativo un
adaptador para un cliente, hay que aplicarlo, verificarlo y revertirlo en un
staging real de su misma versión y edición.
