#!/usr/bin/env python3
"""Valida de forma local un paquete de réplica selectiva de Odoo."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any, Iterable


PLACEHOLDER = re.compile(r"\{\{[A-Z][A-Z0-9_]*\}\}")
CUSTOM_FIELD = re.compile(r"\bx_[A-Za-z0-9_]+\b")
SHA256 = re.compile(r"^[a-f0-9]{64}$")
LOGICAL_KEY = re.compile(r"^[a-z][a-z0-9_]{1,62}$")
CUSTOM_MODEL = re.compile(r"^x_[a-z0-9_.]{2,60}$")
REFERENCE_KEY = re.compile(r"^[A-Z][A-Z0-9_]{1,62}$")
REFERENCE_VALUE = re.compile(r"^\{\{([A-Z][A-Z0-9_]{1,62})\}\}$")
COMPONENT_MODES = {"create", "update", "deactivate"}
PHASE2_FIELD_TYPES = {
    "char", "text", "html", "integer", "float", "monetary", "date",
    "datetime", "boolean", "binary", "selection", "many2one", "one2many",
    "many2many",
}
PHASE3_ACTION_STATES = {"code", "multi", "next_activity", "mail_post", "webhook"}
PHASE3_TRIGGERS = {
    "on_stage_set", "on_user_set", "on_tag_set", "on_state_set",
    "on_priority_set", "on_archive", "on_unarchive", "on_create",
    "on_create_or_write", "on_write", "on_unlink", "on_change", "on_time",
    "on_time_created", "on_time_updated", "on_message_received",
    "on_message_sent", "on_webhook",
}
PHASE3_DEPLOYMENT_ORDER = [
    "computed_fields", "mail_templates", "server_actions", "action_chains",
    "views", "automations", "automation_bindings", "activation_gates",
]
PHASE4_DEPLOYMENT_ORDER = [
    "qweb_views", "reports", "approvals", "access_controls",
    "record_rules", "negative_access_tests",
]
PHASE4_REFERENCE_MODELS = {
    "res.groups", "res.users", "report.paperformat",
    "ir.actions.actions", "ir.actions.server",
}
PHASE4_ACCESS_OPERATIONS = {"read", "create", "write", "unlink"}
SENSITIVE_KEY = re.compile(r"(?:api[_-]?key|password|passwd|secret|token|private[_-]?key)", re.I)
SENSITIVE_VALUE = re.compile(
    r"(?:\b(?:bearer|basic)\s+[a-z0-9._~+/-]+=*|\bsk-[a-z0-9_-]{16,}|-----begin [a-z ]*private key-----)",
    re.I,
)
PORTABLE_ID_KEYS = {
    "id",
    "action_id",
    "binding_id",
    "destination_id",
    "inherit_id",
    "record_id",
    "target_id",
    "view_id",
}


class Validator:
    def __init__(self, root: Path) -> None:
        self.root = root.resolve()
        self.errors: list[str] = []
        self.manifest: dict[str, Any] = {}
        self.fields_by_model: dict[str, set[str]] = {}

    def error(self, message: str) -> None:
        self.errors.append(message)

    def require(self, data: dict[str, Any], keys: Iterable[str], location: str) -> None:
        for key in keys:
            if key not in data:
                self.error(f"Falta '{key}' en {location}.")

    def is_phase2(self) -> bool:
        return self.manifest.get("schema_version") == 2 and self.manifest.get("phase") == 2

    def is_phase3(self) -> bool:
        return self.manifest.get("schema_version") == 2 and self.manifest.get("phase") == 3

    def is_phase4(self) -> bool:
        return self.manifest.get("schema_version") == 2 and self.manifest.get("phase") == 4

    def load_manifest(self) -> None:
        path = self.root / "manifest.json"
        if not path.is_file():
            self.error("No existe manifest.json.")
            return
        try:
            loaded = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
            self.error(f"manifest.json no es JSON UTF-8 válido: {exc}.")
            return
        if not isinstance(loaded, dict):
            self.error("manifest.json debe contener un objeto JSON.")
            return
        self.manifest = loaded

    def validate_structure(self) -> None:
        data = self.manifest
        if not (self.root / "README.md").is_file():
            self.error("No existe README.md en el directorio de réplica.")
        required = [
            "schema_version", "name", "description", "source", "dependencies",
            "deployment_order", "excluded_artifacts",
        ]
        if self.is_phase3():
            required += ["references", "computed_fields", "mail_templates", "server_actions", "views", "automations"]
        elif self.is_phase4():
            required += ["references", "qweb_views", "reports", "approvals", "access_controls", "record_rules", "negative_access_tests"]
        else:
            required += ["fields", "views"]
        self.require(data, required, "manifest.json")
        if data.get("schema_version") not in (1, 2):
            self.error("schema_version debe ser 1 o 2.")
        if data.get("phase") is not None and data.get("phase") not in (2, 3, 4):
            self.error("phase solo puede ser 2, 3 o 4 cuando se declara.")
        if self.is_phase2():
            self.require(data, ["models", "window_actions", "menus"], "manifest.json Fase 2")
        if self.is_phase3() and data.get("deployment_order") != PHASE3_DEPLOYMENT_ORDER:
            self.error("deployment_order no respeta el orden cerrado de Fase 3.")
        if self.is_phase4() and data.get("deployment_order") != PHASE4_DEPLOYMENT_ORDER:
            self.error("deployment_order no respeta el orden cerrado de Fase 4.")
        for key in ("name", "description"):
            if not isinstance(data.get(key), str) or not data.get(key, "").strip():
                self.error(f"'{key}' debe ser texto no vacío.")

        source = data.get("source")
        if not isinstance(source, dict):
            self.error("source debe ser un objeto.")
        else:
            self.require(source, ["environment", "odoo_version", "captured_on", "contains_business_records"], "source")
            if source.get("contains_business_records") is not False:
                self.error("source.contains_business_records debe ser false.")
            hashes = source.get("metadata_hashes", {})
            if not isinstance(hashes, dict):
                self.error("source.metadata_hashes debe ser un objeto cuando se declare.")
            else:
                for model, digest in hashes.items():
                    if not isinstance(model, str) or not isinstance(digest, str) or not SHA256.fullmatch(digest):
                        self.error("Cada hash de metadatos debe ser SHA-256 hexadecimal en minúsculas.")

        dependencies = data.get("dependencies")
        if not isinstance(dependencies, dict):
            self.error("dependencies debe ser un objeto.")
        else:
            for key in ("models", "features"):
                if key not in dependencies or not isinstance(dependencies[key], list):
                    self.error(f"dependencies.{key} debe ser una lista.")
        for key in (
            "fields", "views", "deployment_order", "excluded_artifacts", "models",
            "window_actions", "menus", "computed_fields", "mail_templates",
            "server_actions", "automations",
            "qweb_views", "reports", "approvals", "access_controls",
            "record_rules", "negative_access_tests",
        ):
            if key in data and not isinstance(data[key], list):
                self.error(f"'{key}' debe ser una lista.")
        if "references" in data and not isinstance(data["references"], dict):
            self.error("'references' debe ser un objeto.")
        excluded = data.get("excluded_artifacts", [])
        if isinstance(excluded, list):
            if not excluded:
                self.error("excluded_artifacts no puede estar vacío.")
            elif not any(re.search(r"business|negocio|record", str(item), re.I) for item in excluded):
                self.error("excluded_artifacts debe declarar explícitamente la exclusión de datos de negocio.")

    def validate_fields(self) -> None:
        fields = self.manifest.get("fields", [])
        if not isinstance(fields, list):
            return
        seen: set[tuple[str, str]] = set()
        for index, field in enumerate(fields):
            location = f"fields[{index}]"
            if not isinstance(field, dict):
                self.error(f"{location} debe ser un objeto.")
                continue
            self.require(field, (["key"] if self.is_phase2() else []) + ["model", "name", "label", "type"], location)
            if self.is_phase2():
                self.validate_component_identity(field, location, "mode")
            model, name = field.get("model"), field.get("name")
            if not isinstance(model, str) or not model or not isinstance(name, str) or not name:
                self.error(f"{location}.model y {location}.name deben ser texto no vacío.")
                continue
            pair = (model, name)
            if pair in seen:
                self.error(f"Campo duplicado: {model}.{name}.")
            seen.add(pair)
            self.fields_by_model.setdefault(model, set()).add(name)
            field_type = field.get("type")
            if self.is_phase2() and field_type not in PHASE2_FIELD_TYPES:
                self.error(f"{location}.type no está permitido en Fase 2: {field_type}.")
            if field_type == "binary" and not isinstance(field.get("filename_field"), str):
                self.error(f"{location} binary requiere filename_field.")
            if field_type == "many2many":
                self.require(field, ["relation", "relation_table", "column1", "column2"], location)
            if field_type == "one2many":
                self.require(field, ["relation", "relation_field"], location)
            if field_type == "many2one":
                self.require(field, ["relation"], location)
            if field_type == "monetary":
                self.require(field, ["currency_field"], location)
            compute_file = field.get("compute_file")
            if compute_file is not None:
                self.require(field, ["compute_sha256", "depends"], location)
                self.validate_hashed_file(compute_file, field.get("compute_sha256"), f"{location}.compute_file")
            selection = field.get("selection")
            if selection is not None:
                if not isinstance(selection, list):
                    self.error(f"{location}.selection debe ser una lista.")
                else:
                    values: set[str] = set()
                    for option_index, option in enumerate(selection):
                        if not isinstance(option, dict):
                            self.error(f"{location}.selection[{option_index}] debe ser un objeto.")
                            continue
                        self.require(option, ["sequence", "label", "value"], f"{location}.selection[{option_index}]")
                        value = option.get("value")
                        if isinstance(value, str) and value in values:
                            self.error(f"Valor de selección duplicado en {model}.{name}: {value}.")
                        if isinstance(value, str):
                            values.add(value)

    def validate_component_identity(self, component: dict[str, Any], location: str, mode_key: str) -> None:
        key = component.get("key")
        if not isinstance(key, str) or not LOGICAL_KEY.fullmatch(key):
            self.error(f"{location}.key debe ser una clave lógica técnica estable.")
        mode = component.get(mode_key, "create")
        if mode not in COMPONENT_MODES:
            self.error(f"{location}.{mode_key} debe ser create, update o deactivate.")
        if mode in {"update", "deactivate"}:
            previous_hash = component.get("previous_hash")
            if not isinstance(previous_hash, str) or not SHA256.fullmatch(previous_hash):
                self.error(f"{location}.previous_hash es obligatorio para {mode}.")

    def validate_hashed_file(self, relative: Any, expected: Any, location: str) -> None:
        file_path = self.safe_file(relative, location)
        if not isinstance(expected, str) or not SHA256.fullmatch(expected):
            self.error(f"{location} requiere un SHA-256 hexadecimal en minúsculas.")
        elif file_path and self.normalized_sha256(file_path) != expected:
            self.error(f"El hash de {relative} no coincide con el manifiesto.")

    def safe_file(self, relative: str, location: str) -> Path | None:
        if not isinstance(relative, str) or not relative.strip():
            self.error(f"{location} debe ser una ruta relativa no vacía.")
            return None
        path = (self.root / relative).resolve()
        try:
            path.relative_to(self.root)
        except ValueError:
            self.error(f"{location} no puede salir del directorio de réplica.")
            return None
        if not path.is_file():
            self.error(f"No existe el archivo referenciado: {relative}.")
            return None
        return path

    @staticmethod
    def normalized_sha256(path: Path) -> str:
        raw = path.read_bytes().replace(b"\r\n", b"\n").replace(b"\r", b"\n")
        return hashlib.sha256(raw).hexdigest()

    def action_entries(self) -> list[dict[str, Any]]:
        actions = self.manifest.get("actions", [])
        result: list[dict[str, Any]] = []
        if isinstance(actions, list):
            result.extend(item for item in actions if isinstance(item, dict))
        legacy = self.manifest.get("server_action")
        if isinstance(legacy, dict):
            result.append(legacy)
        return result

    def validate_actions(self) -> None:
        for index, action in enumerate(self.action_entries()):
            location = f"actions[{index}]"
            self.require(
                action,
                ["name", "model", "state", "risk_class", "deployment_method", "source_file", "source_sha256"],
                location,
            )
            file_path = self.safe_file(action.get("source_file"), f"{location}.source_file")
            expected = action.get("source_sha256")
            if not isinstance(expected, str) or not SHA256.fullmatch(expected):
                self.error(f"{location}.source_sha256 debe ser SHA-256 hexadecimal en minúsculas.")
            elif file_path and self.normalized_sha256(file_path) != expected:
                self.error(f"El hash de {action.get('source_file')} no coincide con source_sha256.")
            if file_path:
                try:
                    code = file_path.read_text(encoding="utf-8")
                except UnicodeDecodeError:
                    self.error(f"{action.get('source_file')} debe ser texto UTF-8, no binario.")
                    continue
                self.validate_custom_field_references(code, action.get("model"), location)
                self.scan_secrets(code, action.get("source_file"))

    def validate_phase2_components(self) -> None:
        if not self.is_phase2():
            return
        declared_models: set[str] = set()
        for index, model in enumerate(self.manifest.get("models", [])):
            location = f"models[{index}]"
            if not isinstance(model, dict):
                self.error(f"{location} debe ser un objeto.")
                continue
            self.require(model, ["key", "model", "name"], location)
            self.validate_component_identity(model, location, "mode")
            technical = model.get("model")
            if not isinstance(technical, str) or not CUSTOM_MODEL.fullmatch(technical):
                self.error(f"{location}.model debe comenzar por x_ y usar un nombre técnico válido.")
            else:
                declared_models.add(technical)

        action_keys: set[str] = set()
        for index, action in enumerate(self.manifest.get("window_actions", [])):
            location = f"window_actions[{index}]"
            if not isinstance(action, dict):
                self.error(f"{location} debe ser un objeto.")
                continue
            self.require(action, ["key", "name", "res_model", "view_mode"], location)
            self.validate_component_identity(action, location, "mode")
            key = action.get("key")
            if isinstance(key, str):
                action_keys.add(key)
            view_modes = action.get("view_mode")
            if not isinstance(view_modes, str) or not re.fullmatch(r"[a-z]+(?:,[a-z]+)*", view_modes):
                self.error(f"{location}.view_mode no es válido.")

        prior_menus: set[str] = set()
        for index, menu in enumerate(self.manifest.get("menus", [])):
            location = f"menus[{index}]"
            if not isinstance(menu, dict):
                self.error(f"{location} debe ser un objeto.")
                continue
            self.require(menu, ["key", "name"], location)
            self.validate_component_identity(menu, location, "mode")
            parent_keys = [name for name in ("parent_key", "parent_xml_id", "parent_reference") if menu.get(name)]
            if len(parent_keys) > 1:
                self.error(f"{location} debe declarar un solo tipo de padre.")
            parent = menu.get("parent_key")
            if isinstance(parent, str) and parent not in prior_menus:
                self.error(f"{location}.parent_key debe apuntar a un menú declarado antes.")
            action = menu.get("action_key")
            if isinstance(action, str) and action not in action_keys:
                self.error(f"{location}.action_key no corresponde a una acción de ventana declarada.")
            key = menu.get("key")
            if isinstance(key, str):
                prior_menus.add(key)

        for group in ("models", "fields", "views", "window_actions", "menus"):
            seen: set[str] = set()
            for index, component in enumerate(self.manifest.get(group, [])):
                if isinstance(component, dict) and isinstance(component.get("key"), str):
                    if component["key"] in seen:
                        self.error(f"Clave lógica duplicada en {group}: {component['key']}.")
                    seen.add(component["key"])

    def validate_phase3_file(self, component: dict[str, Any], file_key: str, hash_key: str, location: str) -> None:
        relative = component.get(file_key)
        self.validate_hashed_file(relative, component.get(hash_key), f"{location}.{file_key}")
        file_path = self.safe_file(relative, f"{location}.{file_key}")
        if file_path:
            try:
                self.scan_secrets(file_path.read_text(encoding="utf-8"), str(relative))
            except UnicodeDecodeError:
                self.error(f"{relative} debe ser texto UTF-8, no binario.")

    def phase3_reference(self, value: Any, expected_type: str, location: str, expected_model: str | None = None) -> None:
        match = REFERENCE_VALUE.fullmatch(value) if isinstance(value, str) else None
        if not match:
            self.error(f"{location} debe usar una referencia tipada {{{{CLAVE}}}}.")
            return
        spec = self.manifest.get("references", {}).get(match.group(1))
        if not isinstance(spec, dict) or spec.get("type") != expected_type:
            self.error(f"{location} apunta a una referencia ausente o de tipo incorrecto.")
            return
        if expected_model and spec.get("model") != expected_model:
            self.error(f"{location} debe apuntar al modelo {expected_model}.")

    def validate_phase3_components(self) -> None:
        if not self.is_phase3():
            return
        references = self.manifest.get("references", {})
        if isinstance(references, dict):
            for key, spec in references.items():
                location = f"references.{key}"
                if not isinstance(key, str) or not REFERENCE_KEY.fullmatch(key):
                    self.error(f"{location} no tiene una clave técnica válida.")
                if not isinstance(spec, dict):
                    self.error(f"{location} debe ser un objeto tipado.")
                    continue
                ref_type = spec.get("type")
                if ref_type not in {"email", "https_url", "xml_id"}:
                    self.error(f"{location}.type no está permitido.")
                if ref_type == "xml_id" and not isinstance(spec.get("model"), str):
                    self.error(f"{location} XML ID requiere model.")
                if ref_type != "xml_id" and spec.get("model"):
                    self.error(f"{location}: solo xml_id puede declarar model.")

        groups = ("computed_fields", "mail_templates", "server_actions", "views", "automations")
        all_keys: dict[str, dict[str, Any]] = {}
        for group in groups:
            seen: set[str] = set()
            for index, component in enumerate(self.manifest.get(group, [])):
                location = f"{group}[{index}]"
                if not isinstance(component, dict):
                    self.error(f"{location} debe ser un objeto.")
                    continue
                self.validate_component_identity(component, location, "mode")
                key = component.get("key")
                if isinstance(key, str):
                    if key in seen:
                        self.error(f"Clave lógica duplicada en {group}: {key}.")
                    seen.add(key)
                    if group in {"mail_templates", "server_actions", "automations"}:
                        all_keys[f"{group}:{key}"] = component

        for index, field in enumerate(self.manifest.get("computed_fields", [])):
            if not isinstance(field, dict):
                continue
            location = f"computed_fields[{index}]"
            self.require(field, ["key", "model", "name", "mode", "previous_hash", "compute_file", "compute_sha256", "depends"], location)
            if field.get("mode") != "update":
                self.error(f"{location}.mode debe ser update.")
            if not isinstance(field.get("name"), str) or not field["name"].startswith("x_"):
                self.error(f"{location}.name debe ser un campo Studio x_.")
            self.validate_phase3_file(field, "compute_file", "compute_sha256", location)
            depends = field.get("depends")
            if not isinstance(depends, str) or any(not re.fullmatch(r"[a-z][a-z0-9_]*(?:\.[a-z][a-z0-9_]*)*", item.strip()) for item in depends.split(",")):
                self.error(f"{location}.depends contiene una ruta no válida.")

        templates: dict[str, dict[str, Any]] = {}
        for index, template in enumerate(self.manifest.get("mail_templates", [])):
            if not isinstance(template, dict):
                continue
            location = f"mail_templates[{index}]"
            self.require(template, ["key", "name", "model"], location)
            if isinstance(template.get("key"), str):
                templates[template["key"]] = template
            if template.get("mode", "create") != "deactivate":
                self.validate_phase3_file(template, "subject_file", "subject_sha256", location)
                self.validate_phase3_file(template, "body_html_file", "body_html_sha256", location)
            for key in ("email_to", "email_cc"):
                if template.get(key):
                    self.phase3_reference(template[key], "email", f"{location}.{key}")

        actions: dict[str, dict[str, Any]] = {}
        for index, action in enumerate(self.manifest.get("server_actions", [])):
            if not isinstance(action, dict):
                continue
            location = f"server_actions[{index}]"
            self.require(action, ["key", "name", "model", "state"], location)
            key, state = action.get("key"), action.get("state")
            if isinstance(key, str):
                actions[key] = action
            if state not in PHASE3_ACTION_STATES:
                self.error(f"{location}.state no está permitido: {state}.")
            if state == "code":
                self.validate_phase3_file(action, "code_file", "code_sha256", location)
            elif state == "multi" and not action.get("child_action_keys"):
                self.error(f"{location} requiere child_action_keys.")
            elif state == "mail_post":
                if action.get("template_key") not in templates:
                    self.error(f"{location}.template_key no corresponde a una plantilla declarada.")
            elif state == "next_activity":
                self.phase3_reference(action.get("activity_type_reference"), "xml_id", f"{location}.activity_type_reference", "mail.activity.type")
                if action.get("activity_user_reference"):
                    self.phase3_reference(action["activity_user_reference"], "xml_id", f"{location}.activity_user_reference", "res.users")
                if action.get("activity_note_file"):
                    self.validate_phase3_file(action, "activity_note_file", "activity_note_sha256", location)
            elif state == "webhook":
                self.phase3_reference(action.get("webhook_url"), "https_url", f"{location}.webhook_url")
                fields = action.get("webhook_fields")
                if not isinstance(fields, list) or not 1 <= len(fields) <= 50:
                    self.error(f"{location}.webhook_fields debe contener entre 1 y 50 campos.")

        children: set[str] = set()
        for key, action in actions.items():
            if action.get("state") != "multi":
                continue
            for child in action.get("child_action_keys", []):
                if child not in actions or child == key:
                    self.error(f"server_actions.{key} referencia una acción hija no válida: {child}.")
                elif child in children:
                    self.error(f"La acción hija {child} pertenece a más de una cadena.")
                else:
                    children.add(child)

        def visit(key: str, visiting: set[str], complete: set[str]) -> None:
            if key in complete or key not in actions:
                return
            if key in visiting:
                self.error("Las acciones encadenadas contienen un ciclo.")
                return
            visiting.add(key)
            for child in actions[key].get("child_action_keys", []):
                visit(child, visiting, complete)
            visiting.remove(key)
            complete.add(key)

        complete: set[str] = set()
        for key in actions:
            visit(key, set(), complete)

        for index, view in enumerate(self.manifest.get("views", [])):
            if not isinstance(view, dict):
                continue
            location = f"views[{index}]"
            self.require(view, ["key", "name", "model", "arch_file", "arch_sha256", "action_key"], location)
            parents = [name for name in ("inherit_view_name", "inherit_view_xml_id") if view.get(name)]
            if len(parents) != 1:
                self.error(f"{location} debe declarar exactamente un padre heredado.")
            action_key = view.get("action_key")
            if action_key not in actions:
                self.error(f"{location}.action_key no corresponde a una acción declarada.")
            elif actions[action_key].get("model") != view.get("model"):
                self.error(f"{location} y su acción deben usar el mismo modelo.")

        bound: set[str] = set()
        for index, automation in enumerate(self.manifest.get("automations", [])):
            if not isinstance(automation, dict):
                continue
            location = f"automations[{index}]"
            self.require(automation, ["key", "name", "model", "trigger", "action_keys"], location)
            trigger = automation.get("trigger")
            if automation.get("install_active") is True:
                self.error(f"{location}.install_active debe ser false.")
            activation_mode = automation.get("activation_mode")
            if activation_mode not in {None, "immediate", "gated"}:
                self.error(f"{location}.activation_mode debe ser immediate o gated.")
            if trigger not in PHASE3_TRIGGERS:
                self.error(f"{location}.trigger no está permitido: {trigger}.")
            for prefix in ("filter_domain", "filter_pre_domain"):
                if automation.get(f"{prefix}_file"):
                    self.validate_phase3_file(automation, f"{prefix}_file", f"{prefix}_sha256", location)
            if trigger == "on_webhook":
                self.validate_phase3_file(automation, "record_getter_file", "record_getter_sha256", location)
            keys = automation.get("action_keys")
            if not isinstance(keys, list) or not keys:
                self.error(f"{location}.action_keys debe ser una lista no vacía.")
                continue
            for key in keys:
                if key not in actions:
                    self.error(f"{location} referencia una acción no declarada: {key}.")
                    continue
                if key in children:
                    self.error(f"{location} no puede vincular directamente la acción hija {key}.")
                if key in bound:
                    self.error(f"La acción {key} pertenece a más de una automatización.")
                bound.add(key)
                if actions[key].get("model") != automation.get("model"):
                    self.error(f"{location} y la acción {key} deben usar el mismo modelo.")
                if trigger == "on_change" and actions[key].get("state") != "code":
                    self.error(f"{location}: on_change solo admite acciones code.")

    def validate_phase4_components(self) -> None:
        if not self.is_phase4():
            return
        references = self.manifest.get("references", {})
        if isinstance(references, dict):
            for key, spec in references.items():
                location = f"references.{key}"
                if not isinstance(key, str) or not REFERENCE_KEY.fullmatch(key):
                    self.error(f"{location} no tiene una clave técnica válida.")
                if not isinstance(spec, dict):
                    self.error(f"{location} debe ser un objeto tipado.")
                    continue
                if spec.get("type") not in {"xml_id", "user_login"}:
                    self.error(f"{location}.type debe ser xml_id o user_login.")
                if spec.get("model") not in PHASE4_REFERENCE_MODELS:
                    self.error(f"{location}.model no está permitido en Fase 4.")
                if spec.get("type") == "user_login" and spec.get("model") != "res.users":
                    self.error(f"{location}: user_login solo admite res.users.")

        component_keys: set[str] = set()
        groups = ("qweb_views", "reports", "approvals", "access_controls", "record_rules")
        for group in groups:
            for index, component in enumerate(self.manifest.get(group, [])):
                location = f"{group}[{index}]"
                if not isinstance(component, dict):
                    self.error(f"{location} debe ser un objeto.")
                    continue
                self.validate_component_identity(component, location, "mode")
                key = component.get("key")
                if isinstance(key, str):
                    if key in component_keys:
                        self.error(f"Clave lógica duplicada en Fase 4: {key}.")
                    component_keys.add(key)

        qweb_keys: set[str] = set()
        for index, view in enumerate(self.manifest.get("qweb_views", [])):
            if not isinstance(view, dict):
                continue
            location = f"qweb_views[{index}]"
            self.require(view, ["key", "name", "template_name"], location)
            if isinstance(view.get("key"), str):
                qweb_keys.add(view["key"])
            if not isinstance(view.get("template_name"), str) or not re.fullmatch(r"odooai\.[a-z0-9_]{2,100}", view["template_name"]):
                self.error(f"{location}.template_name debe comenzar por odooai.")
            if view.get("mode", "create") != "deactivate":
                self.validate_hashed_file(view.get("arch_file"), view.get("arch_sha256"), f"{location}.arch_file")
                file_path = self.safe_file(view.get("arch_file"), f"{location}.arch_file")
                if file_path:
                    try:
                        xml = file_path.read_text(encoding="utf-8")
                        root = ET.fromstring(xml)
                        if not any(node.attrib.get("t-name") == view.get("template_name") for node in root.iter()):
                            self.error(f"{location}: el XML no declara el t-name exacto.")
                        for node in root.iter():
                            if str(node.tag).lower() in {"script", "iframe", "object", "embed"}:
                                self.error(f"{location} contiene un elemento ejecutable no permitido.")
                            for name, value in node.attrib.items():
                                if name.lower().startswith("on") or name.lower() in {"t-js", "t-raw", "t-debug", "t-log"}:
                                    self.error(f"{location} contiene una directiva QWeb no permitida.")
                                if name.lower() in {"href", "src"} and re.match(r"\s*(?:javascript|data):", value, re.I):
                                    self.error(f"{location} contiene una URL insegura.")
                    except (UnicodeDecodeError, ET.ParseError) as exc:
                        self.error(f"{location}.arch_file no contiene XML UTF-8 válido: {exc}.")

        def require_ref(key: Any, models: set[str], location: str) -> None:
            spec = references.get(key) if isinstance(key, str) and isinstance(references, dict) else None
            if not isinstance(spec, dict) or spec.get("model") not in models:
                self.error(f"{location} apunta a una referencia ausente o de modelo incorrecto.")

        for index, report in enumerate(self.manifest.get("reports", [])):
            if not isinstance(report, dict):
                continue
            location = f"reports[{index}]"
            self.require(report, ["key", "name", "model", "template_key"], location)
            if report.get("template_key") not in qweb_keys:
                self.error(f"{location}.template_key no corresponde a QWeb de la misma réplica.")
            if report.get("report_type", "qweb-pdf") not in {"qweb-pdf", "qweb-html"}:
                self.error(f"{location}.report_type no está permitido.")
            if report.get("paperformat_ref"):
                require_ref(report["paperformat_ref"], {"report.paperformat"}, f"{location}.paperformat_ref")
            for ref in report.get("group_refs", []):
                require_ref(ref, {"res.groups"}, f"{location}.group_refs")
            for file_key, hash_key in (("print_report_name_file", "print_report_name_sha256"), ("attachment_file", "attachment_sha256"), ("domain_file", "domain_sha256")):
                if report.get(file_key) or report.get(hash_key):
                    self.validate_hashed_file(report.get(file_key), report.get(hash_key), f"{location}.{file_key}")

        for index, approval in enumerate(self.manifest.get("approvals", [])):
            if not isinstance(approval, dict):
                continue
            location = f"approvals[{index}]"
            self.require(approval, ["key", "name", "model"], location)
            if approval.get("mode", "create") != "deactivate":
                targets = bool(approval.get("method")) + bool(approval.get("action_ref"))
                if targets != 1:
                    self.error(f"{location} debe declarar exactamente method o action_ref.")
                if not approval.get("approver_refs") and not approval.get("approval_group_ref"):
                    self.error(f"{location} requiere usuarios o grupo aprobador.")
            if approval.get("action_ref"):
                require_ref(approval["action_ref"], {"ir.actions.actions", "ir.actions.server"}, f"{location}.action_ref")
            for ref in approval.get("approver_refs", []) + approval.get("notify_user_refs", []):
                require_ref(ref, {"res.users"}, f"{location}.user_refs")
            if approval.get("approval_group_ref"):
                require_ref(approval["approval_group_ref"], {"res.groups"}, f"{location}.approval_group_ref")
            if approval.get("domain_file") or approval.get("domain_sha256"):
                self.validate_hashed_file(approval.get("domain_file"), approval.get("domain_sha256"), f"{location}.domain_file")

        for group, ref_field in (("access_controls", "group_ref"), ("record_rules", "group_refs")):
            for index, item in enumerate(self.manifest.get(group, [])):
                if not isinstance(item, dict):
                    continue
                location = f"{group}[{index}]"
                self.require(item, ["key", "name", "model"], location)
                refs = item.get(ref_field, []) if ref_field == "group_refs" else ([item.get(ref_field)] if item.get(ref_field) else [])
                if bool(item.get("global")) == bool(refs):
                    self.error(f"{location} debe declarar grupo(s) o global:true, pero no ambos.")
                for ref in refs:
                    require_ref(ref, {"res.groups"}, f"{location}.{ref_field}")
                if item.get("mode", "create") != "deactivate" and not any(item.get(f"perm_{name}") is True for name in ("read", "write", "create", "unlink")):
                    self.error(f"{location} debe aplicar al menos un permiso.")
                if group == "record_rules" and item.get("mode", "create") != "deactivate":
                    self.validate_hashed_file(item.get("domain_file"), item.get("domain_sha256"), f"{location}.domain_file")

        for index, test in enumerate(self.manifest.get("negative_access_tests", [])):
            location = f"negative_access_tests[{index}]"
            if not isinstance(test, dict):
                self.error(f"{location} debe ser un objeto.")
                continue
            self.require(test, ["key", "model", "operation", "expected_allowed"], location)
            if test.get("operation") not in PHASE4_ACCESS_OPERATIONS:
                self.error(f"{location}.operation no está permitida.")
            if test.get("probe_domain_file") or test.get("probe_domain_sha256"):
                if test.get("operation") != "read":
                    self.error(f"{location}: probe_domain solo se admite para read.")
                self.validate_hashed_file(test.get("probe_domain_file"), test.get("probe_domain_sha256"), f"{location}.probe_domain_file")
        if (self.manifest.get("access_controls") or self.manifest.get("record_rules")) and not self.manifest.get("negative_access_tests"):
            self.error("Toda réplica Fase 4 con seguridad debe declarar negative_access_tests.")

    def validate_views(self) -> None:
        views = self.manifest.get("views", [])
        if not isinstance(views, list):
            return
        for index, view in enumerate(views):
            location = f"views[{index}]"
            if not isinstance(view, dict):
                self.error(f"{location} debe ser un objeto.")
                continue
            if self.is_phase2():
                self.require(view, ["key", "model", "type"], location)
                self.validate_component_identity(view, location, "operation")
            elif self.is_phase3():
                self.require(view, ["key", "name", "model", "arch_file", "arch_sha256", "action_key"], location)
                self.validate_component_identity(view, location, "mode")
            else:
                self.require(view, ["model", "type", "mode", "inherit_view_name", "priority", "active", "arch_template"], location)
            xml = view.get("arch_template")
            if (self.is_phase2() or self.is_phase3()) and isinstance(view.get("arch_file"), str):
                self.validate_hashed_file(view.get("arch_file"), view.get("arch_sha256"), f"{location}.arch_file")
                file_path = self.safe_file(view.get("arch_file"), f"{location}.arch_file")
                if file_path:
                    try:
                        xml = file_path.read_text(encoding="utf-8")
                    except UnicodeDecodeError:
                        self.error(f"{location}.arch_file debe ser XML UTF-8.")
            if (self.is_phase2() and view.get("operation", "create") == "deactivate") or (self.is_phase3() and view.get("mode", "create") == "deactivate"):
                if not xml:
                    continue
                continue
            if not isinstance(xml, str) or not xml.strip():
                self.error(f"{location} requiere arch_template o arch_file con XML no vacío.")
                continue
            try:
                root = ET.fromstring(xml)
            except ET.ParseError as exc:
                self.error(f"XML inválido en {location}.arch_template: {exc}.")
                continue
            model = view.get("model")
            if not isinstance(model, str):
                continue
            if self.is_phase2():
                inherited = bool(view.get("inherit_view_name") or view.get("inherit_view_xml_id"))
                if not model.startswith("x_") and not inherited:
                    self.error(f"{location} debe heredar una vista al extender el modelo estándar {model}.")
            placeholders = set(PLACEHOLDER.findall(xml))
            if self.is_phase3():
                placeholders.discard("{{ACTION_ID}}")
            declared = view.get("placeholders", {})
            if placeholders and not isinstance(declared, dict):
                self.error(f"{location}.placeholders debe declarar todos los marcadores XML.")
                declared = {}
            if isinstance(declared, dict):
                missing = placeholders - set(declared)
                extra = set(declared) - placeholders
                for marker in sorted(missing):
                    self.error(f"Marcador sin resolución declarada en {location}: {marker}.")
                for marker in sorted(extra):
                    self.error(f"Marcador declarado pero no usado en {location}: {marker}.")
                for marker, explanation in declared.items():
                    if not PLACEHOLDER.fullmatch(str(marker)) or not isinstance(explanation, str) or not explanation.strip():
                        self.error(f"Marcador inválido o sin explicación en {location}.placeholders.")
            for element in root.iter():
                if element.tag == "field":
                    name = element.attrib.get("name")
                    if name:
                        self.validate_field_name(name, model, location)
                    filename = element.attrib.get("filename")
                    if filename:
                        self.validate_field_name(filename, model, location)
                if element.tag == "button" and element.attrib.get("type") == "action":
                    name = element.attrib.get("name", "")
                    if self.is_phase2():
                        self.error(f"{location} contiene un botón ejecutable; corresponde a la Fase 3.")
                    if name.isdigit():
                        self.error(f"{location} contiene un ID numérico de acción; usar un marcador declarado.")
                    if self.is_phase3() and name != "{{ACTION_ID}}":
                        self.error(f"{location} debe usar name={{{{ACTION_ID}}}} en todos los botones de acción.")
                if element.tag == "button" and element.attrib.get("type") == "object":
                    self.error(f"{location} contiene un botón type=object, que no es portable ni ejecutable por la réplica.")

    def validate_field_name(self, name: str, model: str, location: str) -> None:
        if name == "x_name" and any(item.get("model") == model for item in self.manifest.get("models", []) if isinstance(item, dict)):
            return
        if name.startswith("x_") and name not in self.fields_by_model.get(model, set()):
            self.error(f"{location} usa el campo no incluido {model}.{name}.")

    def validate_custom_field_references(self, code: str, model: Any, location: str) -> None:
        if not isinstance(model, str):
            return
        for name in sorted(set(CUSTOM_FIELD.findall(code))):
            if not any(name in names for names in self.fields_by_model.values()):
                self.error(f"{location} usa el campo personalizado no incluido: {name}.")

    def scan_secrets(self, value: Any, location: str = "manifest.json") -> None:
        if isinstance(value, dict):
            for key, nested in value.items():
                key_text = str(key)
                if SENSITIVE_KEY.search(key_text):
                    self.error(f"Posible secreto en la clave {location}.{key_text}.")
                self.scan_secrets(nested, f"{location}.{key_text}")
        elif isinstance(value, list):
            for index, nested in enumerate(value):
                self.scan_secrets(nested, f"{location}[{index}]")
        elif isinstance(value, str) and SENSITIVE_VALUE.search(value):
            self.error(f"Posible secreto en {location}.")

    def validate_portable_ids(self, value: Any, path: str = "manifest") -> None:
        if isinstance(value, dict):
            for key, nested in value.items():
                next_path = f"{path}.{key}"
                if key in PORTABLE_ID_KEYS and ".source_evidence" not in path:
                    if isinstance(nested, int) or (isinstance(nested, str) and nested.isdigit()):
                        self.error(f"ID numérico no portable en {next_path}; usar un marcador o source_evidence.")
                self.validate_portable_ids(nested, next_path)
        elif isinstance(value, list):
            for index, nested in enumerate(value):
                self.validate_portable_ids(nested, f"{path}[{index}]")

    def run(self) -> list[str]:
        if not self.root.is_dir():
            return [f"El directorio no existe: {self.root}"]
        self.load_manifest()
        if not self.manifest:
            return self.errors
        self.validate_structure()
        self.scan_secrets(self.manifest)
        self.validate_portable_ids(self.manifest)
        self.validate_fields()
        self.validate_views()
        self.validate_actions()
        self.validate_phase2_components()
        self.validate_phase3_components()
        self.validate_phase4_components()
        return self.errors


def main() -> int:
    parser = argparse.ArgumentParser(description="Valida un paquete de réplica Odoo sin conectarse a Odoo.")
    parser.add_argument("replica", type=Path, help="Directorio que contiene manifest.json")
    args = parser.parse_args()
    errors = Validator(args.replica).run()
    if errors:
        print("VALIDACIÓN FALLIDA")
        for error in errors:
            print(f"- {error}")
        return 1
    print("VALIDACIÓN CORRECTA: paquete portable, coherente y sin secretos evidentes.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
