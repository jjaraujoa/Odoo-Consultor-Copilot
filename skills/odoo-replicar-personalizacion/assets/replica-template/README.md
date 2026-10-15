# Réplica selectiva: <nombre>

## Objetivo funcional

Describir el comportamiento que se conservará.

## Alcance incluido

- <componente del núcleo funcional>

## Exclusiones explícitas

- Datos de negocio, adjuntos y archivos binarios.
- <componentes ajenos o futuros>

## Dependencias y orden de reconstrucción

1. Validar versión, módulos y permisos en staging.
2. Detectar colisiones de nombres técnicos.
3. Crear campos y vistas en el orden indicado por `manifest.json`.
4. Resolver marcadores con identificadores asignados por el destino.
5. Aplicar cada paquete solo tras `APLICAR <id>`.
6. Verificar metadatos y ejecutar las pruebas autorizadas.

## Política de aplicación

Producción es solo lectura. Acciones con código, automatizaciones, webhooks y seguridad requieren runbook manual. Este paquete no contiene datos de negocio ni secretos.

## Pruebas y verificación

Definir precondición, pasos, resultado esperado y limpieza con datos de prueba autorizados.

## Advertencias

Anotar incompatibilidades, marcadores pendientes y decisiones de alcance.

