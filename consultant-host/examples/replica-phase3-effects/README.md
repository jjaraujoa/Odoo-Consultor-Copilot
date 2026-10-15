# Réplica de ejemplo Fase 3

Demuestra acciones de código firmadas, actividades, cadenas, correo, webhook
saliente y webhook entrante sobre `res.partner`.

La preparación exige referencias portables. `TEST_EMAIL` y
`TEST_WEBHOOK_URL` se proporcionan únicamente en memoria al aplicar y no se
guardan en el manifiesto, el paquete ni la auditoría. Las automatizaciones con
efectos externos quedan inactivas hasta ejecutar `PROBAR-EFECTO <id>` y luego
`ACTIVAR-EFECTOS <id>`. El webhook entrante requiere además la confirmación
`PRUEBA-MANUAL <id>`.

Este ejemplo no debe aplicarse sin revisar los dominios, el modelo objetivo y
un registro de prueba autorizado.
