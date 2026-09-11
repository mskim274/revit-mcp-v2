using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Autodesk.Revit.DB;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.Plugin.Services
{
    // Conservative host policy: only built-in instance Comments/Mark writes
    // have an element-sized footprint. Everything else needs document scope.
    internal sealed class WorkScopeGuard
    {
        public readonly WorkScopeRegistry Registry = new WorkScopeRegistry();
        private static readonly HashSet<string> ReadCommands = new HashSet<string>(StringComparer.Ordinal)
        {
            "ping", "get_project_info", "get_commandset_status", "get_levels", "get_views", "get_grids",
            "query_elements", "get_linked_models", "get_sheets", "get_element_info", "get_element_geometry",
            "get_selected_elements", "get_types_by_category", "get_family_types", "get_all_categories"
        };
        public static bool IsReadOnly(string command) => ReadCommands.Contains(command);

        public object Execute(Document doc, string document, string owner, string token,
            Dictionary<string, object> parameters, CancellationToken ct)
        {
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(parameters));
            var p = json.RootElement;
            var op = String(p, "op");
            if ((op == "status" || op == "acquire") && p.TryGetProperty("lease_token", out _))
                throw Invalid("lease_token is only accepted for renew, release, or disable.");
            var allowed = new HashSet<string>(new[] { "op", "idempotency_key", "lease_token" });
            if (op == "acquire") allowed.UnionWith(new[] { "scope", "element_ids", "label", "ttl_seconds" });
            if (op == "renew") allowed.Add("ttl_seconds");
            if (p.EnumerateObject().Any(property => !allowed.Contains(property.Name)))
                throw Invalid("Remove fields that do not apply to this operation.");
            if (p.TryGetProperty("lease_token", out _)) token = String(p, "lease_token");
            object assignment = null;
            switch (op)
            {
                case "status": return Registry.Status(document, owner);
                case "acquire":
                    var scope = String(p, "scope");
                    if (scope != "document" && scope != "elements") throw Invalid("Use scope=document or elements.");
                    var ids = p.TryGetProperty("element_ids", out var value) ? Ids(value) : Array.Empty<long>();
                    if ((scope == "document" && p.TryGetProperty("element_ids", out _)) ||
                        (scope == "elements" && ids.Length == 0)) throw Invalid("Document scope omits element_ids; elements scope requires 1..5000 host IDs.");
                    var watched = new HashSet<long>();
                    foreach (var id in ids)
                    {
                        ct.ThrowIfCancellationRequested();
                        var element = doc.GetElement(ElementIdCompatibility.Create(id));
                        if (element == null) throw Invalid($"Host element {id} no longer exists. Query current host IDs again.");
                        watched.Add(id);
                        var typeId = element.GetTypeId();
                        if (typeId != ElementId.InvalidElementId) watched.Add(typeId.GetValue());
                        if (element.LevelId != ElementId.InvalidElementId) watched.Add(element.LevelId.GetValue());
                    }
                    assignment = Registry.Describe(Registry.Acquire(document, owner, String(p, "idempotency_key"),
                        String(p, "label"), scope == "document", ids, watched, Ttl(p)));
                    break;
                case "renew": assignment = Registry.Describe(Registry.Renew(document, owner, token, Ttl(p))); break;
                case "release": Registry.Release(document, owner, token); break;
                case "disable": Registry.Disable(document, owner, token); break;
                default: throw Invalid("Use op=status, acquire, renew, release, or disable.");
            }
            return new Dictionary<string, object>
            {
                ["op"] = op, ["coordination_enabled"] = Registry.IsEnabled(document), ["assignment"] = assignment,
                ["verification"] = new { performed = true, state_updated = true },
                ["note"] = "Acquire before querying and editing. Release/expiry leaves coordination enabled; disable requires an exclusive document assignment."
            };
        }

        public WorkScopeRegistry.Lease Check(Document doc, string document, string owner, string token,
            string command, Dictionary<string, object> parameters, CancellationToken ct)
        {
            if (IsReadOnly(command)) return null;
            if (!Registry.IsEnabled(document) && string.IsNullOrEmpty(token)) return null;
            var lease = Registry.Require(document, owner, token);
            if (lease.WholeDocument) return Registry.CheckWrite(document, owner, token, true, Array.Empty<long>());
            var targets = new HashSet<long>();
            var safe = false;
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(parameters));
            var p = json.RootElement;
            if (command == "modify_element_parameter")
                safe = SafeParameter(doc, p, targets);
            else if (command == "batch_modify_parameters")
            {
                safe = true;
                if (p.TryGetProperty("modifications", out var modifications) && modifications.ValueKind != JsonValueKind.Null)
                {
                    if (HasValue(p, "element_ids") || HasValue(p, "parameters") ||
                        modifications.ValueKind != JsonValueKind.Array || modifications.GetArrayLength() < 1 || modifications.GetArrayLength() > 5000)
                        safe = false;
                    else foreach (var item in modifications.EnumerateArray())
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!SafeParameter(doc, item, targets)) { safe = false; break; }
                    }
                }
                else if (p.TryGetProperty("element_ids", out var idArray) && p.TryGetProperty("parameters", out var values) &&
                    values.ValueKind == JsonValueKind.Object && !p.TryGetProperty("is_type_param", out _))
                {
                    var ids = Ids(idArray);
                    var names = values.EnumerateObject().Select(v => v.Name).ToArray();
                    if (ids.Length == 0 || names.Length == 0 || (long)ids.Length * names.Length > 5000) safe = false;
                    else foreach (var id in ids)
                    {
                        ct.ThrowIfCancellationRequested();
                        targets.Add(id);
                        if (names.Any(name => !SafeParameter(doc, id, name))) { safe = false; break; }
                    }
                }
                else safe = false;
            }
            return Registry.CheckWrite(document, owner, token, !safe || targets.Count == 0, targets);
        }

        private static bool SafeParameter(Document doc, JsonElement item, HashSet<long> targets)
        {
            if (item.ValueKind != JsonValueKind.Object ||
                (item.TryGetProperty("is_type_param", out var isType) && isType.ValueKind != JsonValueKind.False) ||
                !item.TryGetProperty("element_id", out var idValue) || !TryId(idValue, out var id) ||
                !item.TryGetProperty("parameter_name", out var name) || name.ValueKind != JsonValueKind.String)
                return false;
            targets.Add(id);
            return SafeParameter(doc, id, name.GetString());
        }
        private static bool SafeParameter(Document doc, long id, string name)
        {
            if (id <= 0) return false;
            var element = doc.GetElement(ElementIdCompatibility.Create(id));
            if (element == null || element is ElementType) return false;
            // LookupParameter can be ambiguous when a shared parameter has the
            // same display name. Do not assume the command will pick ours.
            var matches = element.GetParameters(name);
            if (matches.Count != 1) return false;
            var parameter = matches[0];
            var parameterId = parameter.Id.GetValue();
            return parameter.StorageType == StorageType.String && !parameter.IsReadOnly &&
                (parameterId == (long)BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS || parameterId == (long)BuiltInParameter.ALL_MODEL_MARK);
        }
        private static string String(JsonElement p, string name)
        {
            if (!p.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                throw Invalid($"Supply {name} as a non-empty string.");
            return value.GetString();
        }
        private static int Ttl(JsonElement p)
        {
            if (!p.TryGetProperty("ttl_seconds", out var value)) return 300;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var ttl)) throw Invalid("ttl_seconds must be an integer from 30 to 1800.");
            return ttl;
        }
        private static long[] Ids(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 5000) throw Invalid("element_ids must be an array of at most 5000 positive integers.");
            var ids = new List<long>();
            foreach (var id in value.EnumerateArray())
            {
                if (!TryId(id, out var number))
                    throw Invalid("Use positive safe integer host IDs, or decimal strings for larger signed 64-bit IDs.");
                ids.Add(number);
            }
            return ids.Distinct().ToArray();
        }
        private static bool HasValue(JsonElement p, string name) =>
            p.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null;
        private static bool TryId(JsonElement value, out long id)
        {
            id = 0;
            if (value.ValueKind == JsonValueKind.Number)
                return value.TryGetInt64(out id) && id > 0 && id <= 9007199254740991L;
            if (value.ValueKind != JsonValueKind.String) return false;
            var text = value.GetString();
            return text.Length > 0 && text[0] >= '1' && text[0] <= '9' && text.All(c => c >= '0' && c <= '9') &&
                long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id);
        }
        private static WorkScopeException Invalid(string suggestion) => new WorkScopeException("VALIDATION_ERROR", "Invalid work scope input.", suggestion);
    }
}
