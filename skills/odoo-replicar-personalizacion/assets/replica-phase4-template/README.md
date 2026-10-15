# Réplica de gobierno Fase 4: <nombre>

## Objetivo funcional

Describir los reportes, aprobaciones y controles de acceso que se reconstruirán.

## Alcance incluido

- Plantillas QWeb y acciones de reporte.
- Reglas de aprobación Enterprise descubiertas en la base destino.
- ACL, reglas de registro y pruebas negativas de acceso.

## Exclusiones explícitas

- Datos de negocio, adjuntos, credenciales, usuarios reales e IDs del origen.
- Módulos Python, controladores web y cambios directos en producción.

## Aplicación

1. Completar el manifiesto y crear los archivos QWeb/dominio.
2. Calcular el SHA-256 de cada archivo con finales de línea LF.
3. Analizar el destino y resolver grupos, usuarios y formatos mediante XML ID o login.
4. Confirmar exactamente `APLICAR <id>` en staging.
5. Verificar los metadatos y ejecutar cada prueba negativa con un perfil funcional separado.

Producción genera únicamente un runbook. La reversión restaura snapshots o
desactiva componentes creados; no elimina metadatos.
