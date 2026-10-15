# Interfaz para consultores

La primera interfaz conversacional local ya está implementada mediante Codex
Desktop y `OdooConsultor.dll` mediante su lanzador PowerShell. Consulte `docs/copiloto-consultor.md`.

La primera versión de incorporación segura ya está implementada con una
plantilla Excel y paquetes cifrados `.odooai`. Consulte
`docs/incorporacion-segura.md`.

Una iteración futura podrá añadir la interfaz web descrita abajo, pero debe
conservar el contrato local, las validaciones, los estados y la idempotencia ya
implementados.

## Flujo propuesto

1. El consultor inicia sesión con SSO corporativo y MFA.
2. Selecciona un cliente al que está asignado.
3. Registra URL/base de Odoo y prueba JSON-2.
4. Ingresa Telegram User ID, login Odoo y API key individual. El Chat ID se
   deriva automáticamente y la primera versión solo admite chats privados.
5. El navegador envía la API key por TLS directamente al backend.
6. El backend prueba `res.users/context_get`.
7. Si es válida, la cifra con AES-256-GCM o la guarda en un gestor de secretos.
8. La pantalla muestra solo la huella, últimos cuatro caracteres, fecha y estado.
9. Un segundo consultor o responsable aprueba la activación cuando el cliente lo
   exija.
10. Toda alta, rotación y revocación queda auditada.

## Pantallas mínimas

### Clientes

- nombre y slug;
- URL y base Odoo;
- estado de conexión;
- modelos habilitados;
- límites y retención;
- herramientas activas.

### Usuarios vinculados

- Telegram User ID y usuario;
- login Odoo;
- estado activo/revocado;
- últimos cuatro caracteres;
- fecha de última verificación/uso;
- acciones: verificar, rotar, revocar.

### Modelos

- nombre mostrado;
- proveedor e ID API;
- visión/capacidad/coste;
- resultado y fecha de validación;
- habilitado por cliente;
- prioridad automática.

### Auditoría

- consultor;
- cliente;
- acción administrativa;
- fecha/IP;
- resultado;
- antes/después sin secretos.

## Roles

- `platform_admin`: administra plataforma y claves maestras.
- `consultant`: configura solo clientes asignados.
- `client_admin`: aprueba usuarios de su empresa.
- `auditor`: consulta eventos sin modificar.

## Diseño de seguridad

- SSO/OIDC y MFA obligatorios.
- Sesiones cortas y revocables.
- RBAC por cliente.
- CSRF, rate limiting y validación estricta.
- Nunca devolver ciphertext, nonce o API key al navegador.
- Nunca registrar el cuerpo del formulario.
- Reautenticación para rotar/revocar.
- Doble control opcional.
- Gestor de secretos externo en producción.
- API administrativa en hostname/red separada del webhook público.

## Migración desde los scripts

Los endpoints administrativos actuales ya separan:

- crear cliente;
- vincular/rotar usuario;
- revocar;
- validar modelo;
- habilitar modelo por cliente.

La interfaz futura puede consumir contratos equivalentes, pero debe reemplazar
la clave administrativa compartida por identidad OIDC y autorización por rol.
