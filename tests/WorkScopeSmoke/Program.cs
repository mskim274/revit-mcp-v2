using RevitMCP.Plugin.Services;
using Autodesk.Revit.DB;

var passed = 0;
void Test(string name, Action run) { run(); passed++; Console.WriteLine($"PASS {name}"); }
void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
void Error(string code, Action run)
{
    try { run(); } catch (WorkScopeException ex) { Assert(ex.Code == code && !string.IsNullOrWhiteSpace(ex.Suggestion)); return; }
    throw new Exception("Expected " + code);
}
var now = new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc);
var a = new string('a', 32); var b = new string('b', 32); var c = new string('c', 32);
var r = new WorkScopeRegistry(() => now);
WorkScopeRegistry.Lease Claim(string owner, string key, long id, bool whole = false) =>
    r.Acquire("doc", owner, key, key, whole, whole ? [] : [id], [id, 1000], 300);
Test("legacy writes work before coordination", () => Assert(r.CheckWrite("doc", null, null, true, []) == null));
var la = Claim(a, "a1", 1);
var lb = Claim(b, "b1", 2);
Test("disjoint assignments coexist", () => Assert(la.Token != lb.Token));
Test("status hides other owners' tokens", () => {
    var status = System.Text.Json.JsonSerializer.Serialize(r.Status("doc", a));
    Assert(status.Contains(la.Token) && !status.Contains(lb.Token));
});
Test("overlap rejected", () => Error("WORK_SCOPE_CONFLICT", () => Claim(c, "c1", 1)));
Test("whole-document conflicts with elements", () => Error("WORK_SCOPE_CONFLICT", () => Claim(c, "c2", 0, true)));
Test("one assignment per owner", () => Error("VALIDATION_ERROR", () => Claim(a, "a2", 3)));
Test("acquire retry returns original token", () => Assert(Claim(a, "a1", 1).Token == la.Token));
Test("changed acquire replay rejected", () => Error("IDEMPOTENCY_CONFLICT", () => Claim(a, "a1", 9)));
Test("unreserved actor blocked", () => Error("WORK_SCOPE_REQUIRED", () => r.CheckWrite("doc", c, null, true, [])));
Test("legacy connection blocked", () => Error("VALIDATION_ERROR", () => r.CheckWrite("doc", null, null, true, [])));
Test("token cannot be borrowed by different owner", () => Error("WORK_SCOPE_EXPIRED", () => r.CheckWrite("doc", b, la.Token, false, [1])));
Test("write inside assignment succeeds", () => Assert(r.CheckWrite("doc", a, la.Token, false, [1]) == la));
Test("batch partly outside rejected", () => Error("WORK_SCOPE_OUTSIDE", () => r.CheckWrite("doc", a, la.Token, false, [1, 2])));
Test("broad operation needs document", () => Error("WORK_SCOPE_OUTSIDE", () => r.CheckWrite("doc", a, la.Token, true, [])));
Test("document isolation", () => Assert(r.CheckWrite("other", null, null, true, []) == null));
Test("cross-document token rejected", () => Error("WORK_SCOPE_EXPIRED", () => r.CheckWrite("other", a, la.Token, false, [1])));
r.Changed("doc", [1]);
Test("manual target change rejects stale edit", () => Error("WORK_SCOPE_STALE", () => r.CheckWrite("doc", a, la.Token, false, [1])));
Test("disjoint changes do not stale another scope", () => Assert(!lb.Stale));
Test("renew does not acknowledge stale evidence", () => { r.Renew("doc", a, la.Token, 600); Assert(la.Stale); });
r.CompleteWrite(la);
Test("successful own edit advances baseline", () => Assert(!la.Stale));
r.Changed("doc", [1000]);
Test("shared type change stales both dependents", () => Assert(la.Stale && lb.Stale));
Test("other owners cannot release", () => Error("WORK_SCOPE_EXPIRED", () => r.Release("doc", c, la.Token)));
r.Release("doc", a, la.Token); r.Release("doc", a, la.Token);
Test("released acquire replay cannot resurrect", () => Error("WORK_SCOPE_EXPIRED", () => Claim(a, "a1", 1)));
now = now.AddHours(1);
Test("expiry fences previously queued writes", () => Error("WORK_SCOPE_EXPIRED", () => r.CheckWrite("doc", b, lb.Token, false, [2])));
Test("expired acquire retry cannot resurrect", () => Error("WORK_SCOPE_EXPIRED", () => Claim(b, "b1", 2)));
Test("expiry cannot silently disable coordination", () => Assert(r.IsEnabled("doc")));
Test("expired token cannot renew", () => Error("WORK_SCOPE_EXPIRED", () => r.Renew("doc", b, lb.Token, 300)));
var lc = Claim(c, "c3", 0, true);
Test("document holder may perform broad operations", () => Assert(r.CheckWrite("doc", c, lc.Token, true, []) == lc));
r.Changed("doc", [999]);
Test("any transaction stales document lease", () => Assert(lc.Stale));
r.Disable("doc", c, lc.Token);
Test("disable response may be replayed", () => r.Disable("doc", c, lc.Token));
Test("explicit disable restores legacy mode", () => Assert(!r.IsEnabled("doc") && r.CheckWrite("doc", null, null, true, []) == null));
Test("old token still rejected after disable", () => Error("WORK_SCOPE_EXPIRED", () => r.CheckWrite("doc", c, lc.Token, true, [])));
var reenabled = Claim(a, "a3", 1);
Test("old disable token cannot disable a newer coordination session", () => Error("WORK_SCOPE_EXPIRED", () => r.Disable("doc", c, lc.Token)));
Test("element holder cannot disable", () => Error("VALIDATION_ERROR", () => r.Disable("doc", a, reenabled.Token)));

