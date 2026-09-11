using System;
using System.Collections.Generic;

namespace RevitMCP.CommandSet.Helpers
{
    // A local user preference, never a caller-supplied script/RPC parameter.
    // Read the persistent User value on each call, not the inherited Process
    // snapshot, so changing back to prompt takes effect without restarting Revit.
    internal static class ScriptApprovalPolicy
    {
        internal const string SettingName = "REVIT_MCP_SCRIPT_APPROVAL";

        internal static ScriptApprovalDecision Decide(
            string userSetting, Func<string> showDialog)
        {
            var mode = string.IsNullOrEmpty(userSetting) ? "prompt" : userSetting;
            if (mode != "prompt" && mode != "auto")
                throw new ArgumentException(
                    SettingName + " must be exactly 'prompt' or 'auto'.");

            var source = string.IsNullOrEmpty(userSetting)
                ? "default" : "windows_user_environment";
            if (mode == "auto")
                return new ScriptApprovalDecision(true, mode, source, "not_shown");

            if (showDialog == null) throw new ArgumentNullException(nameof(showDialog));
            var result = showDialog();
            return new ScriptApprovalDecision(result == "Yes", mode, source, result);
        }
    }

    internal sealed class ScriptApprovalDecision
    {
        internal bool Approved { get; }
        internal string Mode { get; }
        internal string Source { get; }
        internal string DialogResult { get; }

        internal ScriptApprovalDecision(
            bool approved, string mode, string source, string dialogResult)
        {
            Approved = approved;
            Mode = mode;
            Source = source;
            DialogResult = dialogResult;
        }

        internal Dictionary<string, object> ToData() => new Dictionary<string, object>
        {
            ["approved"] = Approved,
            ["policy"] = Mode,
            ["source"] = Source,
            ["dialog_result"] = DialogResult
        };
    }
}
