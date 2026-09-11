using RevitMCP.CommandSet.Helpers;

var checks = 0;
void Check(bool actual, string label)
{
    if (!actual) throw new Exception(label);
    checks++;
}

foreach (var setting in new string[] { null, "", "prompt" })
{
    foreach (var choice in new string[] { "Yes", "No", "Cancel", "Close", null, "unexpected" })
    {
        var calls = 0;
        var decision = ScriptApprovalPolicy.Decide(setting, () => { calls++; return choice; });
        Check(calls == 1, "Prompt called exactly once");
        Check(decision.Approved == (choice == "Yes"), "Only Yes approves a prompt");
        Check(decision.DialogResult == choice, "Preserve No versus cancellation");
        Check(decision.Mode == "prompt", "Default is prompt");
        Check(decision.Source == (string.IsNullOrEmpty(setting) ? "default" : "windows_user_environment"), "Source metadata");
    }
}

var automatic = ScriptApprovalPolicy.Decide("auto", () => throw new Exception("Must not open dialog"));
Check(automatic.Approved && automatic.Mode == "auto", "Explicit automatic approval");
Check(automatic.DialogResult == "not_shown", "Do not pretend user clicked Yes");
Check((string)automatic.ToData()["source"] == "windows_user_environment", "Automatic source reported");

foreach (var invalid in new[] { "true", "1", "yes", "AUTO", " auto ", " ", "false" })
{
    var threw = false;
    try { ScriptApprovalPolicy.Decide(invalid, () => throw new Exception("Invalid setting showed dialog")); }
    catch (ArgumentException) { threw = true; }
    Check(threw, "Invalid settings fail closed: " + invalid);
}

// No cached approval: revoking the user preference returns to the prompt.
Check(!ScriptApprovalPolicy.Decide("prompt", () => "No").Approved, "Prompt restored after auto");
Check(!ScriptApprovalPolicy.Decide(null, () => "Cancel").Approved, "Unset restores default after auto");
Console.WriteLine($"PASS {checks} script approval checks");
