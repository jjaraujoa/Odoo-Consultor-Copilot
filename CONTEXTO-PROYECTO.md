# Contexto y Decisiones del Proyecto: Copiloto Consultor Odoo (OdooConsultor)

## Propósito

Dotar a consultores funcionales y técnicos de Odoo de un asistente inteligente (Copiloto) que opere directamente desde su entorno de desarrollo local (Codex Desktop, Antigravity) para inspeccionar bases de datos Odoo (14 a 19), replicar personalizaciones Studio de manera determinista, generar datos sintéticos para demostraciones operativas y construir tableros y hojas de cálculo sin riesgo de romper entornos empresariales.

---

## Principios Fundamentales de Diseño

### 1. Entorno Producción: Solo Lectura
- Cualquier intento de modificar registros, instalar módulos o aplicar cambios Studio en un entorno catalogado como producción es bloqueado inmediatamente por el servidor MCP.
- En producción solo se admiten inspecciones declarativas y generación de runbooks manuales.

### 2. Entorno Staging: Paquetes Declarativos Confirmados
- Todas las escrituras en staging requieren la generación de un paquete firmado con hash SHA-256.
- Los paquetes vencen a las **12 horas** de su preparación.
- Exigen confirmación exacta por parte del consultor:
  - Cambios Studio / Parametrización: `APLICAR <id>`
  - Creación de datos de demostración: `CREAR-DEMO <id>`
  - Transiciones de flujo (confirmar, validar, pagar): `EJECUTAR-DEMO <id>`
  - Activación de efectos externos / automatizaciones: `ACTIVAR-EFECTOS <id>`
- El motor verifica el resultado por relectura tras la ejecución y nunca repite llamadas ambiguas.

### 3. Cero Secretos en el Chat o Repositorio
- Las credenciales de acceso a Odoo se gestionan mediante el Administrador de Credenciales de Windows protegidas por DPAPI (Data Protection API) a nivel del usuario de Windows.
- Las carpetas locales de proyectos de clientes residen fuera del repositorio (por ejemplo en `C:\OdooAI\Clientes\<slug>`).
- Las herramientas MCP jamás devuelven contraseñas ni API keys en sus respuestas JSON.

### 4. Herramientas MCP Cerradas y Sin Inyección Arbitraria
- El servidor MCP expone 50 herramientas fuertemente tipadas en C#.
- Se rechaza cualquier mecanismo que permita a la IA ejecutar llamadas `execute_kw` libres o inyectar código no firmado.
- Los botones en vistas XML no admiten `type="object"`; deben usar `type="action"` vinculados a claves lógicas del manifiesto de la réplica.

### 5. Demostraciones con Datos Sintéticos Seguros
- Las demostraciones solo generan registros ficticios con identificadores protegidos (`odoo_ai_demo`).
- Nunca se ejecutan transiciones directas en cascada sin un plan semántico verificado.
- La limpieza (`LIMPIAR-DEMO`) archiva o desactiva registros; **jamás ejecuta `unlink` (eliminación permanente)** para preservar la integridad referencial y contable del ERP.
