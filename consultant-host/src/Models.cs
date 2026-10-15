using System;
using System.Collections.Generic;

namespace OdooConsultor
{
    public sealed class ProjectConfig
    {
        public int schema_version { get; set; }
        public string client_slug { get; set; }
        public string client_name { get; set; }
        public string odoo_version { get; set; }
        public string evidence_policy { get; set; }
        public string write_environment { get; set; }
        public int confirmation_minutes { get; set; }
        public int package_confirmation_minutes { get; set; }
        public int demo_confirmation_minutes { get; set; }
        public string odoo_edition { get; set; }
        public string hosting { get; set; }
        public string api_transport { get; set; }
        public string source_profile { get; set; }
        public string source_library_root { get; set; }
        public string custom_source_path { get; set; }
        public string private_source_processing { get; set; }
        public string enterprise_source_processing { get; set; }
        public string client_source_processing { get; set; }
    }

    public sealed class ConnectionProfile
    {
        public int schema_version { get; set; }
        public string client_slug { get; set; }
        public string environment { get; set; }
        public string credential_target { get; set; }
        public string base_url { get; set; }
        public string database { get; set; }
        public string login { get; set; }
        public long uid { get; set; }
        public string user_name { get; set; }
        public string verified_at { get; set; }
        public bool read_only { get; set; }
        public string credential_fingerprint { get; set; }
        public string server_version { get; set; }
        public string server_series { get; set; }
        public string api_transport { get; set; }
        public string profile_alias { get; set; }
    }

    public sealed class DemoComponent
    {
        public string key { get; set; }
        public string adapter { get; set; }
        public string status { get; set; }
        public Dictionary<string, object> values { get; set; }
        public Dictionary<string, string> references { get; set; }
        public string initial_state { get; set; }
        public string expected_result { get; set; }
        public string cleanup_strategy { get; set; }
        public string capability_hash { get; set; }
        public List<string> commercial_fields { get; set; }
        public string asset_file { get; set; }
        public string asset_sha256 { get; set; }
        public string asset_mimetype { get; set; }
        public long asset_size { get; set; }
        public bool image_required { get; set; }
        public string personalization_variable { get; set; }
        public bool database_verified { get; set; }
        public bool editor_ready { get; set; }
        public string editor_contract { get; set; }
        public string editor_validation { get; set; }
        public string write_fingerprint { get; set; }
    }

    public sealed class DemoStep
    {
        public string key { get; set; }
        public string title { get; set; }
        public string actor { get; set; }
        public string expected { get; set; }
        public string component_key { get; set; }
    }

    public sealed class DemoScenario
    {
        public int schema_version { get; set; }
        public string id { get; set; }
        public string client_slug { get; set; }
        public string demo_slug { get; set; }
        public int demo_number { get; set; }
        public string demo_label { get; set; }
        public string delivery_mode { get; set; }
        public string ownership_registry { get; set; }
        public string registry_status { get; set; }
        public string registry_capability_hash { get; set; }
        public string environment { get; set; }
        public string application { get; set; }
        public string objective { get; set; }
        public List<string> requirements { get; set; }
        public string status { get; set; }
        public string created_at { get; set; }
        public string expires_at { get; set; }
        public string applied_at { get; set; }
        public string verified_at { get; set; }
        public string cleaned_at { get; set; }
        public string capability_hash { get; set; }
        public string source_lock_hash { get; set; }
        public string source_lock_file_hash { get; set; }
        public string profile_fingerprint { get; set; }
        public string server_series { get; set; }
        public string adapter_contract_version { get; set; }
        public string modules_hash { get; set; }
        public Dictionary<string, string> module_states { get; set; }
        public List<Dictionary<string, object>> reference_plan { get; set; }
        public List<Dictionary<string, object>> initial_states { get; set; }
        public List<Dictionary<string, object>> verification_procedure { get; set; }
        public List<Dictionary<string, object>> cleanup_procedure { get; set; }
        public List<Dictionary<string, object>> record_plan { get; set; }
        public List<string> readiness_criteria { get; set; }
        public List<string> external_effects { get; set; }
        public List<string> manual_operations { get; set; }
        public string integrity_hash { get; set; }
        public List<DemoComponent> components { get; set; }
        public List<DemoStep> steps { get; set; }
        public string protected_records { get; set; }
        public List<string> warnings { get; set; }
        public string last_error { get; set; }
        public List<string> replacement_keys { get; set; }
        public string replacement_state { get; set; }
        public string replacement_snapshot { get; set; }
        public string replacement_archived_at { get; set; }
        public string replacement_restored_at { get; set; }
    }

