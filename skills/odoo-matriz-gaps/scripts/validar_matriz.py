#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
scripts/validar_matriz.py - Auditor y Validador de Calidad para la Matriz de GAPs de Odoo
Verifica exhaustivamente el libro Excel generado contra las reglas de consistencia:
1. Catálogo estricto de Odoo Módulo (Columna E) - Apps oficiales y rutas de navegación reales.
2. Coherencia F vs G:K vs N vs P.
3. Estructura de 2 párrafos y 'NO CUBRE' en Columna L cuando F < 100%.
4. Formato de Columna M: 'FUENTE DE NEGOCIO:' obligatorio; 'OBSERVACIÓN:' opcional (sin 'Ninguna').
5. Fórmulas dinámicas y casillas en Consolidado.
6. Consecutivos y conteos reconciliados.
7. Auditoría de cobertura al 100% de fuentes (--knowledge-dir).
"""

import os
import sys
import re
import argparse
import openpyxl

# Salida UTF-8
if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')

MODULOS_OFICIALES = {
    "crm", "ventas", "compras", "inventario", "inventarios", "contabilidad", "facturación",
    "facturacion", "fabricación", "fabricacion", "mrp", "punto de venta", "pos", "tpv",
    "proyectos", "proyecto", "partes de horas", "servicio de asistencia", "helpdesk",
    "reparaciones", "calidad", "mantenimiento", "suscripciones", "sitio web",
    "comercio electrónico", "comercio electronico", "ecommerce", "empleados",
    "reclutamiento", "selección", "seleccion", "ausencias", "gastos", "flota",
    "documentos", "firma", "contactos", "tableros", "hojas de cálculo", "hojas de calculo",
    "ajustes", "configuración", "configuracion", "studio", "localización regional",
    "localizacion regional", "localización / terceros", "localizacion / terceros", "localización", "localizacion", "facturación electrónica", "facturacion electronica"
}

SUBMODULOS_OFICIALES = {
    "iniciativas", "oportunidades", "leads", "pipeline", "equipos de ventas",
    "presupuestos", "pedidos", "cotizaciones", "tarifas", "clientes", "proveedores",
    "solicitudes de presupuesto", "pedidos de compra", "acuerdos de compra",
    "operaciones", "albaranes", "recepciones", "transferencias", "ajustes de inventario",
    "productos", "reglas de abastecimiento", "rutas", "almacenes y ubicaciones", "desechos",
    "lotes", "series", "facturas de cliente", "facturas de proveedor", "asientos contables",
    "conciliación bancaria", "conciliacion bancaria", "bancos", "informes", "informes financieros",
    "plan de cuentas", "activos", "impuestos", "centros de costo", "analítica", "analitica",
    "órdenes de producción", "ordenes de produccion", "listas de materiales",
    "centros de trabajo", "órdenes de trabajo", "ordenes de trabajo", "sesiones", "tickets",
    "sla", "etapas", "puntos de control", "alertas de calidad", "peticiones de mantenimiento",
    "equipamiento", "planes", "contratos", "páginas", "paginas", "catálogo", "catalogo",
    "directorio", "departamentos", "puestos de trabajo", "solicitudes", "peticiones",
    "informes de gastos", "vehículos", "vehiculos", "servicios", "espacios de trabajo",
    "plantillas", "usuarios y compañías", "usuarios y compañias", "automatizaciones",
    "acciones planificadas", "reglas de acceso"
}

def validar_modulo_odoo(modulo_str):
    if not modulo_str or not str(modulo_str).strip():
        return False
    partes = [p.strip().lower() for p in str(modulo_str).split('/')]
    if not partes:
        return False
    modulo_principal = partes[0]
    if modulo_principal not in MODULOS_OFICIALES:
        return False
    if len(partes) > 1:
        for sub in partes[1:]:
            if sub not in MODULOS_OFICIALES and sub not in SUBMODULOS_OFICIALES:
                if not any(k in sub for k in SUBMODULOS_OFICIALES):
                    return False
    return True

def auditar_cobertura_fuentes(knowledge_dir, fuentes_citadas):
    faltantes = set()
    esperados = set()
    if not os.path.exists(knowledge_dir):
        return esperados, faltantes

    for root, _, files in os.walk(knowledge_dir):
        for f in files:
            if not f.endswith('.md'):
                continue
            fpath = os.path.join(root, f)
            with open(fpath, 'r', encoding='utf-8', errors='ignore') as fp:
                txt = fp.read()
                # HUs
                for m in re.finditer(r'##\s+(HU-[A-Z0-9]+-\d+)', txt):
                    esperados.add(m.group(1).upper())
                # Checklist
                for m in re.finditer(r'##\s+([A-Z]{2,5}-\d{2,4})', txt):
                    v = m.group(1).upper()
                    if not v.startswith("HU-") and not v.startswith("REP-"):
                        esperados.add(v)
                # Reportes
                for m in re.finditer(r'id:\s*(REP-[A-Z0-9]+-\d+)', txt):
                    esperados.add(m.group(1).upper())

    for f_id in esperados:
        if f_id not in fuentes_citadas:
            faltantes.add(f_id)

    return esperados, faltantes

def validar_matriz(excel_path, knowledge_dir=None):
    if not os.path.exists(excel_path):
        return {"valido": False, "errores": [f"No existe el archivo: {excel_path}"]}

    wb = openpyxl.load_workbook(excel_path, data_only=False)

    reporte = {
        "archivo": excel_path,
        "valido": True,
        "hojas_auditadas": {},
        "total_requerimientos_funcionales": 0,
        "total_requerimientos_consolidado": 0,
        "fuentes_esperadas": 0,
        "fuentes_citadas": 0,
        "fuentes_faltantes": [],
        "errores": [],
        "advertencias": []
    }

    hojas_excluidas = ["Consolidado"]
    hojas_funcionales = [s for s in wb.sheetnames if s not in hojas_excluidas]

    total_funcionales = 0
    fuentes_encontradas = set()

    for sname in hojas_funcionales:
        ws = wb[sname]
        reqs_hoja = 0
        errores_hoja = []
        advertencias_hoja = []

        for r in range(5, ws.max_row + 1):
            val_a = ws.cell(r, 1).value
            val_b = ws.cell(r, 2).value

            if val_a is None and val_b is None:
                continue

            reqs_hoja += 1
            idx_esperado = reqs_hoja

            # 1. Consecutivo
            try:
                if int(val_a) != idx_esperado:
                    advertencias_hoja.append(f"Fila {r}: Consecutivo en Col A es '{val_a}', se esperaba {idx_esperado}.")
            except (ValueError, TypeError):
                pass

            # 2. Requerimiento (B)
            if not val_b or not str(val_b).strip():
                errores_hoja.append(f"Fila {r}: Columna B (Requerimiento) está vacía.")

            # 3. Módulo Odoo (E)
            val_e = ws.cell(r, 5).value
            if not val_e or not str(val_e).strip():
                errores_hoja.append(f"Fila {r}: Columna E (Odoo Módulo) está vacía.")
            elif not validar_modulo_odoo(str(val_e)):
                errores_hoja.append(f"Fila {r}: Columna E ('{val_e}') no es un módulo o submódulo válido de Odoo.")

            # 4. % Ajuste Nativo (F)
            val_f = ws.cell(r, 6).value
            try:
                f_float = float(val_f) if val_f is not None else 0.0
            except (ValueError, TypeError):
                errores_hoja.append(f"Fila {r}: Columna F (% Ajuste Nativo) no es numérico: '{val_f}'.")
                f_float = 0.0

            # Alternativas G..K
            val_g = bool(ws.cell(r, 7).value)
            val_h = bool(ws.cell(r, 8).value)
            val_i = bool(ws.cell(r, 9).value)
            val_j = bool(ws.cell(r, 10).value)
            val_k = bool(ws.cell(r, 11).value)

            val_l = str(ws.cell(r, 12).value or '').strip()
            val_m = str(ws.cell(r, 13).value or '').strip()
            val_n = str(ws.cell(r, 14).value or '').strip()
            val_p = str(ws.cell(r, 16).value or '').strip()

            # Regla 1: F == 1.0 vs F < 1.0
            if f_float >= 0.999:
                if any([val_g, val_h, val_i, val_j, val_k]):
                    errores_hoja.append(f"Fila {r} ('{val_b}'): F es 100% pero tiene alternativas marcadas en G:K.")
                if val_n.lower() not in ['no', 'false']:
                    errores_hoja.append(f"Fila {r}: F es 100% pero Columna N no es 'No' (valor actual: '{val_n}').")
                if val_p:
                    advertencias_hoja.append(f"Fila {r}: F es 100% pero Columna P tiene texto de desarrollo.")
            else:
                if not any([val_g, val_h, val_i, val_j, val_k]):
                    errores_hoja.append(f"Fila {r} ('{val_b}'): F es menor al 100% ({f_float*100:.0f}%) pero no tiene alternativas en G:K.")
                if val_n.lower() not in ['si', 'sí', 'true']:
                    errores_hoja.append(f"Fila {r}: F es menor al 100% pero Columna N no es 'Si' (valor actual: '{val_n}').")
                if "no cubre" not in val_l.lower():
                    advertencias_hoja.append(f"Fila {r}: F es menor al 100% pero Columna L no contiene 'NO CUBRE'.")

            # Regla 2: Desarrollo (H) vs P
            if val_h and not val_p:
                errores_hoja.append(f"Fila {r} ('{val_b}'): Columna H (Desarrollo) está activa pero Columna P está vacía.")
            elif not val_h and val_p:
                advertencias_hoja.append(f"Fila {r}: Columna H está inactiva pero Columna P tiene texto.")

            # Regla 3: Trazabilidad en M
            if "http://" in val_m.lower() or "https://" in val_m.lower() or "odoo.com" in val_m.lower():
                errores_hoja.append(f"Fila {r}: Columna M contiene enlaces web de Odoo.")

            if re.search(r'observaci[oó]n:\s*(ninguna|n/a|no aplica|-)', val_m, re.I):
                advertencias_hoja.append(f"Fila {r}: Columna M tiene 'OBSERVACIÓN: Ninguna/N/A'. Omitir palabra si no hay observaciones.")

            if "fuente de negocio:" not in val_m.lower():
                errores_hoja.append(f"Fila {r}: Columna M no contiene 'FUENTE DE NEGOCIO:' obligatorio.")
            else:
                # Extraer IDs
                for m_id in re.finditer(r'(HU-[A-Z0-9]+-\d+|REP-[A-Z0-9]+-\d+|[A-Z]{2,5}-\d{2,4})', val_m, re.I):
                    fuentes_encontradas.add(m_id.group(1).upper())

        reporte["hojas_auditadas"][sname] = {
            "requerimientos": reqs_hoja,
            "errores": len(errores_hoja),
            "advertencias": len(advertencias_hoja)
        }
        total_funcionales += reqs_hoja
        reporte["errores"].extend([f"[{sname}] {e}" for e in errores_hoja])
        reporte["advertencias"].extend([f"[{sname}] {w}" for w in advertencias_hoja])

    reporte["total_requerimientos_funcionales"] = total_funcionales

    # 4. Auditoría de la hoja Consolidado
    if "Consolidado" in wb.sheetnames:
        ws_c = wb["Consolidado"]
        reqs_cons = 0
        cons_errores = []

        for r in range(5, ws_c.max_row + 1):
            val_a = ws_c.cell(r, 1).value
            val_b = ws_c.cell(r, 2).value

            if val_a is None and val_b is None:
                continue

            reqs_cons += 1

            if isinstance(val_b, str) and not val_b.startswith("="):
                cons_errores.append(f"Fila {r}: Columna B no es una fórmula vinculada (valor: '{val_b[:30]}').")
            elif val_b and any(err in str(val_b) for err in ["#REF!", "#VALUE!", "#NAME?"]):
                cons_errores.append(f"Fila {r}: Fórmula rota en Columna B ('{val_b}').")

            for c_idx in range(7, 12):
                c_val = str(ws_c.cell(r, c_idx).value or '')
                if not c_val.startswith("="):
                    cons_errores.append(f"Fila {r}, Col {openpyxl.utils.get_column_letter(c_idx)}: Casilla no es fórmula ('{c_val}').")

        reporte["total_requerimientos_consolidado"] = reqs_cons
        if reqs_cons != total_funcionales:
            reporte["errores"].append(f"[Consolidado] Total de filas en Consolidado ({reqs_cons}) no coincide con hojas funcionales ({total_funcionales}).")
        reporte["errores"].extend([f"[Consolidado] {e}" for e in cons_errores])
    else:
        reporte["errores"].append("No se encontró la hoja obligatoria 'Consolidado'.")

    # 5. Cobertura de fuentes
    if knowledge_dir:
        esperados, faltantes = auditar_cobertura_fuentes(knowledge_dir, fuentes_encontradas)
        reporte["fuentes_esperadas"] = len(esperados)
        reporte["fuentes_citadas"] = len(fuentes_encontradas)
        reporte["fuentes_faltantes"] = sorted(list(faltantes))
        if faltantes:
            pct = ((len(esperados) - len(faltantes)) / len(esperados)) * 100 if esperados else 100.0
            muestra = ", ".join(sorted(list(faltantes))[:10])
            extra = f" ... y {len(faltantes)-10} más" if len(faltantes) > 10 else ""
            reporte["errores"].append(f"COBERTURA DE FUENTES INCOMPLETA: Hay {len(faltantes)} fuentes sin mapear en la Matriz ({pct:.1f}% cubierto). Faltantes: {muestra}{extra}.")

    reporte["valido"] = len(reporte["errores"]) == 0
    return reporte

def imprimir_reporte(reporte):
    print("=" * 65)
    print("REPORTE DE AUDITORÍA: MATRIZ DE GAPS ODOO")
    print("=" * 65)
    print(f"Archivo                 : {reporte['archivo']}")
    n_err = len(reporte["errores"])
    estado_txt = "✅ VÁLIDO (0 Errores)" if reporte["valido"] else f"❌ INVÁLIDO ({n_err} Errores Bloqueantes)"
    print(f"Estado General          : {estado_txt}")
    print(f"Requerimientos Totales  : {reporte['total_requerimientos_funcionales']}")
    print(f"Filas en Consolidado    : {reporte['total_requerimientos_consolidado']}")

    if reporte.get("fuentes_esperadas", 0) > 0:
        falt = len(reporte.get("fuentes_faltantes", []))
        tot = reporte["fuentes_esperadas"]
        pct = ((tot - falt) / tot) * 100 if tot else 100.0
        if falt == 0:
            print(f"Cobertura de Fuentes    : ✅ 100% ({tot}/{tot} fuentes mapeadas)")
        else:
            print(f"Cobertura de Fuentes    : ❌ {pct:.1f}% ({falt} fuentes faltantes)")

    print("\nDesglose por Hoja:")
    for hoja, datos in reporte["hojas_auditadas"].items():
        print(f"  - {hoja:15}: {datos['requerimientos']:3} reqs | {datos['errores']} errores | {datos['advertencias']} advertencias")

    if reporte["errores"]:
        print(f"\n❌ ERRORES BLOQUEANTES ({len(reporte['errores'])}):")
        for err in reporte["errores"]:
            print(f"  • {err}")

    if reporte["advertencias"]:
        print(f"\n⚠️ ADVERTENCIAS ({len(reporte['advertencias'])}):")
        for adv in reporte["advertencias"][:15]:
            print(f"  • {adv}")
        if len(reporte["advertencias"]) > 15:
            print(f"  ... y {len(reporte['advertencias']) - 15} advertencias más.")
    print("=" * 65)

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Auditor y Validador de Calidad para la Matriz de GAPs de Odoo")
    parser.add_argument("excel", help="Ruta al archivo Excel de la Matriz de GAPs")
    parser.add_argument("--knowledge-dir", dest="knowledge_dir", help="Directorio con archivos .md de conocimiento para auditar cobertura 100%")
    args = parser.parse_args()

    res = validar_matriz(args.excel, args.knowledge_dir)
    imprimir_reporte(res)
    sys.exit(0 if res["valido"] else 1)
