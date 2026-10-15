# Réplica de app Fase 2: <nombre>

## Objetivo funcional

Describir la estructura Studio que se reconstruirá.

## Alcance incluido

- Modelos, campos, vistas, acciones de ventana y menús declarados.

## Exclusiones explícitas

- Datos de negocio y adjuntos.
- ACL, reglas, automatizaciones, reportes y efectos externos.

## Orden de reconstrucción

1. Analizar dependencias, permisos y colisiones en staging.
2. Revisar el diff y los snapshots.
3. Confirmar exactamente `APLICAR <id>`.
4. Verificar metadatos y revisar visualmente la app.

Producción es solo lectura. Los modelos nuevos no se consideran accesibles para
usuarios funcionales hasta definir su seguridad en la Fase 4.
