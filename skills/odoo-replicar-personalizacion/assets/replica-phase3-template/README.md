# Réplica de automatizaciones Fase 3: <nombre>

## Objetivo funcional

Describir el comportamiento automatizado que se reconstruirá.

## Alcance incluido

- Campos calculados actualizados, plantillas, acciones y automatizaciones declaradas.

## Exclusiones explícitas

- Datos de negocio, adjuntos, credenciales y destinos de producción.
- Reportes, aprobaciones, ACL y reglas de registro.

## Orden de reconstrucción

1. Analizar capacidades, permisos, referencias y colisiones en staging.
2. Revisar el diff, los archivos firmados y los snapshots.
3. Confirmar exactamente `APLICAR <id>`.
4. Verificar los metadatos sin activar efectos externos.
5. Probar un destino externo autorizado con `PROBAR-EFECTO <id>`.
6. Activar por separado con `ACTIVAR-EFECTOS <id>`; un webhook entrante requiere además `PRUEBA-MANUAL <id>`.

Producción es solo lectura. Ningún correo o webhook se considera autorizado por
la confirmación de aplicación del paquete.
