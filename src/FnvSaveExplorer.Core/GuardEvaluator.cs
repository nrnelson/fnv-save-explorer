namespace FnvSaveExplorer.Core;

/// <summary>
/// Evaluates an FNV script <c>if</c>-guard against <b>decoded save state</b> with three-valued (Kleene) logic
/// (ROADMAP §6 #16, Stage B). This is the general condition evaluator the per-trigger completion passes have so
/// far sidestepped: where <see cref="ScriptStartup"/> asks "could this guard hold at game start (world atoms = 0)?"
/// for SGE seeding, <see cref="GuardEvaluator"/> asks "does this guard hold <i>now</i>, given what the save and our
/// computed quest model actually say?" — and answers <c>true</c>, <c>false</c>, or <c>null</c> (unknown).
/// <para><b>Precision-first.</b> Only a function whose value we can decode is resolved; everything else (quest-local
/// vars, globals, world queries we don't model) is <b>unknown</b>, and a comparison/boolean with an unknown operand
/// is unknown. Callers fire an effect only when the whole guard is definitely <c>true</c> — so an unknown guard
/// never fires, and we cannot mis-complete a quest on a condition we couldn't actually check.</para>
/// <para><b>Resolved functions (the first set):</b> <c>&lt;Ref&gt;.GetDead</c> / <c>GetDead &lt;Ref&gt;</c> against
/// the save's death registry; <c>GetObjectiveCompleted</c>/<c>GetObjectiveDisplayed</c>/<c>GetStage</c>/
/// <c>GetStageDone</c>/<c>GetQuestRunning</c>/<c>GetQuestCompleted</c> against the computed quest model; and integer
/// literals. New functions are added one at a time, each validated against the ground-truth oracles.</para>
/// Read-only.
/// </summary>
public sealed class GuardEvaluator
{
    private readonly IReadOnlySet<uint> _dead;
    private readonly IReadOnlyDictionary<string, uint> _refEdids;
    private readonly Func<string, uint?> _resolveQuest;
    private readonly Func<uint, bool?> _questRunning;
    private readonly Func<uint, bool?> _questCompleted;
    private readonly Func<uint, int?> _questStage;
    private readonly Func<uint, int, bool?> _objCompleted;
    private readonly Func<uint, int, bool?> _objDisplayed;

    /// <param name="dead">Save death registry: ref FormIDs (save-space) recorded as dead.</param>
    /// <param name="refEdids">Named placed-ref EDID → save-space FormID (<see cref="PluginDatabase.PlacedReferenceEdids"/>).</param>
    /// <param name="resolveQuest">Quest editor id → save-space quest FormID, or null when unknown.</param>
    /// <param name="questRunning">Quest FormID → running? (null = unknown).</param>
    /// <param name="questCompleted">Quest FormID → completed? (null = unknown).</param>
    /// <param name="questStage">Quest FormID → highest reached stage, or null when unknown.</param>
    /// <param name="objCompleted">(Quest FormID, objective index) → completed? (null = unknown).</param>
    /// <param name="objDisplayed">(Quest FormID, objective index) → displayed? (null = unknown).</param>
    public GuardEvaluator(
        IReadOnlySet<uint> dead,
        IReadOnlyDictionary<string, uint> refEdids,
        Func<string, uint?> resolveQuest,
        Func<uint, bool?> questRunning,
        Func<uint, bool?> questCompleted,
        Func<uint, int?> questStage,
        Func<uint, int, bool?> objCompleted,
        Func<uint, int, bool?> objDisplayed)
    {
        _dead = dead;
        _refEdids = refEdids;
        _resolveQuest = resolveQuest;
        _questRunning = questRunning;
        _questCompleted = questCompleted;
        _questStage = questStage;
        _objCompleted = objCompleted;
        _objDisplayed = objDisplayed;
    }

    /// <summary>Three-valued evaluation of <paramref name="guard"/>: <c>true</c>/<c>false</c> when decidable from
    /// decoded state, <c>null</c> when any atom it depends on is undecodable. A null/blank guard holds (<c>true</c>);
    /// an unparseable guard is unknown (<c>null</c>) — never a spurious <c>true</c>.</summary>
    public bool? Holds(string? guard)
    {
        if (string.IsNullOrWhiteSpace(guard))
            return true;
        try
        {
            var tokens = Tokenize(guard);
            var pos = 0;
            var r = ParseOr(tokens, ref pos);
            return pos == tokens.Count ? r : null;
        }
        catch
        {
            return null;
        }
    }

    // ----- Kleene combinators: false dominates AND, true dominates OR, otherwise unknown -----
    private static bool? And(bool? a, bool? b) =>
        a == false || b == false ? false : a == null || b == null ? null : true;
    private static bool? Or(bool? a, bool? b) =>
        a == true || b == true ? true : a == null || b == null ? null : false;

    private bool? ParseOr(List<string> t, ref int pos)
    {
        var v = ParseAnd(t, ref pos);
        while (pos < t.Count && t[pos] == "||") { pos++; v = Or(v, ParseAnd(t, ref pos)); }
        return v;
    }

    private bool? ParseAnd(List<string> t, ref int pos)
    {
        var v = ParseCmp(t, ref pos);
        while (pos < t.Count && t[pos] == "&&") { pos++; v = And(v, ParseCmp(t, ref pos)); }
        return v;
    }

