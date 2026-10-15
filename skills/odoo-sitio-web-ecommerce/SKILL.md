---
name: odoo-sitio-web-ecommerce
description: >-
  Diseña, parametriza, audita y valida la configuración y catálogo comercial del módulo Sitio Web y eCommerce
  en Odoo 18 y 19. Gestiona sitios web, menús, páginas, categorías públicas, cintas (ribbons), etiquetas de producto,
  publicación en tienda, métodos de entrega y proveedores de pago en modo prueba bajo compuertas de seguridad.
---

# Gestión y Parametrización de Sitio Web y eCommerce en Odoo 18 / 19

Esta skill define los lineamientos funcionales, restricciones operativas y procedimientos técnicos para parametrizar de forma segura los módulos `website` y `website_sale` en Odoo 18 y Odoo 19 utilizando el copiloto funcional `OdooConsultor`.

---

## 1. Alcance y Arquitectura de Adaptadores

La parametrización se divide en dos aplicaciones funcionales cerradas:

1. **`sitio_web`**:
   - `website_setting` (`website`): Parámetros generales del sitio (nombre, dominio, compañía, idioma por defecto).
   - `website_menu` (`website.menu`): Estructura de navegación, jerarquía de menús, enlaces y secuencias.
   - `website_page` (`website.page`): Estado de publicación e indexación en motores de búsqueda.
2. **`ecommerce`**:
   - `ecommerce_public_category` (`product.public.category`): Jerarquía de categorías visibles en la tienda online.
   - `ecommerce_ribbon` (`product.ribbon`): Cintas promocionales y distintivas para productos (ej. "Oferta", "Nuevo").
   - `ecommerce_product_tag` (`product.tag`): Etiquetas de filtrado y búsqueda rápida en catálogo.
   - `ecommerce_product_publish` (`product.template`): Publicación de productos, asignación de categorías web, cintas y etiquetas.
   - `ecommerce_delivery_carrier` (`delivery.carrier`): Métodos de entrega de tarifa fija o recogida local.
   - `ecommerce_payment_provider` (`payment.provider`): Proveedores de pago en modo prueba o transferencia bancaria.

Consulta el catálogo completo de campos y restricciones en [catalogo-adaptadores-web.md](references/catalogo-adaptadores-web.md).

---

## 2. Reglas Críticas de Seguridad y Gobierno

### Regla 1: Staging vs Producción
- **Staging:** Las escrituras requieren preparar un paquete funcional (`funcional_preparar_parametrizacion`) y su confirmación explícita mediante la frase exacta `APLICAR <id>`. Los paquetes vencen a las 12 horas.
- **Producción:** Estrictamente **solo lectura**. Toda parametrización para producción genera un Runbook paso a paso documentando qué valores ingresar manualmente en la interfaz de Odoo.

### Regla 2: Prohibición Absoluta de `unlink` (Eliminación)
- Ningún adaptador ejecuta borrado físico (`unlink`) en Odoo.
- Si el modelo cuenta con campo activo (`active` o `is_published`), la reversión archiva o despublica el registro.
- Si el modelo no tiene campo de archivo (como `website`, `website.menu`, `product.public.category`, `product.ribbon`), solo se admiten modificaciones sobre registros existentes (`functional_update`) respaldadas mediante instantáneas DPAPI para restaurar los valores originales en caso de reversión.

### Regla 3: Pasarelas de Pago y Medios de Envío
- **Proveedores de Pago:** Solo se permite configurar proveedores en modo `test` o `disabled`, o métodos manuales como transferencia bancaria (`payment_custom`). Queda estrictamente prohibido ingresar credenciales de producción, API keys secretas de pasarelas (Stripe, PayPal, MercadoPago) o activar pasarelas en modo real.
- **Métodos de Envío:** Únicamente tarifas fijas o recogida (`delivery_type = 'fixed'`). Prohibido instalar o activar conectores con transportistas externos (DHL, FedEx, UPS, Servientrega) en staging y producción.

---

## 3. Flujo de Trabajo Funcional Recomendado

1. **Inventario Funcional:**
   Ejecutar `funcional_inventariar_aplicacion` especificando `sitio_web` o `ecommerce` para verificar el estado de instalación de los módulos, sitios web existentes y registros preconfigurados.
2. **Diagnóstico y Detección de Brechas:**
   Ejecutar `funcional_diagnosticar_objetivo` pasando el objetivo deseado del cliente (ej. definir categorías de eCommerce, menús o publicar productos).
3. **Preparación del Paquete:**
   Si el diagnóstico identifica elementos parametrizables en staging, invocar `funcional_preparar_parametrizacion`.
4. **Confirmación y Aplicación:**
   Presentar el resumen al consultor funcional y esperar la confirmación `APLICAR <id>`.
5. **Verificación de la Entrega:**
   Comprobar mediante lectura directa de los registros que los cambios se reflejan fielmente en la base de datos de staging.
