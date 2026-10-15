# Guía de Referencia: Estructura Estándar de Documentación de Personalizaciones

Esta guía detalla los estándares de contenido, tablas y secciones que conforman el **Acta de Entrega Técnico-Funcional** estándar para Odoo 17, 18 y 19.

---

## 1. Tabla 0: Ficha Técnica de Historia de Usuario / Contexto

Ubicada al inicio del documento, resume la necesidad y el alcance formal del requerimiento:

| Campo | Etiqueta en Plantilla | Descripción / Contenido Requerido |
| :--- | :--- | :--- |
| **Módulos por afectar** | Fila 0, Celda 1 | Lista de aplicaciones y modelos Odoo involucrados (ej. `Inventario / Transferencias / Productos / Calidad`). |
| **Versión de Odoo** | Fila 1, Celda 1 | Versión exacta y edición (ej. `Odoo 19 Enterprise`, `Odoo 18 Enterprise`, `Odoo 17 Community`). |
| **Dependiente de HU** | Fila 2, Celda 1 | Código de la Historia de Usuario según el backlog (ej. `HU-INV-DOC-SERIALES-01`). |
| **# GAP / Requerimiento** | Fila 3, Celda 1 | Código del GAP o nombre de la brecha funcional (ej. `GAP-INV-04: Automatización y control documental por número de serie en despachos`). |
| **¿Quién?** | Fila 4, Celda 1 | Rol funcional o cargo del usuario solicitante (ej. `Líder de Operaciones y Logística / Gerente de Proyecto`). |
| **¿Quiero qué?** | Fila 5, Celda 1 | Descripción precisa del objetivo operativo que busca la funcionalidad. |
| **¿Para qué?** | Fila 6, Celda 1 | Justificación de negocio, valor aportado y criterio general de éxito. |

---

## 2. Tabla 1: Control de Entrega y Estimación

Control de tiempos y responsables:

- **Estimación**: Horas estimadas aprobadas (ej. `Estimación: 32 horas`).
- **Especialidad**: Equipo o área de consultoría (ej. `Equipo Consultoría Odoo / Calidad`).
- **Mes/Año**: Periodo de desarrollo (ej. `Agosto 2026`).
- **Fecha real de Entrega**: Etiqueta fija `Fecha real de Entrega`.
- **Consultor responsable**: Nombre y cargo del consultor (ej. `Consultor Odoo Senior`).
- **Fecha**: Fecha formal de la entrega (ej. `4 Septiembre 2026`).

---

## 3. Tabla 2: Consideraciones, Reglas de Negocio y Exclusiones

Contiene un encabezado corporativo fijo seguido de 6 consideraciones numeradas en tipografía Arial 9 pt:

1. **Supervisión Humana Obligatoria**: Principio de que las operaciones críticas (envío de correos, transiciones financieras o firmas) no se ejecutan desatendidas, sino mediante validación o compositores supervisados.
2. **Manejo Preventivo de Contingencias**: Qué ocurre si falta un dato crítico (ej. si el contacto no tiene correo electrónico, el sistema no bloquea la operación logística; registra la novedad en chatter y crea una actividad interna).
3. **Preservación de Datos y Adjuntos Manuales**: Protección de adjuntos o información ingresada manualmente por usuarios frente a sobrescrituras de automatizaciones.
4. **Notificaciones Persistentes vs Bloqueos**: Carácter informativo o advertencia de alertas en pantalla (persistentes y descartables) sin bloquear el flujo operativo principal.
5. **Exclusiones Explícitas de Integración**: Declaración de lo que NO forma parte del alcance (ej. sin sincronización con almacenamiento externo Google Drive/SharePoint, sin firma digital externa, sin pasarelas adicionales).
6. **Exclusiones de Reversibilidad (No eliminación física)**: Garantía de que no se ejecutan operaciones de borrado físico (`unlink`) sobre modelos o campos con datos históricos; la reversión usa desactivación o archivo (`active = false`).

---

## 4. Tabla 3: Descripción Funcional y Técnica del Desarrollo (7 Secciones)

### 1. Objetivo General y Alcance de la Solución
Párrafo ejecutivo que describe la finalidad de la personalización, su alcance, el beneficio para la organización y la forma en que resuelve el requerimiento.

### 2. Modelo de Datos y Campos Creados (Estructura)
Desglose por cada modelo técnico afectado (ej. `stock.lot`, `stock.picking`, `product.document`, `ir.attachment`):
- Nombre técnico del campo (ej. `x_studio_declaracion_de_importacion`).
- Etiqueta legible de negocio.
- Tipo de dato (`Char`, `Binary`, `Selection`, `Many2many`, `Boolean`, `Monetary`, etc.).
- Widgets de interfaz aplicados (ej. `widget="file"`, `widget="many2many_tags"`).
- Opciones técnicas de selección: lista exhaustiva de valores (`[('pendiente', 'Pendiente'), ('listo', 'Listo'), ...]`).
- Notas técnicas: almacenado (`store=True`), indexado, solo lectura o de cómputo.

### 3. Vistas y Elementos de Interfaz de Usuario
- Formularios, listas o tableros extendidos (ej. `stock.lot.form`, `stock.picking.form`).
- Estructura de diseño: nuevas pestañas (notebook/page), grupos y columnas.
- Botones de acción:
  - Nombre / Etiqueta del botón.
  - Ubicación en la interfaz (ej. barra de estado, barra de botones superior).
  - Tipo de botón (`type="action"`).
  - Condición de visibilidad (`invisible="..."`).
  - Efecto funcional al hacer clic.

### 4. Acciones del Servidor y Lógica de Procesamiento
Para cada acción configurada:
- Nombre descriptivo de la acción.
- Modelo de aplicación.
- Tipo de acción (`ir.actions.server`: ejecutar código Python, ejecutar varias acciones encadenadas).
- Lógica de negocio paso a paso detallando validaciones, extracciones de datos, deduplicaciones y registros en chatter.
- Cadenas de acciones: secuencia exacta de ejecución y construcción del contexto (`context`).
- Fragmento de código Python limpio, documentado y sin mojibake.

### 5. Reglas de Automatización (`base.automation`)
- Nombre de la regla.
- Modelo base.
- Disparador (`on_create`, `on_write`, `on_time`).
- Campos disparadores (`state`, etc.).
- Dominio previo y Dominio posterior (o dominio de filtro).
- Acción de servidor vinculada.
- Estado (`ACTIVA` o `INACTIVA` con justificación).
- Funcionalidad específica que cumple la regla.

### 6. Plantilla de Correo Electrónico Institucional
- Nombre de la plantilla.
- Modelo aplicable (`stock.picking`, `sale.order`, etc.).
- Asunto dinámico con variables Jinja/QWeb (ej. `{{ object.company_id.name }} - Confirmación...`).
- Informes PDF adjuntos (ej. `stock.report_deliveryslip`).
- Estructura del cuerpo HTML, incluyendo marcas técnicas invisibles para auditoría (ej. `id="custom_documentacion_despacho"`).

### 7. Criterios de Aceptación y Matriz de Pruebas (DoD)
Matriz de pruebas unitarias y de integración numeradas:
- **Título**: Identificador de la prueba (ej. `Prueba 1: Carga de documentos 'Listo'`).
- **Condición / Preparación**: Estado inicial del registro o datos de prueba requeridos.
- **Resultado Esperado**: Comportamiento exacto del sistema (valores de campos, alertas visuales, registros en chatter, cambios de estado).