    public sealed class DemoActionItem
    {
        public string key { get; set; }
        public string action { get; set; }
        public string component_key { get; set; }
        public List<string> related_component_keys { get; set; }
        public string model { get; set; }
        public string status { get; set; }
        public string expected_state { get; set; }
        public string precondition_hash { get; set; }
        public bool cancel_backorder { get; set; }
        public bool expect_backorder { get; set; }
        public string verification { get; set; }
        public string block { get; set; }
        public List<string> depends_on { get; set; }
        public string protected_parameters { get; set; }
    }

    public sealed class DemoActionPlan
    {
        public int schema_version { get; set; }
        public string id { get; set; }
        public string client_slug { get; set; }
        public string environment { get; set; }
        public string scenario_id { get; set; }
        public string demo_slug { get; set; }
        public string status { get; set; }
        public string created_at { get; set; }
        public string expires_at { get; set; }
        public string applied_at { get; set; }
        public string verified_at { get; set; }
        public string profile_fingerprint { get; set; }
        public string scenario_integrity_hash { get; set; }
        public string policy_version { get; set; }
        public List<DemoActionItem> actions { get; set; }
        public List<string> warnings { get; set; }
        public string last_error { get; set; }
        public string integrity_hash { get; set; }
        public string flow_block { get; set; }
        public string instance_contract_hash { get; set; }
    }

    public sealed class DemoCompensationItem
    {
        public string component_key { get; set; }
        public string adapter { get; set; }
        public string model { get; set; }
        public string operation { get; set; }
        public string status { get; set; }
        public string precondition_hash { get; set; }
        public string protected_snapshot { get; set; }
        public string verification { get; set; }
    }

    public sealed class DemoCompensationPlan
    {
        public int schema_version { get; set; }
        public string id { get; set; }
        public string client_slug { get; set; }
        public string environment { get; set; }
        public string scenario_id { get; set; }
        public string demo_slug { get; set; }
        public string status { get; set; }
        public string created_at { get; set; }
        public string expires_at { get; set; }
        public string applied_at { get; set; }
        public string profile_fingerprint { get; set; }
        public string policy_version { get; set; }
        public List<DemoCompensationItem> items { get; set; }
        public List<string> warnings { get; set; }
        public string integrity_hash { get; set; }
    }

    public sealed class ChangeOperation
    {
        public string operation_id { get; set; }
        public string type { get; set; }
        public string model { get; set; }
        public Dictionary<string, object> parameters { get; set; }
        public string status { get; set; }
        public long result_id { get; set; }
        public string verification { get; set; }
        public string expected_relations_hash { get; set; }
    }

    public sealed class ChangeSet
    {
        public int schema_version { get; set; }
        public string id { get; set; }
        public string client_slug { get; set; }
        public string environment { get; set; }
        public string category { get; set; }
        public string risk { get; set; }
        public string status { get; set; }
        public string model { get; set; }
        public string created_at { get; set; }
        public string expires_at { get; set; }
        public string applied_at { get; set; }
        public string verified_at { get; set; }
        public string rolled_back_at { get; set; }
        public string precondition_hash { get; set; }
        public string profile_fingerprint { get; set; }
        public string requested_by { get; set; }
        public string impact { get; set; }
        public string rollback_strategy { get; set; }
        public string policy_version { get; set; }
        public string objective { get; set; }
        public string contract_hash { get; set; }
        public string capability_hash { get; set; }
        public string integrity_hash { get; set; }
        public List<Dictionary<string, object>> preconditions { get; set; }
        public List<Dictionary<string, object>> validations { get; set; }
        public List<ChangeOperation> operations { get; set; }
        public List<string> warnings { get; set; }
        public string last_error { get; set; }
        public string replica_slug { get; set; }
        public string replica_manifest_hash { get; set; }
        public List<string> target_models { get; set; }
        public Dictionary<string, object> replica_descriptor { get; set; }
        public string uat_status { get; set; }
        public string uat_target_hash { get; set; }
        public string backup_reference { get; set; }
    }

