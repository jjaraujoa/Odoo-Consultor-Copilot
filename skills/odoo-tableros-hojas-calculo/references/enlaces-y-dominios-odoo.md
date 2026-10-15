# Referencia Técnica: Enlaces odoo://view y Dominios AST 2D

## 1. Desensamble del Frontend de Odoo Spreadsheet

En Odoo (módulo `spreadsheet`, archivo `spreadsheet.o_spreadsheet.min.js`), el servicio de enlaces de menú de celdas (`spreadsheetLinkMenuCellService`) gestiona los clics en enlaces con protocolo `odoo://view/`:

```javascript
function parseViewLink(url) {
    // Extrae la parte posterior a odoo://view/
    const jsonStr = decodeURIComponent(url.slice("odoo://view/".length));
    return JSON.parse(jsonStr);
}

// Al hacer clic en la celda:
const { viewType, action, name } = parseViewLink(url);
// Odoo invoca directamente al servicio de navegación pasando action.xmlId:
await navigateTo(env, action.xmlId, {
    additionalContext: action.context,
    viewType: viewType || "list",
    views: action.views,
    domain: action.domain,
    name: name,
});
```

### Por qué falla si no hay `action`:
Si el JSON se genera con propiedades planas:
```json
// INCORRECTO:
{
  "name": "Proyectos",
  "viewType": "list",
  "xmlId": "project.open_view_project_all",
  "modelName": "project.project",
  "domain": [["user_id", "=", 25]]
}
```
En este caso `action` es `undefined`. Al ejecutar `action.xmlId`, el motor JavaScript de Odoo se detiene con:
`Uncaught TypeError: Cannot read properties of undefined (reading 'xmlId')`.

---

## 2. Estructura Exacta del Payload JSON

```json
{
  "name": "Título descriptivo de la ventana",
  "viewType": "list",
  "action": {
    "xmlId": "modulo.action_xml_id",
    "modelName": "modelo.tecnico",
    "views": [
      [false, "list"],
      [false, "form"]
    ],
    "domain": [
      ["campo_1", "operador", "valor"],
      ["campo_2", "operador", "valor"]
    ],
    "context": {}
  }
}
```

En la celda de la hoja de cálculo se coloca en formato Markdown:
```markdown
[Texto a mostrar](odoo://view/{"name":"Proyectos A tiempo","viewType":"list","action":{"xmlId":"project.open_view_project_all","modelName":"project.project","views":[[false,"list"],[false,"form"]],"domain":[["user_id","=",25],["last_update_status","=","on_track"]],"context":{}}})
```

---

## 3. Catálogo de Acciones Comunes en Odoo

| Módulo / Modelo | XML ID de Acción (`action.xmlId`) | Modelo Técnico (`action.modelName`) |
| :--- | :--- | :--- |
| **Proyectos** (`project.project`) | `project.open_view_project_all` | `project.project` |
| **Tareas** (`project.task`) | `project.action_view_all_task` | `project.task` |
| **Helpdesk** (`helpdesk.ticket`) | `helpdesk.helpdesk_ticket_action_main_tree` | `helpdesk.ticket` |
| **Ventas / Cotizaciones** (`sale.order`) | `sale.action_quotations_with_onboarding` | `sale.order` |
| **Pedidos de Venta** (`sale.order`) | `sale.action_orders` | `sale.order` |
| **CRM Oportunidades** (`crm.lead`) | `crm.crm_lead_action_pipeline` | `crm.lead` |
| **Facturas de Cliente** (`account.move`) | `account.action_move_out_invoice_type` | `account.move` |
| **Facturas de Proveedor** (`account.move`) | `account.action_move_in_invoice_type` | `account.move` |
| **Compras** (`purchase.order`) | `purchase.purchase_rfq` | `purchase.order` |
| **Inventario / Transferencias** (`stock.picking`) | `stock.action_picking_tree_all` | `stock.picking` |

---

## 4. Dominios AST 2D y el problema del aplanamiento en PowerShell

El evaluador de dominios de Odoo (`domain.js` / `toAST`) espera un arreglo de tuplas:
- `[["campo", "=", valor]]` -> Evaluado como AST de dominio con 1 criterio.
- Si recibe `["campo", "=", valor]`, Odoo interpreta que el primer elemento es un sub-árbol y al intentar hacer `.map()` sobre `"campo"` (string) arroja:
  `InvalidDomainError: Invalid domain representation: campo,=,valor (TypeError: elem.map is not a function at toAST)`

### Generador C# in-memory para PowerShell
Para evitar cualquier aplanamiento accidental de arreglos en scripts PowerShell, se recomienda compilar una clase tipada auxiliar:

```csharp
using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

public static class OdooLinkHelper
{
    public static string CreateLink(string label, string title, string xmlId, string model, List<object[]> domain)
    {
        var payload = new Dictionary<string, object>
        {
            { "name", title },
            { "viewType", "list" },
            { "action", new Dictionary<string, object>
                {
                    { "xmlId", xmlId },
                    { "modelName", model },
                    { "views", new object[] { new object[] { false, "list" }, new object[] { false, "form" } } },
                    { "domain", domain },
                    { "context", new Dictionary<string, object>() }
                }
            }
        };
        var serializer = new JavaScriptSerializer();
        return "[" + label + "](odoo://view/" + serializer.Serialize(payload) + ")";
    }
}
```
Uso en PowerShell:
```powershell
Add-Type -TypeDefinition $csharpCode -ReferencedAssemblies "System.Web.Extensions"
$domain = New-Object 'System.Collections.Generic.List[object[]]'
$domain.Add([object[]]@("user_id", "=", 25))
$domain.Add([object[]]@("last_update_status", "=", "on_track"))

$link = [OdooLinkHelper]::CreateLink("54", "Proyectos A tiempo", "project.open_view_project_all", "project.project", $domain)
```
