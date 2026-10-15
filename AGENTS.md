# Instrucciones de Trabajo para Agentes (Odoo-Consultor-Copilot)

## Ámbito Exclusivo: Repositorio Público Open Source

> [!IMPORTANT]
> **Este directorio de trabajo corresponde ÚNICA Y EXCLUSIVAMENTE al repositorio público Open Source (`jjaraujoa/Odoo-Consultor-Copilot`).**
> - **Cero referencias cruzadas**: Queda estrictamente prohibido introducir, referenciar, sincronizar o hacer merge con repositorios privados corporativos, ramas externas cerradas o documentación interna de clientes.
> - **Exclusividad de Remotos**: Todos los cambios, commits, branches y pushes deben dirigirse de manera exclusiva a `origin` (`https://github.com/jjaraujoa/Odoo-Consultor-Copilot.git`).
> - **Neutralidad y Sanitización**: Todo código, plantilla, caso de prueba o documentación debe ser estrictamente neutral, genérico y libre de cualquier marca o formato institucional privado.
> - **Ramas Públicas**: Todo desarrollo se realiza sobre `main` o ramas de funcionalidad estándar (`feature/*`, `fix/*`). Nunca crear ni utilizar ramas asociadas a perfiles privados.

## Idioma y Usuario

- Trabajar y documentar siempre en español.
- Explicar los procedimientos de forma sencilla y clara; el responsable funcional está aprendiendo estas herramientas por primera vez.
- No afirmar que una integración funciona sin probarla exhaustivamente con la suite de pruebas o documentar claramente la validación pendiente.

---

## Alcance del Copiloto Funcional

