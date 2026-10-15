# Demostraciones schema 4

OdooConsultor 0.10 trata una demostración como un conjunto de registros
sintéticos reales, relacionados y verificables en staging. La investigación,
la matriz y los guiones explican la funcionalidad, pero no sustituyen la
preparación de la base cuando existe un adaptador cerrado.

La vista previa de `demo_preparar_escenario` informa:

- `deliverable: database_records`;
- la etiqueta corta `DEMO <n>`, visible solo para el consultor;
- modelos, cantidades, relaciones y estados iniciales;
- componentes sin adaptador que permanecen como runbook;
- efectos externos bloqueados y estrategia de limpieza;
- disponibilidad del registro de propiedad `hybrid` o `local_only`;
- confirmación exacta `CREAR-DEMO <id>`.

Los nombres, títulos, asuntos, preguntas y contenidos no incluyen marcadores
técnicos. El ID largo permanece en la confirmación y en los archivos locales,
pero no se copia a los registros mostrados al cliente.

## Propiedad e idempotencia

El snapshot DPAPI local es la fuente principal de propiedad. Si el usuario tiene
lectura y creación sobre `ir.model.data`, cada componente recibe además un XML ID
oculto bajo el módulo `odoo_ai_demo`. Si no existe ese permiso, la demo continúa
como `local_only` y lo informa en la vista previa.

Un reintento resuelve primero DPAPI y después el XML ID. Nunca reutiliza un
registro por coincidencia de nombre: una colisión detiene el componente con
`demo_business_collision`. Antes de crear se guarda una huella de intención; si
no se recibe respuesta de Odoo, el estado queda ambiguo y el motor no adopta
silenciosamente ningún registro.

## Verificación y limpieza

Después de la confirmación deben verificarse existencia, valores, relaciones y
estados seguros. Email Marketing exige además `editor_ready`. La demo solo queda
`verified` cuando todos los criterios aplicables se cumplen.

`LIMPIAR-DEMO <id>` archiva, desactiva, despublica o separa únicamente registros
protegidos por el escenario. Nunca ejecuta `unlink` y conserva los XML IDs como
evidencia técnica. Los escenarios 1–3 y los registros ya existentes no se migran
ni se renombran.
