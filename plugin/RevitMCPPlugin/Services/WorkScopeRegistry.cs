using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RevitMCP.Plugin.Services
{
    // Host-owned state. All calls, including change notifications, run on the
    // Revit main thread. No Revit dependency so the state machine is testable.
    internal sealed class WorkScopeRegistry
    {
        private readonly Func<DateTime> _clock;
        private readonly Dictionary<string, DocumentState> _documents = new Dictionary<string, DocumentState>();
        private sealed class DocumentState
        {
            public bool Enabled;
            public readonly List<Lease> Leases = new List<Lease>();
        }

        internal sealed class Lease
        {
            public string Token;
            public string Owner;
            public string Key;
            public string Label;
            public bool WholeDocument;
            public HashSet<long> Elements;
            public HashSet<long> WatchedElements;
            public DateTime Expires;
            public bool Released;
            public bool Stale;
            public long Revision;
            public bool Active(DateTime now) => !Released && Expires > now;
        }

        public WorkScopeRegistry(Func<DateTime> clock = null) { _clock = clock ?? (() => DateTime.UtcNow); }
        public bool IsEnabled(string document) => _documents.TryGetValue(document, out var state) && state.Enabled;
        public void Forget(string document) => _documents.Remove(document);

        public Lease Acquire(string document, string owner, string key, string label,
            bool wholeDocument, IEnumerable<long> ids, IEnumerable<long> watched, int ttl)
        {
            ValidateOwner(owner);
            if (string.IsNullOrWhiteSpace(key) || key.Length > 512 ||
                string.IsNullOrWhiteSpace(label) || label.Length > 120 || ttl < 30 || ttl > 1800)
                throw Invalid("Use a non-empty retry key (max 512), label (max 120), and ttl_seconds=30..1800.");
            var elements = new HashSet<long>(ids);
            if (elements.Count > 5000 || elements.Any(id => id <= 0) ||
                (wholeDocument ? elements.Count != 0 : elements.Count == 0))
                throw Invalid("Choose document scope without IDs, or elements scope with 1..5000 positive host IDs.");
            if (!_documents.TryGetValue(document, out var state))
            {
                state = new DocumentState();
                _documents.Add(document, state);
            }
            var now = _clock();
            // Keep tombstones for the host lifetime: a delayed acquire retry
            // must never silently resurrect an expired or released reservation.
            var previous = state.Leases.FirstOrDefault(l => l.Owner == owner && l.Key == key);
            if (previous != null)
            {
                if (previous.WholeDocument != wholeDocument || !previous.Elements.SetEquals(elements) || previous.Label != label)
                    throw new WorkScopeException("IDEMPOTENCY_CONFLICT", "The acquire key was used for another scope.", "Use a new idempotency_key for a different assignment.");
                if (!previous.Active(now)) throw Expired();
                return previous;
            }
            if (state.Leases.Any(l => l.Owner == owner && l.Active(now)))
                throw Invalid("This MCP process already owns an assignment in this document. Release it before acquiring another; use renew to extend it.");
            var conflicts = state.Leases.Where(l => l.Active(now) &&
                (wholeDocument || l.WholeDocument || l.Elements.Overlaps(elements))).ToArray();
            if (conflicts.Length != 0)
                throw new WorkScopeException("WORK_SCOPE_CONFLICT",
                    "Scope overlaps active assignments: " + string.Join(", ", conflicts.Take(10).Select(l => l.Label)),
                    "Inspect revit_work_scope status; choose disjoint host IDs or wait for the assignments to be released/expire.");
            if (state.Leases.Count >= 1000 || state.Leases.Count(l => l.Active(now)) >= 50)
                throw Invalid("Scope capacity reached (50 active / 1000 assignments per document lifetime). Release unused assignments; restart the host to clear history when work is finished.");
            var lease = new Lease
            {
                Token = Guid.NewGuid().ToString("N"), Owner = owner, Key = key, Label = label,
                WholeDocument = wholeDocument, Elements = elements,
                WatchedElements = new HashSet<long>(watched.Concat(elements)), Expires = now.AddSeconds(ttl)
            };
            state.Leases.Add(lease);
            state.Enabled = true;
            return lease;
        }

        public Lease Require(string document, string owner, string token)
        {
            ValidateOwner(owner);
            if (string.IsNullOrEmpty(token))
                throw new WorkScopeException("WORK_SCOPE_REQUIRED", "This document requires a work scope for changes.",
                    "Acquire an assignment using revit_work_scope, then query the model before editing.");
            if (!_documents.TryGetValue(document, out var state)) throw Expired();
            var lease = state.Leases.FirstOrDefault(l => l.Token == token && l.Owner == owner);
            if (lease == null || !lease.Active(_clock())) throw Expired();
            return lease;
        }

        public Lease CheckWrite(string document, string owner, string token, bool wholeDocument, IEnumerable<long> targets)
        {
            if (!IsEnabled(document) && string.IsNullOrEmpty(token)) return null;
            var lease = Require(document, owner, token);
            if (!lease.WholeDocument && (wholeDocument || !lease.Elements.IsSupersetOf(targets)))
                throw new WorkScopeException("WORK_SCOPE_OUTSIDE", "The command exceeds the reserved write scope.",
                    "Use host IDs inside your assignment. For geometry, types, scripts, UI or exports, release element assignments and acquire scope=document.");
            if (lease.Stale)
                throw new WorkScopeException("WORK_SCOPE_STALE", "Reserved elements or their tracked dependencies changed since the assignment was acquired.",
                    "Release this assignment, acquire it with a new idempotency_key, and re-query the model before recalculating edits. Renew does not clear this conflict.");
            return lease;
        }

        public Lease Renew(string document, string owner, string token, int ttl)
        {
            if (ttl < 30 || ttl > 1800) throw Invalid("Use ttl_seconds=30..1800.");
            var lease = Require(document, owner, token);
            lease.Expires = _clock().AddSeconds(ttl);
            return lease;
        }

        public void Release(string document, string owner, string token)
        {
            ValidateOwner(owner);
            if (!_documents.TryGetValue(document, out var state)) throw Expired();
            var lease = state.Leases.FirstOrDefault(l => l.Token == token && l.Owner == owner);
            if (lease == null) throw Expired();
            lease.Released = true; // Idempotent, including after expiry.
        }

        public void Disable(string document, string owner, string token)
        {
            // Replay a lost disable response without changing a subsequently
            // re-enabled document or accepting another owner's token.
            if (_documents.TryGetValue(document, out var state) && !state.Enabled &&
                state.Leases.Any(l => l.Token == token && l.Owner == owner && l.WholeDocument && l.Released)) return;
            var lease = Require(document, owner, token);
            if (!lease.WholeDocument) throw Invalid("Only the active document-scope holder may disable coordination.");
            lease.Released = true;
            _documents[document].Enabled = false;
        }

        public void Changed(string document, IEnumerable<long> changedIds)
        {
            if (!_documents.TryGetValue(document, out var state) || !state.Enabled) return;
            var ids = new HashSet<long>(changedIds);
            foreach (var lease in state.Leases.Where(l => !l.Released))
            {
                if (lease.WholeDocument || lease.WatchedElements.Overlaps(ids))
                {
                    lease.Stale = true;
                    lease.Revision++;
                }
            }
        }

        public void CompleteWrite(Lease lease) { if (lease != null) lease.Stale = false; }

        public object Status(string document, string owner)
        {
            var leases = _documents.TryGetValue(document, out var state)
                ? state.Leases.Where(l => l.Active(_clock())).ToArray() : Array.Empty<Lease>();
            return new Dictionary<string, object>
            {
                ["coordination_enabled"] = IsEnabled(document), ["active_count"] = leases.Length,
                ["assignments"] = leases.Select(l => Describe(l, l.Owner == owner)).ToArray()
            };
        }

        public object Describe(Lease lease, bool includeToken = true) => new Dictionary<string, object>
        {
            ["scope"] = lease.WholeDocument ? "document" : "elements", ["label"] = lease.Label,
            ["element_count"] = lease.Elements.Count,
            ["element_ids_preview"] = lease.Elements.OrderBy(id => id).Take(20).Select(id => id.ToString(CultureInfo.InvariantCulture)).ToArray(),
            ["expires_at_utc"] = lease.Expires.ToString("O"), ["stale"] = lease.Stale,
            ["revision"] = lease.Revision, ["owned_by_caller"] = includeToken,
            ["lease_token"] = includeToken ? lease.Token : null
        };

        private static void ValidateOwner(string owner)
        {
            if (!Guid.TryParseExact(owner, "N", out _)) throw Invalid("Use a per-MCP-process agent_id as a 32-character UUID.");
        }
        private static WorkScopeException Invalid(string suggestion) => new WorkScopeException("VALIDATION_ERROR", "Invalid work scope request.", suggestion);
        private static WorkScopeException Expired() => new WorkScopeException("WORK_SCOPE_EXPIRED", "Assignment is missing, released, expired, or owned by another MCP process.",
            "Acquire a new assignment with a new idempotency_key, then re-query. Never reuse an expired token.");
    }

    internal sealed class WorkScopeException : Exception
    {
        public string Code { get; }
        public string Suggestion { get; }
        public WorkScopeException(string code, string message, string suggestion) : base(message)
        { Code = code; Suggestion = suggestion; }
    }
}