- **Plataforma**: Copiloto para consultores técnicos y funcionales de Odoo (14 a 19, Enterprise y Community).
- **Ejecución**: Codex Desktop / Antigravity mediante `OdooConsultor.dll` (C# / .NET 8), lanzador PowerShell `OdooConsultor.ps1` y servidor MCP local por STDIO.
- **Entornos**:
  - **Producción**: Solo lectura para operaciones manuales o arbitrarias. Modificaciones exclusivamente declarativas mediante paquetes de promoción certificados (`promocion_aplicar_paquete`), con confirmación reforzada (`PROMOVER-PRODUCCION <id> RESPALDO-CONFIRMADO`), referencia de respaldo obligatoria y ventana de 2 horas.
  - **Staging**: Escrituras declarativas mediante paquetes confirmados (`APLICAR <id>`, `CREAR-DEMO <id>`, `EJECUTAR-DEMO <id>`) que vencen a las 12 horas.
- **Seguridad e Identidad**:
  - Las credenciales viven en el Administrador de Credenciales de Windows protegidas por DPAPI; nunca en el chat, archivos de texto ni argumentos.
  - Los modelos y herramientas son cerrados; nunca aceptar métodos o llamadas arbitrarias elegidas libremente por el LLM.

---

## Protocolo Estricto para Pruebas de Conexión (Clientes Nuevos o Existentes)

Cuando el usuario solicite "probar la conexión", "verificar la conexión" o similar:
1. **Acción Inmediata**: Invocar directamente la herramienta MCP `conexion_probar(entorno=...)`. Si el usuario no especificó si es Staging o Producción, ejecutar sin argumentos para probar los perfiles configurados.
2. **Si el perfil NO está configurado (`configured: false`)**: Informar de forma clara y directa que el entorno no está configurado en el proyecto local y esperar a que el consultor lo configure con `OdooConsultor.ps1 conexion agregar`. Prohibido inventar llamadas arbitrarias, divagar o perder tiempo explorando archivos.
3. **Si el perfil está configurado**: Reportar el resultado de forma limpia con:
   - Estado de la conexión (Éxito o Error).
   - Entorno (`staging` o `production`).
   - URL y Base de datos técnica.
   - Usuario autenticado (Nombre, Login y UID).
   - Permisos de Administrador (`Sí` o `No`).
   - Versión de Odoo y Transporte (`json2` o `legacy_rpc`).
   - Latencia de respuesta en milisegundos.

---

## Reglas para Creación y Modificación de Tableros y Hojas de Cálculo (Odoo 18/19)

Al construir o modificar hojas de cálculo y tableros (modelos `spreadsheet.dashboard` y `documents.document`):

1. **Estructura requerida para enlaces `odoo://view`:**
   Todo enlace en celdas DEBE contener obligatoriamente el objeto anidado `action`:
   `[Texto Visible](<odoo://view/{"name":"Título de la Vista","viewType":"list","action":{"xmlId":"<modulo.action_xml_id>","modelName":"<modelo>","views":[[false,"list"],[false,"form"]],"domain":[[campo,operador,valor]],"context":{}}}>)`
   Colocar `modelName` o `domain` en la raíz sin envolver en `action` provoca el error fatal: `TypeError: Cannot read properties of undefined (reading 'xmlId')`.

2. **Regla estricta de dominios Odoo (AST 2D):**
   - El parámetro `domain` tanto en enlaces `odoo://view` como en `searchParams.domain` de figuras (`type: "odoo_bar"`, etc.) DEBE ser siempre un arreglo bidimensional de tuplas: `[["campo", "operador", valor], ...]`.
   - NUNCA un arreglo plano `["campo", "=", valor]`.

3. **Prevención de caracteres corruptos (UTF-8 y Escapes Unicode):**
   - Escribir las cadenas en el JSON usando secuencias de escape Unicode nativas (`\u00f3` para ó, `\u00ed` para í, `\u00e1` para á, `\u00e9` para é, `\u00fa` para ú, `\u00f1` para ñ).

4. **Etiquetas comerciales y limpias:**
   - Nunca mostrar nombres técnicos de campos entre paréntesis (evitar `"A tiempo (on_track)"`). Utilizar nombres limpios orientados al usuario final.

---

## Reglas Estrictas contra Corrupción de Caracteres (Anti-Mojibake)

1. **Vistas XML (`ir.ui.view` / Studio / QWeb):**
   - Prohibido usar caracteres no ASCII crudos en atributos o en el cuerpo de vistas XML.
   - Usar obligatoriamente entidades numéricas XML estándar de 7 bits:
     - Minúsculas: `&#225;` (á), `&#233;` (é), `&#237;` (í), `&#243;` (ó), `&#250;` (ú), `&#241;` (ñ), `&#252;` (ü).
     - Mayúsculas: `&#193;` (Á), `&#201;` (É), `&#205;` (Í), `&#211;` (Ó), `&#218;` (Ú), `&#209;` (Ñ), `&#220;` (Ü).
     - Signos: `&#191;` (¿), `&#161;` (¡), `&#176;` (°).

2. **JSON y Parámetros RPC:**
   - Todo carácter fuera del rango ASCII (`> 127`) debe ser escapado como secuencia Unicode nativa (`\u00e1`, `\u00e9`, etc.).

3. **Scripts de PowerShell:**
   - Forzar explícitamente codificación UTF-8 sin BOM:
     `[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)`
     `[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)`
     `$OutputEncoding = [System.Text.UTF8Encoding]::new($false)`

---

## Reglas de Demostraciones y Réplicas Studio

- Las demostraciones solo generan datos sintéticos con identificadores trazables.
- Toda transición de estado (confirmar venta, validar albarán, contabilizar) requiere un plan semántico separado con `EJECUTAR-DEMO <id>`.
- La compensación o limpieza desactiva o archiva; **NUNCA ejecuta `unlink`**.
- Los botones en vistas Studio usan `type="action"` con claves lógicas; `type="object"` continúa estrictamente bloqueado.

---

## Reglas Estrictas para Promoción a Producción (Pase Seguro)

1. **Cero Datos de Prueba o Demostración:**
   - Terminantemente prohibido incluir, incrustar o generar registros sintéticos o de ejemplo (`odoo_ai_demo`, `synthetic_record`, `demo_*`) en el entorno de producción.
   - El compilador de paquetes de promoción inspecciona exhaustivamente los componentes y operaciones, rechazando cualquier intento con error bloqueante si detecta datos o pruebas.

2. **Requisitos Previos Obligatorios:**
   - **Certificación previa en Staging:** La réplica debe haber sido aplicada y auditada al 100% en Staging antes de poder promoverse a Producción.
   - **Análisis de impacto con cero conflictos:** `promocion_analizar_impacto` debe arrojar viabilidad limpia y 0 conflictos bloqueantes en la base de producción.
   - **Referencia de respaldo verificada (`backup_reference`):** Es obligatorio proporcionar el ID o referencia del snapshot/respaldo previo realizado en producción (mínimo 5 caracteres).

3. **Ventana Temporal y Token de Confirmación Reforzado:**
   - La ventana de vigencia del paquete de promoción en producción es de **2 horas** (frente a 12 horas en staging).
   - La confirmación requiere explícitamente el token: `PROMOVER-PRODUCCION <id> RESPALDO-CONFIRMADO`.

4. **Aislamiento de Efectos Externos:**
   - Las plantillas de correo y automatizaciones con webhooks o acciones de correo se crean obligatoriamente en estado **inactivo** (`active=False`).
   - Su activación en producción requiere un paso independiente y explícito con la herramienta `promocion_activar_efectos_externos` y el token `ACTIVAR-EFECTOS-PRODUCCION <id>`.

5. **Auditoría Post-Aplicación y Certificado de Trazabilidad:**
   - Inmediatamente tras la aplicación, el host re-inspecciona en solo lectura cada componente aplicado en producción.
   - Emite el `certificado_trazabilidad.json` firmado con SHA-256 (`signature_hash`) y registra la operación inmutable en `auditoria.jsonl`.

6. **Garantía Visual y Sincronización Automática de Vistas:**
   - Para evitar que campos promovidos queden invisibles en la interfaz de usuario (eliminando reprocesos), `promocion_aplicar_paquete` inspecciona automáticamente el entorno de Staging, identifica las vistas XML heredadas que despliegan dichos campos (pestañas, bloques, ribbons o columnas), resuelve sus vistas padre en Producción y las sincroniza de forma segura aplicando entidades numéricas estándar de 7 bits (Anti-Mojibake). Verifica al 100% que todo campo promovido sea visible en pantalla antes de cerrar el pase.

---

## Ciclo de Desarrollo y Compilación

- Después de compilar y probar una nueva versión de `OdooConsultor`:
  ```powershell
  powershell -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\Build-OdooConsultor.ps1
  powershell -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\Test-OdooConsultor.ps1
  ```
- Si la DLL está en uso por MCP, usar `Activate-OdooConsultorUpdate.ps1` para dejar la actualización pendiente y que el lanzador la active en el siguiente reinicio de Codex.
