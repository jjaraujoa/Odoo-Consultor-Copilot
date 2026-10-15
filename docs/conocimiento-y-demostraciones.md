# Conocimiento verificable y demostraciones funcionales

## Estado actual en OdooConsultor 0.10

La implementación local incorpora biblioteca de fuentes, bloqueo por commits,
búsqueda acotada, identificación de instancia, transporte RPC cerrado para Odoo
18 y escenarios Marketing schema 4. Estos preparan registros reales de staging:
landing no publicada con
formulario CRM cerrado, encuesta inactiva, mailings A/B en borrador y campaña con
actividades de Marketing Automation inactivas. Los nombres visibles son naturales
y la propiedad se protege con DPAPI más XML ID oculto cuando hay permiso. Las
110 comprobaciones locales validan allowlists, HTML generado, confirmaciones,
idempotencia, colisiones, fallback local, recuperación por XML ID, source lock,
auditoría y limpieza sin `unlink`.

En Proarca Odoo 18 Enterprise el escenario real quedó `verified`: 40 componentes
verificados, una sección visual omitida de forma esperada, cero pendientes y
cero efectos externos. La revisión visual completa y `LIMPIAR-DEMO` siguen
pendientes y deben ser autorizados por el consultor.

La lectura existente del entorno piloto en Odoo 19 debe revalidarse después de activar
el host 0.9.0; ninguna credencial se migra ni se vuelve a solicitar si el
perfil sigue vigente.

## Biblioteca compartida

La ruta predeterminada es:

```text
C:\OdooAI\Fuentes\Odoo\
  documentation\18.0\
  documentation\19.0\
  community\18.0\
  community\19.0\
  enterprise\18.0\
  enterprise\19.0\
  index\catalog.json
```

`documentation` corresponde al repositorio público que origina la documentación
publicada; no es evidencia independiente de esa misma documentación. `community`
contiene el núcleo público y aporta código, pruebas, manifiestos y docstrings.
Enterprise agrega módulos privados y Odoo.sh ejecuta Community, Enterprise y el
código del cliente.

Git usa autenticación interactiva o el administrador de credenciales de Windows.
No se admiten tokens en argumentos, archivos, variables MCP o el chat. La
sincronización es explícita y usa avance rápido; se bloquea si el checkout tiene
cambios locales.

## Jerarquía de evidencia

| Nivel | Uso correcto |
|---|---|
| `verificado_base` | Confirmar módulos, permisos, modelos, campos, configuración y resultados reales. |
| `oficial_documentacion` | Explicar el comportamiento previsto para la versión exacta. |
| `oficial_codigo` | Aclarar implementación; el commit no demuestra que esté desplegado. |
| `codigo_cliente` | Explicar módulos privados cuando existe autorización interna. |
| `comunidad_no_verificada` | Proponer hipótesis o ideas que requieren validación. |

Los conflictos se documentan. Una fuente comunitaria nunca confirma que una
función sea nativa ni puede originar por sí sola una escritura.

## Proyecto y conexión Odoo 18

Actualice o cree el proyecto con versión exacta:

```powershell
powershell -NoProfile -ExecutionPolicy RemoteSigned -File .\consultant-host\OdooConsultor.ps1 proyecto actualizar `
  --proyecto "C:\OdooAI\Clientes\cliente-demo" `
  --version 18.0 `
  --edicion enterprise `
  --alojamiento odoo_sh
```

Al registrar Odoo 18, indique siempre el nombre técnico de la base con `--base`.
El ejecutable consulta primero la versión del servidor, bloquea discrepancias y
usa internamente `authenticate` y `execute_kw` solo para los métodos requeridos
por herramientas cerradas. No existe herramienta RPC o JSON‑2 genérica.

## Preparar una demo

1. Identificar versión y transporte con `odoo_identificar_instancia`.
2. Inventariar Marketing con `demo_inventariar_aplicacion`.
3. Revisar/sincronizar fuentes y crear `source-lock.json`.
4. Investigar por versión, módulo y símbolo; etiquetar comunidad como no verificada.
5. Preparar el escenario y revisar la matriz, guiones, advertencias y limpieza.
6. Confirmar exactamente `CREAR-DEMO <id>` dentro de las 12 horas posteriores a
   preparar el escenario.
7. Ensayar la interfaz en modo de solo lectura; el consultor ejecuta cualquier
   paso web que cree registros o produzca efectos.
8. Verificar pasos y escenario por MCP.
9. Confirmar `LIMPIAR-DEMO <id>` y atender elementos marcados para limpieza manual.

La carpeta contiene `brief.yaml`, `fuentes.json`, `source-lock.json`,
`matriz-capacidades.md`, el escenario, checklists, guiones, resultados y limpieza.
No contiene datos reales, secretos, IDs en claro ni contenido privado de código.

El escenario v2 admite una `configuracion` cerrada para textos comerciales,
layouts `hero_form` o `split_form`, preguntas y matrices, variante A/B, imagen
no entregable, variable nativa `recipient_name`, objetivo `crm.lead` o
`mailing.contact` y filtros `all_demo_records` o `has_email`. El motor genera el
HTML y los dominios; el modelo no puede proporcionar XML, QWeb, JavaScript,
expresiones ni dominios Odoo libres.

## Validación real pendiente

En un staging Odoo.sh 18 se debe comprobar: autenticación por API key y nombre de
base; versión y módulos; inventario Marketing; preparación, aplicación,
idempotencia, verificación y limpieza; y ausencia total de correos, mensajes,
publicaciones o webhooks. Después se debe revalidar lectura del entorno piloto Odoo 19.

## Referencias oficiales

- [Repositorio de documentación de Odoo](https://github.com/odoo/documentation)
- [Código fuente público de Odoo](https://github.com/odoo/odoo)
- [Odoo 19: instalación desde fuentes y ediciones](https://www.odoo.com/documentation/19.0/administration/on_premise/source.html)
- [Odoo 18: API externa RPC](https://www.odoo.com/documentation/18.0/developer/reference/external_api.html)
- [Odoo 19: API JSON-2](https://www.odoo.com/documentation/19.0/developer/reference/external_api.html)
- [Acuerdo de partnership y acceso Enterprise](https://www.odoo.com/documentation/19.0/legal/terms/partnership.html)
