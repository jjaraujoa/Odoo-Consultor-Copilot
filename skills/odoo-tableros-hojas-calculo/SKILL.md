---
name: odoo-tableros-hojas-calculo
description: >-
  Construye, modifica, audita y valida hojas de cálculo y tableros interactivos en Odoo 18 y 19
  (modelos spreadsheet.dashboard y documents.document). Aplica la estructura estricta para enlaces
  odoo://view con objeto action anidado, dominios AST 2D, codificación Unicode nativa y etiquetas
  comerciales limpias.
---

# Generación y Modificación de Tableros y Hojas de Cálculo en Odoo 18 / 19

Esta skill proporciona las directrices de arquitectura, estándares de formato y herramientas de validación para construir o actualizar hojas de cálculo de Odoo (`spreadsheet.dashboard` y `documents.document`).

---

## 1. Reglas Críticas de Desarrollo

Al generar o modificar el JSON en `spreadsheet_data`:

### Regla 1: Estructura Obligatoria para Enlaces `odoo://view`
El frontend web de Odoo (`spreadsheetLinkMenuCellService` en `spreadsheet.o_spreadsheet.min.js`) procesa los enlaces de vista con el siguiente flujo:
```javascript
const { viewType, action, name } = parseViewLink(url);
await navigateTo(env, action.xmlId, ...);
```

**Requisito indispensable:** Todo enlace en celdas DEBE contener obligatoriamente el objeto anidado `"action"`:
```markdown
[Texto Visible](<odoo://view/{"name":"Título de la Vista","viewType":"list","action":{"xmlId":"<modulo.action_xml_id>","modelName":"<modelo>","views":[[false,"list"],[false,"form"]],"domain":[[campo,operador,valor]],"context":{}}}>)
```

> [!CAUTION]
> Si colocas `modelName` o `domain` en la raíz del JSON sin envolver en `action`, Odoo arroja el error fatal en el navegador:
> `TypeError: Cannot read properties of undefined (reading 'xmlId')`

### Regla 2: Formato Estricto de Dominios Odoo (AST 2D)
- El parámetro `domain` (tanto dentro de enlaces `odoo://view` como en `searchParams.domain` de figuras `"type": "odoo_bar"`) DEBE ser SIEMPRE un **arreglo bidimensional de tuplas de 3 elementos**:
  `[["campo", "operador", valor], ...]`
- NUNCA uses un arreglo plano unidimensional `["campo", "=", valor]`. En Odoo, esto dispara:
  `InvalidDomainError: Invalid domain representation ... (TypeError: elem.map is not a function at toAST)`
- **Cuidado en PowerShell:** El operador `+` y la interpolación `@(...)` en PowerShell aplanan arreglos silenciosamente. Para evitarlo, utiliza colecciones fuertemente tipadas de .NET:
  `New-Object 'System.Collections.Generic.List[object[]]'` o genera el JSON vía C# in-memory (`Add-Type`).

### Regla 3: Prevención de Caracteres Corruptos (UTF-8 / Unicode Escapes)
- Para evitar que los caracteres con tildes y eñes se corrompan en el navegador del cliente (ej. `Distribución` -> `DistribuÃ³n`), escribe las cadenas en el JSON usando **secuencias de escape Unicode nativas**:
  - `\u00f3` para **ó**, `\u00d3` para **Ó**
  - `\u00ed` para **í**, `\u00cd` para **Í**
  - `\u00e1` para **á**, `\u00c1` para **Á**
  - `\u00e9` para **é**, `\u00c9` para **É**
  - `\u00fa` para **ú**, `\u00da` para **Ú**
  - `\u00f1` para **ñ**, `\u00d1` para **Ñ**

### Regla 4: Etiquetas Comerciales y Limpias
- Nunca muestres nombres de campos técnicos entre paréntesis en tablas ni en leyendas de gráficos (ej. evitar `"A tiempo (on_track)"`, `"En riesgo (at_risk)"`, `"En espera (on_hold)"`).
- Utiliza siempre nombres limpios orientados al usuario de negocio: **"A tiempo"**, **"En riesgo"**, **"En espera"**, **"Atrasado"**.

---

## 2. Métodos de Interactividad en Odoo Spreadsheet

Existen dos maneras de lograr que un usuario haga clic en una celda y vea los registros subyacentes:

### Método A: Inserción Nativa Odoo (Recomendado sin código)
1. En Odoo, ve a la aplicación correspondiente (ej. Helpdesk, Proyectos, Ventas).
2. Configura los filtros deseados y cambia a la **Vista Tabla Dinámica (Pivote)** o **Vista Gráfico**.
3. Pulsa el botón superior **"Insertar en hoja de cálculo"**.
4. Odoo inserta fórmulas nativas `=PIVOT.VALUE(...)` o `=ODOO.LIST(...)`.
5. Los usuarios pueden hacer clic derecho > *"Ver registros"* en cualquier valor de la tabla y Odoo abre automáticamente la lista filtrada.

### Método B: Enlaces Markdown Personalizados (`odoo://view`)
Para tablas manuales o formatos ejecutivos personalizados:
1. Construye el enlace en la celda siguiendo la estructura de la **Regla 1**.
2. Verifica que las acciones XML correspondan a la aplicación:
   - Proyectos: `xmlId: "project.open_view_project_all"`, `modelName: "project.project"`
   - Helpdesk: `xmlId: "helpdesk.helpdesk_ticket_action_main_tree"`, `modelName: "helpdesk.ticket"`
   - Ventas: `xmlId: "sale.action_quotations_with_onboarding"`, `modelName: "sale.order"`
   - CRM / Leads: `xmlId: "crm.crm_lead_action_pipeline"`, `modelName: "crm.lead"`
   - Facturas: `xmlId: "account.action_move_out_invoice_type"`, `modelName: "account.move"`

---

## 3. Flujo de Trabajo y Validación

1. **Modelado**: Diseña la estructura de hojas (`Tablero`, `Data`), celdas, formatos y figuras.
2. **Generación Segura**: Ejecuta la serialización mediante C# o PowerShell tipado asegurando dominios 2D y secuencias Unicode.
3. **Auditoría Previa al Despliegue**:
   Ejecuta el script de validación estática:
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\skills\odoo-tableros-hojas-calculo\scripts\Validar-SpreadsheetJson.ps1 -Path .\mi_tablero.json
   ```
4. **Despliegue Controlado**:
   - Actualiza tanto `spreadsheet.dashboard` como el documento respaldado en `documents.document` para mantener la sincronización entre el menú de Tableros y Documentos.