var doc = new Document();
foreach (var id in new long[] { 1, 2 })
{
    var element = new Element();
    element.Parameters["Comments"] = [new((long)BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)];
    element.Parameters["Mark"] = [new((long)BuiltInParameter.ALL_MODEL_MARK)];
    element.Parameters["Height"] = [new(42, StorageType.Double)];
    element.Parameters["Custom text"] = [new(43)];
    doc.Elements[id] = element;
}
var g = new WorkScopeGuard();
g.Execute(doc, "doc", a, null, new() { ["op"] = "acquire", ["scope"] = "elements", ["element_ids"] = new[] { 1 }, ["label"] = "review", ["idempotency_key"] = "guard1" }, default);
var gl = g.Registry.Acquire("doc", a, "guard1", "review", false, [1], [], 300);
Dictionary<string, object> Modification(object id, string name = "Comments") => new() { ["element_id"] = id, ["parameter_name"] = name, ["value"] = "checked" };
void Check(string command, Dictionary<string, object> p) => g.Check(doc, "doc", a, gl.Token, command, p, default);
Test("built-in instance comments allowed", () => Check("modify_element_parameter", Modification(1)));
Test("built-in instance mark allowed", () => Check("modify_element_parameter", Modification(1, "Mark")));
Test("geometry parameter requires document", () => Error("WORK_SCOPE_OUTSIDE", () => Check("modify_element_parameter", Modification(1, "Height"))));
Test("custom string requires document", () => Error("WORK_SCOPE_OUTSIDE", () => Check("modify_element_parameter", Modification(1, "Custom text"))));
Test("type parameter cannot bypass policy", () => { var p = Modification(1); p["is_type_param"] = true; Error("WORK_SCOPE_OUTSIDE", () => Check("modify_element_parameter", p)); });
Test("decimal string ID is supported", () => Check("modify_element_parameter", Modification("1")));
Test("malformed string ID cannot bypass policy", () => Error("WORK_SCOPE_OUTSIDE", () => Check("modify_element_parameter", Modification("1e0"))));
Test("uniform batch metadata allowed", () => Check("batch_modify_parameters", new() { ["element_ids"] = new[] { 1 }, ["parameters"] = new { Comments = "x", Mark = "y" } }));
Test("actual MCP uniform batch with null unused shape allowed", () => Check("batch_modify_parameters", new() { ["modifications"] = null, ["element_ids"] = new[] { 1 }, ["parameters"] = new { Comments = "x" } }));
Test("actual MCP explicit batch with null unused shape allowed", () => Check("batch_modify_parameters", new() { ["modifications"] = new[] { Modification(1) }, ["element_ids"] = null, ["parameters"] = null }));
Test("mixed batch scope rejected atomically", () => Error("WORK_SCOPE_OUTSIDE", () => Check("batch_modify_parameters", new() { ["modifications"] = new[] { Modification(1), Modification(2) } })));
Test("mixed batch type write rejected", () => { var p = Modification(1); p["is_type_param"] = true; Error("WORK_SCOPE_OUTSIDE", () => Check("batch_modify_parameters", new() { ["modifications"] = new[] { Modification(1), p } })); });
Test("unknown future command defaults to document", () => Error("WORK_SCOPE_OUTSIDE", () => Check("future_mutation", new())));
Test("queries remain available without token", () => Assert(g.Check(doc, "doc", null, null, "query_elements", new(), default) == null));
foreach (var command in new[] { "execute_script", "set_active_view", "delete_elements", "create_wall", "rename_type", "export_view", "reload_commandset", "move_elements" })
    Test(command + " requires exclusive document", () => Error("WORK_SCOPE_OUTSIDE", () => Check(command, new())));
doc.Elements[1].Parameters["Comments"].Add(new(999));
Test("ambiguous parameter names cannot bypass policy", () => Error("WORK_SCOPE_OUTSIDE", () => Check("modify_element_parameter", Modification(1))));
Test("invalid host acquisition does not enable coordination", () => { Error("VALIDATION_ERROR", () => g.Execute(doc, "missing", c, null, new() { ["op"] = "acquire", ["scope"] = "elements", ["element_ids"] = new[] { 99 }, ["label"] = "missing", ["idempotency_key"] = "bad" }, default)); Assert(!g.Registry.IsEnabled("missing")); });
Test("invalid TTL rejected", () => Error("VALIDATION_ERROR", () => g.Execute(doc, "doc", a, gl.Token, new() { ["op"] = "renew", ["ttl_seconds"] = 1 }, default)));
Test("irrelevant operation fields rejected", () => Error("VALIDATION_ERROR", () => g.Execute(doc, "doc", a, gl.Token, new() { ["op"] = "release", ["scope"] = "document" }, default)));
doc.Elements[9007199254740993L] = doc.Elements[2];
var largeGuard = new WorkScopeGuard();
Test("64-bit string IDs are reserved and checked without precision loss", () => {
    var result = largeGuard.Execute(doc, "large", a, null, new() { ["op"] = "acquire", ["scope"] = "elements", ["element_ids"] = new[] { "9007199254740993" }, ["label"] = "large", ["idempotency_key"] = "large" }, default);
    var token = System.Text.Json.JsonSerializer.SerializeToElement(result).GetProperty("assignment").GetProperty("lease_token").GetString();
    Assert(largeGuard.Check(doc, "large", a, token, "modify_element_parameter", Modification("9007199254740993"), default) != null);
});
Console.WriteLine($"{passed} work scope checks passed (policy uses test doubles; live Revit acceptance still required).");