    public sealed class FunctionalGap
    {
        public string key { get; set; }
        public string kind { get; set; }
        public string status { get; set; }
        public string classification { get; set; }
        public string adapter { get; set; }
        public string module { get; set; }
        public string question { get; set; }
        public string action { get; set; }
        public string reason { get; set; }
        public Dictionary<string, object> current { get; set; }
        public Dictionary<string, object> desired { get; set; }
        public Dictionary<string, object> proposed_operation { get; set; }
    }

    public sealed class FunctionalDiagnostic
    {
        public int schema_version { get; set; }
        public string id { get; set; }
        public string client_slug { get; set; }
        public string diagnostic_slug { get; set; }
        public string environment { get; set; }
        public string application { get; set; }
        public string objective { get; set; }
        public string status { get; set; }
        public string created_at { get; set; }
        public string profile_fingerprint { get; set; }
        public string server_series { get; set; }
        public string policy_version { get; set; }
        public string inventory_hash { get; set; }
        public string precondition_hash { get; set; }
        public string capability_hash { get; set; }
        public string objective_hash { get; set; }
        public string integrity_hash { get; set; }
        public List<Dictionary<string, object>> requirements { get; set; }
        public List<FunctionalGap> gaps { get; set; }
        public List<string> warnings { get; set; }
    }

    public sealed class ModulePlanItem
    {
        public string name { get; set; }
        public string display_name { get; set; }
        public string state { get; set; }
        public string origin { get; set; }
        public string license { get; set; }
        public bool application { get; set; }
        public bool requested { get; set; }
        public List<string> dependencies { get; set; }
        public string structure_hash { get; set; }
    }

    public sealed class ModuleInstallationPlan
    {
        public int schema_version { get; set; }
        public string id { get; set; }
        public string client_slug { get; set; }
        public string environment { get; set; }
        public string objective { get; set; }
        public string demo_slug { get; set; }
        public string status { get; set; }
        public string created_at { get; set; }
        public string expires_at { get; set; }
        public string applied_at { get; set; }
        public string verified_at { get; set; }
        public string profile_fingerprint { get; set; }
        public string server_series { get; set; }
        public string policy_version { get; set; }
        public string precondition_hash { get; set; }
        public string capability_hash { get; set; }
        public string source_lock_hash { get; set; }
        public string integrity_hash { get; set; }
        public List<string> requested_modules { get; set; }
        public List<string> already_installed { get; set; }
        public List<string> modules_to_install { get; set; }
        public List<string> potential_auto_install { get; set; }
        public List<string> installed_by_plan { get; set; }
        public List<ModulePlanItem> inventory { get; set; }
        public List<string> warnings { get; set; }
        public string last_error { get; set; }
    }

    public sealed class AuditEvent
    {
        public int schema_version { get; set; }
        public string event_id { get; set; }
        public string occurred_at { get; set; }
        public string client_slug { get; set; }
        public string environment { get; set; }
        public string actor { get; set; }
        public string tool { get; set; }
        public string package_id { get; set; }
        public string outcome { get; set; }
        public string summary { get; set; }
        public long duration_ms { get; set; }
        public string previous_hash { get; set; }
        public string event_hash { get; set; }
    }

    public sealed class ToolResult
    {
        public bool ok { get; set; }
        public object data { get; set; }
        public string error_code { get; set; }
        public string message { get; set; }

        public static ToolResult Success(object value)
        {
            return new ToolResult { ok = true, data = value };
        }

        public static ToolResult Failure(string code, string value)
        {
            return new ToolResult { ok = false, error_code = code, message = value };
        }
    }

    public sealed class ConsultantException : Exception
    {
        public string Code { get; private set; }

        public ConsultantException(string code, string message) : base(message)
        {
            Code = code;
        }

        public ConsultantException(string code, string message, Exception innerException) : base(message, innerException)
        {
            Code = code;
        }
    }
}
