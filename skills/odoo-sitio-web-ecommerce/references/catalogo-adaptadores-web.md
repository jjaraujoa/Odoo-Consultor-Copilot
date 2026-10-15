# Catálogo de Adaptadores Funcionales: Sitio Web y eCommerce

Este documento detalla los modelos, campos autorizados y políticas de reversión de los adaptadores de `sitio_web` y `ecommerce` en `OdooConsultor` (versión 0.18.0 o superior).

---

## 1. Aplicación: `sitio_web`

Módulos requeridos: `website`

### 1.1. `website_setting`
- **Modelo Odoo:** `website`
- **Propósito:** Configuración general del sitio web corporativo o de tienda.
- **Campos autorizados:**
  - `name`: Nombre descriptivo del sitio web.
  - `domain`: Dominio asignado (ej. `https://staging.miempresa.com`).
  - `company_id`: ID de la compañía propietaria en entornos multicoñía.
  - `default_lang_id`: ID del idioma principal del sitio.
- **Política de reversión:** Modelo sin campo `active`. Modificaciones protegidas por instantánea DPAPI. Soporta creación controlada o actualización.

### 1.2. `website_menu`
- **Modelo Odoo:** `website.menu`
- **Propósito:** Estructura de navegación principal y menús secundarios.
- **Campos autorizados:**
  - `name`: Etiqueta visible en la navegación.
  - `url`: Ruta relativa (ej. `/shop`, `/contactus`) o URL externa.
  - `parent_id`: ID del menú superior para jerarquías anidadas.
  - `sequence`: Orden de aparición en la barra de menú.
  - `website_id`: ID del sitio web al que pertenece el menú.
  - `new_window`: Booleano (abrir enlace en pestaña nueva).
- **Política de reversión:** Instantánea DPAPI en actualizaciones; reajuste seguro sin eliminación.

### 1.3. `website_page`
- **Modelo Odoo:** `website.page`
- **Propósito:** Control de estado de publicación e indexabilidad SEO de páginas web.
- **Campos autorizados:**
  - `is_published`: Publicada o en borrador.
  - `website_indexed`: Permitir indexación en motores de búsqueda (Google, Bing).
  - `website_id`: ID del sitio web al que pertenece la página.
- **Política de reversión:** Archivo/despublicación vía `is_published = false`.

---

## 2. Aplicación: `ecommerce`

Módulos requeridos: `website_sale`

### 2.1. `ecommerce_public_category`
- **Modelo Odoo:** `product.public.category`
- **Propósito:** Categorías públicas que estructuran la navegación de la tienda en línea.
- **Campos autorizados:**
  - `name`: Nombre comercial de la categoría visible al público.
  - `parent_id`: ID de la categoría padre para árbol de categorías.
  - `sequence`: Orden de visualización en el menú lateral y catálogo.
  - `website_id`: ID del sitio web (si se restringe a un sitio particular).
- **Política de reversión:** Actualización con instantánea DPAPI.

### 2.2. `ecommerce_ribbon`
- **Modelo Odoo:** `product.ribbon`
- **Propósito:** Cintas promocionales o etiquetas visuales sobre la imagen del producto (ej. "Sale", "Nuevo", "Agotado").
- **Campos autorizados:**
  - `name`: Nombre técnico de la cinta.
  - `html`: Texto o contenido HTML simple visible en la cinta.
  - `bg_color`: Color de fondo (ej. `#e53e3e`).
  - `text_color`: Color del texto (ej. `#ffffff`).
  - `html_class`: Clase CSS aplicada por el tema de Odoo.
- **Política de reversión:** Actualización con instantánea DPAPI.

### 2.3. `ecommerce_product_tag`
- **Modelo Odoo:** `product.tag`
- **Propósito:** Etiquetas de producto para filtrado faceted en el eCommerce.
- **Campos autorizados:**
  - `name`: Nombre de la etiqueta.
  - `sequence`: Orden de prioridad.
- **Política de reversión:** Archivo/desactivación si aplica o instantánea DPAPI.

### 2.4. `ecommerce_product_publish`
- **Modelo Odoo:** `product.template`
- **Propósito:** Parametrización de visibilidad de productos existentes en la tienda online.
- **Campos autorizados:**
  - `website_published`: Publicado en la tienda web (`True`/`False`).
  - `website_sequence`: Prioridad de visualización en la cuadrícula de productos.
  - `website_ribbon_id`: ID de la cinta promocional asignada (`product.ribbon`).
  - `public_categ_ids`: IDs de las categorías públicas asociadas (comando relacional `[(6, 0, [ids])]`).
  - `product_tag_ids`: IDs de las etiquetas asignadas.
- **Política de reversión:** Reversión mediante `website_published = false` y restauración de estado original vía DPAPI.

### 2.5. `ecommerce_delivery_carrier`
- **Modelo Odoo:** `delivery.carrier`
- **Propósito:** Métodos de entrega disponibles en el checkout de la tienda.
- **Campos autorizados:**
  - `name`: Nombre del método de envío (ej. "Envío Estándar", "Recogida en Tienda").
  - `delivery_type`: Tipo de tarificación (estrictamente restringido a `'fixed'`).
  - `fixed_price`: Costo fijo del envío.
  - `product_id`: ID del producto de servicio asociado al transporte.
  - `is_published`: Visibilidad en el checkout del eCommerce.
- **Política de reversión:** Reversión mediante `active = false` o `is_published = false`.

### 2.6. `ecommerce_payment_provider`
- **Modelo Odoo:** `payment.provider`
- **Propósito:** Pasarelas y medios de pago activos en el eCommerce.
- **Campos autorizados:**
  - `name`: Nombre público del proveedor.
  - `state`: Modo operativo (estrictamente restringido a `'test'` o `'disabled'`).
  - `is_published`: Habilitado en la pantalla de pago del cliente.
  - `company_id`: ID de la compañía.
- **Política de reversión:** Reversión mediante `state = 'disabled'` e `is_published = false`. Prohibido ingresar credenciales secretas en automatizaciones.
