# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [1.0.0] - 2026-10-15

### 🚀 Public Open Source General Availability

First official public production release of **Odoo Consultor Copilot**, elevating the local Model Context Protocol (MCP) server to a complete enterprise functional suite.

#### Added
- **62 Strongly-Typed MCP Tools**: Closed declarative contracts covering database discovery, live connection diagnostics, Studio customizations (Phases 1-4), GAP analysis, master data onboarding, spreadsheet/dashboards, and safe production promotion.
- **Autonomous Database Discovery (`odoo_descubrir_base_datos`)**: Infers or discovers technical database name, hosting architecture (SaaS, Odoo.sh, On-Premise), and server version from URL without prior credentials.
- **Live Connection Health Diagnostic (`conexion_probar`)**: Real-time authentication verification against `res.users`, administrative privileges (`base.group_system`), transport negotiation (JSON-2 vs legacy RPC), and roundtrip latency measurement in milliseconds.
- **Safe Production Promotion Pipeline (`promocion_*`)**:
  - Pre-promotion collision detection (`promocion_analizar_impacto`) with zero blocking conflicts gate.
  - Staging verification requirement and mandatory pre-production backup reference token.
  - Reinforced 2-hour window confirmation: `PROMOVER-PRODUCCION <id> RESPALDO-CONFIRMADO`.
  - External effect isolation (automations and webhooks created inactive by default).
  - Cryptographic traceability certificate (`certificado_trazabilidad.json`) signed with SHA-256.
  - Automatic visual synchronization of inherited XML views to eliminate post-promotion manual rework.
- **Studio 4-Phase Replication Engine (`replica_*`)**:
  - Phase 1: Custom fields (`char`, `binary`, `selection`, `many2many`), technical selections, and inherited views.
  - Phase 2: Structural apps/models, calculated and signed fields, window actions, and menus.
  - Phase 3: `base.automation`, email templates, automated activities, and controlled webhooks.
  - Phase 4: QWeb reports, Enterprise approvals, record rules, and ACLs with negative testing.
- **Interactive Dashboards & Spreadsheets (`tablero_*`)**: Full compatibility checking for Odoo 18/19 spreadsheet models (`spreadsheet.dashboard`, `documents.document`) with 2D AST domain normalization and auto-fix capabilities.
- **Automated Handover Artifact Generation**: Delivery minutes / acceptance acts generated automatically in Microsoft Word (`.docx`) and Markdown via `odoo-documentar-personalizacion`.
- **SDK-style Project & CI/CD**: Added `OdooConsultor.csproj` (SDK-style net48) and automated GitHub Actions workflow on `windows-latest` running 199 self-tests and automated release packaging.
- **Bilingual Documentation**: Full English `README.md` and complete Spanish `README.es.md`.
- **MIT License**: Open source licensing under the MIT License by Jorge Araujo.

---

## [0.24.0] - 2026-10-12
- Added live connection testing (`conexion_probar`) with latency benchmarking.
- Added autonomous database discovery (`odoo_descubrir_base_datos`).
- Expanded internal regression suite to 199 automated checks.

## [0.23.0] - 2026-10-07
- Formal production promotion pipeline (`promocion_preparar_paquete`, `promocion_aplicar_paquete`, `promocion_activar_efectos_externos`).
- Zero Demo Data enforcement in production promotions.
- Cryptographic traceability certificate generation (`certificado_trazabilidad.json`).

## [0.21.0] - 2026-09-28
- Continuous cross-application operational flows (`demo_preparar_flujo`): CRM -> Sales -> Delivery -> Invoicing -> Payments.
- Non-destructive demo cleanup (`LIMPIAR-DEMO`) using record archiving instead of `unlink`.

## [0.20.0] - 2026-09-15
- Interactive spreadsheet and dashboard tools with 5 compatibility rules for Odoo 18 and 19.
- Automatic 2D AST domain normalization and Unicode escaping engine.

## [0.18.0] - 2026-09-02
- Studio Phase 2, 3, and 4 engine (structural models, calculated fields, automations, QWeb, and ACLs).

## [0.10.0] - 2026-08-15
- Initial prototype of the C# MCP server over stdio for Odoo 14 to 19 with Windows DPAPI credential isolation.
