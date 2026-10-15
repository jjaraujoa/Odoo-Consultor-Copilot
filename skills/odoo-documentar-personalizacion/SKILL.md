---
name: odoo-documentar-personalizacion
description: Genera la documentación técnico-funcional y el acta de entrega formal en Word (.docx) y Markdown de cualquier personalización implementada en Odoo (17, 18 o 19) tras su certificación o aplicación en Producción o Staging. Mantiene estrictamente el formato estándar técnico-funcional, la tipografía Arial/Navy, el control de cambios, las 6 consideraciones/exclusiones obligatorias y las 7 secciones técnicas estándar.
---

# Documentación Formal de Personalizaciones Odoo (Acta de Entrega .docx y Markdown)

Esta skill permite generar de forma automática, reproducible y formal la **documentación técnico-funcional y acta de entrega** de cualquier personalización desarrollada en Odoo (versiones 17, 18 y 19).

Produce dos entregables sincronizados:
1. **Acta de Entrega Oficial Word (`.docx`)**: Siguiendo rigurosamente la plantilla estándar de entrega técnico-funcional (encabezados, estilos Arial, títulos Navy `#1B365D`, gris oscuro `#333333`, código Consolas y tablas oficiales).
2. **Documento Técnico Markdown (`documentacion.md`)**: Ubicado en `promociones/<slug>/` o `replicas/<slug>/` para control de versiones en Git y consulta rápida en el repositorio.

---

## Principios y Estándares Obligatorios

1. **Nomenclatura Limpia de Producción**:
   - Utilizar **exclusivamente los nombres técnicos definitivos de producción** en modelos, campos (`x_studio_...`), vistas y acciones.
   - Omitir nombres históricos o prefijos temporales de staging.
2. **Formato Estricto de 4 Tablas y 7 Secciones**:
   - Todo documento DEBE contener sin excepción la Ficha HU (Tabla 0), Control de Entrega (Tabla 1), Consideraciones y Exclusiones (Tabla 2) y las 7 secciones técnicas en la Tabla 3.
3. **Cero Caracteres Corruptos (Anti-Mojibake)**:
   - Todo el proceso de generación en Python y PowerShell opera bajo codificación UTF-8 sin BOM, garantizando que tildes, eñes y caracteres especiales se visualicen con nitidez en Word.
4. **Seguridad y Confidencialidad**:
   - Nunca incluir credenciales, API keys, contraseñas ni registros reales de clientes o terceros. La documentación describe la arquitectura y la parametrización, no datos sensibles.

---

## Flujo de Ejecución Paso a Paso

### Paso 1: Recopilación de Insumos y Metadatos

Al invocar esta skill tras finalizar una personalización o certificación en producción:

1. **Identificar la fuente técnica**:
   - Si existe una promoción: leer `promociones/<slug>/runbook.md`, `promocion.json` y `certificado_trazabilidad.json`.
   - Si existe una réplica: leer `replicas/<slug>/manifest.json` y los archivos de código en `replicas/<slug>/acciones/`.
   - Si se inspecciona una base directa: consultar mediante `odoo_inspeccionar_modelo` o `studio_inventariar`.
2. **Confirmar metadatos contextuales con el usuario** (si no se pueden inferir automáticamente):
   - **Cliente / Entorno**: Nombre del cliente (ej. Cliente Demo).
   - **Módulos**: Áreas afectadas (ej. `Inventario / Transferencias / Productos / Calidad`).
   - **Versión de Odoo**: ej. `Odoo 19 Enterprise`.
   - **Código HU**: ej. `HU-INV-DOC-SERIALES-01`.
   - **# GAP / Nombre**: ej. `GAP-INV-04: Automatización y control documental por número de serie`.
   - **Rol solicitante**: ej. `Líder de Operaciones y Logística`.
   - **Horas estimadas**: ej. `32 horas`.
   - **Consultor responsable**: Nombre y cargo del consultor.
   - **Fecha real de entrega**: ej. `4 Septiembre 2026`.

---

### Paso 2: Estructurar los Datos en `documentacion.json`

Crear un archivo `documentacion.json` en la carpeta de la promoción o réplica (`promociones/<slug>/documentacion.json` o `scratch/documentacion.json`) conforme al esquema de `resources/documentacion_schema.json`:

```json
{
  "tabla_0_contexto": {
    "modulos": "Inventario / Transferencias / Productos / Calidad",
    "version_odoo": "Odoo 19 Enterprise",
    "codigo_hu": "HU-INV-DOC-SERIALES-01",
    "codigo_gap_nombre": "GAP-INV-04: Automatización y control documental por número de serie",
    "rol_solicitante": "Líder de Operaciones y Logística / Gerente de Proyecto",
    "objetivo_funcional": "Gestionar la documentación técnica y legal asociada a los números de serie...",
    "justificacion_criterio_exito": "Garantizar trazabilidad, cumplimiento regulatorio y entrega formal..."
  },
  "tabla_1_control": {
    "estimacion_horas": "Estimación: 32 horas",
    "equipo_especialidad": "Equipo Consultoría Odoo / Calidad",
    "mes_ano": "Agosto 2026",
    "consultor_responsable": "Consultor Odoo Senior",
    "fecha_entrega": "4 Septiembre 2026"
  },
  "tabla_2_consideraciones": [
    "1. El envío de documentación por correo NO se dispara automáticamente al validar...",
    "2. Si la entrega se valida sin correo electrónico configurado, la operación no se bloquea...",
    "3. Los documentos adjuntos añadidos manualmente por el usuario se preservan íntegramente...",
    "4. La notificación de documentos pendientes es de carácter informativo persistente y descartable...",
    "5. Exclusiones explícitas: No incluye sincronización con servidores externos (Drive, SharePoint)...",
    "6. Exclusiones de reversibilidad: No se eliminan campos ni adjuntos físicos en caso de desinstalación..."
  ],
  "tabla_3_cuerpo": {
    "seccion_1_objetivo": "La presente personalización tiene como finalidad...",
    "seccion_2_modelo_datos": [
      {
        "modelo": "Lotes / Números de serie (stock.lot)",
        "descripcion": "En el formulario de Lotes/Series se establecen...",
        "campos": [
          {
            "nombre_tecnico": "x_studio_declaracion_de_importacion",
            "etiqueta": "Declaración de Importación",
            "tipo": "Binary (Archivo)",
            "widget": "file",
            "notas": "Vinculado al campo de nombre"
          }
        ],
        "notas_adicionales": [
          {
            "titulo": "Opciones técnicas de selección",
            "descripcion": "0: Pendiente | 1: Listo | 2: Solo uso interno..."
          }
        ]
      }
    ],
    "seccion_3_vistas_ui": [
      {
        "vista": "Formulario de Transferencia (stock.picking.form)",
        "descripcion": "Se configuran dos botones de acción en el encabezado:",
        "elementos": [
          {
            "titulo": "Botón 'Cargar Documentos Listos'",
            "descripcion": "Barra de botones superior. Tipo: Acción de servidor..."
          }
        ]
      }
    ],
    "seccion_4_acciones_servidor": [
      {
        "nombre": "Inventario: cargar documentos listos",
        "modelo": "stock.picking (Transferir)",
        "tipo": "Ejecutar código Python",
        "logica": [
          "Valida que la operación sea de tipo salida y no esté cancelada.",
          "Inspecciona las líneas de operaciones detalladas para extraer seriales..."
        ]
      }
    ],
    "seccion_5_automatizaciones": [
      {
        "nombre": "Inventario: registrar documentación sin correo al validar",
        "modelo": "stock.picking",
        "disparador": "Al actualizar / guardar registros",
        "campo_disparador": "state",
        "dominio_posterior": "['state', '=', 'done'], ['partner_id.email', '=', False]",
        "accion": "Inventario: preparar documentación sin correo",
        "estado": "ACTIVA",
        "justificacion": "Garantiza que una salida a un cliente sin correo no se bloquee ni falle."
      }
    ],
    "seccion_6_plantillas_correo": [
      {
        "nombre": "Entrega: confirmación con documentación",
        "modelo": "stock.picking",
        "asunto": "{{ object.company_id.name }} - Confirmación de despacho ({{ object.name or 'N/D' }})",
        "informes_adjuntos": "stock.report_deliveryslip (PDF)",
        "cuerpo_html": "Saluda al cliente por su nombre, confirma el número de despacho..."
      }
    ],
    "seccion_7_pruebas_dod": [
      {
        "titulo": "Prueba 1: Carga de documentos 'Listo'",
        "condicion": "Tener seriales con documentos en estado 'Listo'. Pulsar botón.",
        "resultado_esperado": "Los archivos se incorporan en 'Documentos a Enviar' y se registran en chatter."
      }
    ]
  }
}
```

---

### Paso 3: Generar el Documento Word (.docx)

Ejecutar el script lanzador PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File "skills\odoo-documentar-personalizacion\scripts\generar_acta.ps1" `
  -JsonPath "<ruta-al-documentacion.json>" `
  -OutputPath "<ruta-de-salida-del-documento.docx>"
```

*Nota*: Si el cliente requiere una plantilla Word específica con membrete exclusivo, se puede agregar el parámetro:
`-TemplatePath "C:\OdooAI\Clientes\<cliente>\plantilla_cliente.docx"`.

---

### Paso 4: Generar la Versión Markdown (`documentacion.md`)

Crear simultáneamente el archivo `documentacion.md` en la misma carpeta (`promociones/<slug>/documentacion.md` o en la carpeta del cliente):
- Contiene exactamente los mismos textos y tablas estructuradas en sintaxis GitHub Flavored Markdown.
- Facilita el rastreo de cambios vía Git en el repositorio.

---

### Paso 5: Entrega al Consultor Funcional

Presentar al usuario:
1. Enlace clicable al documento Word generado: `[Nombre-Documento.docx](file:///...)`.
2. Resumen ejecutivo de los 7 componentes documentados.
3. Confirmación de que se preservaron los estilos estándar de entrega y la ausencia de caracteres corruptos.
