# Hoja de Ruta (Roadmap): Copiloto Consultor Odoo (OdooConsultor)

Evolución histórica y capacidades del copiloto funcional y técnico de Odoo.

---

## Versiones y Capacidades Completadas

- **0.1.0 – 0.9.0**: Servidor MCP base en C# (.NET 8), inspección declarativa de modelos, campos y permisos; transporte JSON-2 (Odoo 19) y RPC cerrado (Odoo 14-18); almacenamiento seguro en Credenciales de Windows (DPAPI).
- **0.10.0**: Réplicas Studio Fase 1 (campos aditivos, selecciones y vistas heredadas) y compilador de políticas de riesgo.
- **0.11.0**: Diagnóstico y parametrización funcional schema 1; instalación verificada de módulos y aplicaciones oficiales en staging con análisis de dependencias.
- **0.12.0**: Motor de datos sintéticos de demostración operativa: generación de hasta 100 registros tipados y relacionados (terceros, analítica, productos, proyectos, CRM, ventas, compras, facturas, asientos, inventario y mantenimiento).
- **0.13.0**: Planes semánticos de transición ejecutables con `EJECUTAR-DEMO <id>` (confirmar ventas y compras, ganar o perder oportunidades, reservar o validar transferencias y contabilizar facturas).
- **0.14.0 – 0.16.0**: Catálogo ampliado con distribución analítica automática, tarifas, acuerdos de compra, lotes con vencimiento, vehículos de Flota, inventario en Odoo 19, cantidades ejecutadas y gestión de backorders.
- **0.17.0**: Escenarios operativos schema 5 y planes de flujo schema 2; captura automática de documentos derivados y compensación segura sin `unlink`.
- **0.18.0 – 0.19.0**: Réplicas Studio Fases 2, 3 y 4 (modelos estructurales, campos calculados firmados, `base.automation`, webhooks controlados, QWeb, aprobaciones y ACLs).
- **0.20.0**: Herramientas MCP para Tableros y Hojas de Cálculo (`spreadsheet.dashboard`, `documents.document`): validación contra 5 reglas de compatibilidad de Odoo 18/19, enlaces `odoo://view` con objeto `action`, dominios AST 2D, escapes Unicode nativos y auto-reparación (`auto_fix: true`).
- **0.21.0**: Cobertura integral Odoo 17, 18 y 19 con flujos transversales continuos (`demo_preparar_flujo` con bloque `general` o `transversal`) que encadenan transiciones entre múltiples aplicaciones (CRM -> Ventas -> Albaranes -> Facturas -> Pagos) en un solo plan trazable.
- **0.23.0**: Promoción Segura y Declarativa a Producción:
  - Pipeline formal con 3 herramientas MCP (`promocion_preparar_paquete`, `promocion_aplicar_paquete`, `promocion_activar_efectos_externos`), elevando el catálogo a 60 herramientas cerradas.
  - Puertas de seguridad estrictas: 100% verificado en Staging, análisis de colisiones en Producción (`conflicts == 0`), registro obligatorio de snapshot/respaldo previo y ventana de vigencia de 2 horas.
  - Regla estricta de Cero Datos Demo: Prohibición total de incluir registros de prueba (`odoo_ai_demo`, `synthetic_record`, `demo_`) en promociones a producción.
  - Confirmación reforzada: `PROMOVER-PRODUCCION <id> RESPALDO-CONFIRMADO`.
  - Aislamiento de efectos externos: automatizaciones de correos/webhooks se crean inactivas hasta confirmación separada `ACTIVAR-EFECTOS-PRODUCCION <id>`.
  - Auditoría post-aplicación al 100%, emisión de Certificado de Trazabilidad (`certificado_trazabilidad.json`) firmado con SHA-256 y handoff formal a `odoo-documentar-personalizacion`.
- **0.24.0**: Descubrimiento Autónomo de BD y Prueba Integral de Conexión en Vivo:
  - Herramienta de descubrimiento sin autenticación previa (`odoo_descubrir_base_datos`): infiere o descubre el nombre técnico real de la base de datos a partir únicamente de la URL de la instancia (Odoo.sh, SaaS *.odoo.com o On-Premise) junto con la versión y serie del servidor.
  - Herramienta de validación en vivo de conexión (`conexion_probar`): evalúa conectividad real, autenticación, identidad del usuario (`res.users`), permisos de administrador (`base.group_system`) y latencia en milisegundos, reportando limpiamente si el entorno no está configurado, elevando el catálogo a 62 herramientas cerradas.
  - Suite de pruebas de regresión ampliada a 199 comprobaciones automáticas con 0 errores.

---

## Próximos Pasos Planificados

1. **Ampliación de Validaciones Studio**:
   - Detección estática de referencias huérfanas en vistas XML complejas.
   - Simulador de permisos de usuario para verificar ACLs sin ejecutar transacciones en el servidor.
2. **Tableros y Gráficos Avanzados**:
   - Nuevos adaptadores de gráficos Odoo (line, pie, radar) con validación automática de paletas accesibles.
   - Plantillas preconstruidas para tableros financieros ejecutivos.
3. **Optimización de Caché de Descubrimiento**:
   - Caché local indexada de modelos y campos para reducir latencia en inspecciones repetitivas en bases de datos con gran cantidad de módulos instalados.