    // cmp := '(' or-expr ')' | operand ( compareOp operand )?   (bare operand = truthiness)
    private bool? ParseCmp(List<string> t, ref int pos)
    {
        if (pos < t.Count && t[pos] == "(")
        {
            pos++;
            var v = ParseOr(t, ref pos);
            if (pos < t.Count && t[pos] == ")") pos++;
            return v;
        }
        var left = ReadOperand(t, ref pos);
        if (pos < t.Count && Array.IndexOf(CompareOps, t[pos]) >= 0)
        {
            var op = t[pos++];
            var right = ReadOperand(t, ref pos);
            return left is { } a && right is { } b ? Compare(a, op, b) : null; // unknown operand => unknown
        }
        return left is { } v2 ? v2 != 0 : null; // bare truthiness
    }

    /// <summary>An operand's integer value, or null when undecodable. Handles a numeric literal, a
    /// <c>&lt;Ref&gt;.Method</c> member call (only <c>GetDead</c> is decoded), and a prefix query function
    /// (<c>GetStage</c>/<c>GetObjective*</c>/<c>GetQuest*</c>) with its argument tokens. Anything else — a local
    /// variable, a global, a world query we don't model — is unknown.</summary>
    private int? ReadOperand(List<string> t, ref int pos)
    {
        if (pos >= t.Count)
            return null;
        var head = t[pos++];

        if (int.TryParse(head, out var n))
            return n;

        // <Ref>.Method  (FNV reference member call, e.g. "VFSFistoREF.GetDead")
        var dot = head.IndexOf('.');
        if (dot > 0)
        {
            var refName = head[..dot];
            var method = head[(dot + 1)..];
            if (method.Equals("GetDead", StringComparison.OrdinalIgnoreCase))
                return _refEdids.TryGetValue(refName, out var rf) ? (_dead.Contains(rf) ? 1 : 0) : null;
            return null; // other member calls (GetDisabled/GetUnconscious/GetItemCount/…) not decoded yet
        }

        // Prefix query function with N argument tokens.
        if (Arity.TryGetValue(head, out var arity))
        {
            var a = new string[arity];
            for (var i = 0; i < arity; i++)
            {
                if (pos >= t.Count || t[pos] is "&&" or "||" or "(" or ")" || Array.IndexOf(CompareOps, t[pos]) >= 0)
                    return null; // malformed call — be conservative
                a[i] = t[pos++];
            }
            return EvalFunction(head, a);
        }

        // GetDead in prefix form: "GetDead <Ref>"
        if (head.Equals("GetDead", StringComparison.OrdinalIgnoreCase) && pos < t.Count)
        {
            var refName = t[pos++];
            return _refEdids.TryGetValue(refName, out var rf) ? (_dead.Contains(rf) ? 1 : 0) : null;
        }

        return null; // bare local var / global / unmodelled function
    }

    private static readonly Dictionary<string, int> Arity = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GetStage"] = 1, ["GetQuestRunning"] = 1, ["GetQuestCompleted"] = 1,
        ["GetStageDone"] = 2, ["GetObjectiveCompleted"] = 2, ["GetObjectiveDisplayed"] = 2,
    };

    private static int? FromBool(bool? b) => b is { } v ? (v ? 1 : 0) : null;

    private int? EvalFunction(string fn, string[] args)
    {
        if (_resolveQuest(args[0]) is not { } quest)
            return null; // unknown quest edid
        switch (fn.ToLowerInvariant())
        {
            case "getstage":
                return _questStage(quest);
            case "getqueststage":
                return _questStage(quest);
            case "getquestrunning":
                return FromBool(_questRunning(quest));
            case "getquestcompleted":
                return FromBool(_questCompleted(quest));
            case "getstagedone" when int.TryParse(args[1], out var sd):
                return _questStage(quest) is { } st ? (st >= sd ? 1 : 0) : null;
            case "getobjectivecompleted" when int.TryParse(args[1], out var oc):
                return FromBool(_objCompleted(quest, oc));
            case "getobjectivedisplayed" when int.TryParse(args[1], out var od):
                return FromBool(_objDisplayed(quest, od));
            default:
                return null;
        }
    }

    private static bool Compare(int a, string op, int b) => op switch
    {
        "==" => a == b, "!=" => a != b, "<" => a < b, "<=" => a <= b, ">" => a > b, ">=" => a >= b, _ => false,
    };

    // ----- tokenizer (mirrors ScriptStartup's: identifiers keep dots so "Ref.Method" is one token) -----
    private static readonly string[] MultiCharOps = ["==", "!=", "<=", ">=", "&&", "||"];
    private static readonly string[] CompareOps = ["==", "!=", "<", "<=", ">", ">="];

    private static List<string> Tokenize(string s)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c is '(' or ')') { tokens.Add(c.ToString()); i++; continue; }
            var two = i + 1 < s.Length ? s.Substring(i, 2) : null;
            if (two is not null && Array.IndexOf(MultiCharOps, two) >= 0) { tokens.Add(two); i += 2; continue; }
            if (c is '<' or '>') { tokens.Add(c.ToString()); i++; continue; }
            var start = i;
            while (i < s.Length)
            {
                var d = s[i];
                if (char.IsWhiteSpace(d) || d is '(' or ')' or '<' or '>') break;
                if (i + 1 < s.Length && Array.IndexOf(MultiCharOps, s.Substring(i, 2)) >= 0) break;
                i++;
            }
            tokens.Add(s[start..i]);
        }
        return tokens;
    }
}
